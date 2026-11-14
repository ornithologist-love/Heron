package automation

import (
	"fmt"
	"slices"
	"strings"
)

type teamExecution struct {
	TaskIDs      []string `json:"task_ids"`
	MachineNames []string `json:"machine_names"`
}

func (p *teamExecution) validate(tenant string, assignments map[string]assignment) error {
	if p == nil {
		return nil
	}
	if len(p.TaskIDs) == 0 || len(p.MachineNames) == 0 {
		return fmt.Errorf("租户 %s 的 team_execution 必须指定任务和机器", tenant)
	}
	for _, id := range p.TaskIDs {
		if strings.TrimSpace(id) != id || id == "" || !clean(id, 128) {
			return fmt.Errorf("租户 %s 的 team_execution.task_ids 无效", tenant)
		}
	}
	for _, name := range p.MachineNames {
		if assignments[strings.ToLower(name)].TenantID != tenant {
			return fmt.Errorf("租户 %s 的 team_execution.machine_names 必须在本租户的机器池中显式登记", tenant)
		}
	}
	return nil
}

func (p *teamExecution) allowsMachine(name string) bool {
	return slices.ContainsFunc(p.MachineNames, func(candidate string) bool { return strings.EqualFold(candidate, name) })
}

func (c config) webTenants(actor, selected string, team bool) ([]tenantConfig, error) {
	if !team {
		if selected != "" {
			tenant, err := c.tenant(actor, selected)
			return []tenantConfig{tenant}, err
		}
		if visible := c.visible(actor); len(visible) > 0 {
			return visible, nil
		}
	} else if strings.TrimSpace(actor) != "" {
		var visible []tenantConfig
		for _, tenant := range c.Tenants {
			if tenant.TeamExecution != nil && (selected == "" || selected == tenant.ID) {
				visible = append(visible, tenant)
			}
		}
		if len(visible) > 0 {
			return visible, nil
		}
	}
	return nil, fault(403, "当前账号没有可访问的自动化租户。")
}

func (c config) webTenant(actor, selected string, team bool) (tenantConfig, error) {
	if !team {
		return c.tenant(actor, selected)
	}
	tenants, err := c.webTenants(actor, selected, true)
	if err != nil {
		return tenantConfig{}, err
	}
	if len(tenants) != 1 {
		return tenantConfig{}, fault(400, "请选择 tenant_id。")
	}
	return tenants[0], nil
}

func (c config) webExecution(actor string, request CreateRequest, team bool) (tenantConfig, error) {
	tenant, err := c.webTenant(actor, request.TenantID, team)
	if err != nil || !team {
		return tenant, err
	}
	name, err := tenant.inspectionMachine(request.MachineName)
	if err != nil {
		return tenantConfig{}, err
	}
	if _, registered := tenant.taskDefinition(request.TaskID, ""); !registered ||
		!slices.Contains(tenant.TeamExecution.TaskIDs, request.TaskID) || !tenant.TeamExecution.allowsMachine(name) {
		return tenantConfig{}, fault(403, "该流程或执行目标未向团队成员开放。")
	}
	return tenant, nil
}
