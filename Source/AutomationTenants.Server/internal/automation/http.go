package automation

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"log"
	"net"
	"net/http"
	"strconv"
	"strings"
	"time"
)

func Run(ctx context.Context, filename string) error {
	c, err := loadConfig(filename)
	if err != nil {
		return err
	}
	store, err := openStore(c)
	if err != nil {
		return err
	}
	defer store.lock.Close()
	service := &service{config: c, store: store}
	go service.dispatchEvents(ctx)
	go service.runSchedules(ctx)
	listener, err := net.Listen("tcp", c.Listen)
	if err != nil {
		return errors.New("Go 后台监听端口不可用")
	}
	defer listener.Close()
	mux := http.NewServeMux()
	mcpHandler := service.automationMCP()
	mux.Handle("/automation", mcpHandler)
	mux.Handle("/automation/{$}", mcpHandler)
	mux.HandleFunc("/internal/mcp-admission", service.mcpAdmission)
	mux.Handle("/", http.HandlerFunc(service.serve))
	server := &http.Server{Handler: mux, ReadHeaderTimeout: 10 * time.Second,
		IdleTimeout: 60 * time.Second, MaxHeaderBytes: 16384, ErrorLog: log.New(io.Discard, "", 0)}
	finished := make(chan error, 1)
	go func() { finished <- server.Serve(listener) }()
	log.Printf("AutomationTenants ready on %s", c.Listen)
	select {
	case <-ctx.Done():
	case err = <-finished:
		if errors.Is(err, http.ErrServerClosed) {
			err = nil
		}
	}
	shutdown, stop := context.WithTimeout(context.Background(), 5*time.Second)
	defer stop()
	if server.Shutdown(shutdown) != nil {
		_ = server.Close()
	}
	return err
}

func sendJSON(w http.ResponseWriter, status int, value any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(status)
	encoder := json.NewEncoder(w)
	encoder.SetEscapeHTML(false)
	_ = encoder.Encode(value)
}

func readJSON(w http.ResponseWriter, r *http.Request, value any) error {
	r.Body = http.MaxBytesReader(w, r.Body, 16*1024*1024)
	decoder := json.NewDecoder(r.Body)
	if err := decoder.Decode(value); err != nil {
		return fault(400, "请求 JSON 格式无效或超出大小限制。")
	}
	var extra any
	if decoder.Decode(&extra) != io.EOF {
		return fault(400, "请求必须只有一个 JSON 对象。")
	}
	return nil
}

func (s *service) serve(w http.ResponseWriter, r *http.Request) {
	value, err := s.route(w, r)
	if err != nil {
		status, message := 500, "远程自动化后台暂时不可用。"
		var expected *apiFault
		if errors.As(err, &expected) {
			status, message = expected.status, expected.message
		} else {
			log.Printf("Remote automation persistence or request operation failed: %T", err)
		}
		if receipt, ok := value.(LogsReceipt); ok {
			sendJSON(w, status, map[string]any{"error": message, "next_offset": receipt.NextOffset})
		} else {
			sendJSON(w, status, map[string]any{"error": message})
		}
		return
	}
	if response, ok := value.(resourceResponse); ok {
		w.Header().Set("Content-Type", response.ContentType)
		w.Header().Set("Cache-Control", "no-store")
		w.WriteHeader(response.Status)
		_, _ = w.Write(response.Body)
		return
	}
	sendJSON(w, 200, value)
}

