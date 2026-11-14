package automation

import (
	"context"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/json"
	"errors"
	"fmt"
	"log"
	"net/http"
	"regexp"
	"slices"
	"sort"
	"strings"
	"time"
	"unicode"

	"github.com/modelcontextprotocol/go-sdk/mcp"
)

type mcpConfig struct {
	TenantIDs   []string `json:"tenant_ids"`
	BearerToken string   `json:"bearer_token,omitempty"`
	// Named callers (bots) get their own credential, audit actor and server-enforced
	// limits. Their tokens open only this automation endpoint, not other host
	// endpoints.
	Callers []mcpCaller `json:"callers,omitempty"`
}

type mcpCaller struct {
	Name             string   `json:"name"`
	UserName         string   `json:"user_name,omitempty"`
	BearerToken      string   `json:"bearer_token"`
	TaskIDs          []string `json:"task_ids,omitempty"`
	MaxReruns        int      `json:"max_reruns"`
	MaxStartsPerHour int      `json:"max_starts_per_hour"`
}

var mcpCallerName = regexp.MustCompile(`^[a-z][a-z0-9-]{1,31}$`)
var mcpUserName = regexp.MustCompile(`^[a-zA-Z0-9._-]{1,128}$`)

func (c *mcpConfig) validateCallers() error {
	names, tokens := map[string]bool{}, map[string]bool{c.BearerToken: c.BearerToken != ""}
	for _, caller := range c.Callers {
		if !mcpCallerName.MatchString(caller.Name) || caller.Name == "anonymous" || names[caller.Name] {
			return errors.New("MCP callers 的 name 必须是唯一的小写标识（字母开头，2-32 位），且不能为 anonymous")
		}
		if len(caller.BearerToken) < 32 || !clean(caller.BearerToken, 512) ||
			strings.ContainsFunc(caller.BearerToken, unicode.IsSpace) || tokens[caller.BearerToken] {
			return errors.New("MCP caller " + caller.Name + " 的 bearer_token 至少 32 字节、不含空白，且不能与其他凭据相同")
		}
		if caller.MaxReruns < 1 || caller.MaxStartsPerHour < 1 {
			return errors.New("MCP caller " + caller.Name + " 必须明确配置正数 max_reruns 与 max_starts_per_hour")
		}
		if caller.UserName != "" && (!mcpUserName.MatchString(caller.UserName) || len(caller.TaskIDs) == 0) {
			return errors.New("个人 MCP caller 必须指定有效的 user_name 与 task_ids")
		}
		for _, id := range caller.TaskIDs {
			if id == "" || !clean(id, 128) {
				return errors.New("MCP caller " + caller.Name + " 的 task_ids 无效")
			}
		}
		names[caller.Name], tokens[caller.BearerToken] = true, true
	}
	return nil
}

// mcpIdentity is what a tool call acts as. The shared team credential keeps the
// historical anonymous actor and has no caller limits.
type mcpIdentity struct {
	Actor            string
	TaskIDs          []string
	MaxReruns        int
	MaxStartsPerHour int
}

var anonymousMCP = mcpIdentity{Actor: mcpActor}

func (id mcpIdentity) controls(taskID string) bool {
	return len(id.TaskIDs) == 0 || slices.Contains(id.TaskIDs, taskID)
}

func (id mcpIdentity) requireTask(taskID string) error {
	if !id.controls(taskID) {
		return fault(403, "此 MCP 调用方未被授权控制该任务。")
	}
	return nil
}

// admit enforces caller budgets atomically with run creation.
func (id mcpIdentity) admit(rerunRoot string) func(*tenantState, time.Time) error {
	if id.MaxStartsPerHour < 1 && id.MaxReruns < 1 {
		return nil
	}
	return func(state *tenantState, now time.Time) error {
		starts, reruns := 0, 0
		for _, run := range state.Runs {
			if run.CreatedBy != id.Actor {
				continue
			}
			if now.Sub(run.CreatedAt) < time.Hour {
				starts++
			}
			if rerunRoot != "" && run.RerunOf == rerunRoot {
				reruns++
			}
		}
		if id.MaxStartsPerHour > 0 && starts >= id.MaxStartsPerHour {
			return fault(429, fmt.Sprintf("此调用方最近一小时已启动 %d 次，达到上限；请交给人处理。", starts))
		}
		if rerunRoot != "" && id.MaxReruns > 0 && reruns >= id.MaxReruns {
			return fault(429, fmt.Sprintf("这条运行已被此调用方重跑 %d 次，达到上限；请告警并交给人处理。", reruns))
		}
		return nil
	}
}

