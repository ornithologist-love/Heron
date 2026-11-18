package automation

import (
	"bytes"
	"context"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
	"time"
)

func mockService(t *testing.T) *service {
	t.Helper()
	root := t.TempDir()
	c := config{Directory: filepath.Join(root, "state"), DefaultTenant: "orchard", Protocol: mockProtocol()}
	for _, id := range []string{"orchard", "meadow"} {
		c.Tenants = append(c.Tenants, tenantConfig{ID: id, Admins: []string{"operator"},
			Tasks: []TaskDefinition{{ID: "job", Name: "Mock job"}},
			Pools: []poolConfig{{Pool: Pool{ID: "pool"}, MachineNames: []string{id + "-box"}}}})
	}
	data, err := json.Marshal(c)
	if err != nil {
		t.Fatal(err)
	}
	filename := filepath.Join(root, "config.json")
	if err := os.WriteFile(filename, data, 0600); err != nil {
		t.Fatal(err)
	}
	c, err = loadConfig(filename)
	if err != nil {
		t.Fatal(err)
	}
	store, err := openStore(c)
	if err != nil {
		t.Fatal(err)
	}
	s := &service{config: c, store: store}
	t.Cleanup(func() { s.store.lock.Close() })
	return s
}

func mockProtocol() protocolConfig {
	return protocolConfig{WebhookHeaderPrefix: "X-Orchard", MCPRealm: "orchard-mcp", LegacyPublicationOwner: "orchard",
		ActorHeader: "X-Orchard-Actor", AccessHeader: "X-Orchard-Access", MCPServerName: "Orchard Automation"}
}

func seedRun(t *testing.T, s *service, tenant string) string {
	t.Helper()
	id := strings.Repeat("a", 32)
	err := s.store.change(tenant, func(state *tenantState) error {
		state.Runs[id] = RemoteRun{ID: id, TenantID: tenant, PoolID: "pool", MachineName: tenant + "-box", TaskID: "job", Source: "local", State: "running"}
		return nil
	})
	if err != nil {
		t.Fatal(err)
	}
	return id
}

// Anchor: Docs/automation-tenants-server.md#tenant-boundary
func TestAgentCannotSelectAnotherTenantOrChangeItsRun(t *testing.T) {
	s := mockService(t)
	request := httptest.NewRequest("POST", "/api/agent/automation/poll", strings.NewReader(`{"machine_name":"orchard-box","tenant_id":"meadow","tasks":[]}`))
	reply := httptest.NewRecorder()
	s.serve(reply, request)
	var poll PollReply
	if reply.Code != 200 || json.Unmarshal(reply.Body.Bytes(), &poll) != nil || poll.TenantID != "orchard" {
		t.Fatalf("machine assignment was not authoritative: %d %s", reply.Code, reply.Body)
	}
	id := seedRun(t, s, "meadow")
	before, _ := os.ReadFile(s.store.stateFile("meadow"))
	_, err := s.appendLogs(id, LogsRequest{MachineName: "orchard-box", Stream: "events", Lines: []string{`{"message":"foreign"}`}})
	if err == nil {
		t.Fatal("cross-tenant Agent log write accepted")
	}
	_, err = s.report(id, ReportRequest{MachineName: "orchard-box"})
	if err == nil {
		t.Fatal("cross-tenant Agent report accepted")
	}
	after, _ := os.ReadFile(s.store.stateFile("meadow"))
	if !bytes.Equal(before, after) {
		t.Fatal("another tenant's state was modified")
	}
}

