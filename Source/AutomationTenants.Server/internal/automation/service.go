package automation

import (
	"bytes"
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"maps"
	"slices"
	"sort"
	"strings"
	"time"
)

type service struct {
	config      config
	store       *store
	inspections map[string]*pendingInspection
}

const serverCapability = "pregel.server.v1"
const mutexCapability = "pregel.mutex.v1"

type apiFault struct {
	status  int
	message string
}

func (e *apiFault) Error() string            { return e.message }
func fault(status int, message string) error { return &apiFault{status, message} }

func validateParameters(parameters map[string]string) error {
	if len(parameters) > 512 {
		return fault(400, "parameters 过多。")
	}
	for key, value := range parameters {
		if key == "" || !clean(key, 128) || len(value) > 256*1024 {
			return fault(400, "parameters 格式无效。")
		}
	}
	return nil
}

func parameters(values map[string]string) map[string]string {
	if values == nil {
		return map[string]string{}
	}
	return maps.Clone(values)
}

func (s *service) inventory(actor, selected string, team bool) (Inventory, error) {
	visible, err := s.config.webTenants(actor, selected, team)
	if err != nil {
		return Inventory{}, err
	}
	s.store.mu.Lock()
	defer s.store.mu.Unlock()
	result := Inventory{Tenants: []Tenant{}, Machines: []Machine{}, Runs: []RemoteRun{}, Tasks: []CatalogTask{}, Organizations: map[string]Organization{}, Schedules: map[string][]TeamSchedule{}}
	for _, tenant := range visible {
		view := tenant.view()
		if team && !tenant.TeamExecution.allowsMachine(view.ServerMachine) {
			view.ServerMachine = ""
		}
		result.Tenants = append(result.Tenants, view)
		allowedTasks := map[string]bool{}
		for _, task := range tenant.registeredTasks() {
			if team && !slices.Contains(tenant.TeamExecution.TaskIDs, task.ID) {
				continue
			}
			allowedTasks[task.ID] = true
			result.Tasks = append(result.Tasks, CatalogTask{TenantID: tenant.ID, ID: task.ID, Name: task.Name})
		}
		organization := organizationView(s.store.states[tenant.ID].Organization)
		if team {
			visible := organizationView(nil)
			for task, group := range organization.Assignments {
				if allowedTasks[task] {
					visible.Assignments[task] = group
				}
			}
			for _, group := range organization.Groups {
				for _, id := range visible.Assignments {
					if id == group.ID {
						visible.Groups = append(visible.Groups, group)
						break
					}
				}
			}
			organization = visible
		}
		result.Organizations[tenant.ID] = organization
		if !team {
			result.Schedules[tenant.ID] = scheduleViews(s.store.states[tenant.ID], time.Now())
		}
		for _, machine := range s.store.states[tenant.ID].Machines {
			if assigned, err := s.assignMachine(machine.MachineName, tenant.ID); err != nil || assigned.PoolID != machine.PoolID {
				continue
			}
			if team && !tenant.TeamExecution.allowsMachine(machine.MachineName) {
				continue
			}
			machine = catalogMachine(machine)
			if team {
				machine.Tasks = slices.DeleteFunc(machine.Tasks, func(task Task) bool {
					return !allowedTasks[task.ID]
				})
			}
			result.Machines = append(result.Machines, machine)
		}
		for _, run := range s.store.states[tenant.ID].Runs {
			if team && !strings.EqualFold(run.CreatedBy, actor) {
				continue
			}
			result.Runs = append(result.Runs, run)
		}
	}
	sort.Slice(result.Machines, func(i, j int) bool { return result.Machines[i].MachineName < result.Machines[j].MachineName })
	sort.Slice(result.Runs, func(i, j int) bool { return result.Runs[i].CreatedAt.After(result.Runs[j].CreatedAt) })
	return result, nil
}

func organizationView(value *Organization) Organization {
	if value == nil {
		return Organization{Groups: []TaskGroup{}, Assignments: map[string]string{}, Starred: []string{}}
	}
	return *value
}

