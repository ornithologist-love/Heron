package automation

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"io"
	"net/http"
	"net/url"
	"slices"
	"sort"
	"strings"
	"time"
)

const deploymentCapability = "pregel.deploy.v1"
const tenantSourceCapability = "pregel.tenant-source.v1"

// Deployment describes source preparation, never a second graph execution state machine.
type DeploymentSource struct {
	SourceID       string `json:"source_id"`
	Revision       string `json:"revision"`
	Sha256         string `json:"sha256"`
	SharedRevision string `json:"shared_revision"`
	SharedSha256   string `json:"shared_sha256"`
}

type DeploymentRequest struct {
	ID            string           `json:"id"`
	TaskID        string           `json:"task_id"`
	Source        DeploymentSource `json:"source"`
	CatalogPath   string           `json:"catalog_path,omitempty"`
	RegisterGraph bool             `json:"register_graph,omitempty"`
}

type Deployment struct {
	DeploymentRequest
	TenantID    string    `json:"tenant_id"`
	PoolID      string    `json:"pool_id"`
	MachineName string    `json:"machine_name"`
	RequestID   string    `json:"request_id"`
	CreatedBy   string    `json:"created_by"`
	State       string    `json:"state"`
	Detail      string    `json:"detail"`
	CreatedAt   time.Time `json:"created_at"`
	UpdatedAt   time.Time `json:"updated_at"`
	Task        *Task     `json:"task,omitempty"`
}

type DeployRequest struct {
	TenantID      string `json:"tenant_id,omitempty"`
	MachineName   string `json:"machine_name"`
	TaskID        string `json:"task_id"`
	RequestID     string `json:"request_id"`
	RegisterGraph bool   `json:"register_graph,omitempty"`
}

type DeploymentReport struct {
	MachineName string           `json:"machine_name"`
	State       string           `json:"state"`
	Detail      string           `json:"detail"`
	Source      DeploymentSource `json:"source"`
	Task        *Task            `json:"task,omitempty"`
}

// The caller selects a registered task, never an arbitrary source URL or filesystem path.
func (s *service) deploymentSource(ctx context.Context, tenant tenantConfig, taskID string) (DeploymentSource, error) {
	if _, ok := tenant.taskDefinition(taskID, ""); !ok {
		return DeploymentSource{}, fault(404, "流程尚未在云端登记。")
	}
	ctx, cancel := context.WithTimeout(ctx, 15*time.Second)
	defer cancel()
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, tenant.ScriptSourceURL, nil)
	if err != nil {
		return DeploymentSource{}, fault(503, "官方发布目录配置无效。")
	}
	client := &http.Client{CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
	response, err := client.Do(req)
	if err != nil {
		return DeploymentSource{}, fault(503, "无法读取官方发布目录，请稍后重试。")
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		return DeploymentSource{}, fault(503, "官方发布目录暂不可用。")
	}
	type sourcePackage struct {
		ID       string `json:"id"`
		TaskID   string `json:"taskId"`
		TenantID string `json:"tenantId"`
		Revision string `json:"revision"`
		Sha256   string `json:"sha256"`
	}
	var feed struct {
		Shared  sourcePackage   `json:"shared"`
		Scripts []sourcePackage `json:"scripts"`
	}
	data, err := io.ReadAll(io.LimitReader(response.Body, (4<<20)+1))
	if err != nil || len(data) > 4<<20 || json.Unmarshal(data, &feed) != nil || feed.Shared.ID != "Shared" {
		return DeploymentSource{}, fault(503, "官方发布目录格式无效。")
	}
	for _, item := range feed.Scripts {
		id := item.TaskID
		if id == "" {
			hash := sha256.Sum256([]byte(strings.ToUpper(item.ID)))
			id = "official." + hex.EncodeToString(hash[:12])
		}
		owner := item.TenantID
		if owner == "" {
			owner = s.config.Protocol.LegacyPublicationOwner
		}
		if id != taskID || owner != tenant.ID {
			continue
		}
		if item.ID == "" || item.Revision == "" || feed.Shared.Revision == "" || len(item.Sha256) != 64 || len(feed.Shared.Sha256) != 64 {
			return DeploymentSource{}, fault(503, "官方流程缺少版本或归档指纹。")
		}
		return DeploymentSource{item.ID, item.Revision, item.Sha256, feed.Shared.Revision, feed.Shared.Sha256}, nil
	}
	return DeploymentSource{}, fault(409, "此流程未在允许分发的官方目录中发布；工程本地任务不能自动分发。")
}

func deploymentRetry(state *tenantState, actor string, request DeployRequest) (Deployment, bool, error) {
	for _, job := range state.Deployments {
		if job.CreatedBy == actor && job.RequestID == request.RequestID {
			if job.TaskID != request.TaskID || job.RegisterGraph != request.RegisterGraph || !strings.EqualFold(job.MachineName, request.MachineName) {
				return Deployment{}, false, fault(409, "request_id 已用于不同部署；重试必须保留目标、流程和登记方式。")
			}
			return job, true, nil
		}
	}
	return Deployment{}, false, nil
}

