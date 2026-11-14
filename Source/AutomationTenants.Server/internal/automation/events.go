package automation

import (
	"bytes"
	"context"
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"log"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"
	"unicode"
)

// Run events are recorded in the same atomic state write as the change they
// describe, so a crash can never persist a state change without its event.
// Delivery is at least once, in order per hook; receivers deduplicate by id.

const (
	eventOutboxLimit  = 1000
	eventBatch        = 20
	eventMaxAge       = 24 * time.Hour
	eventRetryCeiling = 10 * time.Minute
)

type eventHookConfig struct {
	Name   string `json:"name"`
	URL    string `json:"url"`
	Secret string `json:"secret"`
}

type RunEvent struct {
	ID            string       `json:"id"`
	Type          string       `json:"type"`
	TenantID      string       `json:"tenant_id"`
	OccurredAt    time.Time    `json:"occurred_at"`
	PreviousState string       `json:"previous_state"`
	State         string       `json:"state"`
	Review        *EventReview `json:"review,omitempty"`
	Run           EventRun     `json:"run"`
}

type EventReview struct {
	ID    string `json:"id"`
	Title string `json:"title,omitempty"`
}

type EventRun struct {
	ID          string            `json:"id"`
	TaskID      string            `json:"task_id"`
	TaskName    string            `json:"task_name"`
	MachineName string            `json:"machine_name"`
	PoolID      string            `json:"pool_id"`
	Source      string            `json:"source"`
	CreatedBy   string            `json:"created_by"`
	ParentRunID string            `json:"parent_run_id,omitempty"`
	RootRunID   string            `json:"root_run_id,omitempty"`
	RerunOf     string            `json:"rerun_of,omitempty"`
	Revision    int64             `json:"revision"`
	Detail      string            `json:"detail"`
	Parameters  map[string]string `json:"parameters"`
	CreatedAt   time.Time         `json:"created_at"`
	UpdatedAt   time.Time         `json:"updated_at"`
}

type outboxEntry struct {
	Hook          string    `json:"hook"`
	Event         RunEvent  `json:"event"`
	Attempts      int       `json:"attempts"`
	NextAttemptAt time.Time `json:"next_attempt_at"`
}

func validateEventHooks(tenant string, hooks []eventHookConfig) error {
	names := map[string]bool{}
	for _, hook := range hooks {
		if !safeID.MatchString(hook.Name) || names[hook.Name] {
			return errors.New("租户 " + tenant + " 的 event_hooks name 必须唯一且只含字母、数字、- 与 _")
		}
		address, err := url.Parse(hook.URL)
		if err != nil || address.Host == "" || address.User != nil || address.Fragment != "" ||
			(address.Scheme != "http" && address.Scheme != "https") {
			return errors.New("事件订阅 " + hook.Name + " 的 url 必须是不含凭据的 HTTP(S) 地址")
		}
		if len(hook.Secret) < 32 || !clean(hook.Secret, 512) || strings.ContainsFunc(hook.Secret, unicode.IsSpace) {
			return errors.New("事件订阅 " + hook.Name + " 的 secret 至少 32 字节且不含空白")
		}
		names[hook.Name] = true
	}
	return nil
}

func reviewOf(run RemoteRun) *EventReview {
	if run.State != "awaiting_review" {
		return nil
	}
	var header struct {
		Review *EventReview `json:"review"`
	}
	if len(run.Snapshot) == 0 || json.Unmarshal(run.Snapshot, &header) != nil || header.Review == nil {
		return &EventReview{}
	}
	return header.Review
}

// A run event is a state change, or a new review while already awaiting one.
func (s *store) recordEvents(tenant string, before, after *tenantState) {
	hooks := s.hooks[tenant]
	if len(hooks) == 0 {
		return
	}
	now := time.Now().UTC()
	queued := len(after.Outbox)
	for id, run := range after.Runs {
		old, existed := before.Runs[id]
		review := reviewOf(run)
		if existed && old.State == run.State {
			previous := reviewOf(old)
			if review == nil || previous != nil && previous.ID == review.ID {
				continue
			}
		}
		key := make([]byte, 16)
		if _, err := rand.Read(key); err != nil {
			continue
		}
		event := RunEvent{ID: hex.EncodeToString(key), Type: "run.state_changed", TenantID: tenant, OccurredAt: now,
			PreviousState: old.State, State: run.State, Review: review, Run: EventRun{
				ID: run.ID, TaskID: run.TaskID, TaskName: run.TaskName, MachineName: run.MachineName, PoolID: run.PoolID,
				Source: run.Source, CreatedBy: run.CreatedBy, ParentRunID: run.ParentRunID, RootRunID: run.RootRunID,
				RerunOf: run.RerunOf, Revision: run.Revision, Detail: run.Detail, Parameters: run.Parameters,
				CreatedAt: run.CreatedAt, UpdatedAt: run.UpdatedAt}}
		for _, hook := range hooks {
			after.Outbox = append(after.Outbox, outboxEntry{Hook: hook.Name, Event: event, NextAttemptAt: now})
		}
	}
	if dropped := len(after.Outbox) - eventOutboxLimit; dropped > 0 {
		log.Printf("Run events: tenant %s outbox full, dropped %d oldest", tenant, dropped)
		after.Outbox = after.Outbox[dropped:]
	}
	if len(after.Outbox) == queued {
		return
	}
	select {
	case s.wake <- struct{}{}:
	default:
	}
}