const mcpActor = "mcp:anonymous"
const mcpResultLimit = 1024 * 1024

type mcpPageInput struct {
	TenantID string `json:"tenant_id,omitempty" jsonschema:"Limit to a tenant returned by automation_machines; omit to list all MCP-enabled tenants."`
	Offset   int    `json:"offset,omitempty" jsonschema:"Page offset, initially 0. Use next_offset from the previous result."`
	Limit    int    `json:"limit,omitempty" jsonschema:"Page size, default 25, maximum 100."`
}

type mcpMachineListInput struct {
	mcpPageInput
	MachineName string `json:"machine_name,omitempty" jsonschema:"Optional exact machine name, case insensitive."`
}

type mcpTaskListInput struct {
	mcpMachineListInput
	TaskID string `json:"task_id,omitempty" jsonschema:"Optional stable cloud task ID."`
}

type mcpRunListInput struct {
	mcpTaskListInput
	RequestID    string `json:"request_id,omitempty" jsonschema:"Find a previous start or rerun by this caller's request_id."`
	State        string `json:"state,omitempty" jsonschema:"Filter by native run state."`
	UpdatedSince string `json:"updated_since,omitempty" jsonschema:"RFC 3339 time, inclusive. Returns runs whose updated_at is at or after it, OLDEST change first, so a poller can page forward. Keep the last item's updated_at for the next poll and de-duplicate by run_id plus revision."`
}

type mcpRerunInput struct {
	TenantID  string `json:"tenant_id,omitempty" jsonschema:"Tenant ID; required when more than one tenant is enabled."`
	RunID     string `json:"run_id" jsonschema:"A finished root run to run again with its recorded launch parameters on the same machine."`
	RequestID string `json:"request_id" jsonschema:"Caller-generated unique ID. Reuse the SAME ID after a lost response; a new ID is a new rerun and counts against the caller's rerun budget."`
	Reason    string `json:"reason" jsonschema:"Why a rerun is expected to help, for example the diagnosed transient cause. Recorded with the new run."`
}

type mcpExecutionInput struct {
	TenantID    string            `json:"tenant_id,omitempty" jsonschema:"Tenant ID; required when more than one tenant is enabled."`
	MachineName string            `json:"machine_name" jsonschema:"Exact target from automation_machines."`
	TaskID      string            `json:"task_id" jsonschema:"Stable cloud task ID from automation_tasks; local names and configurations do not create new tasks."`
	Parameters  map[string]string `json:"parameters,omitempty" jsonschema:"Per-run parameter overrides, encoded as strings. Omitted values use the target machine defaults. Inspect these same values before starting."`
}

func (in mcpExecutionInput) request() CreateRequest {
	return CreateRequest{TenantID: in.TenantID, MachineName: in.MachineName, TaskID: in.TaskID, Parameters: in.Parameters}
}

type mcpStartInput struct {
	mcpExecutionInput
	RequestID string `json:"request_id" jsonschema:"Caller-generated unique ID, for example a UUID. Reuse the SAME ID and arguments after a lost response; use a NEW ID only for a new execution. Persisted across reconnects and server restarts."`
}

type mcpRunInput struct {
	TenantID string `json:"tenant_id,omitempty" jsonschema:"Tenant ID; required when more than one tenant is enabled."`
	RunID    string `json:"run_id" jsonschema:"Cloud run ID returned by automation_start or automation_runs."`
}

type mcpGetInput struct {
	mcpRunInput
	IncludeSnapshot bool `json:"include_snapshot,omitempty" jsonschema:"For automation_get, include the native snapshot, output and subgraph; default false. Large results may exceed the response limit."`
}