func (s *service) organize(actor, selected string, request Organization) (Organization, error) {
	tenant, err := s.config.tenant(actor, selected)
	if err != nil {
		return Organization{}, err
	}
	next := Organization{Groups: []TaskGroup{}, Assignments: map[string]string{}, Starred: []string{}}
	if len(request.Groups) > 200 || len(request.Assignments) > 5000 || len(request.Starred) > 5000 {
		return Organization{}, fault(400, "分组或收藏过多。")
	}
	groups := map[string]bool{}
	for _, group := range request.Groups {
		group.Name = strings.TrimSpace(group.Name)
		if group.ID == "" || !clean(group.ID, 64) || group.Name == "" || len([]rune(group.Name)) > 40 || groups[group.ID] {
			return Organization{}, fault(400, "分组名称无效或重复。")
		}
		groups[group.ID] = true
		next.Groups = append(next.Groups, group)
	}
	for task, group := range request.Assignments {
		if task == "" || !clean(task, 256) || !groups[group] {
			return Organization{}, fault(400, "任务分组无效。")
		}
		next.Assignments[task] = group
	}
	for _, task := range request.Starred {
		if task == "" || !clean(task, 256) {
			return Organization{}, fault(400, "收藏任务无效。")
		}
		if !slices.Contains(next.Starred, task) {
			next.Starred = append(next.Starred, task)
		}
	}
	s.store.mu.Lock()
	defer s.store.mu.Unlock()
	err = s.store.change(tenant.ID, func(state *tenantState) error {
		state.Organization = &next
		return nil
	})
	return next, err
}

func (s *service) create(actor string, request CreateRequest, team bool) (RemoteRun, error) {
	tenant, err := s.config.webExecution(actor, request, team)
	if err != nil {
		return RemoteRun{}, err
	}
	return s.createRun(tenant, actor, request)
}

// Callers resolve their entry policy before entering the shared dispatch path.
func (s *service) createRun(tenant tenantConfig, actor string, request CreateRequest) (RemoteRun, error) {
	return s.createRunAdmitted(tenant, actor, request, nil)
}

