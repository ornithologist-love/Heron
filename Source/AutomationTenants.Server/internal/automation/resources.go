package automation

import (
	"bytes"
	"encoding/json"
	"io"
	"net/http"
	"reflect"
	"strings"
	"time"
)

// The C# Pregel service owns resource rules, queues and durable leases. Go only
// supplies the machine's configured tenant and relays the resulting protocol.
var resourceClient = &http.Client{
	Timeout:       30 * time.Second,
	CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse },
}

type resourceResponse struct {
	Status      int
	ContentType string
	Body        []byte
}

func (s *service) proxyResources(w http.ResponseWriter, r *http.Request, action string) (resourceResponse, error) {
	if action != "acquire" && action != "state" && action != "release" && action != "expire" && action != "list" {
		return resourceResponse{}, fault(404, "资源协议接口不存在。")
	}
	var payload map[string]json.RawMessage
	if err := readJSON(w, r, &payload); err != nil {
		return resourceResponse{}, err
	}
	for key := range payload {
		if strings.EqualFold(key, "delegated_parent_run_id") || strings.EqualFold(key, "delegated_parent_machine_name") {
			delete(payload, key)
			continue
		}
		for _, identity := range []string{"machine_name", "owner_machine_name", "tenant_id"} {
			if key != identity && strings.EqualFold(key, identity) {
				return resourceResponse{}, fault(400, "资源协议身份字段必须使用 snake_case。")
			}
		}
	}
	var rawName string
	if payload == nil || json.Unmarshal(payload["machine_name"], &rawName) != nil {
		return resourceResponse{}, fault(400, "资源请求需要 machine_name。")
	}
	name, err := machineName(rawName)
	if err != nil {
		return resourceResponse{}, err
	}
	var tenant string
	if raw, ok := payload["tenant_id"]; ok && json.Unmarshal(raw, &tenant) != nil {
		return resourceResponse{}, fault(400, "资源协议 tenant_id 无效。")
	}
	owner := ""
	if rawOwner, exists := payload["owner_machine_name"]; exists {
		if !bytes.Equal(bytes.TrimSpace(rawOwner), []byte("null")) && json.Unmarshal(rawOwner, &owner) != nil {
			return resourceResponse{}, fault(400, "owner_machine_name 格式无效。")
		}
		owner = strings.TrimSpace(owner)
		if owner == "" {
			delete(payload, "owner_machine_name")
		} else {
			owner, err = machineName(owner)
			if err != nil {
				return resourceResponse{}, err
			}
			payload["owner_machine_name"], _ = json.Marshal(owner)
		}
	}
	s.store.mu.Lock()
	if tenant == "" {
		for _, candidate := range s.machineAssignments(name) {
			if s.config.tenantByID[candidate.TenantID].ResourceServerURL != "" {
				tenant = candidate.TenantID
				break
			}
		}
	}
	assigned, err := s.assignMachine(name, tenant)
	var ownerErr error
	if err == nil && owner != "" {
		_, ownerErr = s.assignMachine(owner, assigned.TenantID)
	}
	s.store.mu.Unlock()
	if err != nil {
		return resourceResponse{}, err
	}
	if ownerErr != nil {
		return resourceResponse{}, fault(403, "资源请求的执行机器必须与调用方属于同一租户。")
	}
	server := s.config.tenantByID[assigned.TenantID].ResourceServerURL
	if server == "" {
		return resourceResponse{}, fault(503, "当前租户尚未配置 Pregel 资源服务 resource_server_url。")
	}
	payload["tenant_id"], _ = json.Marshal(assigned.TenantID)
	payload["machine_name"], _ = json.Marshal(name)
	if action == "acquire" {
		if parent, parentMachine := s.delegatedResourceParent(assigned, name, payload["request"]); parent != "" {
			payload["delegated_parent_run_id"], _ = json.Marshal(parent)
			payload["delegated_parent_machine_name"], _ = json.Marshal(parentMachine)
		}
	}
	body, err := json.Marshal(payload)
	if err != nil {
		return resourceResponse{}, err
	}
	request, err := http.NewRequestWithContext(r.Context(), http.MethodPost, server+"/resources/"+action, bytes.NewReader(body))
	if err != nil {
		return resourceResponse{}, err
	}
	request.Header.Set("Content-Type", "application/json")
	response, err := resourceClient.Do(request)
	if err != nil {
		return resourceResponse{}, fault(503, "Pregel 资源服务暂时不可用。")
	}
	defer response.Body.Close()
	const maxReplyBytes = 16 * 1024 * 1024
	reply, err := io.ReadAll(io.LimitReader(response.Body, maxReplyBytes+1))
	if err != nil || len(reply) > maxReplyBytes {
		return resourceResponse{}, fault(502, "Pregel 资源服务响应无法读取或超出大小限制。")
	}
	contentType := response.Header.Get("Content-Type")
	if contentType == "" {
		contentType = "application/json; charset=utf-8"
	}
	return resourceResponse{Status: response.StatusCode, ContentType: contentType, Body: reply}, nil
}

// Bind cross-machine inheritance to the child invocation already delivered by
// Go. Local nested scopes have no such delivery and are checked by C# instead.
func (s *service) delegatedResourceParent(assigned assignment, machine string, raw json.RawMessage) (string, string) {
	var request struct {
		RunID         string          `json:"run_id"`
		ParentContext json.RawMessage `json:"parent_context"`
	}
	if json.Unmarshal(raw, &request) != nil || request.RunID == "" || len(request.ParentContext) == 0 ||
		bytes.Equal(bytes.TrimSpace(request.ParentContext), []byte("null")) {
		return "", ""
	}
	var invocation json.RawMessage
	var parentMachine string
	s.store.mu.Lock()
	if state := s.store.states[assigned.TenantID]; state != nil {
		for _, run := range state.Runs {
			if run.AcceptedAt != nil && run.ParentRunID != "" && run.ChildRunID == request.RunID &&
				strings.EqualFold(run.MachineName, machine) {
				parent, exists := state.Runs[run.ParentRunID]
				if !exists || parent.MachineName == "" {
					continue
				}
				invocation = bytes.Clone(run.Subgraph)
				parentMachine = parent.MachineName
				break
			}
		}
	}
	s.store.mu.Unlock()
	var child struct {
		ParentRunID           string          `json:"parent_run_id"`
		ParentResourceContext json.RawMessage `json:"parent_resource_context"`
	}
	if json.Unmarshal(invocation, &child) != nil || child.ParentRunID == "" {
		return "", ""
	}
	var declared, requested map[string]any
	if json.Unmarshal(child.ParentResourceContext, &declared) != nil || declared == nil ||
		json.Unmarshal(request.ParentContext, &requested) != nil || requested == nil || !reflect.DeepEqual(declared, requested) {
		return "", ""
	}
	return child.ParentRunID, parentMachine
}