type mcpLogsInput struct {
	TenantID   string `json:"tenant_id,omitempty" jsonschema:"Tenant ID; required when more than one tenant is enabled."`
	RunID      string `json:"run_id" jsonschema:"Cloud run ID."`
	After      int64  `json:"after,omitempty" jsonschema:"Server next_cursor from the previous page; initially 0. This is not the Agent upload byte offset."`
	Limit      int    `json:"limit,omitempty" jsonschema:"Log records per page, default 50, maximum 100. Reduce if a result is too large."`
	IncludeRaw bool   `json:"include_raw,omitempty" jsonschema:"Include original events for diagnostics; default false. Large raw events may exceed the response limit."`
}

type mcpReviewInput struct {
	TenantID    string `json:"tenant_id,omitempty" jsonschema:"Tenant ID; required when more than one tenant is enabled."`
	RunID       string `json:"run_id" jsonschema:"Cloud run ID."`
	Action      string `json:"action" jsonschema:"approve, revise or abandon. Read the current native review before deciding."`
	ReviewID    string `json:"review_id" jsonschema:"Current review ID returned by automation_get."`
	ContentHash string `json:"content_hash" jsonschema:"Hash of the review content actually read; stale reviews are rejected."`
	Feedback    string `json:"feedback,omitempty" jsonschema:"Required for revise; instructions for continuing the existing run."`
}

func (s *service) automationMCP() http.Handler {
	if s.config.MCP == nil {
		return http.NotFoundHandler()
	}
	options := &mcp.StreamableHTTPOptions{
		Stateless: true, JSONResponse: true, MaxRequestBodyBytes: 4 << 20, PropagateRequestCancellation: true,
	}
	// One server per identity, so every tool call carries its actor without trusting request data.
	newHandler := func(id mcpIdentity) http.Handler {
		server := s.newAutomationMCPServer(id)
		return mcp.NewStreamableHTTPHandler(func(*http.Request) *mcp.Server { return server }, options)
	}
	shared := newHandler(anonymousMCP)
	type callerHandler struct {
		token   [32]byte
		handler http.Handler
	}
	callers := []callerHandler{}
	for _, caller := range s.config.MCP.Callers {
		actor := "mcp:" + caller.Name
		if caller.UserName != "" {
			actor = "mcp:user:" + strings.ToLower(caller.UserName) + ":" + caller.Name
		}
		callers = append(callers, callerHandler{sha256.Sum256([]byte(caller.BearerToken)), newHandler(mcpIdentity{
			Actor: actor, TaskIDs: caller.TaskIDs, MaxReruns: caller.MaxReruns, MaxStartsPerHour: caller.MaxStartsPerHour,
		})})
	}
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Cache-Control", "no-store")
		if token, ok := bearerToken(r); ok {
			provided := sha256.Sum256([]byte(token))
			for _, caller := range callers {
				if subtle.ConstantTimeCompare(caller.token[:], provided[:]) == 1 {
					caller.handler.ServeHTTP(w, r)
					return
				}
			}
		}
		if s.config.MCP.BearerToken != "" && !s.authorizeMCP(w, r) {
			return
		}
		shared.ServeHTTP(w, r)
	})
}