// admit runs under the store lock after the target is known runnable, so a
// caller budget is checked and consumed atomically with the new run.
func (s *service) createRunAdmitted(tenant tenantConfig, actor string, request CreateRequest,
	admit func(state *tenantState, now time.Time) error) (RemoteRun, error) {
	name, err := tenant.executionMachine(request.MachineName)
	if err != nil {
		return RemoteRun{}, err
	}
	if err := validateParameters(request.Parameters); err != nil {
		return RemoteRun{}, err
	}
	if request.TaskID == "" || !clean(request.TaskID, 128) {
		return RemoteRun{}, fault(400, "task_id 无效。")
	}
	if !clean(request.RequestID, 128) || strings.TrimSpace(request.RequestID) != request.RequestID {
		return RemoteRun{}, fault(400, "request_id 无效。")
	}
	requestHash := ""
	if request.RequestID != "" {
		// Hash the caller's overrides, not changing machine defaults. A retry must
		// return the original persisted run even after its defaults or state change.
		data, err := json.Marshal(CreateRequest{TenantID: tenant.ID, MachineName: strings.ToLower(name),
			TaskID: request.TaskID, Parameters: parameters(request.Parameters)})
		if err != nil {
			return RemoteRun{}, err
		}
		if request.RerunOf != "" {
			// The same request_id must not rerun a different lineage.
			data = append(data, "\x00rerun_of:"+request.RerunOf...)
		}
		hash := sha256.Sum256(data)
		requestHash = hex.EncodeToString(hash[:])
	}
	source, err := s.missingTaskSource(tenant, actor, request, name)
	if err != nil {
		return RemoteRun{}, err
	}
	s.store.mu.Lock()
	defer s.store.mu.Unlock()
	if request.RequestID != "" {
		for _, run := range s.store.states[tenant.ID].Runs {
			if run.RequestID == request.RequestID && run.CreatedBy == actor {
				if run.RequestHash != requestHash {
					return RemoteRun{}, fault(409, "request_id 已用于不同的启动请求；重试必须保留原参数，新执行请使用新 request_id。")
				}
				return run, nil
			}
		}
	}
	var created RemoteRun
	err = s.store.change(tenant.ID, func(state *tenantState) error {
		machine, ok := state.Machines[strings.ToLower(name)]
		if !ok {
			return fault(404, "机器尚未上报可运行任务。")
		}
		if strings.EqualFold(name, tenant.ServerMachine) && !slices.Contains(machine.Capabilities, serverCapability) {
			return fault(409, "配置的服务器尚未上报 Pregel 父图宿主能力，请启动服务器执行进程。")
		}
		if tenant.ResourceServerURL != "" && !slices.Contains(machine.Capabilities, mutexCapability) {
			return fault(409, "当前租户使用统一 Pregel 资源协调；目标机器尚不支持，请先升级 Agent。")
		}
		assignment, err := s.assignMachine(machine.MachineName, tenant.ID)
		if err != nil || assignment.PoolID != machine.PoolID {
			return fault(409, "机器的租户归属已变更，请刷新目录。")
		}
		task := executionTask(machine, request.TaskID, false)
		if task == nil {
			if source == nil {
				return fault(404, "机器未上报这个任务，请先下发流程部署。")
			}
			definition, registered := tenant.taskDefinition(request.TaskID, "")
			if !registered {
				return fault(404, "流程已不在云端登记目录中。")
			}
			task = &Task{ID: request.TaskID, ConfigurationID: request.TaskID, Name: definition.Name,
				Parameters: map[string]string{}, CanRun: true, ScriptRevision: source.Revision + ":shared:" + source.SharedRevision}
		}
		if !task.CanRun {
			return fault(409, "任务暂不可运行："+task.UnavailableReason)
		}
		now := time.Now().UTC()
		if admit != nil {
			if err := admit(state, now); err != nil {
				return err
			}
		}
		id := make([]byte, 16)
		if _, err := rand.Read(id); err != nil {
			return err
		}
		values := parameters(task.Parameters)
		maps.Copy(values, request.Parameters)
		created = RemoteRun{ID: hex.EncodeToString(id), TenantID: tenant.ID, PoolID: assignment.PoolID,
			RequestID: request.RequestID, RequestHash: requestHash,
			MachineName: machine.MachineName, TaskID: task.ID, TaskName: task.Name, Parameters: values,
			ConfigurationID: taskConfiguration(*task),
			ScriptRevision:  task.ScriptRevision, Source: "cloud", State: "queued", Revision: 1, Detail: "等待机器接单", CreatedAt: now,
			UpdatedAt: now, CreatedBy: actor, RerunOf: request.RerunOf, RerunReason: request.RerunReason, ScheduledFor: request.ScheduledFor}
		if source != nil {
			job, err := s.putDeployment(state, tenant, actor, DeployRequest{TenantID: tenant.ID, MachineName: name,
				TaskID: request.TaskID, RequestID: "run-" + created.ID}, *source)
			if err != nil {
				return err
			}
			created.DeploymentID, created.Detail = job.ID, "等待目标 Agent 部署流程；可按 deployment_id 查询准备进度"
		}
		state.Runs[created.ID] = created
		return nil
	})
	return created, err
}

func requireRun(state *tenantState, id string) (RemoteRun, error) {
	if !runID.MatchString(id) {
		return RemoteRun{}, fault(400, "运行 ID 无效。")
	}
	run, exists := state.Runs[id]
	if !exists {
		return RemoteRun{}, fault(404, "找不到运行记录。")
	}
	return run, nil
}

func (s *service) get(actor, selected, id string, team bool) (RemoteRun, error) {
	tenant, err := s.config.webTenant(actor, selected, team)
	if err != nil {
		return RemoteRun{}, err
	}
	run, err := s.getRun(tenant, id)
	if err == nil && team && !strings.EqualFold(run.CreatedBy, actor) {
		return RemoteRun{}, fault(404, "找不到运行记录。")
	}
	return run, err
}

func (s *service) getRun(tenant tenantConfig, id string) (RemoteRun, error) {
	s.store.mu.Lock()
	defer s.store.mu.Unlock()
	return requireRun(s.store.states[tenant.ID], id)
}

func (s *service) stop(actor, selected, id string) (RemoteRun, error) {
	tenant, err := s.config.tenant(actor, selected)
	if err != nil {
		return RemoteRun{}, err
	}
	return s.stopRun(tenant, actor, id)
}

