package automation

import (
	"encoding/json"
	"errors"
	"fmt"
	"net"
	"net/url"
	"os"
	"path/filepath"
	"regexp"
	"slices"
	"strings"
	"unicode"
)

type poolConfig struct {
	Pool
	MachineNames      []string `json:"machine_names"`
	LegacyMaxParallel int      `json:"max_parallel"`
}

type tenantConfig struct {
	ApprovedWorkflowsPath string   `json:"approved_workflows_path,omitempty"`
	ID                    string   `json:"id"`
	Name                  string   `json:"name"`
	Admins                []string `json:"admins"`
	LegacyMaxParallel     int      `json:"max_parallel"`
	DefaultPool           string   `json:"default_pool"`
	ServerMachine         string   `json:"server_machine"`
	ResourceServerURL     string   `json:"resource_server_url"`
	// ScriptSourceURL selects this tenant's published catalog (for example an eval question bank);
	// empty means the global script_source_url.
	ScriptSourceURL string            `json:"script_source_url,omitempty"`
	Pools           []poolConfig      `json:"pools"`
	Tasks           []TaskDefinition  `json:"tasks,omitempty"`
	TeamExecution   *teamExecution    `json:"team_execution,omitempty"`
	EventHooks      []eventHookConfig `json:"event_hooks,omitempty"`
}

type config struct {
	Protocol                   protocolConfig `json:"protocol,omitempty"`
	MCP                        *mcpConfig     `json:"mcp,omitempty"`
	ScriptSourceURL            string         `json:"script_source_url,omitempty"`
	ApprovedWorkflowsPath      string         `json:"approved_workflows_path,omitempty"`
	Listen                     string         `json:"listen"`
	Directory                  string         `json:"directory"`
	DefaultTenant              string         `json:"default_tenant"`
	AllowDefaultTenantFallback bool           `json:"allow_default_tenant_fallback,omitempty"`
	Tenants                    []tenantConfig `json:"tenants"`
	assignments                map[string]assignment
	tenantByID                 map[string]tenantConfig
}

type assignment struct {
	TenantID string
	PoolID   string
}

var safeID = regexp.MustCompile(`^[a-zA-Z0-9][a-zA-Z0-9_-]{0,95}$`)
var runID = regexp.MustCompile(`^[a-f0-9]{32}$`)

func clean(value string, limit int) bool {
	return len(value) <= limit && !strings.ContainsFunc(value, unicode.IsControl)
}

func machineName(value string) (string, error) {
	value = strings.TrimSpace(value)
	if value == "" || !clean(value, 128) {
		return "", fault(400, "machine_name 必须是有效机器名。")
	}
	return value, nil
}

// A published catalog is a plain HTTP(S) address configured on the server: no credentials, query or fragment.
func validSourceURL(value string) bool {
	source, err := url.Parse(value)
	return err == nil && source.User == nil && source.RawQuery == "" && source.Fragment == "" &&
		(source.Scheme == "http" || source.Scheme == "https") && source.Host != ""
}

