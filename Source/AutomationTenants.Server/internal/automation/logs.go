package automation

import (
	"bufio"
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
)

type logIndex struct {
	Position int64
	Length   int64
}

type runLogs struct {
	Offsets map[string]int64
	Index   []logIndex
	Length  int64
}

func (s *store) logFile(tenant, id string) string {
	return filepath.Join(s.directory, "tenants", tenant, "runs", id, "events.jsonl")
}

// The append-only journal is authoritative for both source offsets and server
// page cursors. A crash after append but before its HTTP reply needs no replay
// transaction: reopening reconstructs exactly what was already committed.
func (s *store) loadLogs(tenant, id string) (*runLogs, error) {
	key := tenant + "/" + id
	if cached := s.logs[key]; cached != nil {
		return cached, nil
	}
	result := &runLogs{Offsets: map[string]int64{}, Index: []logIndex{}}
	path := s.logFile(tenant, id)
	f, err := os.OpenFile(path, os.O_RDWR, 0600)
	if errors.Is(err, os.ErrNotExist) {
		s.logs[key] = result
		return result, nil
	}
	if err != nil {
		return nil, err
	}
	defer f.Close()
	reader := bufio.NewReader(f)
	for {
		line, err := reader.ReadBytes('\n')
		if err == io.EOF {
			if len(line) > 0 {
				// Only the incomplete tail of this server-owned journal is discarded.
				if err := f.Truncate(result.Length); err != nil {
					return nil, err
				}
				if err := f.Sync(); err != nil {
					return nil, err
				}
			}
			break
		}
		if err != nil {
			return nil, err
		}
		var entry LogLine
		if json.Unmarshal(line, &entry) != nil || entry.Sequence != int64(len(result.Index))+1 || entry.Offset != result.Offsets[entry.Stream] {
			return nil, errors.New("远程运行日志索引损坏")
		}
		result.Offsets[entry.Stream] = entry.Offset + int64(len([]byte(entry.Raw))) + 1
		result.Index = append(result.Index, logIndex{result.Length, int64(len(line))})
		result.Length += int64(len(line))
	}
	s.logs[key] = result
	return result, nil
}

func (s *service) appendLogs(id string, request LogsRequest) (LogsReceipt, error) {
	if request.Stream == "" || !clean(request.Stream, 1024) || request.Offset < 0 || len(request.Lines) > 5000 {
		return LogsReceipt{}, fault(400, "日志 stream、offset 或批次格式无效。")
	}
	for _, line := range request.Lines {
		if strings.ContainsRune(line, '\n') || !json.Valid([]byte(line)) {
			return LogsReceipt{}, fault(400, "日志批次只能包含完整的原始 events.jsonl 行。")
		}
	}
	s.store.mu.Lock()
	defer s.store.mu.Unlock()
	if s.ignoredRun(request.MachineName, id) {
		next := request.Offset
		for _, line := range request.Lines {
			next += int64(len(line)) + 1
		}
		return LogsReceipt{NextOffset: next}, nil
	}
	assignment, err := s.agentRun(request.MachineName, id)
	if err != nil {
		return LogsReceipt{}, err
	}
	run := s.store.states[assignment.TenantID].Runs[id]
	if run.Source != "local" && run.AcceptedAt == nil {
		return LogsReceipt{}, fault(409, "运行尚未由机器接单。")
	}
	logs, err := s.store.loadLogs(assignment.TenantID, id)
	if err != nil {
		return LogsReceipt{}, err
	}
	confirmed := logs.Offsets[request.Stream]
	if request.Offset > confirmed {
		return LogsReceipt{confirmed}, fault(409, "日志偏移不连续，请从已确认位置续传。")
	}
	position := request.Offset
	first := 0
	for first < len(request.Lines) && position < confirmed {
		position += int64(len([]byte(request.Lines[first]))) + 1
		first++
	}
	if position < confirmed || position == confirmed && first == len(request.Lines) {
		return LogsReceipt{confirmed}, nil
	}
	if position != confirmed {
		return LogsReceipt{confirmed}, fault(409, "日志续传位置必须位于完整事件行边界。")
	}
	if first == len(request.Lines) {
		return LogsReceipt{confirmed}, nil
	}
	var batch bytes.Buffer
	encoder := json.NewEncoder(&batch)
	encoder.SetEscapeHTML(false)
	indexes := make([]logIndex, 0, len(request.Lines)-first)
	sequence := int64(len(logs.Index))
	for _, raw := range request.Lines[first:] {
		sequence++
		before := batch.Len()
		entry := LogLine{Sequence: sequence, Stream: request.Stream, Offset: position, Text: eventText(raw), Raw: raw}
		if err := encoder.Encode(entry); err != nil {
			return LogsReceipt{}, err
		}
		indexes = append(indexes, logIndex{logs.Length + int64(before), int64(batch.Len() - before)})
		position += int64(len([]byte(raw))) + 1
	}
	path := s.store.logFile(assignment.TenantID, id)
	if err := os.MkdirAll(filepath.Dir(path), 0700); err != nil {
		return LogsReceipt{}, err
	}
	f, err := os.OpenFile(path, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0600)
	if err != nil {
		return LogsReceipt{}, err
	}
	_, err = f.Write(batch.Bytes())
	if err == nil {
		err = f.Sync()
	}
	closed := f.Close()
	if err == nil {
		err = closed
	}
	if err != nil {
		delete(s.store.logs, assignment.TenantID+"/"+id)
		return LogsReceipt{}, err
	}
	logs.Offsets[request.Stream] = position
	logs.Index = append(logs.Index, indexes...)
	logs.Length += int64(batch.Len())
	return LogsReceipt{position}, nil
}