func (s *service) stopRun(tenant tenantConfig, actor, id string) (RemoteRun, error) {
	s.store.mu.Lock()
	defer s.store.mu.Unlock()
	var result RemoteRun
	err := s.store.change(tenant.ID, func(state *tenantState) error {
		run, err := requireRun(state, id)
		if err != nil {
			return err
		}
		if terminal(run.State) || run.CancelRequested {
			result = run
			return nil
		}
		machine := state.Machines[strings.ToLower(run.MachineName)]
		if run.Source == "local" || run.State == "awaiting_review" && (run.ParentRunID == "" || slices.Contains(machine.Capabilities, controlCapability)) {
			request := ReviewRequest{Action: "stop"}
			if run.State == "awaiting_review" {
				header, present, err := snapshotInfo(run.Snapshot)
				if err != nil || !present || header.Review == nil {
					return fault(409, "缺少本次审核内容，请等待机器上报后重试。")
				}
				request = ReviewRequest{Action: "abandon", ReviewID: header.Review.ID, ContentHash: header.Review.ContentHash}
			}
			if _, err = queueControl(state, run, actor, request); err != nil {
				return err
			}
		}
		cancelTree(state, id, true)
		result = state.Runs[id]
		return nil
	})
	return result, err
}

func validatePoll(request *PollRequest) error {
	if !clean(request.HostVersion, 128) || len(request.Capabilities) > 64 || len(request.Tasks) > 2000 {
		return fault(400, "机器任务目录格式无效。")
	}
	for _, capability := range request.Capabilities {
		if !clean(capability, 128) {
			return fault(400, "capabilities 格式无效。")
		}
	}
	seen := map[string]bool{}
	for index, task := range request.Tasks {
		configuration := taskConfiguration(task)
		if task.ID == "" || !clean(task.ID, 128) || !clean(configuration, 128) || seen[configuration] || !clean(task.Name, 512) || !clean(task.LocalNickname, 512) || !clean(task.ScriptRevision, 1024) || !clean(task.GraphFingerprint, 256) || !clean(task.UnavailableReason, 2000) {
			return fault(400, "tasks 格式无效或任务 ID 重复。")
		}
		seen[configuration] = true
		if task.Triggers != nil && (len(task.Triggers.Schedules) > 20 || !clean(task.Triggers.ReadOnlyReason, 2000)) {
			return fault(400, "任务触发器格式无效。")
		}
		if err := validateParameters(task.Parameters); err != nil {
			return err
		}
		if task.Name == "" {
			task.Name = task.ID
		}
		task.Parameters = parameters(task.Parameters)
		request.Tasks[index] = task
	}
	if request.Capabilities == nil {
		request.Capabilities = []string{}
	}
	if request.Tasks == nil {
		request.Tasks = []Task{}
	}
	return nil
}

func (s *service) poll(request PollRequest) (PollReply, error) {
	name, err := machineName(request.MachineName)
	if err != nil {
		return PollReply{}, err
	}
	if err := validatePoll(&request); err != nil {
		return PollReply{}, err
	}
	s.store.mu.Lock()
	defer s.store.mu.Unlock()
	assignments := s.machineAssignments(name)
	if len(assignments) == 0 {
		return PollReply{}, fault(403, "机器未配置租户归属。")
	}
	var merged PollReply
	for index, assigned := range assignments {
		reply, err := s.pollAssigned(name, assigned, request)
		if err != nil {
			return PollReply{}, err
		}
		if index == 0 {
			merged = reply
			continue
		}
		merged.TaskCatalog = append(merged.TaskCatalog, reply.TaskCatalog...)
		merged.Requests = append(merged.Requests, reply.Requests...)
		merged.Controls = append(merged.Controls, reply.Controls...)
		merged.Inspections = append(merged.Inspections, reply.Inspections...)
		merged.ServerSchedules = append(merged.ServerSchedules, reply.ServerSchedules...)
		merged.Deployments = append(merged.Deployments, reply.Deployments...)
		merged.ResourceCoordinationEnabled = merged.ResourceCoordinationEnabled || reply.ResourceCoordinationEnabled
	}
	return merged, nil
}

