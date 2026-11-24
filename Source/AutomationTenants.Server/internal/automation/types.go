package automation

import (
	"encoding/json"
	"time"
)

type Pool struct {
	ID   string `json:"id"`
	Name string `json:"name"`
}

type Tenant struct {
	ID            string `json:"id"`
	Name          string `json:"name"`
	ServerMachine string `json:"server_machine,omitempty"`
	Pools         []Pool `json:"pools"`
}

type Task struct {
	ID                string            `json:"id"`
	ConfigurationID   string            `json:"configuration_id,omitempty"`
	DefinitionID      string            `json:"definition_id,omitempty"`
	Name              string            `json:"name"`
	LocalNickname     string            `json:"local_nickname,omitempty"`
	Parameters        map[string]string `json:"parameters"`
	ScriptRevision    string            `json:"script_revision"`
	GraphFingerprint  string            `json:"graph_fingerprint,omitempty"`
	CanRun            bool              `json:"can_run"`
	UnavailableReason string            `json:"unavailable_reason"`
	Triggers          *TaskTriggers     `json:"triggers,omitempty"`
}

type Machine struct {
	TenantID     string    `json:"tenant_id"`
	PoolID       string    `json:"pool_id"`
	MachineName  string    `json:"machine_name"`
	HostVersion  string    `json:"host_version"`
	Capabilities []string  `json:"capabilities"`
	Tasks        []Task    `json:"tasks"`
	LastSeen     time.Time `json:"last_seen"`
}

type RemoteRun struct {
	DeploymentID        string               `json:"deployment_id,omitempty"`
	ID                  string               `json:"id"`
	TenantID            string               `json:"tenant_id"`
	PoolID              string               `json:"pool_id"`
	MachineName         string               `json:"machine_name"`
	TaskID              string               `json:"task_id"`
	ConfigurationID     string               `json:"configuration_id,omitempty"`
	TaskName            string               `json:"task_name"`
	LocalNickname       string               `json:"local_nickname,omitempty"`
	Parameters          map[string]string    `json:"parameters"`
	ScriptRevision      string               `json:"script_revision"`
	Source              string               `json:"source"`
	State               string               `json:"state"`
	CancelRequested     bool                 `json:"cancel_requested"`
	LocalRunID          string               `json:"local_run_id,omitempty"`
	Snapshot            json.RawMessage      `json:"snapshot,omitempty"`
	Revision            int64                `json:"revision"`
	Detail              string               `json:"detail"`
	LogsComplete        bool                 `json:"logs_complete"`
	CreatedAt           time.Time            `json:"created_at"`
	UpdatedAt           time.Time            `json:"updated_at"`
	AcceptedAt          *time.Time           `json:"accepted_at,omitempty"`
	CreatedBy           string               `json:"created_by"`
	ParentRunID         string               `json:"parent_run_id,omitempty"`
	RootRunID           string               `json:"root_run_id,omitempty"`
	ParentLocalRunID    string               `json:"parent_local_run_id,omitempty"`
	ChildRunID          string               `json:"child_run_id,omitempty"`
	ParentNodeID        string               `json:"parent_node_id,omitempty"`
	Subgraph            json.RawMessage      `json:"subgraph,omitempty"`
	Output              json.RawMessage      `json:"output,omitempty"`
	RequestHash         string               `json:"request_hash,omitempty"`
	RequestID           string               `json:"request_id,omitempty"`
	RerunOf             string               `json:"rerun_of,omitempty"`
	RerunReason         string               `json:"rerun_reason,omitempty"`
	CancellationFailure *CancellationFailure `json:"cancellation_failure,omitempty"`
	Control             *RunControl          `json:"control,omitempty"`
	// The team schedule occurrence this run was dispatched for.
	ScheduledFor    *time.Time          `json:"scheduled_for,omitempty"`
	PullFromPool    bool                `json:"pull_from_pool,omitempty"`
	ArchiveSHA256   string              `json:"archive_sha256,omitempty"`
	ReceivedArchive *poolArchiveReceipt `json:"received_archive,omitempty"`
}

