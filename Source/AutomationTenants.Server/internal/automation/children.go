package automation

import (
	"bytes"
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"slices"
	"strings"
	"time"
)

const subgraphCapability = "pregel.subgraph.v1"
const maxSubgraphPayload = 4 * 1024 * 1024

type subgraphHeader struct {
	ParentRunID          string          `json:"parent_run_id"`
	NodeID               string          `json:"node_id"`
	InvocationID         string          `json:"invocation_id"`
	ChildRunID           string          `json:"child_run_id"`
	RootGraphFingerprint string          `json:"root_graph_fingerprint"`
	GraphFingerprint     string          `json:"graph_fingerprint"`
	MutationPath         []string        `json:"mutation_path"`
	Input                json.RawMessage `json:"input"`
	ResumeValue          json.RawMessage `json:"resume_value"`
	ResumeInterruptID    string          `json:"resume_interrupt_id"`
}

// Go keeps the encoded runtime payload opaque. Canonical object encoding makes
// retries independent of JSON property order without interpreting CLR types.
func canonicalObject(raw json.RawMessage, label string) (json.RawMessage, error) {
	if len(raw) == 0 || len(raw) > maxSubgraphPayload || !bytes.HasPrefix(bytes.TrimSpace(raw), []byte("{")) || !json.Valid(raw) {
		return nil, fault(400, label+" 必须是非空 JSON 对象且不超过 4 MiB。")
	}
	decoder := json.NewDecoder(bytes.NewReader(raw))
	decoder.UseNumber()
	var value map[string]any
	if decoder.Decode(&value) != nil || len(value) == 0 {
		return nil, fault(400, label+" 必须是非空 JSON 对象。")
	}
	canonical, err := json.Marshal(value)
	if err != nil {
		return nil, err
	}
	if len(canonical) > maxSubgraphPayload {
		return nil, fault(400, label+" 规范化后的 JSON 对象超过 4 MiB。")
	}
	return canonical, nil
}

func validateSubgraph(raw json.RawMessage) (json.RawMessage, subgraphHeader, error) {
	value, err := canonicalObject(raw, "subgraph")
	var header subgraphHeader
	if err != nil {
		return nil, header, err
	}
	if json.Unmarshal(value, &header) != nil {
		return nil, header, fault(400, "subgraph 调用格式无效。")
	}
	for _, id := range []string{header.ParentRunID, header.NodeID, header.InvocationID, header.ChildRunID,
		header.RootGraphFingerprint, header.GraphFingerprint} {
		if id == "" || !clean(id, 256) {
			return nil, header, fault(400, "subgraph 缺少稳定运行、节点、调用或图身份。")
		}
	}
	if len(header.MutationPath) == 0 || len(header.MutationPath) > 64 {
		return nil, header, fault(400, "subgraph.mutation_path 无效。")
	}
	for _, part := range header.MutationPath {
		if part == "" || !clean(part, 256) {
			return nil, header, fault(400, "subgraph.mutation_path 无效。")
		}
	}
	if _, err := canonicalObject(header.Input, "subgraph.input"); err != nil {
		return nil, header, err
	}
	if header.ResumeInterruptID != "" || len(header.ResumeValue) > 0 && !bytes.Equal(bytes.TrimSpace(header.ResumeValue), []byte("null")) {
		return nil, header, fault(409, "等待人工处理的子图需要在目标机器处理，尚不接受远程恢复批准。")
	}
	return value, header, nil
}

func rootRunID(run RemoteRun) string {
	if run.RootRunID != "" {
		return run.RootRunID
	}
	return run.ID
}

func findParent(state *tenantState, assigned assignment, name, localID string) (RemoteRun, error) {
	if localID == "" || !clean(localID, 128) {
		return RemoteRun{}, fault(400, "parent_local_run_id 无效。")
	}
	for _, run := range state.Runs {
		if strings.EqualFold(run.MachineName, name) && run.LocalRunID == localID {
			if run.PoolID != assigned.PoolID {
				return RemoteRun{}, fault(403, "父运行不属于当前机器的机器池。")
			}
			return run, nil
		}
	}
	return RemoteRun{}, fault(404, "父运行尚未上报，请附带当前 parent_snapshot 后重试。")
}