func (s *service) newAutomationMCPServer(id mcpIdentity) *mcp.Server {
	server := mcp.NewServer(&mcp.Implementation{Name: s.config.Protocol.MCPServerName, Version: "1.2.0"}, &mcp.ServerOptions{
		Instructions: "Control registered Pregel automation through its existing Agents. First list machines and tasks. A registered workflow need not already exist on the target: use automation_deploy and poll automation_deployment until ready before inspecting. Starting an unreported officially distributable task also queues deployment automatically. Then inspect the exact target and parameter overrides. Start and rerun return immediately; poll automation_get and automation_logs. A disconnected MCP request does not stop a run. Keep task_id stable. Never retry a start or rerun with a new request_id after an unknown outcome. To watch for changes, poll automation_runs with updated_since. Calls are recorded as " + id.Actor + "; a named caller may be limited to some tasks and to a start and rerun budget enforced by the server, so a refusal means hand the problem to a person, not try another way. Interactive agent sessions, arbitrary shell commands and workflow publishing are outside this endpoint.",
	})
	addAutomationTool(server, "automation_deploy", "Deploy a cloud-registered workflow from the published official distribution catalog to a connected Agent, even when that machine has not reported the task. Pins source and Shared fingerprints. Returns a durable deployment id immediately; poll automation_deployment until ready, then inspect/start. No interactive session or manual task creation is needed. Project-local tasks and arbitrary uploads are not distributed.",
		false, true, false, func(ctx context.Context, in DeployRequest) (any, error) {
			if err := id.requireTask(in.TaskID); err != nil {
				return nil, err
			}
			tenant, err := s.mcpTenant(in.TenantID)
			if err != nil {
				return nil, err
			}
			return s.deploy(ctx, tenant, id.Actor, in)
		})
	addAutomationTool(server, "automation_deployment", "Read a persisted workflow deployment receipt: queued, preparing, ready or failed, with target, pinned versions and failure detail. Query again after disconnect; never create a new request_id just to recover a response.",
		true, true, false, func(_ context.Context, in struct {
			TenantID     string `json:"tenant_id,omitempty"`
			DeploymentID string `json:"deployment_id"`
		}) (any, error) {
			tenant, err := s.mcpTenant(in.TenantID)
			if err != nil {
				return nil, err
			}
			return s.getDeployment(tenant, in.DeploymentID)
		})
	addAutomationTool(server, "automation_machines", "List MCP-enabled tenants and reported machines, capabilities and last_seen. Paginated; no execution is started.", true, true, false, s.mcpMachines)
	addAutomationTool(server, "automation_tasks", "List registered cloud tasks and whether this caller may control each. Set machine_name to see that machine's reported availability and revision; inspect for resolved parameters and validation.",
		true, true, false, func(ctx context.Context, in mcpTaskListInput) (any, error) { return s.mcpTasks(ctx, id, in) })
	addAutomationTool(server, "automation_inspect", "Ask the target Agent for its native parameter form, resolved defaults, validation and execution plan. If not reported, first use automation_deploy and wait for ready. Does not execute the graph; may wait up to 20 seconds.", true, true, false, s.mcpInspect)
	addAutomationTool(server, "automation_start", "Queue ONE registered task with per-run overrides. An unreported but officially distributable workflow is first deployed by a compatible Agent; deployment_id exposes preparation progress. Requires request_id; identical retries return the original run. Use automation_get/logs; no interactive session or manual worker installation is needed.",
		false, true, false, func(ctx context.Context, in mcpStartInput) (any, error) { return s.mcpStart(ctx, id, in) })
	addAutomationTool(server, "automation_rerun", "Run a finished root run again from the beginning, on the same machine with its recorded launch parameters. This is a fresh run, not a resume from the failed node. Requires request_id and reason; the new run records rerun_of. Counts against this caller's rerun budget per original run.",
		false, true, false, func(ctx context.Context, in mcpRerunInput) (any, error) { return s.mcpRerun(ctx, id, in) })
	addAutomationTool(server, "automation_runs", "List run summaries, newest first; filter by task, machine, state or a previous request_id of this caller. With updated_since, returns changed runs oldest change first for polling. Use automation_get for parameters and native review/artifact metadata.",
		true, true, false, func(ctx context.Context, in mcpRunListInput) (any, error) { return s.mcpRuns(ctx, id, in) })
	addAutomationTool(server, "automation_get", "Read a run's current native state, immutable launch parameters, review and artifact metadata. include_snapshot optionally returns the full native record. logs_complete is transport completion, not business success.", true, true, false, s.mcpGet)
	addAutomationTool(server, "automation_logs", "Read persisted run logs by server cursor. Follow next_cursor and has_more; no new records does not imply the run finished.", true, true, false, s.mcpLogs)
	addAutomationTool(server, "automation_stop", "Request cancellation through the existing Pregel/Agent path. Read automation_get until the Agent reports a final state; the request itself is not proof of termination.",
		false, true, true, func(ctx context.Context, in mcpRunInput) (any, error) { return s.mcpStop(ctx, id, in) })
	addAutomationTool(server, "automation_review", "Submit approve/revise/abandon for the exact native review content previously read. The Agent confirms application asynchronously; stale content is rejected.",
		false, false, true, func(ctx context.Context, in mcpReviewInput) (any, error) { return s.mcpReview(ctx, id, in) })
	// Execution state lives in the existing store, never in a transient MCP session.
	return server
}