func loadConfig(filename string) (config, error) {
	var c config
	data, err := os.ReadFile(filename)
	if err != nil {
		return c, errors.New("无法读取 Go 后台配置")
	}
	if json.Unmarshal(data, &c) != nil {
		return c, errors.New("Go 后台配置格式无效")
	}
	if err := c.Protocol.configure(); err != nil {
		return c, err
	}
	if c.Listen == "" {
		c.Listen = "127.0.0.1:5183"
	}
	if c.ScriptSourceURL == "" {
		c.ScriptSourceURL = "http://127.0.0.1:5173/api/automation-scripts/catalog.json"
	}
	if !validSourceURL(c.ScriptSourceURL) {
		return c, errors.New("script_source_url 必须是服务端配置的 HTTP(S) 官方发布目录")
	}
	host, _, err := net.SplitHostPort(c.Listen)
	if err != nil || host != "127.0.0.1" && host != "::1" && host != "localhost" {
		return c, errors.New("Go 后台只允许 loopback 监听")
	}
	if c.Directory == "" || len(c.Tenants) == 0 {
		return c, errors.New("需要配置 directory 和至少一个 tenant")
	}
	if !filepath.IsAbs(c.Directory) {
		c.Directory = filepath.Join(filepath.Dir(filename), c.Directory)
	}
	c.Directory, err = filepath.Abs(c.Directory)
	if err != nil {
		return c, errors.New("Go 后台数据目录无效")
	}
	c.assignments = map[string]assignment{}
	if c.ApprovedWorkflowsPath != "" && !filepath.IsAbs(c.ApprovedWorkflowsPath) {
		c.ApprovedWorkflowsPath = filepath.Join(filepath.Dir(filename), c.ApprovedWorkflowsPath)
	}
	if _, err = readWorkflowCatalog(c.ApprovedWorkflowsPath); err != nil {
		return c, errors.New("已审核流程目录无法读取")
	}
	c.tenantByID = map[string]tenantConfig{}
	for index, tenant := range c.Tenants {
		// A tenant bound to its own published library (script_source_url) also reads that library's ledger.
		if tenant.ApprovedWorkflowsPath == "" {
			tenant.ApprovedWorkflowsPath = c.ApprovedWorkflowsPath
		} else {
			if !filepath.IsAbs(tenant.ApprovedWorkflowsPath) {
				tenant.ApprovedWorkflowsPath = filepath.Join(filepath.Dir(filename), tenant.ApprovedWorkflowsPath)
			}
			if _, err = readWorkflowCatalog(tenant.ApprovedWorkflowsPath); err != nil {
				return c, fmt.Errorf("租户 %s 的已审核流程目录无法读取", tenant.ID)
			}
		}
		if tenant.Tasks == nil {
			return c, errors.New("请显式配置租户 tasks 任务目录；机器上报不能创建云端任务")
		}
		if err := validateTaskDefinitions(tenant.Tasks); err != nil {
			return c, err
		}
		if err := validateEventHooks(tenant.ID, tenant.EventHooks); err != nil {
			return c, err
		}
		if !safeID.MatchString(tenant.ID) || len(tenant.Pools) == 0 {
			return c, errors.New("tenant id 或 pools 配置无效")
		}
		if tenant.LegacyMaxParallel != 0 {
			return c, fmt.Errorf("租户 %s 的 max_parallel 已停用；请将容量约束迁入 Pregel MutexRule，并移除该字段或设为 0", tenant.ID)
		}
		if tenant.ServerMachine != "" {
			tenant.ServerMachine, err = machineName(tenant.ServerMachine)
			if err != nil {
				return c, errors.New("server_machine 配置无效")
			}
		}
		if tenant.ResourceServerURL != "" {
			address, parseErr := url.Parse(tenant.ResourceServerURL)
			if parseErr != nil || address.Host == "" || address.User != nil || address.RawQuery != "" || address.Fragment != "" || address.RawPath != "" ||
				(address.Scheme != "http" && address.Scheme != "https") || (address.Path != "" && address.Path != "/") {
				return c, errors.New("resource_server_url 必须是 loopback HTTP(S) 服务基址，不含路径、凭据、查询或片段")
			}
			host := strings.ToLower(address.Hostname())
			ip := net.ParseIP(host)
			if host != "localhost" && (ip == nil || !ip.IsLoopback()) {
				return c, errors.New("resource_server_url 只允许 loopback 地址")
			}
			tenant.ResourceServerURL = strings.TrimRight(address.String(), "/")
		}
		if tenant.ScriptSourceURL == "" {
			tenant.ScriptSourceURL = c.ScriptSourceURL
		} else if !validSourceURL(tenant.ScriptSourceURL) {
			return c, fmt.Errorf("租户 %s 的 script_source_url 必须是服务端配置的 HTTP(S) 发布目录", tenant.ID)
		}
		if _, exists := c.tenantByID[tenant.ID]; exists {
			return c, errors.New("tenant id 重复")
		}
		if tenant.Name == "" {
			tenant.Name = tenant.ID
		}
		pools := map[string]bool{}
		for i, pool := range tenant.Pools {
			if !safeID.MatchString(pool.ID) || pools[pool.ID] {
				return c, errors.New("pool id 配置无效")
			}
			if pool.LegacyMaxParallel != 0 {
				return c, fmt.Errorf("租户 %s 机器池 %s 的 max_parallel 已停用；请将容量约束迁入 Pregel MutexRule，并移除该字段或设为 0", tenant.ID, pool.ID)
			}
			pools[pool.ID] = true
			if pool.Name == "" {
				pool.Name = pool.ID
			}
			tenant.Pools[i] = pool
			for _, name := range pool.MachineNames {
				name, err = machineName(name)
				if err != nil {
					return c, errors.New("machine_names 配置无效")
				}
				key := strings.ToLower(name)
				if _, exists := c.assignments[key]; exists {
					return c, fmt.Errorf("机器 %s 有重复 tenant/pool 映射", name)
				}
				c.assignments[key] = assignment{tenant.ID, pool.ID}
			}
		}
		if tenant.DefaultPool == "" && len(tenant.Pools) == 1 {
			tenant.DefaultPool = tenant.Pools[0].ID
		}
		if tenant.DefaultPool != "" && !pools[tenant.DefaultPool] {
			return c, errors.New("default_pool 不在 tenant 的 pools 中")
		}
		for i, actor := range tenant.Admins {
			actor = strings.ToLower(strings.TrimSpace(actor))
			if actor == "" || !clean(actor, 128) {
				return c, errors.New("tenant admins 配置无效")
			}
			tenant.Admins[i] = actor
		}
		if err := tenant.TeamExecution.validate(tenant.ID, c.assignments); err != nil {
			return c, err
		}
		c.Tenants[index] = tenant
		c.tenantByID[tenant.ID] = tenant
	}
	if c.DefaultTenant == "" && len(c.Tenants) == 1 {
		c.DefaultTenant = c.Tenants[0].ID
	}
	if c.DefaultTenant != "" {
		if _, exists := c.tenantByID[c.DefaultTenant]; !exists {
			return c, errors.New("default_tenant 不在 tenants 中")
		}
	}
	if c.AllowDefaultTenantFallback && c.tenantByID[c.DefaultTenant].DefaultPool == "" {
		return c, errors.New("allow_default_tenant_fallback 需要配置 default_tenant 及其 default_pool")
	}
	if c.MCP != nil {
		if !clean(c.MCP.BearerToken, 512) || strings.ContainsFunc(c.MCP.BearerToken, unicode.IsSpace) {
			return c, errors.New("MCP bearer_token 不能包含空白或控制字符，且不能超过 512 字节")
		}
		if len(c.MCP.TenantIDs) == 0 {
			return c, errors.New("MCP 启用时必须明确配置 tenant_ids")
		}
		seen := map[string]bool{}
		for _, id := range c.MCP.TenantIDs {
			if _, exists := c.tenantByID[id]; !exists || seen[id] {
				return c, errors.New("MCP tenant_ids 包含未知或重复租户")
			}
			seen[id] = true
		}
		if err := c.MCP.validateCallers(); err != nil {
			return c, err
		}
	}
	for _, tenant := range c.Tenants {
		if tenant.ServerMachine == "" {
			continue
		}
		assigned, assignErr := c.assign(tenant.ServerMachine)
		if assignErr != nil || assigned.TenantID != tenant.ID {
			return c, fmt.Errorf("租户 %s 的 server_machine 必须归属本租户的机器池", tenant.ID)
		}
	}
	return c, nil
}