// Anchor: Docs/automation-tenants-server.md#durability
func TestLogAcknowledgementsUseUTF8BytesAndReplayAcrossRestart(t *testing.T) {
	s := mockService(t)
	id := seedRun(t, s, "orchard")
	lines := []string{"{\"message\":\"梨🍐\"}\r", "{\"message\":\"plum\"}"}
	request := LogsRequest{MachineName: "orchard-box", Stream: "events", Lines: lines}
	want := int64(len(lines[0]) + len(lines[1]) + 2)
	for range 2 {
		receipt, err := s.appendLogs(id, request)
		if err != nil || receipt.NextOffset != want {
			t.Fatalf("byte receipt: %+v %v", receipt, err)
		}
	}
	if err := s.store.lock.Close(); err != nil {
		t.Fatal(err)
	}
	reopened, err := openStore(s.config)
	if err != nil {
		t.Fatal(err)
	}
	s.store = reopened
	receipt, err := s.appendLogs(id, request)
	if err != nil || receipt.NextOffset != want {
		t.Fatalf("restarted receipt: %+v %v", receipt, err)
	}
	third := `{"message":"peach"}`
	request.Offset = int64(len(lines[0]) + 1)
	request.Lines = []string{lines[1], third}
	receipt, err = s.appendLogs(id, request)
	want += int64(len(third) + 1)
	if err != nil || receipt.NextOffset != want {
		t.Fatalf("overlap receipt: %+v %v", receipt, err)
	}
	journal, err := os.ReadFile(s.store.logFile("orchard", id))
	if err != nil || bytes.Count(journal, []byte{'\n'}) != 3 {
		t.Fatal("replayed log was duplicated or lost")
	}
	for _, offset := range []int64{want - 1, want + 1} {
		request.Offset = offset
		request.Lines = []string{third}
		receipt, err = s.appendLogs(id, request)
		if err == nil || receipt.NextOffset != want {
			t.Fatal("invalid boundary changed acknowledged bytes")
		}
	}
}

// Anchor: Docs/automation-tenants-server.md#durability
func TestOneWriterOwnsAStateRootUntilItsLockCloses(t *testing.T) {
	s := mockService(t)
	second, err := openStore(s.config)
	if err == nil {
		second.lock.Close()
		t.Fatal("second writer opened the same state root")
	}
	if err := s.store.lock.Close(); err != nil {
		t.Fatal(err)
	}
	second, err = openStore(s.config)
	if err != nil {
		t.Fatal(err)
	}
	s.store = second
}

type transportFunc func(*http.Request) (*http.Response, error)

func (f transportFunc) RoundTrip(r *http.Request) (*http.Response, error) { return f(r) }

// Anchor: Docs/automation-tenants-server.md#webhook-identity
func TestWebhookSignsTheExactTimestampAndBodyWithConfiguredHeaderNames(t *testing.T) {
	for _, prefix := range []string{"X-Orchard", "X-Meadow"} {
		t.Run(prefix, func(t *testing.T) {
			secret := strings.Repeat("s", 32)
			start := time.Now().Unix()
			client := &http.Client{Transport: transportFunc(func(r *http.Request) (*http.Response, error) {
				body, err := io.ReadAll(r.Body)
				if err != nil {
					t.Fatal(err)
				}
				stamp := r.Header.Get(prefix + "-Timestamp")
				now, err := strconv.ParseInt(stamp, 10, 64)
				if err != nil || now < start || now > time.Now().Unix() {
					t.Fatal("invalid event timestamp")
				}
				mac := hmac.New(sha256.New, []byte(secret))
				mac.Write([]byte(stamp + "."))
				mac.Write(body)
				if r.Header.Get(prefix+"-Signature") != "sha256="+hex.EncodeToString(mac.Sum(nil)) || r.Header.Get(prefix+"-Event-Id") != "mock-event" {
					t.Fatal("webhook identity or HMAC does not cover exact request bytes")
				}
				if !json.Valid(body) || r.Header.Get("Content-Type") != "application/json; charset=utf-8" {
					t.Fatal("invalid webhook body")
				}
				return &http.Response{StatusCode: 204, Body: io.NopCloser(strings.NewReader(""))}, nil
			})}
			_, err := postEvent(context.Background(), client, eventHookConfig{URL: "http://127.0.0.1/mock", Secret: secret}, RunEvent{ID: "mock-event"}, prefix)
			if err != nil {
				t.Fatal(err)
			}
		})
	}
}