func bearerToken(r *http.Request) (string, bool) {
	values := r.Header.Values("Authorization")
	if len(values) != 1 {
		return "", false
	}
	parts := strings.Fields(values[0])
	if len(parts) != 2 || !strings.EqualFold(parts[0], "Bearer") {
		return "", false
	}
	return parts[1], true
}

// Nginx uses the same private credential for the existing C# MCP entrances.
// It receives only an admission result; the credential never appears in the body.
func (s *service) mcpAdmission(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Cache-Control", "no-store")
	if s.authorizeMCP(w, r) {
		w.WriteHeader(http.StatusNoContent)
	}
}

func (s *service) authorizeMCP(w http.ResponseWriter, r *http.Request) bool {
	if s.config.MCP == nil || s.config.MCP.BearerToken == "" {
		sendJSON(w, http.StatusServiceUnavailable, map[string]string{"error": "MCP 共享凭据尚未配置。"})
		return false
	}
	valid := false
	if token, ok := bearerToken(r); ok {
		expected := sha256.Sum256([]byte(s.config.MCP.BearerToken))
		provided := sha256.Sum256([]byte(token))
		valid = subtle.ConstantTimeCompare(expected[:], provided[:]) == 1
	}
	if !valid {
		w.Header().Set("WWW-Authenticate", `Bearer realm="`+s.config.Protocol.MCPRealm+`"`)
		sendJSON(w, http.StatusUnauthorized, map[string]string{"error": "需要有效的团队 MCP Bearer Token。"})
	}
	return valid
}

func addAutomationTool[In any](server *mcp.Server, name, description string, readOnly, idempotent, destructive bool, handle func(context.Context, In) (any, error)) {
	mcp.AddTool(server, &mcp.Tool{Name: name, Description: description, Annotations: &mcp.ToolAnnotations{
		ReadOnlyHint: readOnly, IdempotentHint: idempotent, DestructiveHint: &destructive,
	}}, func(ctx context.Context, _ *mcp.CallToolRequest, in In) (*mcp.CallToolResult, any, error) {
		value, err := handle(ctx, in)
		status, message := 500, "自动化操作结果未知；启动请求请保留原 request_id 查询或重试。"
		if err == nil {
			data, encodeErr := json.Marshal(value)
			if encodeErr == nil && len(data) <= mcpResultLimit {
				return &mcp.CallToolResult{StructuredContent: json.RawMessage(data), Content: []mcp.Content{&mcp.TextContent{Text: string(data)}}}, nil, nil
			}
			if encodeErr == nil {
				err = fault(413, "结果过大；请缩小 limit、筛选目录或关闭 include_snapshot。启动已提交时，请用原 request_id 查询，勿换 ID 重启。")
			} else {
				err = encodeErr
			}
		}
		var expected *apiFault
		if errors.As(err, &expected) {
			status, message = expected.status, expected.message
		} else {
			log.Printf("Automation MCP %s failed: %T", name, err)
		}
		data, _ := json.Marshal(map[string]any{"status": status, "error": message})
		return &mcp.CallToolResult{IsError: true, StructuredContent: json.RawMessage(data), Content: []mcp.Content{&mcp.TextContent{Text: string(data)}}}, nil, nil
	})
}

// This policy never trusts caller-supplied actor headers or adds a fake admin.
func (s *service) mcpTenants(selected string) ([]tenantConfig, error) {
	var tenants []tenantConfig
	if s.config.MCP != nil {
		for _, id := range s.config.MCP.TenantIDs {
			if selected == "" || id == selected {
				tenants = append(tenants, s.config.tenantByID[id])
			}
		}
	}
	if len(tenants) == 0 {
		return nil, fault(403, "此租户未向自动化 MCP 开放。")
	}
	return tenants, nil
}