func (s *service) observeParent(name, localID string, raw json.RawMessage) error {
	header, present, err := snapshotInfo(raw)
	if err != nil || !present || header.RunID != localID {
		if err != nil {
			return err
		}
		return fault(400, "parent_snapshot 必须与 parent_local_run_id 对应。")
	}
	title, revision := header.TaskID, ""
	s.store.mu.Lock()
	assigned, err := s.assignLocalRun(name, header.RemoteRequestID, localID, header.TaskID)
	if err != nil {
		s.store.mu.Unlock()
		return err
	}
	machine, exists := s.store.states[assigned.TenantID].Machines[strings.ToLower(name)]
	if !exists {
		s.store.mu.Unlock()
		return fault(503, "父运行所在机器尚未完成 Agent 首次登记，请等待 worker 的首次 poll 后重试。")
	}
	for _, task := range machine.Tasks {
		if taskConfiguration(task) == header.TaskID {
			title, revision = task.Name, task.ScriptRevision
			break
		}
	}
	s.store.mu.Unlock()
	_, err = s.observe(ObservationRequest{MachineName: name, TaskID: header.TaskID, TaskName: title,
		ScriptRevision: revision, Snapshot: raw})
	return err
}

func (s *service) createChild(request ChildRequest) (RemoteRun, error) {
	if err := validateCancellationFailure(request.Failure); err != nil {
		return RemoteRun{}, err
	}
	if request.Failure != nil && !request.CancelRequested {
		return RemoteRun{}, fault(400, "failure 只能随 cancel_requested 提交。")
	}
	name, err := machineName(request.MachineName)
	if err != nil {
		return RemoteRun{}, err
	}
	if request.ParentLocalRunID == "" || !clean(request.ParentLocalRunID, 128) {
		return RemoteRun{}, fault(400, "parent_local_run_id 无效。")
	}
	target, err := machineName(request.TargetMachineName)
	if err != nil {
		return RemoteRun{}, err
	}
	if request.TaskID == "" || !clean(request.TaskID, 128) {
		return RemoteRun{}, fault(400, "task_id 无效。")
	}
	if err := validateParameters(request.Parameters); err != nil {
		return RemoteRun{}, err
	}
	invocation, header, err := validateSubgraph(request.Subgraph)
	if err != nil {
		return RemoteRun{}, err
	}
	requestBytes, err := json.Marshal(struct {
		Target     string            `json:"target"`
		TaskID     string            `json:"task_id"`
		Parameters map[string]string `json:"parameters"`
		Subgraph   json.RawMessage   `json:"subgraph"`
	}{strings.ToLower(target), request.TaskID, parameters(request.Parameters), invocation})
	if err != nil {
		return RemoteRun{}, err
	}
	digest := sha256.Sum256(requestBytes)
	requestHash := hex.EncodeToString(digest[:])
	if len(request.ParentSnapshot) > 0 && !bytes.Equal(bytes.TrimSpace(request.ParentSnapshot), []byte("null")) {
		if err := s.observeParent(name, request.ParentLocalRunID, request.ParentSnapshot); err != nil {
			return RemoteRun{}, err
		}
	}
	s.store.mu.Lock()
	defer s.store.mu.Unlock()
	assigned, err := s.assignLocalRun(name, "", request.ParentLocalRunID, "")
	if err != nil {
		return RemoteRun{}, err
	}
	var result RemoteRun
	err = s.store.change(assigned.TenantID, func(state *tenantState) error {
		parent, err := findParent(state, assigned, name, request.ParentLocalRunID)
		if err != nil {
			return err
		}
		root, err := requireRun(state, rootRunID(parent))
		if err != nil {
			return err
		}
		cancelRequested := request.CancelRequested || terminal(parent.State) || parent.CancelRequested ||
			terminal(root.State) || root.CancelRequested
		// Check the durable invocation before mutable catalog, status and quota checks.
		for _, run := range state.Runs {
			if run.ParentRunID == parent.ID && run.ChildRunID == header.ChildRunID {
				if run.RequestHash != requestHash {
					return fault(409, "同一个子图调用身份已用于不同的目标、图或输入。")
				}
				if cancelRequested {
					if err := requestChildStop(state, run.ID, request.Failure); err != nil {
						return err
					}
				}
				result = state.Runs[run.ID]
				return nil
			}
		}
		depth := 0
		for ancestor := parent; ; {
			depth++
			if depth > 64 {
				return fault(400, "子图调用层级超过限制。")
			}
			if ancestor.ParentRunID == "" {
				break
			}
			ancestor, err = requireRun(state, ancestor.ParentRunID)
			if err != nil {
				return err
			}
		}
		targetAssignment, err := s.assignMachine(target, assigned.TenantID)
		if err != nil {
			return fault(403, "子图目标机器必须属于父运行所在租户。")
		}
		targetName := target
		task := &Task{ID: request.TaskID, Name: request.TaskID}
		if definition, ok := s.config.tenantByID[assigned.TenantID].taskDefinition(request.TaskID, ""); ok {
			task.ID, task.Name, task.ConfigurationID = definition.ID, definition.Name, request.TaskID
		} else {
			// A child invocation belongs to its registered root workflow even when
			// cancellation wins before the target advertises its configuration.
			task.ID, task.Name, task.ConfigurationID = parent.TaskID, parent.TaskName, request.TaskID
		}
		if !cancelRequested {
			if s.config.tenantByID[assigned.TenantID].ResourceServerURL == "" {
				return fault(409, "跨机器子图需要统一 Pregel 资源协调，请配置 resource_server_url。")
			}
			caller, exists := state.Machines[strings.ToLower(name)]
			if !exists || !slices.Contains(caller.Capabilities, mutexCapability) {
				return fault(409, "父运行所在机器尚不支持统一 Pregel 资源协调，请先升级 Agent。")
			}
			machine, exists := state.Machines[strings.ToLower(target)]
			if !exists {
				return fault(404, "目标机器尚未上报任务目录。")
			}
			if machine.PoolID != targetAssignment.PoolID {
				return fault(409, "目标机器的机器池归属已变更，请等待目录刷新。")
			}
			if !slices.Contains(machine.Capabilities, subgraphCapability) {
				return fault(409, "目标机器尚不支持子图执行，请先更新 Agent。")
			}
			if !slices.Contains(machine.Capabilities, mutexCapability) {
				return fault(409, "目标机器尚不支持统一 Pregel 资源协调，请先升级 Agent。")
			}
			targetName = machine.MachineName
			task = executionTask(machine, request.TaskID, true)
			if task == nil {
				task = executionTask(machine, parent.TaskID, true)
			}
			if task == nil {
				return fault(404, "目标机器未上报同一云端任务的可运行根任务。")
			}
			if task.GraphFingerprint != "" && task.GraphFingerprint != header.RootGraphFingerprint {
				return fault(409, "目标根任务的图指纹与调用方不一致。")
			}
		}
		key := make([]byte, 16)
		if _, err := rand.Read(key); err != nil {
			return err
		}
		now := time.Now().UTC()
		result = RemoteRun{ID: hex.EncodeToString(key), TenantID: assigned.TenantID, PoolID: targetAssignment.PoolID,
			MachineName: targetName, TaskID: task.ID, TaskName: task.Name, Parameters: parameters(request.Parameters),
			ConfigurationID: taskConfiguration(*task),
			ScriptRevision:  task.ScriptRevision, Source: "cloud", State: "queued", Revision: 1, Detail: "等待目标机器接收子图",
			CreatedAt: now, UpdatedAt: now, CreatedBy: parent.CreatedBy, ParentRunID: parent.ID, RootRunID: root.ID,
			ParentLocalRunID: parent.LocalRunID, ChildRunID: header.ChildRunID, ParentNodeID: header.NodeID,
			Subgraph: invocation, RequestHash: requestHash}
		if cancelRequested {
			// A cancellation can win the race with the first create. Keep its identity
			// durable so a delayed original POST cannot enqueue the invocation later.
			result.State = "cancelled"
			result.CancelRequested = true
			result.LogsComplete = true
			result.Detail = "子图在接单前已取消，未启动执行"
			if request.Failure != nil {
				failure := *request.Failure
				result.CancellationFailure = &failure
				result.State = "failed"
				result.Detail = failure.Code + ": " + failure.Message
			}
		}
		state.Runs[result.ID] = result
		return nil
	})
	return result, err
}