// Anchor: Docs/automation-tenants-server.md#webhook-identity
func TestProtocolIdentitiesAreRequiredAndRejectHeaderInjection(t *testing.T) {
	if p := mockProtocol(); p.configure() != nil {
		t.Fatal("complete protocol identity rejected")
	}
	for _, missing := range []func(*protocolConfig){
		func(p *protocolConfig) { p.WebhookHeaderPrefix = "" },
		func(p *protocolConfig) { p.MCPRealm = "" },
		func(p *protocolConfig) { p.LegacyPublicationOwner = "" },
		func(p *protocolConfig) { p.ActorHeader = "" },
		func(p *protocolConfig) { p.AccessHeader = "" },
		func(p *protocolConfig) { p.MCPServerName = "" },
	} {
		p := mockProtocol()
		missing(&p)
		if p.configure() == nil {
			t.Fatal("missing protocol identity was replaced by a built-in name")
		}
	}
	for _, bad := range []func(*protocolConfig){
		func(p *protocolConfig) { p.WebhookHeaderPrefix = "X-Test\r\nInjected" },
		func(p *protocolConfig) { p.MCPRealm = "realm\" forged" },
	} {
		p := mockProtocol()
		bad(&p)
		if p.configure() == nil {
			t.Fatal("invalid protocol header accepted")
		}
	}
}

// Anchor: Docs/automation-tenants-server.md#publication-ledger
func TestOnlyPublishedEntriesRegisterTasksAndTheLedgerIsReadOnly(t *testing.T) {
	s := mockService(t)
	path := filepath.Join(t.TempDir(), "ledger.json")
	body := []byte(`{"schema":1,"published":{"accepted":{"tenant_id":"orchard","task_id":"approved","title":"Mock approved","definition_id":"mock-graph"},"foreign":{"tenant_id":"meadow","task_id":"foreign","title":"Other tenant"}},"submissions":{"pending":{"tenant_id":"orchard","task_id":"pending","title":"Unreviewed"}}}`)
	if err := os.WriteFile(path, body, 0400); err != nil {
		t.Fatal(err)
	}
	tenant := s.config.tenantByID["orchard"]
	tenant.ApprovedWorkflowsPath = path
	tasks := tenant.registeredTasks()
	if len(tasks) != 2 || tasks[1].ID != "approved" {
		t.Fatalf("unpublished or foreign task registered: %+v", tasks)
	}
	after, err := os.ReadFile(path)
	if err != nil || !bytes.Equal(body, after) {
		t.Fatal("reader modified the publication ledger")
	}
}

// Anchor: Docs/automation-tenants-server.md#tenant-boundary
func TestServiceListensOnlyOnLoopback(t *testing.T) {
	for listen, allowed := range map[string]bool{
		"127.0.0.1:5183": true, "[::1]:5183": true, "localhost:5183": true,
		"0.0.0.0:5183": false, "[::]:5183": false, "192.0.2.10:5183": false, ":5183": false,
	} {
		c := mockService(t).config
		c.Listen = listen
		data, err := json.Marshal(c)
		if err != nil {
			t.Fatal(err)
		}
		filename := filepath.Join(t.TempDir(), "config.json")
		if err := os.WriteFile(filename, data, 0600); err != nil {
			t.Fatal(err)
		}
		if _, err := loadConfig(filename); (err == nil) != allowed {
			t.Fatalf("listen %q: allowed=%v err=%v", listen, allowed, err)
		}
	}
}