func (s *service) route(w http.ResponseWriter, r *http.Request) (any, error) {
	path := strings.TrimSuffix(r.URL.Path, "/")
	if path == "/health" && r.Method == http.MethodGet {
		return map[string]bool{"healthy": true}, nil
	}
	const admin = "/api/remote-automation"
	const agent = "/api/agent/automation"
	actor := r.Header.Get(s.config.Protocol.ActorHeader)
	team := r.Header.Get(s.config.Protocol.AccessHeader) == "team"
	tenant := r.URL.Query().Get("tenant_id")
	switch {
	case path == "/internal/machine-memberships" && r.Method == http.MethodGet:
		return s.membershipView(), nil
	case path == "/internal/machine-memberships" && r.Method == http.MethodPost:
		var change membershipChange
		if err := readJSON(w, r, &change); err != nil {
			return nil, err
		}
		return s.saveMemberships(change)
	case path == "/internal/workflow-source-identity" && r.Method == http.MethodPost:
		var request workflowIdentityRequest
		if err := readJSON(w, r, &request); err != nil {
			return nil, err
		}
		return s.workflowIdentity(actor, request)
	case path == "/internal/workflow-validation" && r.Method == http.MethodGet:
		query := r.URL.Query()
		run, err := s.workflowValidation(tenant, query.Get("machine_name"), query.Get("task_id"), query.Get("run_id"), query.Get("uploader"))
		if err != nil || !query.Has("after") {
			return run, err
		}
		after, err := strconv.ParseInt(query.Get("after"), 10, 64)
		if err != nil {
			return nil, fault(400, "日志游标无效。")
		}
		return s.runLogPage(s.config.tenantByID[tenant], run.ID, after, 200)
	case path == admin && r.Method == http.MethodGet:
		return s.inventory(actor, tenant, team)
	case path == admin+"/deployments" && r.Method == http.MethodPost:
		var request DeployRequest
		if err := readJSON(w, r, &request); err != nil {
			return nil, err
		}
		if request.TenantID == "" {
			request.TenantID = tenant
		}
		selected, err := s.config.webExecution(actor, CreateRequest{TenantID: request.TenantID,
			MachineName: request.MachineName, TaskID: request.TaskID}, team)
		if err != nil {
			return nil, err
		}
		return s.deploy(r.Context(), selected, actor, request)
	case strings.HasPrefix(path, admin+"/deployments/") && r.Method == http.MethodGet:
		selected, err := s.config.tenant(actor, tenant)
		if err != nil {
			return nil, err
		}
		return s.getDeployment(selected, strings.TrimPrefix(path, admin+"/deployments/"))
	case path == admin+"/runs" && r.Method == http.MethodPost:
		var request CreateRequest
		if err := readJSON(w, r, &request); err != nil {
			return nil, err
		}
		if request.TenantID == "" {
			request.TenantID = tenant
		}
		return s.create(actor, request, team)
	case path == admin+"/organization" && r.Method == http.MethodPost:
		if team {
			return nil, fault(403, "只有管理员可以编辑任务组织。")
		}
		var request Organization
		if err := readJSON(w, r, &request); err != nil {
			return nil, err
		}
		return s.organize(actor, tenant, request)
	case path == admin+"/schedules" && r.Method == http.MethodPost:
		if team {
			return nil, fault(403, "只有管理员可以修改团队定时。")
		}
		var request ScheduleRequest
		if err := readJSON(w, r, &request); err != nil {
			return nil, err
		}
		return s.saveSchedule(actor, tenant, request)
	case path == admin+"/inspect" && r.Method == http.MethodPost:
		var request CreateRequest
		if err := readJSON(w, r, &request); err != nil {
			return nil, err
		}
		if request.TenantID == "" {
			request.TenantID = tenant
		}
		return s.inspect(r.Context(), actor, request, team)
	case path == agent+"/poll" && r.Method == http.MethodPost:
		var request PollRequest
		if err := readJSON(w, r, &request); err != nil {
			return nil, err
		}
		return s.poll(request)
	case strings.HasPrefix(path, agent+"/deployments/") && r.Method == http.MethodPost:
		var report DeploymentReport
		if err := readJSON(w, r, &report); err != nil {
			return nil, err
		}
		return s.reportDeployment(strings.TrimPrefix(path, agent+"/deployments/"), report)
	case path == agent+"/observations" && r.Method == http.MethodPost:
		var request ObservationRequest
		if err := readJSON(w, r, &request); err != nil {
			return nil, err
		}
		return s.observe(request)
	case strings.HasPrefix(path, agent+"/resources/") && r.Method == http.MethodPost:
		return s.proxyResources(w, r, strings.TrimPrefix(path, agent+"/resources/"))
	case path == agent+"/children" && r.Method == http.MethodPost:
		var request ChildRequest
		if err := readJSON(w, r, &request); err != nil {
			return nil, err
		}
		return s.createChild(request)
	case strings.HasPrefix(path, agent+"/children/") && r.Method == http.MethodPost:
		parts := strings.Split(strings.TrimPrefix(path, agent+"/children/"), "/")
		var request ChildAccessRequest
		if err := readJSON(w, r, &request); err != nil {
			return nil, err
		}
		if len(parts) == 1 {
			return s.getChild(parts[0], request)
		}
		if len(parts) == 2 && parts[1] == "stop" {
			return s.stopChild(parts[0], request)
		}
		if len(parts) == 2 && parts[1] == "logs" {
			return s.childLogs(parts[0], request)
		}
	case strings.HasPrefix(path, agent+"/inspections/") && r.Method == http.MethodPost:
		var report InspectionReport
		if err := readJSON(w, r, &report); err != nil {
			return nil, err
		}
		return s.reportInspection(strings.TrimPrefix(path, agent+"/inspections/"), report)
	case strings.HasPrefix(path, admin+"/runs/"):
		parts := strings.Split(strings.TrimPrefix(path, admin+"/runs/"), "/")
		if len(parts) == 1 && r.Method == http.MethodGet {
			return s.get(actor, tenant, parts[0], team)
		}
		if len(parts) == 2 && parts[1] == "stop" && r.Method == http.MethodPost {
			if team {
				return nil, fault(403, "只有管理员可以停止运行。")
			}
			return s.stop(actor, tenant, parts[0])
		}
		if len(parts) == 2 && parts[1] == "discard" && r.Method == http.MethodPost {
			return s.discard(actor, tenant, parts[0])
		}
		if len(parts) == 2 && parts[1] == "review" && r.Method == http.MethodPost {
			if team {
				return nil, fault(403, "只有管理员可以处理运行中的审核。")
			}
			var request ReviewRequest
			if err := readJSON(w, r, &request); err != nil {
				return nil, err
			}
			return s.review(actor, tenant, parts[0], request)
		}
		if len(parts) == 2 && parts[1] == "logs" && r.Method == http.MethodGet {
			after, limit := int64(0), 200
			var err error
			if raw := r.URL.Query().Get("after"); raw != "" {
				after, err = strconv.ParseInt(raw, 10, 64)
				if err != nil {
					return nil, fault(400, "after 游标无效。")
				}
			}
			if raw := r.URL.Query().Get("limit"); raw != "" {
				limit, err = strconv.Atoi(raw)
				if err != nil {
					return nil, fault(400, "limit 无效。")
				}
			}
			return s.logPage(actor, tenant, parts[0], after, limit, team)
		}
	case strings.HasPrefix(path, agent+"/runs/") && r.Method == http.MethodPost:
		parts := strings.Split(strings.TrimPrefix(path, agent+"/runs/"), "/")
		if len(parts) == 2 && parts[1] == "report" {
			var request ReportRequest
			if err := readJSON(w, r, &request); err != nil {
				return nil, err
			}
			return s.report(parts[0], request)
		}
		if len(parts) == 2 && parts[1] == "logs" {
			var request LogsRequest
			if err := readJSON(w, r, &request); err != nil {
				return nil, err
			}
			return s.appendLogs(parts[0], request)
		}
		if len(parts) == 3 && parts[1] == "controls" {
			var report ControlReport
			if err := readJSON(w, r, &report); err != nil {
				return nil, err
			}
			return s.reportControl(parts[0], parts[2], report)
		}
	}
	return nil, fault(404, "接口不存在。")
}