// dispatchEvents delivers each hook's queue in order; a failed head blocks only its own hook.
func (s *service) dispatchEvents(ctx context.Context) {
	client := &http.Client{Timeout: 10 * time.Second}
	ticker := time.NewTicker(5 * time.Second)
	defer ticker.Stop()
	for {
		for tenant := range s.store.hooks {
			s.deliverTenant(ctx, client, tenant)
		}
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
		case <-s.store.wake:
		}
	}
}

type delivery struct {
	entry outboxEntry
	err   error
	final bool
}

func (s *service) deliverTenant(ctx context.Context, client *http.Client, tenant string) {
	// Each hook's due prefix, in queue order; an entry still waiting on backoff blocks the rest of its hook.
	s.store.mu.Lock()
	due := map[string][]outboxEntry{}
	var order []string
	blocked := map[string]bool{}
	now, total := time.Now().UTC(), 0
	for _, entry := range s.store.states[tenant].Outbox {
		if blocked[entry.Hook] || total >= eventBatch {
			continue
		}
		if entry.NextAttemptAt.After(now) {
			blocked[entry.Hook] = true
			continue
		}
		if due[entry.Hook] == nil {
			order = append(order, entry.Hook)
		}
		due[entry.Hook] = append(due[entry.Hook], entry)
		total++
	}
	s.store.mu.Unlock()
	if total == 0 {
		return
	}
	hooks := map[string]eventHookConfig{}
	for _, hook := range s.store.hooks[tenant] {
		hooks[hook.Name] = hook
	}
	results := make([]delivery, 0, total)
	for _, name := range order {
		for _, entry := range due[name] {
			final, err := postEvent(ctx, client, hooks[name], entry.Event, s.config.Protocol.WebhookHeaderPrefix)
			results = append(results, delivery{entry, err, final})
			if err != nil && !final {
				break
			}
		}
	}
	s.store.mu.Lock()
	defer s.store.mu.Unlock()
	err := s.store.change(tenant, func(state *tenantState) error {
		for _, result := range results {
			index := -1
			for i, entry := range state.Outbox {
				if entry.Hook == result.entry.Hook && entry.Event.ID == result.entry.Event.ID {
					index = i
					break
				}
			}
			if index < 0 {
				continue
			}
			entry := &state.Outbox[index]
			expired := time.Since(entry.Event.OccurredAt) > eventMaxAge
			if result.err == nil || result.final || expired {
				if result.err != nil {
					log.Printf("Run events: dropped event %s for hook %s after %d attempts: %s",
						entry.Event.ID, entry.Hook, entry.Attempts+1, result.err)
				}
				state.Outbox = append(state.Outbox[:index], state.Outbox[index+1:]...)
				continue
			}
			entry.Attempts++
			wait := min(time.Duration(1<<min(entry.Attempts, 10))*5*time.Second, eventRetryCeiling)
			entry.NextAttemptAt = time.Now().UTC().Add(wait)
			if entry.Attempts == 1 || entry.Attempts%10 == 0 {
				log.Printf("Run events: hook %s delivery failed (attempt %d): %s", entry.Hook, entry.Attempts, result.err)
			}
		}
		return nil
	})
	if err != nil {
		log.Printf("Run events: failed to persist delivery results for tenant %s", tenant)
	}
}

// postEvent signs "<timestamp>.<body>" with HMAC-SHA256. A 4xx other than 408/429
// means the receiver rejects this event for good and retrying cannot help.
func postEvent(ctx context.Context, client *http.Client, hook eventHookConfig, event RunEvent, headerPrefix string) (bool, error) {
	if hook.URL == "" {
		return true, errors.New("订阅已从配置移除")
	}
	body, err := json.Marshal(event)
	if err != nil {
		return true, err
	}
	timestamp := strconv.FormatInt(time.Now().Unix(), 10)
	mac := hmac.New(sha256.New, []byte(hook.Secret))
	mac.Write([]byte(timestamp + "."))
	mac.Write(body)
	request, err := http.NewRequestWithContext(ctx, http.MethodPost, hook.URL, bytes.NewReader(body))
	if err != nil {
		return true, errors.New("无法创建请求")
	}
	request.Header.Set("Content-Type", "application/json; charset=utf-8")
	request.Header.Set(headerPrefix+"-Event-Id", event.ID)
	request.Header.Set(headerPrefix+"-Timestamp", timestamp)
	request.Header.Set(headerPrefix+"-Signature", "sha256="+hex.EncodeToString(mac.Sum(nil)))
	response, err := client.Do(request)
	if err != nil {
		return false, errors.New("连接失败")
	}
	response.Body.Close()
	switch {
	case response.StatusCode >= 200 && response.StatusCode < 300:
		return false, nil
	case response.StatusCode >= 400 && response.StatusCode < 500 && response.StatusCode != 408 && response.StatusCode != 429:
		return true, errors.New("接收方拒绝 HTTP " + strconv.Itoa(response.StatusCode))
	default:
		return false, errors.New("接收方返回 HTTP " + strconv.Itoa(response.StatusCode))
	}
}