// The caller holds store.mu. Each tenant only sees its own registered tasks and runs.
func (s *service) pollAssigned(name string, assignment assignment, request PollRequest) (PollReply, error) {
	tenant := s.config.tenantByID[assignment.TenantID]
	if strings.EqualFold(name, tenant.ServerMachine) && !slices.Contains(request.Capabilities, serverCapability) {
		return PollReply{}, fault(409, "配置的服务器必须以 Pregel 父图宿主模式接入。")
	}
	var pool Pool
	for _, candidate := range tenant.Pools {
		if candidate.ID == assignment.PoolID {
			pool = candidate.Pool
		}
	}
	reply := PollReply{TenantID: assignment.TenantID, PoolID: assignment.PoolID,
		TaskCatalog:                 tenant.registeredTasks(),
		ResourceCoordinationEnabled: tenant.ResourceServerURL != "", Requests: []RunRequest{}, Inspections: []InspectionRequest{}, Controls: []ControlRequest{}, ServerSchedules: []ServerSchedule{}}
	now := time.Now().UTC()
	request.Tasks = tenant.normalizeTasks(request.Tasks)
	machine := Machine{tenant.ID, pool.ID, name, request.HostVersion, request.Capabilities, request.Tasks, now}
	apply := func(state *tenantState) error {
		state.Machines[strings.ToLower(name)] = machine
		adoptSchedules(state, machine, now)
		reply.ServerSchedules = serverSchedules(tenant, state, machine, request.Capabilities)
		runs := slices.Collect(maps.Values(state.Runs))
		sort.Slice(runs, func(i, j int) bool {
			if runs[i].CreatedAt.Equal(runs[j].CreatedAt) {
				return runs[i].ID < runs[j].ID
			}
			return runs[i].CreatedAt.Before(runs[j].CreatedAt)
		})
		for _, run := range runs {
			if !strings.EqualFold(run.MachineName, name) || run.PoolID != pool.ID {
				continue
			}
			if run.Control != nil && run.Control.State == "pending" && slices.Contains(request.Capabilities, controlCapability) {
				reply.Controls = append(reply.Controls, ControlRequest{RunControl: *run.Control,
					RunID: run.ID, TaskID: runConfiguration(run), LocalRunID: run.LocalRunID})
			}
			if run.Source == "local" {
				continue
			}
			if run.AcceptedAt == nil && !terminal(run.State) {
				if run.DeploymentID != "" && !slices.Contains(request.Capabilities, deploymentCapability) {
					continue
				}
				if !advanceDeployment(state, &run, now) {
					continue
				}
				if tenant.ResourceServerURL != "" && !slices.Contains(request.Capabilities, mutexCapability) {
					continue
				}
				canRun := run.DeploymentID != "" && agentCatalogPath(tenant, request.Capabilities) != ""
				for _, task := range request.Tasks {
					if task.ID == run.TaskID && taskConfiguration(task) == runConfiguration(run) {
						canRun = canRun || task.CanRun
						break
					}
				}
				if !canRun || len(run.Subgraph) > 0 && !slices.Contains(request.Capabilities, subgraphCapability) {
					continue
				}
				if _, exists := state.Runs[rootRunID(run)]; !exists {
					return errors.New("子图根运行记录不存在")
				}
				// Resource admission belongs to Pregel Mutex. Go only delivers the
				// immutable invocation to its selected execution host.
				run.State = "accepted"
				run.AcceptedAt = &now
				run.UpdatedAt = now
				run.Revision++
				run.Detail = "机器已接单，等待本地执行记录"
				state.Runs[run.ID] = run
			}
			if run.AcceptedAt != nil && (!terminal(run.State) || !run.LogsComplete) {
				var source *DeploymentSource
				var catalogPath string
				if job, exists := state.Deployments[run.DeploymentID]; exists {
					pinned := job.Source
					source = &pinned
					catalogPath = agentCatalogPath(tenant, request.Capabilities)
				}
				reply.Requests = append(reply.Requests, RunRequest{ID: run.ID, TaskID: runConfiguration(run), Parameters: run.Parameters,
					TenantID: tenant.ID, DeploymentCatalogPath: catalogPath, ResourceCoordinationEnabled: tenant.ResourceServerURL != "",
					DeploymentSource: source, CancelRequested: run.CancelRequested, State: run.State, Subgraph: run.Subgraph, CancellationFailure: run.CancellationFailure, ScheduledFor: run.ScheduledFor})
			}
		}
		return nil
	}
	// Most polls only prove the machine is still there. Those update memory alone: rewriting the
	// whole tenant state for every machine every two seconds is what saturates the server.
	seen := tenant.ID + "/" + strings.ToLower(name)
	var err error
	if heartbeatOnly(s.store.states[tenant.ID], machine) && now.Sub(s.store.seenOnDisk[seen]) <= heartbeatPersist {
		err = apply(s.store.states[tenant.ID])
	} else if err = s.store.change(tenant.ID, apply); err == nil {
		s.store.seenOnDisk[seen] = now
	}
	if err == nil && slices.Contains(request.Capabilities, "pregel.inspect.v1") {
		for _, inspection := range s.inspections {
			if inspection.assignment == assignment && strings.EqualFold(inspection.machineName, name) {
				reply.Inspections = append(reply.Inspections, inspection.request)
			}
		}
		sort.Slice(reply.Inspections, func(i, j int) bool { return reply.Inspections[i].ID < reply.Inspections[j].ID })
	}
	if err == nil {
		reply.Deployments = deploymentMailbox(s.store.states[tenant.ID], tenant, machine)
	}
	return reply, err
}