// Anchor: Docs/automation-tenants-server.md#durability
func TestRecoveryDiscardsOnlyAnIncompleteJournalTail(t *testing.T) {
	s := mockService(t)
	id := seedRun(t, s, "orchard")
	request := LogsRequest{MachineName: "orchard-box", Stream: "events", Lines: []string{`{"message":"plum"}`}}
	first, err := s.appendLogs(id, request)
	if err != nil {
		t.Fatal(err)
	}
	journal := s.store.logFile("orchard", id)
	committed, err := os.ReadFile(journal)
	if err != nil {
		t.Fatal(err)
	}
	reopen := func(tail string) error {
		if err := s.store.lock.Close(); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(journal, append(append([]byte{}, committed...), tail...), 0600); err != nil {
			t.Fatal(err)
		}
		reopened, err := openStore(s.config)
		if err != nil {
			t.Fatal(err)
		}
		s.store = reopened
		_, err = s.appendLogs(id, request)
		return err
	}

	if err := reopen(`{"sequence":2,"str`); err != nil {
		t.Fatalf("an incomplete final record must be discarded, got %v", err)
	}
	recovered, err := os.ReadFile(journal)
	if err != nil || !bytes.Equal(recovered, committed) {
		t.Fatalf("recovery kept a partial record or lost committed bytes (offset %d)", first.NextOffset)
	}
	if err := reopen("not a journal record\n"); err == nil {
		t.Fatal("an invalid complete record must be an error, not silently skipped")
	}
}

// Anchor: Docs/automation-tenants-server.md#tenant-script-source
func TestEachTenantDeploysOnlyFromItsOwnPublishedLibrary(t *testing.T) {
	feed := func(owner string) *httptest.Server {
		body := `{"shared":{"id":"Shared","revision":"s1","sha256":"` + strings.Repeat("b", 64) + `"},` +
			`"scripts":[{"id":"Q1","taskId":"job","tenantId":"` + owner + `","revision":"r1","sha256":"` + strings.Repeat("a", 64) + `"}]}`
		return httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) { io.WriteString(w, body) }))
	}
	official, eval := feed("orchard"), feed("meadow")
	defer official.Close()
	defer eval.Close()
	root := t.TempDir()
	c := config{Directory: filepath.Join(root, "state"), DefaultTenant: "orchard", Protocol: mockProtocol(), ScriptSourceURL: official.URL}
	for _, id := range []string{"orchard", "meadow"} {
		tenant := tenantConfig{ID: id, Admins: []string{"operator"}, Tasks: []TaskDefinition{{ID: "job", Name: "Mock job"}},
			Pools: []poolConfig{{Pool: Pool{ID: "pool"}, MachineNames: []string{id + "-box"}}}}
		if id == "meadow" {
			tenant.ScriptSourceURL = eval.URL
		}
		c.Tenants = append(c.Tenants, tenant)
	}
	data, _ := json.Marshal(c)
	filename := filepath.Join(root, "config.json")
	if err := os.WriteFile(filename, data, 0600); err != nil {
		t.Fatal(err)
	}
	c, err := loadConfig(filename)
	if err != nil {
		t.Fatal(err)
	}
	s := &service{config: c}
	for _, id := range []string{"orchard", "meadow"} {
		source, err := s.deploymentSource(context.Background(), c.tenantByID[id], "job")
		if err != nil || source.SourceID != "Q1" {
			t.Fatalf("%s did not deploy from its own library: %+v %v", id, source, err)
		}
	}
	// A library owned by another tenant never resolves, even if a tenant is pointed at it.
	wrong := c.tenantByID["orchard"]
	wrong.ScriptSourceURL = eval.URL
	if _, err := s.deploymentSource(context.Background(), wrong, "job"); err == nil {
		t.Fatal("deployed a package owned by another tenant")
	}
	c.Tenants[0].ScriptSourceURL = "http://user:secret@host/catalog.json"
	data, _ = json.Marshal(c)
	if err := os.WriteFile(filename, data, 0600); err != nil {
		t.Fatal(err)
	}
	if _, err := loadConfig(filename); err == nil {
		t.Fatal("accepted a tenant script_source_url with credentials")
	}
}