// A failure decided by the parent Pregel runtime, not by Go's transport queue.
type CancellationFailure struct {
	Code    string `json:"code"`
	Message string `json:"message"`
}

type Inventory struct {
	Tenants  []Tenant      `json:"tenants"`
	Machines []Machine     `json:"machines"`
	Runs     []RemoteRun   `json:"runs"`
	Tasks    []CatalogTask `json:"tasks"`
	// Keyed by tenant ID; how the console groups and stars tasks, shared by everyone on the tenant.
	Organizations map[string]Organization `json:"organizations"`
	// Keyed by tenant ID; the team's authoritative task schedules.
	Schedules map[string][]TeamSchedule `json:"schedules"`
}

// Console-only arrangement of tasks. It never affects dispatch, so unknown task IDs are kept
// for tasks that are temporarily offline.
type Organization struct {
	Groups      []TaskGroup       `json:"groups"`
	Assignments map[string]string `json:"assignments"`
	Starred     []string          `json:"starred"`
}

type TaskGroup struct {
	ID   string `json:"id"`
	Name string `json:"name"`
}

type CreateRequest struct {
	RequestID   string            `json:"request_id,omitempty"`
	TenantID    string            `json:"tenant_id"`
	MachineName string            `json:"machine_name"`
	TaskID      string            `json:"task_id"`
	Parameters  map[string]string `json:"parameters"`
	// Set only by the MCP rerun path, never decoded from a web request.
	RerunOf     string `json:"-"`
	RerunReason string `json:"-"`
	// Set only by the team scheduler.
	ScheduledFor *time.Time `json:"-"`
}

type PollRequest struct {
	MachineName  string   `json:"machine_name"`
	HostVersion  string   `json:"host_version"`
	Capabilities []string `json:"capabilities"`
	Tasks        []Task   `json:"tasks"`
}

type RunRequest struct {
	PullFromPool                bool                 `json:"pull_from_pool,omitempty"`
	TenantID                    string               `json:"tenant_id,omitempty"`
	ResourceCoordinationEnabled bool                 `json:"resource_coordination_enabled,omitempty"`
	DeploymentCatalogPath       string               `json:"deployment_catalog_path,omitempty"`
	DeploymentSource            *DeploymentSource    `json:"deployment_source,omitempty"`
	ID                          string               `json:"id"`
	TaskID                      string               `json:"task_id"`
	Parameters                  map[string]string    `json:"parameters"`
	CancelRequested             bool                 `json:"cancel_requested"`
	State                       string               `json:"state"`
	Subgraph                    json.RawMessage      `json:"subgraph,omitempty"`
	CancellationFailure         *CancellationFailure `json:"cancellation_failure,omitempty"`
	ScheduledFor                *time.Time           `json:"scheduled_for,omitempty"`
}

type ChildRequest struct {
	MachineName       string               `json:"machine_name"`
	ParentLocalRunID  string               `json:"parent_local_run_id"`
	ParentSnapshot    json.RawMessage      `json:"parent_snapshot,omitempty"`
	TargetMachineName string               `json:"target_machine_name"`
	TaskID            string               `json:"task_id"`
	Parameters        map[string]string    `json:"parameters"`
	Subgraph          json.RawMessage      `json:"subgraph"`
	CancelRequested   bool                 `json:"cancel_requested"`
	Failure           *CancellationFailure `json:"failure,omitempty"`
}

type ChildAccessRequest struct {
	MachineName      string               `json:"machine_name"`
	ParentLocalRunID string               `json:"parent_local_run_id"`
	After            int64                `json:"after"`
	Limit            int                  `json:"limit"`
	ArchiveSHA256    string               `json:"archive_sha256,omitempty"`
	Failure          *CancellationFailure `json:"failure,omitempty"`
}

