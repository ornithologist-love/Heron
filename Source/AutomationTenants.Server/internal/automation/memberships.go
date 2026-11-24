package automation

import (
	"encoding/json"
	"errors"
	"maps"
	"os"
	"path/filepath"
	"slices"
	"strings"
)

type machineMemberships struct {
	Tenants  []Tenant            `json:"tenants"`
	Machines map[string][]string `json:"machines"`
	Default  []string            `json:"default_tenant_ids"`
}

type membershipChange struct {
	MachineName string   `json:"machine_name"`
	TenantIDs   []string `json:"tenant_ids"`
}

func (s *store) membershipFile() string {
	return filepath.Join(s.directory, "machine-memberships.json")
}

func (s *store) loadMemberships(c config) error {
	s.memberships = map[string][]assignment{}
	data, err := os.ReadFile(s.membershipFile())
	if errors.Is(err, os.ErrNotExist) {
		return nil
	}
	if err != nil {
		return err
	}
	var values map[string][]string
	if json.Unmarshal(data, &values) != nil {
		return errors.New("机器租户归属文件无效")
	}
	for name, ids := range values {
		valid, err := machineName(name)
		if err != nil || valid != name || name != strings.ToLower(name) {
			return errors.New("机器租户归属文件中的机器名无效")
		}
		assignments, err := c.memberships(name, ids)
		if err != nil {
			return err
		}
		s.memberships[name] = assignments
	}
	return nil
}

func (c config) memberships(name string, ids []string) ([]assignment, error) {
	result := make([]assignment, 0, len(ids))
	seen := map[string]bool{}
	for _, id := range ids {
		tenant, ok := c.tenantByID[id]
		if !ok || seen[id] {
			return nil, fault(400, "租户分组包含未知或重复的租户。")
		}
		pool := tenant.DefaultPool
		if original := c.assignments[strings.ToLower(name)]; original.TenantID == id {
			pool = original.PoolID
		}
		if pool == "" {
			return nil, fault(400, "所选租户需要配置默认机器池。")
		}
		seen[id] = true
		result = append(result, assignment{id, pool})
	}
	slices.SortFunc(result, func(left, right assignment) int {
		if left.TenantID == c.DefaultTenant && right.TenantID != c.DefaultTenant {
			return -1
		}
		if right.TenantID == c.DefaultTenant && left.TenantID != c.DefaultTenant {
			return 1
		}
		return strings.Compare(left.TenantID, right.TenantID)
	})
	return result, nil
}

// The caller holds store.mu. An empty persisted list explicitly removes all memberships.
func (s *service) machineAssignments(name string) []assignment {
	if assigned, ok := s.store.memberships[strings.ToLower(name)]; ok {
		return assigned
	}
	assigned, err := s.config.assign(name)
	if err != nil {
		return nil
	}
	return []assignment{assigned}
}

// The caller holds store.mu.
func (s *service) assignMachine(name, tenant string) (assignment, error) {
	assignments := s.machineAssignments(name)
	if tenant == "" && len(assignments) != 0 {
		return assignments[0], nil
	}
	for _, assigned := range assignments {
		if assigned.TenantID == tenant {
			return assigned, nil
		}
	}
	return assignment{}, fault(403, "机器不属于目标租户与机器池。")
}

// The caller holds store.mu. A previous receipt decides where a local run belongs.
func (s *service) assignLocalRun(name, remoteID, localID, taskID string) (assignment, error) {
	if remoteID != "" {
		return s.agentRun(name, remoteID)
	}
	for _, assigned := range s.machineAssignments(name) {
		for _, run := range s.store.states[assigned.TenantID].Runs {
			if localID != "" && strings.EqualFold(run.MachineName, name) && run.LocalRunID == localID {
				return assigned, nil
			}
		}
	}
	for _, assigned := range s.machineAssignments(name) {
		if _, registered := s.config.tenantByID[assigned.TenantID].taskDefinition(taskID, ""); registered {
			return assigned, nil
		}
	}
	return s.assignMachine(name, "")
}

func tenantIDs(assignments []assignment) []string {
	ids := make([]string, 0, len(assignments))
	for _, assigned := range assignments {
		ids = append(ids, assigned.TenantID)
	}
	return ids
}

func (s *service) membershipView() machineMemberships {
	s.store.mu.Lock()
	defer s.store.mu.Unlock()
	return s.membershipViewLocked()
}

func (s *service) membershipViewLocked() machineMemberships {
	view := machineMemberships{Tenants: []Tenant{}, Machines: map[string][]string{}, Default: []string{}}
	for _, tenant := range s.config.Tenants {
		view.Tenants = append(view.Tenants, tenant.view())
	}
	if (len(s.config.Tenants) == 1 || s.config.AllowDefaultTenantFallback) &&
		s.config.tenantByID[s.config.DefaultTenant].DefaultPool != "" {
		view.Default = []string{s.config.DefaultTenant}
	}
	for name, original := range s.config.assignments {
		view.Machines[name] = []string{original.TenantID}
	}
	for name, assigned := range s.store.memberships {
		view.Machines[name] = tenantIDs(assigned)
	}
	return view
}

func (s *service) saveMemberships(change membershipChange) (machineMemberships, error) {
	name, err := machineName(change.MachineName)
	if err != nil {
		return machineMemberships{}, err
	}
	if change.TenantIDs == nil {
		return machineMemberships{}, fault(400, "必须提交完整的租户分组。")
	}
	key := strings.ToLower(name)
	next, err := s.config.memberships(key, change.TenantIDs)
	if err != nil {
		return machineMemberships{}, err
	}
	s.store.mu.Lock()
	defer s.store.mu.Unlock()
	for _, prior := range s.machineAssignments(key) {
		if slices.Contains(next, prior) {
			continue
		}
		tenant := s.config.tenantByID[prior.TenantID]
		if strings.EqualFold(tenant.ServerMachine, name) || tenant.TeamExecution != nil && tenant.TeamExecution.allowsMachine(name) {
			return machineMemberships{}, fault(409, "当前租户仍将这台机器用作固定执行目标。")
		}
		state := s.store.states[prior.TenantID]
		for _, run := range state.Runs {
			if strings.EqualFold(run.MachineName, name) && (!terminal(run.State) || !run.LogsComplete || poolChildHoldsMachine(run)) {
				return machineMemberships{}, fault(409, "这台机器还有未结束或未回传完毕的运行。")
			}
		}
		for _, job := range state.Deployments {
			if strings.EqualFold(job.MachineName, name) && job.State != "ready" && job.State != "failed" {
				return machineMemberships{}, fault(409, "这台机器还有未完成的流程部署。")
			}
		}
	}
	updated := maps.Clone(s.store.memberships)
	updated[key] = next
	data := make(map[string][]string, len(updated))
	for key, assigned := range updated {
		data[key] = tenantIDs(assigned)
	}
	if err := writeAtomic(s.store.membershipFile(), data); err != nil {
		return machineMemberships{}, err
	}
	s.store.memberships = updated
	return s.membershipViewLocked(), nil
}