// The caller holds the store mutex. Access is limited to the direct parent;
// target-machine reporting continues through agentRun and its existing boundary.
func (s *service) childForParent(id string, request ChildAccessRequest) (assignment, RemoteRun, error) {
	name, err := machineName(request.MachineName)
	if err != nil {
		return assignment{}, RemoteRun{}, err
	}
	assigned, err := s.assignLocalRun(name, "", request.ParentLocalRunID, "")
	if err != nil {
		return assigned, RemoteRun{}, err
	}
	state := s.store.states[assigned.TenantID]
	parent, err := findParent(state, assigned, name, request.ParentLocalRunID)
	if err != nil {
		return assigned, RemoteRun{}, err
	}
	child, err := requireRun(state, id)
	if err != nil {
		return assigned, RemoteRun{}, err
	}
	if child.ParentRunID != parent.ID {
		return assigned, RemoteRun{}, fault(403, "子图调用不属于这条父运行。")
	}
	return assigned, child, nil
}

func (s *service) getChild(id string, request ChildAccessRequest) (RemoteRun, error) {
	s.store.mu.Lock()
	defer s.store.mu.Unlock()
	_, child, err := s.childForParent(id, request)
	return child, err
}

func validateCancellationFailure(failure *CancellationFailure) error {
	if failure != nil && (failure.Code == "" || !clean(failure.Code, 128) ||
		failure.Message == "" || len(failure.Message) > 8000) {
		return fault(400, "failure 需要有效 code 与 message。")
	}
	return nil
}