// Reading a graph only needs its host; execution prerequisites belong to run admission.
func (t tenantConfig) inspectionMachine(requested string) (string, error) {
	if strings.TrimSpace(requested) != "" {
		return machineName(requested)
	}
	if t.ServerMachine == "" {
		return "", fault(409, "当前租户尚未配置服务器 Pregel 宿主，请配置 server_machine 或明确选择执行机器。")
	}
	return t.ServerMachine, nil
}

func (t tenantConfig) executionMachine(requested string) (string, error) {
	name, err := t.inspectionMachine(requested)
	if err != nil {
		return "", err
	}
	if strings.EqualFold(name, t.ServerMachine) && t.ResourceServerURL == "" {
		return "", fault(409, "服务器父图执行需要配置 Pregel 资源服务 resource_server_url。")
	}
	return name, nil
}

func (c config) assign(name string) (assignment, error) {
	if mapped, exists := c.assignments[strings.ToLower(name)]; exists {
		return mapped, nil
	}
	if (len(c.Tenants) == 1 || c.AllowDefaultTenantFallback) && c.DefaultTenant != "" {
		tenant := c.tenantByID[c.DefaultTenant]
		if tenant.DefaultPool != "" {
			return assignment{tenant.ID, tenant.DefaultPool}, nil
		}
	}
	return assignment{}, fault(403, "机器尚未配置唯一的租户与机器池归属。")
}

func (t tenantConfig) view() Tenant {
	pools := make([]Pool, 0, len(t.Pools))
	for _, pool := range t.Pools {
		pools = append(pools, pool.Pool)
	}
	return Tenant{ID: t.ID, Name: t.Name, ServerMachine: t.ServerMachine, Pools: pools}
}

// Every signed-in account works in every tenant: it sees tasks, machines and runs and may run,
// stop and schedule them. Only reviewing a Shared change asks for a tenant administrator.
func (c config) visible(actor string) []tenantConfig {
	if strings.TrimSpace(actor) == "" {
		return []tenantConfig{}
	}
	return slices.Clone(c.Tenants)
}

func (c config) administers(actor, tenant string) bool {
	actor = strings.ToLower(strings.TrimSpace(actor))
	return actor != "" && slices.Contains(c.tenantByID[tenant].Admins, actor)
}

func (c config) tenant(actor, selected string) (tenantConfig, error) {
	visible := c.visible(actor)
	if len(visible) == 0 {
		return tenantConfig{}, fault(403, "请登录后使用自动化。")
	}
	if selected == "" && len(visible) == 1 {
		return visible[0], nil
	}
	for _, tenant := range visible {
		if tenant.ID == selected {
			return tenant, nil
		}
	}
	if selected == "" {
		return tenantConfig{}, fault(400, "请选择 tenant_id。")
	}
	return tenantConfig{}, fault(403, "租户不存在。")
}