func (s *service) mcpTenant(selected string) (tenantConfig, error) {
	tenants, err := s.mcpTenants(selected)
	if err != nil {
		return tenantConfig{}, err
	}
	if len(tenants) != 1 {
		return tenantConfig{}, fault(400, "请从 automation_machines 返回的租户中选择 tenant_id。")
	}
	return tenants[0], nil
}

func mcpPage[T any](items []T, offset, limit int) (any, error) {
	if limit == 0 {
		limit = 25
	}
	if offset < 0 || limit < 1 || limit > 100 {
		return nil, fault(400, "offset 必须非负，limit 必须介于 1 和 100。")
	}
	start := min(offset, len(items))
	end := start + min(limit, len(items)-start)
	return map[string]any{"items": items[start:end], "next_offset": end, "has_more": end < len(items)}, nil
}

func (s *service) mcpMachines(_ context.Context, in mcpMachineListInput) (any, error) {
	tenants, err := s.mcpTenants(in.TenantID)
	if err != nil {
		return nil, err
	}
	items := []Machine{}
	views := []Tenant{}
	s.store.mu.Lock()
	for _, tenant := range tenants {
		views = append(views, tenant.view())
		for _, machine := range s.store.states[tenant.ID].Machines {
			if in.MachineName == "" || strings.EqualFold(in.MachineName, machine.MachineName) {
				machine.Tasks = nil
				items = append(items, machine)
			}
		}
	}
	s.store.mu.Unlock()
	sort.Slice(items, func(i, j int) bool {
		return items[i].TenantID+"/"+items[i].MachineName < items[j].TenantID+"/"+items[j].MachineName
	})
	page, err := mcpPage(items, in.Offset, in.Limit)
	if err != nil {
		return nil, err
	}
	return map[string]any{"tenants": views, "machines": page, "observed_at": time.Now().UTC()}, nil
}

func (s *service) mcpTasks(_ context.Context, id mcpIdentity, in mcpTaskListInput) (any, error) {
	tenants, err := s.mcpTenants(in.TenantID)
	if err != nil {
		return nil, err
	}
	items := []map[string]any{}
	s.store.mu.Lock()
	defer s.store.mu.Unlock()
	for _, tenant := range tenants {
		machine, present := s.store.states[tenant.ID].Machines[strings.ToLower(in.MachineName)]
		for _, task := range tenant.registeredTasks() {
			if in.TaskID != "" && task.ID != in.TaskID {
				continue
			}
			item := map[string]any{"tenant_id": tenant.ID, "task_id": task.ID, "name": task.Name, "can_control": id.controls(task.ID)}
			if in.MachineName != "" {
				item["machine_name"], item["reported"], item["can_run"] = in.MachineName, false, false
				if present {
					if reported := executionTask(machine, task.ID, false); reported != nil {
						item["reported"], item["can_run"], item["unavailable_reason"] = true, reported.CanRun, reported.UnavailableReason
						item["script_revision"] = reported.ScriptRevision
					}
				}
			}
			items = append(items, item)
		}
	}
	sort.Slice(items, func(i, j int) bool {
		return items[i]["tenant_id"].(string)+"/"+items[i]["task_id"].(string) < items[j]["tenant_id"].(string)+"/"+items[j]["task_id"].(string)
	})
	return mcpPage(items, in.Offset, in.Limit)
}

func (s *service) mcpInspect(ctx context.Context, in mcpExecutionInput) (any, error) {
	tenant, err := s.mcpExecutionTenant(in)
	if err != nil {
		return nil, err
	}
	return s.inspectRun(ctx, tenant, in.request())
}

func (s *service) mcpExecutionTenant(in mcpExecutionInput) (tenantConfig, error) {
	tenant, err := s.mcpTenant(in.TenantID)
	if err != nil {
		return tenantConfig{}, err
	}
	for _, task := range tenant.registeredTasks() {
		if task.ID == in.TaskID {
			return tenant, nil
		}
	}
	return tenantConfig{}, fault(404, "请使用 automation_tasks 返回的云端 task_id，不能使用本地配置 ID 或昵称。")
}