// A restart must still find machines recently seen, so the last-seen time reaches disk this often.
const heartbeatPersist = 30 * time.Second

// heartbeatOnly reports whether this poll leaves everything but the machine's last-seen time as it was:
// the same report as last time, no schedule to adopt and no run waiting for this machine to accept.
func heartbeatOnly(state *tenantState, machine Machine) bool {
	known, exists := state.Machines[strings.ToLower(machine.MachineName)]
	if !exists {
		return false
	}
	known.LastSeen = machine.LastSeen
	before, err := json.Marshal(known)
	if err != nil {
		return false
	}
	after, err := json.Marshal(machine)
	if err != nil || !bytes.Equal(before, after) {
		return false
	}
	probe := tenantState{Schedules: maps.Clone(state.Schedules)}
	adoptSchedules(&probe, machine, machine.LastSeen)
	if len(probe.Schedules) != len(state.Schedules) {
		return false
	}
	for _, run := range state.Runs {
		if run.AcceptedAt == nil && run.Source != "local" && !terminal(run.State) && strings.EqualFold(run.MachineName, machine.MachineName) {
			return false
		}
	}
	return true
}

type snapshotHeader struct {
	RunID           string    `json:"run_id"`
	TaskID          string    `json:"task_id"`
	Status          string    `json:"status"`
	Revision        int64     `json:"revision"`
	Summary         string    `json:"summary"`
	RemoteRequestID string    `json:"remote_request_id"`
	QueuedAt        time.Time `json:"queued_at"`
	Review          *struct {
		ID          string `json:"id"`
		ContentHash string `json:"content_hash"`
		CanRevise   *bool  `json:"can_revise"`
	} `json:"review"`
	TaskSnapshot *struct {
		Parameters map[string]string `json:"parameters"`
		TemplateID string            `json:"template_id"`
	} `json:"task_snapshot"`
	Launch *struct {
		Parameters map[string]string `json:"parameters"`
	} `json:"launch"`
}

func snapshotInfo(raw json.RawMessage) (snapshotHeader, bool, error) {
	if len(raw) == 0 || bytes.Equal(bytes.TrimSpace(raw), []byte("null")) {
		return snapshotHeader{}, false, nil
	}
	var header snapshotHeader
	if !bytes.HasPrefix(bytes.TrimSpace(raw), []byte("{")) || json.Unmarshal(raw, &header) != nil || !validRunState(header.Status) || header.Revision < 0 {
		return header, false, fault(400, "snapshot 必须是现有 AutomationRunRecord 对象。")
	}
	return header, true, nil
}

func (s *service) agentRun(name, id string) (assignment, error) {
	name, err := machineName(name)
	if err != nil {
		return assignment{}, err
	}
	if !runID.MatchString(id) {
		return assignment{}, fault(400, "运行 ID 无效。")
	}
	assignments := s.machineAssignments(name)
	if len(assignments) == 0 {
		return assignment{}, fault(403, "机器未配置租户归属。")
	}
	for _, assigned := range assignments {
		if run, ok := s.store.states[assigned.TenantID].Runs[id]; ok {
			if !strings.EqualFold(run.MachineName, name) || run.PoolID != assigned.PoolID {
				return assignment{}, fault(403, "运行不属于这台机器的租户与机器池。")
			}
			return assigned, nil
		}
	}
	return assignment{}, fault(404, "找不到运行。")
}

