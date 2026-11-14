package automation

import (
	"encoding/json"
	"os"
	"sort"
	"strings"
	"sync"
	"time"
)

type publishedWorkflow struct {
	TenantID     string `json:"tenant_id"`
	TaskID       string `json:"task_id"`
	Title        string `json:"title"`
	DefinitionID string `json:"definition_id"`
}
type workflowCatalogCache struct {
	modified time.Time
	size     int64
	entries  map[string]publishedWorkflow
}

var workflowCatalogs = struct {
	sync.Mutex
	files map[string]workflowCatalogCache
}{files: map[string]workflowCatalogCache{}}

// The C# source publisher is the sole writer. Only its approved publication map registers workflows.
func readWorkflowCatalog(path string) (map[string]publishedWorkflow, error) {
	if path == "" {
		return nil, nil
	}
	workflowCatalogs.Lock()
	defer workflowCatalogs.Unlock()
	info, err := os.Stat(path)
	if os.IsNotExist(err) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	previous := workflowCatalogs.files[path]
	if info.ModTime() == previous.modified && info.Size() == previous.size {
		return previous.entries, nil
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	var ledger struct {
		Schema    int                          `json:"schema"`
		Published map[string]publishedWorkflow `json:"published"`
	}
	if err = json.Unmarshal(data, &ledger); err != nil {
		return nil, err
	}
	if ledger.Schema != 1 {
		return nil, fault(500, "流程发布状态版本无效。")
	}
	workflowCatalogs.files[path] = workflowCatalogCache{info.ModTime(), info.Size(), ledger.Published}
	return ledger.Published, nil
}
func (t tenantConfig) registeredTasks() []TaskDefinition {
	entries, err := readWorkflowCatalog(t.ApprovedWorkflowsPath)
	if err != nil { // Fail closed for new publications; never reinterpret an unreviewed submission as a task.
		workflowCatalogs.Lock()
		entries = workflowCatalogs.files[t.ApprovedWorkflowsPath].entries
		workflowCatalogs.Unlock()
	}
	tasks := append([]TaskDefinition{}, t.Tasks...)
	keys := make([]string, 0, len(entries))
	for key := range entries {
		keys = append(keys, key)
	}
	sort.Strings(keys)
	for _, key := range keys {
		entry := entries[key]
		if entry.TenantID != t.ID || entry.TaskID == "" || entry.Title == "" {
			continue
		}
		found := false
		for i := range tasks {
			if tasks[i].ID == entry.TaskID {
				found = true
				break
			}
		}
		if !found {
			tasks = append(tasks, TaskDefinition{ID: entry.TaskID, Name: entry.Title})
		}
	}
	return tasks
}

type workflowIdentityRequest struct {
	TenantID     string `json:"tenant_id"`
	MachineName  string `json:"machine_name"`
	TaskID       string `json:"task_id"`
	MachineUser  bool   `json:"machine_user"`
	RequireAdmin bool   `json:"require_admin"`
}

func (s *service) workflowIdentity(actor string, request workflowIdentityRequest) (any, error) {
	name, err := machineName(request.MachineName)
	if err != nil && !request.RequireAdmin {
		return nil, err
	}
	tenant, ok := s.config.tenantByID[request.TenantID]
	if !ok {
		return nil, fault(400, "租户不存在。")
	}
	if request.RequireAdmin {
		if !s.config.administers(actor, request.TenantID) {
			return nil, fault(403, "只有该租户的管理员可以审核 Shared 变更。")
		}
	}
	kind, user := "human", strings.TrimSpace(actor)
	if request.MachineUser {
		assigned, assignErr := s.config.assign(name)
		if assignErr != nil || assigned.TenantID != tenant.ID {
			return nil, fault(403, "机器不属于目标租户。")
		}
		s.store.mu.Lock()
		_, known := s.store.states[tenant.ID].Machines[strings.ToLower(name)]
		s.store.mu.Unlock()
		if !known {
			return nil, fault(403, "构建机虚拟用户只允许已登记机器提交。")
		}
		kind, user = "build_machine", "machine:"+strings.ToLower(name)
	} else if user == "" {
		return nil, fault(401, "请登录后上传流程。")
	}
	definition, registered := tenant.taskDefinition(request.TaskID, "")
	targets := []CatalogTask{}
	for _, task := range tenant.registeredTasks() {
		targets = append(targets, CatalogTask{TenantID: tenant.ID, ID: task.ID, Name: task.Name})
	}
	return map[string]any{"tenant_id": tenant.ID, "machine_name": name, "user_id": user, "user_kind": kind, "task_registered": registered, "task_name": definition.Name, "tasks": targets}, nil
}

// Only the C# review route calls this with the immutable scope of a stored submission.
func (s *service) workflowValidation(tenant, machine, task, id, uploader string) (RemoteRun, error) {
	s.store.mu.Lock()
	defer s.store.mu.Unlock()
	if state := s.store.states[tenant]; state != nil && id != "" {
		for _, run := range state.Runs {
			if (run.ID == id || run.LocalRunID == id) && run.TaskID == task && run.ParentRunID == "" &&
				strings.EqualFold(run.MachineName, machine) && (run.Source == "local" || strings.EqualFold(run.CreatedBy, uploader)) {
				return run, nil
			}
		}
	}
	return RemoteRun{}, fault(404, "找不到与本次提交对应的运行记录。")
}