func (s *service) logPage(actor, selected, id string, after int64, limit int, team bool) (LogsPage, error) {
	run, err := s.get(actor, selected, id, team)
	if err != nil {
		return LogsPage{}, err
	}
	return s.runLogPage(s.config.tenantByID[run.TenantID], id, after, limit)
}

func (s *service) runLogPage(tenant tenantConfig, id string, after int64, limit int) (LogsPage, error) {
	if after < 0 || limit < 1 || limit > 2000 {
		return LogsPage{}, fault(400, "日志游标或 limit 无效。")
	}
	s.store.mu.Lock()
	defer s.store.mu.Unlock()
	if _, err := requireRun(s.store.states[tenant.ID], id); err != nil {
		return LogsPage{}, err
	}
	return s.readLogPage(tenant.ID, id, after, limit)
}

// The caller validates ownership and cursor bounds while holding the store mutex.
func (s *service) readLogPage(tenant, id string, after int64, limit int) (LogsPage, error) {
	logs, err := s.store.loadLogs(tenant, id)
	if err != nil {
		return LogsPage{}, err
	}
	page := LogsPage{Lines: []LogLine{}, NextCursor: after}
	if after >= int64(len(logs.Index)) {
		return page, nil
	}
	f, err := os.Open(s.store.logFile(tenant, id))
	if err != nil {
		return page, err
	}
	defer f.Close()
	end := min(int64(len(logs.Index)), after+int64(limit))
	for index := after; index < end; index++ {
		entry := logs.Index[index]
		reader := io.NewSectionReader(f, entry.Position, entry.Length)
		var line LogLine
		if err := json.NewDecoder(reader).Decode(&line); err != nil {
			return LogsPage{}, errors.New("运行日志无法读取")
		}
		page.Lines = append(page.Lines, line)
		page.NextCursor = line.Sequence
	}
	page.HasMore = page.NextCursor < int64(len(logs.Index))
	return page, nil
}

func eventField(value map[string]json.RawMessage, names ...string) json.RawMessage {
	for key, field := range value {
		normalized := strings.ToLower(strings.ReplaceAll(key, "_", ""))
		for _, name := range names {
			if normalized == name {
				return field
			}
		}
	}
	return nil
}

func stringValue(value json.RawMessage) string {
	var text string
	if json.Unmarshal(value, &text) == nil {
		return text
	}
	var object map[string]json.RawMessage
	if json.Unmarshal(value, &object) == nil {
		_ = json.Unmarshal(eventField(object, "value"), &text)
	}
	return text
}

// Display only: the native snapshot remains the sole source of Step state.
// Raw events are retained alongside this text for full diagnostics.
func eventText(raw string) string {
	var value map[string]json.RawMessage
	if json.Unmarshal([]byte(raw), &value) != nil {
		return raw
	}
	for depth := 0; depth < 32 && stringValue(eventField(value, "kind")) == "subgraph.event"; depth++ {
		var child map[string]json.RawMessage
		if json.Unmarshal(eventField(value, "data"), &child) != nil || child == nil {
			break
		}
		value = child
	}
	kind := stringValue(eventField(value, "kind"))
	title := stringValue(eventField(value, "nodetitle"))
	if title == "" {
		title = stringValue(eventField(value, "nodeid"))
	}
	message := stringValue(eventField(value, "message"))
	var failure map[string]json.RawMessage
	if json.Unmarshal(eventField(value, "error"), &failure) == nil {
		if detail := stringValue(eventField(failure, "message")); detail != "" && !strings.Contains(message, detail) {
			message += "\n" + detail
		}
	}
	label := kind
	if title != "" {
		label = title + " · " + kind
	}
	if source := stringValue(eventField(value, "source")); source != "" {
		label += "/" + source
	}
	if stamp := stringValue(eventField(value, "timestamp")); stamp != "" {
		label = stamp + " " + label
	}
	if label == "" {
		return message
	}
	return fmt.Sprintf("[%s] %s", label, message)
}