func (s *service) mcpStart(_ context.Context, id mcpIdentity, in mcpStartInput) (any, error) {
	if in.RequestID == "" {
		return nil, fault(400, "启动必须提供 request_id，重试时保留同一个 ID。")
	}
	if err := id.requireTask(in.TaskID); err != nil {
		return nil, err
	}
	tenant, err := s.mcpExecutionTenant(in.mcpExecutionInput)
	if err != nil {
		return nil, err
	}
	request := in.request()
	request.RequestID = in.RequestID
	run, err := s.createRunAdmitted(tenant, id.Actor, request, id.admit(""))
	if err != nil {
		return nil, err
	}
	return mcpRunSummary(run), nil
}

func (s *service) mcpRerun(_ context.Context, id mcpIdentity, in mcpRerunInput) (any, error) {
	reason := strings.TrimSpace(in.Reason)
	if in.RequestID == "" {
		return nil, fault(400, "重跑必须提供 request_id，重试时保留同一个 ID。")
	}
	if reason == "" || !clean(reason, 2000) {
		return nil, fault(400, "重跑必须写明原因（不超过 2000 字节）。")
	}
	tenant, err := s.mcpTenant(in.TenantID)
	if err != nil {
		return nil, err
	}
	source, err := s.getRun(tenant, in.RunID)
	if err != nil {
		return nil, err
	}
	if err := id.requireTask(source.TaskID); err != nil {
		return nil, err
	}
	switch {
	case source.ParentRunID != "":
		return nil, fault(409, "子图运行随父运行执行，请重跑父运行。")
	case !terminal(source.State):
		return nil, fault(409, "运行尚未结束；先等它结束或用 automation_stop 停止。")
	case len(source.Parameters) == 0:
		return nil, fault(409, "这条运行没有记录启动参数，不能按原参数重跑。")
	}
	root := source.RerunOf
	if root == "" {
		root = source.ID
	}
	run, err := s.createRunAdmitted(tenant, id.Actor, CreateRequest{RequestID: in.RequestID, TenantID: tenant.ID,
		MachineName: source.MachineName, TaskID: source.TaskID, Parameters: source.Parameters,
		RerunOf: root, RerunReason: reason}, id.admit(root))
	if err != nil {
		return nil, err
	}
	return mcpRunSummary(run), nil
}

func mcpRunSummary(run RemoteRun) map[string]any {
	return map[string]any{"run_id": run.ID, "tenant_id": run.TenantID, "machine_name": run.MachineName,
		"task_id": run.TaskID, "task_name": run.TaskName, "request_id": run.RequestID, "created_by": run.CreatedBy,
		"state": run.State, "detail": run.Detail, "deployment_id": run.DeploymentID, "cancel_requested": run.CancelRequested, "logs_complete": run.LogsComplete,
		"created_at": run.CreatedAt, "updated_at": run.UpdatedAt, "revision": run.Revision, "parent_run_id": run.ParentRunID,
		"rerun_of": run.RerunOf, "source": run.Source}
}

func (s *service) mcpRuns(_ context.Context, id mcpIdentity, in mcpRunListInput) (any, error) {
	tenants, err := s.mcpTenants(in.TenantID)
	if err != nil {
		return nil, err
	}
	if in.State != "" && !validRunState(in.State) {
		return nil, fault(400, "state 不是有效的原生运行状态。")
	}
	var since time.Time
	if in.UpdatedSince != "" {
		if since, err = time.Parse(time.RFC3339Nano, in.UpdatedSince); err != nil {
			return nil, fault(400, "updated_since 必须是 RFC 3339 时间。")
		}
	}
	runs := []RemoteRun{}
	s.store.mu.Lock()
	for _, tenant := range tenants {
		for _, run := range s.store.states[tenant.ID].Runs {
			if (in.MachineName == "" || strings.EqualFold(in.MachineName, run.MachineName)) &&
				(in.TaskID == "" || in.TaskID == run.TaskID) && (in.State == "" || in.State == run.State) &&
				(in.RequestID == "" || in.RequestID == run.RequestID && run.CreatedBy == id.Actor) &&
				(since.IsZero() || !run.UpdatedAt.Before(since)) {
				runs = append(runs, run)
			}
		}
	}
	s.store.mu.Unlock()
	sort.Slice(runs, func(i, j int) bool {
		if !since.IsZero() {
			// Polling pages forward through changes, oldest first.
			if runs[i].UpdatedAt.Equal(runs[j].UpdatedAt) {
				return runs[i].ID < runs[j].ID
			}
			return runs[i].UpdatedAt.Before(runs[j].UpdatedAt)
		}
		if runs[i].CreatedAt.Equal(runs[j].CreatedAt) {
			return runs[i].ID < runs[j].ID
		}
		return runs[i].CreatedAt.After(runs[j].CreatedAt)
	})
	items := make([]map[string]any, 0, len(runs))
	for _, run := range runs {
		items = append(items, mcpRunSummary(run))
	}
	return mcpPage(items, in.Offset, in.Limit)
}