// The caller has verified direct-parent access and holds the store write lock.
func requestChildStop(state *tenantState, id string, failure *CancellationFailure) error {
	child := state.Runs[id]
	if failure != nil {
		if child.CancellationFailure != nil && *child.CancellationFailure != *failure {
			return fault(409, "子图已保存不同的取消失败原因。")
		}
		// Execution terminals are authoritative. A cancellation before acceptance
		// may receive its parent's already-decided failure after the parent ended.
		if child.CancellationFailure == nil && (!terminal(child.State) || child.AcceptedAt == nil && child.State == "cancelled") {
			reason := *failure
			child.CancellationFailure = &reason
			child.Revision++
			child.UpdatedAt = time.Now().UTC()
			state.Runs[id] = child
		}
	}
	cancelTree(state, id, true)
	child = state.Runs[id]
	if child.CancellationFailure != nil && child.AcceptedAt == nil && child.State == "cancelled" {
		child.State = "failed"
		child.Detail = child.CancellationFailure.Code + ": " + child.CancellationFailure.Message
		child.Revision++
		child.UpdatedAt = time.Now().UTC()
		state.Runs[id] = child
	}
	return nil
}

func (s *service) stopChild(id string, request ChildAccessRequest) (RemoteRun, error) {
	if err := validateCancellationFailure(request.Failure); err != nil {
		return RemoteRun{}, err
	}
	s.store.mu.Lock()
	defer s.store.mu.Unlock()
	assigned, _, err := s.childForParent(id, request)
	if err != nil {
		return RemoteRun{}, err
	}
	var result RemoteRun
	err = s.store.change(assigned.TenantID, func(state *tenantState) error {
		if err := requestChildStop(state, id, request.Failure); err != nil {
			return err
		}
		result = state.Runs[id]
		return nil
	})
	return result, err
}

func (s *service) childLogs(id string, request ChildAccessRequest) (LogsPage, error) {
	if request.Limit == 0 {
		request.Limit = 200
	}
	if request.After < 0 || request.Limit < 1 || request.Limit > 2000 {
		return LogsPage{}, fault(400, "日志游标或 limit 无效。")
	}
	s.store.mu.Lock()
	defer s.store.mu.Unlock()
	assigned, _, err := s.childForParent(id, request)
	if err != nil {
		return LogsPage{}, err
	}
	return s.readLogPage(assigned.TenantID, id, request.After, request.Limit)
}

// Keep cancellation in the existing request/confirmation protocol. Ancestors
// may be terminal while accepted descendants finish their own Finally cleanup.
func cancelTree(state *tenantState, id string, includeSelf bool) {
	selected := map[string]bool{id: true}
	for changed := true; changed; {
		changed = false
		for _, run := range state.Runs {
			if run.ParentRunID != "" && selected[run.ParentRunID] && !selected[run.ID] {
				selected[run.ID] = true
				changed = true
			}
		}
	}
	if !includeSelf {
		delete(selected, id)
	}
	now := time.Now().UTC()
	for childID := range selected {
		run, exists := state.Runs[childID]
		if !exists || terminal(run.State) || run.CancelRequested {
			continue
		}
		run.CancelRequested = true
		run.UpdatedAt = now
		run.Revision++
		if run.AcceptedAt == nil && run.LocalRunID == "" {
			run.State = "cancelled"
			run.LogsComplete = true
			run.Detail = "接单前已取消，未启动执行"
		} else {
			run.Detail = "已请求停止，等待机器确认"
		}
		state.Runs[childID] = run
	}
}