func (s *service) putDeployment(state *tenantState, tenant tenantConfig, actor string, request DeployRequest, source DeploymentSource) (Deployment, error) {
	if previous, found, err := deploymentRetry(state, actor, request); found || err != nil {
		return previous, err
	}
	if request.RegisterGraph && tenant.PoolPackageDirectory == "" {
		return Deployment{}, fault(400, "此租户尚未配置机器池题包目录。")
	}
	machine, ok := state.Machines[strings.ToLower(request.MachineName)]
	if !ok {
		return Deployment{}, fault(404, "目标机器尚未接入自动化 Agent。")
	}
	assigned, err := s.assignMachine(machine.MachineName, tenant.ID)
	if err != nil || assigned.PoolID != machine.PoolID {
		return Deployment{}, fault(409, "目标机器归属已变化，请刷新目录。")
	}
	if !slices.Contains(machine.Capabilities, deploymentCapability) {
		return Deployment{}, fault(409, "目标 Agent 尚不支持云端流程部署，请先升级客户端；无需手动或通过远程会话安装 worker。")
	}
	for _, job := range state.Deployments {
		if job.State != "ready" && job.State != "failed" && strings.EqualFold(job.MachineName, machine.MachineName) && job.TaskID == request.TaskID {
			return Deployment{}, fault(409, "同一流程正在部署，请查询 deployment_id="+job.ID)
		}
	}
	id := make([]byte, 16)
	if _, err := rand.Read(id); err != nil {
		return Deployment{}, err
	}
	now := time.Now().UTC()
	job := Deployment{DeploymentRequest: DeploymentRequest{ID: hex.EncodeToString(id), TaskID: request.TaskID, Source: source, RegisterGraph: request.RegisterGraph},
		TenantID: tenant.ID, PoolID: machine.PoolID, MachineName: machine.MachineName, RequestID: request.RequestID,
		CreatedBy: actor, State: "queued", Detail: "等待目标 Agent 拉取并准备流程", CreatedAt: now, UpdatedAt: now}
	if state.Deployments == nil {
		state.Deployments = map[string]Deployment{}
	}
	state.Deployments[job.ID] = job
	return job, nil
}

func (s *service) deploy(ctx context.Context, tenant tenantConfig, actor string, request DeployRequest) (Deployment, error) {
	name, err := tenant.executionMachine(request.MachineName)
	if err != nil {
		return Deployment{}, err
	}
	request.MachineName = name
	if request.RequestID == "" || !clean(request.RequestID, 128) || strings.TrimSpace(request.RequestID) != request.RequestID {
		return Deployment{}, fault(400, "部署必须提供有效 request_id；重试保持原 ID。")
	}
	s.store.mu.Lock()
	previous, found, err := deploymentRetry(s.store.states[tenant.ID], actor, request)
	s.store.mu.Unlock()
	if found || err != nil {
		return previous, err
	}
	source, err := s.deploymentSource(ctx, tenant, request.TaskID)
	if err != nil {
		return Deployment{}, err
	}
	s.store.mu.Lock()
	defer s.store.mu.Unlock()
	var job Deployment
	err = s.store.change(tenant.ID, func(state *tenantState) error {
		var err error
		job, err = s.putDeployment(state, tenant, actor, request, source)
		return err
	})
	return job, err
}

func (s *service) getDeployment(tenant tenantConfig, id string) (Deployment, error) {
	s.store.mu.Lock()
	defer s.store.mu.Unlock()
	job, exists := s.store.states[tenant.ID].Deployments[id]
	if !exists {
		return Deployment{}, fault(404, "找不到流程部署记录。")
	}
	return job, nil
}