func (s *service) report(id string, request ReportRequest) (RemoteRun, error) {
	if !clean(request.LocalRunID, 128) || len(request.Detail) > 8000 {
		return RemoteRun{}, fault(400, "执行回执格式无效。")
	}
	incoming, hasSnapshot, err := snapshotInfo(request.Snapshot)
	if err != nil {
		return RemoteRun{}, err
	}
	if !hasSnapshot && !validRunState(request.State) {
		return RemoteRun{}, fault(400, "需要 snapshot 或有效 state。")
	}
	var output json.RawMessage
	if len(request.Output) > 0 && !bytes.Equal(bytes.TrimSpace(request.Output), []byte("null")) {
		output, err = canonicalObject(request.Output, "output")
		if err != nil {
			return RemoteRun{}, err
		}
	}
	s.store.mu.Lock()
	defer s.store.mu.Unlock()
	if s.ignoredRun(request.MachineName, id) {
		return RemoteRun{ID: id, MachineName: request.MachineName, State: request.State}, nil
	}
	assignment, err := s.agentRun(request.MachineName, id)
	if err != nil {
		return RemoteRun{}, err
	}
	var result RemoteRun
	err = s.store.change(assignment.TenantID, func(state *tenantState) error {
		run := state.Runs[id]
		if run.Source != "local" && run.AcceptedAt == nil {
			return fault(409, "运行尚未由机器接单。")
		}
		localID := request.LocalRunID
		if hasSnapshot {
			if localID == "" {
				localID = incoming.RunID
			}
			if localID == "" || incoming.RunID != localID || incoming.TaskID != runConfiguration(run) {
				return fault(409, "本地运行记录与已下发任务不一致。")
			}
		}
		if localID != "" {
			if run.LocalRunID != "" && run.LocalRunID != localID {
				return fault(409, "这个请求已关联另一条本地运行记录。")
			}
			run.LocalRunID = localID
		}
		previous, previousSnapshot, err := snapshotInfo(run.Snapshot)
		if err != nil {
			return errors.New("持久运行快照格式无效")
		}
		nextState := request.State
		if hasSnapshot {
			nextState = incoming.Status
		}
		accept := !terminal(run.State) || nextState == run.State
		if previousSnapshot && hasSnapshot && incoming.Revision < previous.Revision {
			accept = false
		}
		// A startup-only acknowledgement cannot erase a native record or rewind it.
		if !hasSnapshot && previousSnapshot && nextState == "accepted" {
			accept = false
		}
		if accept {
			if len(run.Subgraph) > 0 && nextState == "succeeded" && len(output) == 0 && len(run.Output) == 0 {
				return fault(409, "子图成功结果缺少编码输出，必须与终态快照一同上报。")
			}
			if len(output) > 0 {
				if len(run.Subgraph) == 0 {
					return fault(400, "只有子图调用可以上报编码输出。")
				}
				if len(run.Output) > 0 && !bytes.Equal(run.Output, output) {
					return fault(409, "子图调用已保存不同的编码输出。")
				}
				run.Output = output
			}
			run.State = nextState
			if hasSnapshot {
				run.Snapshot = bytes.Clone(request.Snapshot)
				if incoming.Launch != nil {
					run.Parameters = parameters(incoming.Launch.Parameters)
				} else if incoming.TaskSnapshot != nil {
					run.Parameters = parameters(incoming.TaskSnapshot.Parameters)
				}
				run.Detail = incoming.Summary
				if request.Detail != "" {
					run.Detail = request.Detail
				}
			} else if request.Detail != "" {
				run.Detail = request.Detail
			}
		}
		if request.LogsComplete && terminal(run.State) {
			run.LogsComplete = true
		}
		old := state.Runs[id]
		left, _ := json.Marshal(old)
		right, _ := json.Marshal(run)
		if !bytes.Equal(left, right) {
			run.Revision++
			run.UpdatedAt = time.Now().UTC()
			state.Runs[id] = run
		}
		if terminal(run.State) {
			cancelTree(state, run.ID, false)
		}
		result = run
		return nil
	})
	return result, err
}

func (s *service) requireAdminRun(actor, selected, id string) (tenantConfig, error) {
	tenant, err := s.config.tenant(actor, selected)
	if err != nil {
		return tenant, err
	}
	if _, err := requireRun(s.store.states[tenant.ID], id); err != nil {
		return tenant, err
	}
	return tenant, nil
}