type PollReply struct {
	Deployments                 []DeploymentRequest `json:"deployments,omitempty"`
	TenantID                    string              `json:"tenant_id"`
	PoolID                      string              `json:"pool_id"`
	ResourceCoordinationEnabled bool                `json:"resource_coordination_enabled"`
	TaskCatalog                 []TaskDefinition    `json:"task_catalog"`
	Requests                    []RunRequest        `json:"requests"`
	Inspections                 []InspectionRequest `json:"inspections"`
	Controls                    []ControlRequest    `json:"controls"`
	ServerSchedules             []ServerSchedule    `json:"server_schedules"`
}

type ReviewRequest struct {
	Action      string `json:"action"`
	ReviewID    string `json:"review_id"`
	ContentHash string `json:"content_hash"`
	Feedback    string `json:"feedback"`
}

// A transport receipt. Native Pregel records remain authoritative for execution state.
type RunControl struct {
	ID          string    `json:"id"`
	Action      string    `json:"action"`
	ReviewID    string    `json:"review_id,omitempty"`
	ContentHash string    `json:"content_hash,omitempty"`
	Feedback    string    `json:"feedback,omitempty"`
	State       string    `json:"state"`
	Detail      string    `json:"detail"`
	CreatedBy   string    `json:"created_by"`
	CreatedAt   time.Time `json:"created_at"`
	UpdatedAt   time.Time `json:"updated_at"`
}

type ControlRequest struct {
	RunControl
	RunID      string `json:"run_id"`
	TaskID     string `json:"task_id"`
	LocalRunID string `json:"local_run_id"`
}

type ControlReport struct {
	MachineName string `json:"machine_name"`
	State       string `json:"state"`
	Detail      string `json:"detail"`
}

type ObservationRequest struct {
	MachineName     string          `json:"machine_name"`
	TaskID          string          `json:"task_id"`
	ConfigurationID string          `json:"configuration_id,omitempty"`
	TaskName        string          `json:"task_name"`
	LocalNickname   string          `json:"local_nickname,omitempty"`
	ScriptRevision  string          `json:"script_revision"`
	Snapshot        json.RawMessage `json:"snapshot"`
}

type ObservationReceipt struct {
	ID string `json:"id"`
}

type InspectionRequest struct {
	ID         string            `json:"id"`
	TaskID     string            `json:"task_id"`
	Parameters map[string]string `json:"parameters"`
}

type InspectionReport struct {
	MachineName string          `json:"machine_name"`
	Result      json.RawMessage `json:"result"`
	Error       string          `json:"error"`
}

type ReportRequest struct {
	MachineName  string          `json:"machine_name"`
	LocalRunID   string          `json:"local_run_id"`
	Snapshot     json.RawMessage `json:"snapshot"`
	State        string          `json:"state"`
	Detail       string          `json:"detail"`
	LogsComplete bool            `json:"logs_complete"`
	Output       json.RawMessage `json:"output,omitempty"`
}

type LogsRequest struct {
	MachineName string   `json:"machine_name"`
	Stream      string   `json:"stream"`
	Offset      int64    `json:"offset"`
	Lines       []string `json:"lines"`
}

type LogsReceipt struct {
	NextOffset int64 `json:"next_offset"`
}

type LogLine struct {
	Sequence int64  `json:"sequence"`
	Stream   string `json:"stream"`
	Offset   int64  `json:"offset"`
	Text     string `json:"text"`
	Raw      string `json:"raw"`
}

type LogsPage struct {
	Lines      []LogLine `json:"lines"`
	NextCursor int64     `json:"next_cursor"`
	HasMore    bool      `json:"has_more"`
}

func terminal(state string) bool {
	return state == "succeeded" || state == "skipped" || state == "failed" || state == "cancelled" || state == "unknown"
}

func validRunState(state string) bool {
	return terminal(state) || state == "queued" || state == "accepted" || state == "running" || state == "awaiting_review"
}