func (s *service) mcpGet(_ context.Context, in mcpGetInput) (any, error) {
	tenant, err := s.mcpTenant(in.TenantID)
	if err != nil {
		return nil, err
	}
	run, err := s.getRun(tenant, in.RunID)
	if err != nil {
		return nil, err
	}
	result := map[string]any{"run_id": run.ID, "snapshot_available": len(run.Snapshot) > 0, "output_available": len(run.Output) > 0}
	var native map[string]json.RawMessage
	if json.Unmarshal(run.Snapshot, &native) == nil {
		for _, field := range []string{"review", "artifacts"} {
			if value := eventField(native, field); len(value) > 0 {
				result[field] = value
			}
		}
	}
	if !in.IncludeSnapshot {
		run.Snapshot, run.Output, run.Subgraph = nil, nil, nil
	}
	run.RequestHash = ""
	result["run"] = run
	return result, nil
}

func (s *service) mcpLogs(_ context.Context, in mcpLogsInput) (any, error) {
	tenant, err := s.mcpTenant(in.TenantID)
	if err != nil {
		return nil, err
	}
	if in.Limit == 0 {
		in.Limit = 50
	}
	if in.Limit < 1 || in.Limit > 100 {
		return nil, fault(400, "日志 limit 必须介于 1 和 100。")
	}
	page, err := s.runLogPage(tenant, in.RunID, in.After, in.Limit)
	if err != nil {
		return nil, err
	}
	if !in.IncludeRaw {
		for i := range page.Lines {
			page.Lines[i].Raw = ""
		}
	}
	return page, nil
}

// A task-limited caller must not stop or decide reviews for other tasks.
func (s *service) mcpControlled(id mcpIdentity, tenant tenantConfig, runID string) error {
	if len(id.TaskIDs) == 0 {
		return nil
	}
	run, err := s.getRun(tenant, runID)
	if err != nil {
		return err
	}
	return id.requireTask(run.TaskID)
}

func (s *service) mcpStop(_ context.Context, id mcpIdentity, in mcpRunInput) (any, error) {
	tenant, err := s.mcpTenant(in.TenantID)
	if err != nil {
		return nil, err
	}
	if err := s.mcpControlled(id, tenant, in.RunID); err != nil {
		return nil, err
	}
	run, err := s.stopRun(tenant, id.Actor, in.RunID)
	if err != nil {
		return nil, err
	}
	return mcpRunSummary(run), nil
}

func (s *service) mcpReview(_ context.Context, id mcpIdentity, in mcpReviewInput) (any, error) {
	tenant, err := s.mcpTenant(in.TenantID)
	if err != nil {
		return nil, err
	}
	if err := s.mcpControlled(id, tenant, in.RunID); err != nil {
		return nil, err
	}
	run, err := s.reviewRun(tenant, id.Actor, in.RunID, ReviewRequest{Action: in.Action, ReviewID: in.ReviewID, ContentHash: in.ContentHash, Feedback: in.Feedback})
	if err != nil {
		return nil, err
	}
	return map[string]any{"run": mcpRunSummary(run), "control": run.Control}, nil
}