func (s *service) reportDeployment(id string, report DeploymentReport) (Deployment, error) {
	name, err := machineName(report.MachineName)
	if err != nil {
		return Deployment{}, err
	}
	if !slices.Contains([]string{"preparing", "ready", "failed"}, report.State) || !clean(report.Detail, 4000) {
		return Deployment{}, fault(400, "部署回执格式无效。")
	}
	s.store.mu.Lock()
	defer s.store.mu.Unlock()
	var assigned assignment
	for _, candidate := range s.machineAssignments(name) {
		if _, ok := s.store.states[candidate.TenantID].Deployments[id]; ok {
			assigned = candidate
			break
		}
	}
	if assigned.TenantID == "" {
		return Deployment{}, fault(403, "部署回执与目标机器或发布版本不符。")
	}
	var result Deployment
	err = s.store.change(assigned.TenantID, func(state *tenantState) error {
		job, ok := state.Deployments[id]
		if !ok || job.PoolID != assigned.PoolID || !strings.EqualFold(job.MachineName, name) || job.Source != report.Source {
			return fault(403, "部署回执与目标机器或发布版本不符。")
		}
		if job.State == "ready" || job.State == "failed" {
			result = job
			return nil
		}
		if report.State == "ready" {
			if report.Task == nil || report.Task.ID != job.TaskID {
				return fault(400, "就绪回执缺少对应流程目录。")
			}
			if job.RegisterGraph && report.Task.GraphFingerprint == "" {
				job.State, job.Detail, job.UpdatedAt = "failed", "部署未上报机器池所需的图指纹。", time.Now().UTC()
				state.Deployments[id], result = job, job
				return nil
			}
			poll := PollRequest{Tasks: []Task{*report.Task}}
			if err := validatePoll(&poll); err != nil {
				return err
			}
			task, valid := s.config.tenantByID[assigned.TenantID].normalizeTask(poll.Tasks[0])
			if !valid || task.ID != job.TaskID {
				return fault(409, "部署流程已不在云端登记目录中。")
			}
			machine := state.Machines[strings.ToLower(name)]
			machine.Tasks = slices.DeleteFunc(machine.Tasks, func(item Task) bool { return taskConfiguration(item) == taskConfiguration(task) })
			machine.Tasks = append(machine.Tasks, task)
			state.Machines[strings.ToLower(name)] = machine
			job.Task = &task
		}
		job.State, job.Detail, job.UpdatedAt = report.State, report.Detail, time.Now().UTC()
		state.Deployments[id], result = job, job
		return nil
	})
	return result, err
}

func agentCatalogPath(tenant tenantConfig, capabilities []string) string {
	if !slices.Contains(capabilities, tenantSourceCapability) {
		return ""
	}
	catalog, err := url.Parse(tenant.ScriptSourceURL)
	if err != nil || !strings.HasSuffix(catalog.Path, "/catalog.json") {
		return ""
	}
	return catalog.EscapedPath()
}

func deploymentMailbox(state *tenantState, tenant tenantConfig, machine Machine) []DeploymentRequest {
	result := []DeploymentRequest{}
	if !slices.Contains(machine.Capabilities, deploymentCapability) {
		return result
	}
	for _, job := range state.Deployments {
		if job.PoolID == machine.PoolID && strings.EqualFold(job.MachineName, machine.MachineName) && job.State != "ready" && job.State != "failed" {
			request := job.DeploymentRequest
			request.CatalogPath = agentCatalogPath(tenant, machine.Capabilities)
			result = append(result, request)
		}
	}
	sort.Slice(result, func(i, j int) bool { return result[i].ID < result[j].ID })
	return result
}

func (s *service) missingTaskSource(tenant tenantConfig, actor string, request CreateRequest, name string) (*DeploymentSource, error) {
	s.store.mu.Lock()
	state := s.store.states[tenant.ID]
	for _, run := range state.Runs {
		if request.RequestID != "" && run.RequestID == request.RequestID && run.CreatedBy == actor {
			s.store.mu.Unlock()
			return nil, nil
		}
	}
	machine, exists := state.Machines[strings.ToLower(name)]
	missing := exists && executionTask(machine, request.TaskID, false) == nil
	multipleSources := false
	if exists && agentCatalogPath(tenant, machine.Capabilities) != "" {
		for _, assigned := range s.machineAssignments(name) {
			if s.config.tenantByID[assigned.TenantID].ScriptSourceURL != tenant.ScriptSourceURL {
				multipleSources = true
				break
			}
		}
	}
	s.store.mu.Unlock()
	if !missing && multipleSources {
		publications, err := readWorkflowCatalog(tenant.ApprovedWorkflowsPath)
		if err != nil {
			return nil, err
		}
		// Installed files can now belong to a different library, including its Shared.
		// A published run needs its own pinned deployment even if the Agent listed the task.
		for _, publication := range publications {
			if publication.TenantID == tenant.ID && publication.TaskID == request.TaskID {
				missing = true
				break
			}
		}
	}
	if !missing {
		return nil, nil
	}
	source, err := s.deploymentSource(context.Background(), tenant, request.TaskID)
	return &source, err
}

// The run remains queued until source preparation has a durable machine receipt.
func advanceDeployment(state *tenantState, run *RemoteRun, now time.Time) bool {
	if run.DeploymentID == "" {
		return true
	}
	job, exists := state.Deployments[run.DeploymentID]
	if exists && job.State == "ready" && job.Task != nil && job.Task.CanRun {
		run.ConfigurationID, run.ScriptRevision = taskConfiguration(*job.Task), job.Task.ScriptRevision
		return true
	}
	if !exists || job.State == "failed" || job.State == "ready" {
		detail := "流程部署记录缺失。"
		if exists {
			detail = "流程部署失败：" + job.Detail
			if job.State == "ready" && job.Task != nil {
				detail = "流程已部署，但目标机不可运行：" + job.Task.UnavailableReason
			}
		}
		run.State, run.Detail, run.LogsComplete, run.UpdatedAt = "failed", detail, true, now
		run.Revision++
		state.Runs[run.ID] = *run
	}
	return false
}
