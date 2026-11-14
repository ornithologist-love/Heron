package automation

import (
	"bytes"
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"maps"
	"slices"
	"strings"
	"time"
)

// Projections only live for the waiting web request; they do not create a run.
type pendingInspection struct {
	assignment  assignment
	machineName string
	request     InspectionRequest
	result      chan InspectionReport
}

func (s *service) inspect(ctx context.Context, actor string, request CreateRequest, team bool) (json.RawMessage, error) {
	tenant, err := s.config.webExecution(actor, request, team)
	if err != nil {
		return nil, err
	}
	return s.inspectRun(ctx, tenant, request)
}

func (s *service) inspectRun(ctx context.Context, tenant tenantConfig, request CreateRequest) (json.RawMessage, error) {
	name, err := tenant.inspectionMachine(request.MachineName)
	if err != nil {
		return nil, err
	}
	if request.TaskID == "" || !clean(request.TaskID, 128) {
		return nil, fault(400, "task_id 无效。")
	}
	if err := validateParameters(request.Parameters); err != nil {
		return nil, err
	}
	inspection, err := s.queueInspection(tenant.ID, name, request)
	if err != nil {
		return nil, err
	}
	defer func() {
		s.store.mu.Lock()
		delete(s.inspections, inspection.request.ID)
		s.store.mu.Unlock()
	}()
	deadline, cancel := context.WithTimeout(ctx, 20*time.Second)
	defer cancel()
	select {
	case report := <-inspection.result:
		if report.Error != "" {
			return nil, fault(409, report.Error)
		}
		return report.Result, nil
	case <-deadline.Done():
		return nil, fault(504, "构建机未及时返回参数与流程图，请检查机器连接后重试。")
	}
}

func (s *service) queueInspection(tenant, name string, request CreateRequest) (*pendingInspection, error) {
	s.store.mu.Lock()
	defer s.store.mu.Unlock()
	machine, exists := s.store.states[tenant].Machines[strings.ToLower(name)]
	if !exists {
		return nil, fault(404, "机器尚未上报任务目录。")
	}
	if strings.EqualFold(name, s.config.tenantByID[tenant].ServerMachine) && !slices.Contains(machine.Capabilities, serverCapability) {
		return nil, fault(409, "配置的服务器尚未上报 Pregel 父图宿主能力，请启动服务器执行进程。")
	}
	assigned, err := s.config.assign(machine.MachineName)
	if err != nil || assigned.TenantID != tenant || assigned.PoolID != machine.PoolID {
		return nil, fault(409, "机器归属已变更，请刷新目录。")
	}
	if !slices.Contains(machine.Capabilities, "pregel.inspect.v1") {
		return nil, fault(409, "此机器尚不支持参数与流程图预览，请先更新构建机。")
	}
	task := executionTask(machine, request.TaskID, false)
	if task == nil {
		return nil, fault(404, "目标机尚未准备这个流程，请先下发流程部署后再检查参数。")
	}
	key := make([]byte, 16)
	if _, err := rand.Read(key); err != nil {
		return nil, err
	}
	values := parameters(task.Parameters)
	maps.Copy(values, request.Parameters)
	inspection := &pendingInspection{assignment: assigned, machineName: machine.MachineName,
		request: InspectionRequest{ID: hex.EncodeToString(key), TaskID: taskConfiguration(*task), Parameters: values},
		result:  make(chan InspectionReport, 1)}
	if s.inspections == nil {
		s.inspections = map[string]*pendingInspection{}
	}
	s.inspections[inspection.request.ID] = inspection
	return inspection, nil
}

func (s *service) reportInspection(id string, report InspectionReport) (map[string]bool, error) {
	name, err := machineName(report.MachineName)
	if err != nil {
		return nil, err
	}
	assigned, err := s.config.assign(name)
	if err != nil {
		return nil, err
	}
	if len(report.Error) > 8000 || report.Error == "" &&
		(!bytes.HasPrefix(bytes.TrimSpace(report.Result), []byte("{")) || !json.Valid(report.Result)) {
		return nil, fault(400, "参数与流程图投影格式无效。")
	}
	s.store.mu.Lock()
	defer s.store.mu.Unlock()
	inspection := s.inspections[id]
	if inspection == nil {
		// A browser may leave or time out while the machine is preparing its projection.
		return map[string]bool{"accepted": false}, nil
	}
	if inspection.assignment != assigned || !strings.EqualFold(inspection.machineName, name) {
		return nil, fault(403, "预览请求不属于这台机器。")
	}
	delete(s.inspections, id)
	inspection.result <- report
	return map[string]bool{"accepted": true}, nil
}
