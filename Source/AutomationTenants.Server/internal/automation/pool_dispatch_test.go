package automation

import (
	"archive/zip"
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"net/http/httptest"
	"os"
	"strings"
	"testing"
	"time"
)

// Anchor: Docs/automation-tenants-server.md#pool-subgraph-delivery
func TestPoolDeploymentRequiresGraphFingerprint(t *testing.T) {
	s := mockService(t)
	tenant := s.config.tenantByID["orchard"]
	tenant.PoolPackageDirectory = t.TempDir()
	s.config.tenantByID[tenant.ID] = tenant
	poll := PollRequest{MachineName: "orchard-box", Capabilities: []string{deploymentCapability}}
	if _, err := s.poll(poll); err != nil {
		t.Fatal(err)
	}
	for _, test := range []struct{ id, fingerprint, state string }{
		{"missing", "", "failed"}, {"reported", "compiled", "ready"},
	} {
		var job Deployment
		if err := s.store.change(tenant.ID, func(state *tenantState) error {
			var err error
			job, err = s.putDeployment(state, tenant, "operator", DeployRequest{
				MachineName: poll.MachineName, TaskID: "job", RequestID: test.id, RegisterGraph: true,
			}, DeploymentSource{SourceID: "Q1"})
			return err
		}); err != nil {
			t.Fatal(err)
		}
		mail, err := s.poll(poll)
		if err != nil || len(mail.Deployments) != 1 || !mail.Deployments[0].RegisterGraph {
			t.Fatalf("deployment did not request a registered graph: %+v, %v", mail.Deployments, err)
		}
		receipt, err := s.reportDeployment(job.ID, DeploymentReport{
			MachineName: poll.MachineName, State: "ready", Source: job.Source,
			Task: &Task{ID: "job", GraphFingerprint: test.fingerprint, CanRun: true},
		})
		if err != nil || receipt.State != test.state {
			t.Fatalf("fingerprint %q gave state %q, want %q: %v", test.fingerprint, receipt.State, test.state, err)
		}
	}
}

// Anchor: Docs/automation-tenants-server.md#pool-subgraph-delivery
func TestPoolChildBindingAndReleaseSurviveRetryAndRestart(t *testing.T) {
	s := mockService(t)
	tenant := s.config.tenantByID["orchard"]
	tenant.ServerMachine = "orchard-box"
	tenant.ResourceServerURL = "http://127.0.0.1:5184"
	tenant.Pools = append(tenant.Pools, poolConfig{
		Pool: Pool{ID: "workers"}, PullSubgraphs: true,
		MachineNames: []string{"worker-a", "worker-b", "worker-old"},
	})
	s.config.tenantByID[tenant.ID] = tenant
	for _, name := range append(tenant.Pools[1].MachineNames, "unlisted") {
		s.config.assignments[name] = assignment{tenant.ID, "workers"}
	}
	parentID := seedRun(t, s, tenant.ID)
	if err := s.store.change(tenant.ID, func(state *tenantState) error {
		parent := state.Runs[parentID]
		parent.LocalRunID = "parent"
		state.Runs[parentID] = parent
		state.Machines[tenant.ServerMachine] = Machine{
			TenantID: tenant.ID, PoolID: "pool", MachineName: tenant.ServerMachine, Capabilities: []string{mutexCapability},
		}
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	owner := ChildAccessRequest{MachineName: tenant.ServerMachine, ParentLocalRunID: "parent"}
	create := func(child string, deadline *time.Time) ChildRequest {
		t.Helper()
		payload, err := json.Marshal(map[string]any{
			"parent_run_id": "parent", "node_id": "node", "invocation_id": child, "child_run_id": child,
			"root_graph_fingerprint": "root", "graph_fingerprint": "child", "mutation_path": []string{"child"},
			"input": map[string]int{"value": 1}, "target": "pool:workers",
			"resource_admission": map[string]any{"queue_deadline_utc": deadline},
		})
		if err != nil {
			t.Fatal(err)
		}
		return ChildRequest{MachineName: owner.MachineName, ParentLocalRunID: owner.ParentLocalRunID,
			TargetMachineName: "pool:workers", TaskID: "job", Subgraph: payload}
	}
	createQueued := func(request ChildRequest) RemoteRun {
		t.Helper()
		run, err := s.createChild(request)
		if err != nil || run.State != "queued" || run.MachineName != "" {
			t.Fatalf("queue = %+v, %v", run, err)
		}
		return run
	}
	poll := func(name, fingerprint string) (PollReply, error) {
		return s.poll(PollRequest{MachineName: name, HostVersion: "mock",
			Capabilities: []string{mutexCapability, subgraphCapability},
			Tasks:        []Task{{ID: "job", Name: "Mock job", CanRun: true, GraphFingerprint: fingerprint}},
		})
	}
	assertPoll := func(name, fingerprint, id string) {
		t.Helper()
		reply, err := poll(name, fingerprint)
		if err != nil {
			t.Fatal(err)
		}
		if id == "" {
			if len(reply.Requests) != 0 {
				t.Fatalf("%s received an unexpected delivery: %+v", name, reply.Requests)
			}
			return
		}
		if len(reply.Requests) != 1 || reply.Requests[0].ID != id {
			t.Fatalf("%s delivery = %+v, want %s", name, reply.Requests, id)
		}
		var header subgraphHeader
		if err := json.Unmarshal(reply.Requests[0].Subgraph, &header); err != nil || header.Target != name {
			t.Fatalf("bound target = %s, %v", header.Target, err)
		}
	}

	firstRequest := create("first", nil)
	first := createQueued(firstRequest)
	assertPoll("worker-old", "old-root", "")
	assertPoll("unlisted", "root", "")
	claims := make(chan string, 2)
	for _, worker := range []string{"worker-a", "worker-b"} {
		go func() {
			reply, err := poll(worker, "root")
			if err != nil || len(reply.Requests) > 1 || len(reply.Requests) == 1 && reply.Requests[0].ID != first.ID {
				t.Errorf("%s claim = %+v, %v", worker, reply.Requests, err)
			}
			if len(reply.Requests) == 1 {
				claims <- worker
			} else {
				claims <- ""
			}
		}()
	}
	worker, other := <-claims, <-claims
	if worker == "" {
		worker, other = other, worker
	}
	if worker == "" || other != "" {
		t.Fatalf("expected exactly one claim, got %q and %q", worker, other)
	}
	swapped := "worker-b"
	if worker == swapped {
		swapped = "worker-a"
	}
	assertPoll(worker, "root", first.ID)
	retry, err := s.createChild(firstRequest)
	if err != nil || retry.ID != first.ID || retry.MachineName != worker {
		t.Fatalf("repeat create = %+v, %v", retry, err)
	}

	second := createQueued(create("second", nil))
	assertPoll(worker, "root", first.ID)
	assertPoll(swapped, "root", second.ID)
	third := createQueued(create("third", nil))
	var archive bytes.Buffer
	writer := zip.NewWriter(&archive)
	entry, err := writer.Create("sessions/session.jsonl")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := entry.Write([]byte("{\"type\":\"mock\"}\n")); err != nil {
		t.Fatal(err)
	}
	if err := writer.Close(); err != nil {
		t.Fatal(err)
	}
	sum := sha256.Sum256(archive.Bytes())
	owner.ArchiveSHA256 = hex.EncodeToString(sum[:])
	if _, err := s.releasePoolChild(first.ID, owner); err == nil {
		t.Fatal("released a running child")
	}
	if _, err := s.report(first.ID, ReportRequest{MachineName: worker, State: "failed", LogsComplete: true}); err != nil {
		t.Fatal(err)
	}
	assertPoll(worker, "root", "")
	if _, err := s.releasePoolChild(first.ID, owner); err == nil {
		t.Fatal("released a terminal child before storing the archive")
	}
	upload := func(content []byte) *httptest.ResponseRecorder {
		req := httptest.NewRequest("POST", "/internal/automation/children/"+first.ID+"/archive", bytes.NewReader(content))
		req.Header.Set("X-Pregel-Parent-Machine", owner.MachineName)
		req.Header.Set("X-Pregel-Parent-Run", owner.ParentLocalRunID)
		req.Header.Set("X-Pregel-Archive-Sha256", owner.ArchiveSHA256)
		reply := httptest.NewRecorder()
		s.serve(reply, req)
		return reply
	}
	corrupt := bytes.Clone(archive.Bytes())
	corrupt[0] ^= 1
	if reply := upload(corrupt); reply.Code != 409 {
		t.Fatalf("corrupted archive: %d %s", reply.Code, reply.Body)
	}
	if reply := upload(archive.Bytes()); reply.Code != 200 {
		t.Fatalf("archive receive: %d %s", reply.Code, reply.Body)
	}
	assertPoll(worker, "root", "")

	if err := s.store.lock.Close(); err != nil {
		t.Fatal(err)
	}
	reopened, err := openStore(s.config)
	if err != nil {
		t.Fatal(err)
	}
	s.store = reopened
	assertPoll(worker, "root", "")
	if reply := upload(archive.Bytes()); reply.Code != 200 {
		t.Fatalf("retry upload: %d %s", reply.Code, reply.Body)
	}
	if err := os.Remove(s.store.poolArchivePath(first, owner.ArchiveSHA256)); err != nil {
		t.Fatal(err)
	}
	if _, err := s.releasePoolChild(first.ID, owner); err == nil {
		t.Fatal("released an attempt whose received archive is missing")
	}
	if reply := upload(archive.Bytes()); reply.Code != 200 {
		t.Fatalf("recover missing archive: %d %s", reply.Code, reply.Body)
	}
	wrongOwner := owner
	wrongOwner.MachineName = swapped
	if _, err := s.releasePoolChild(first.ID, wrongOwner); err == nil {
		t.Fatal("released another parent's child")
	}
	body, err := json.Marshal(owner)
	if err != nil {
		t.Fatal(err)
	}
	release := func(prefix string) *httptest.ResponseRecorder {
		reply := httptest.NewRecorder()
		s.serve(reply, httptest.NewRequest("POST", prefix+first.ID+"/release", bytes.NewReader(body)))
		return reply
	}
	if reply := release("/api/agent/automation/children/"); reply.Code != 404 {
		t.Fatalf("release exposed through the agent entrance: %d", reply.Code)
	}
	reply := release("/internal/automation/children/")
	var released RemoteRun
	if err := json.Unmarshal(reply.Body.Bytes(), &released); reply.Code != 200 || err != nil || released.ArchiveSHA256 != owner.ArchiveSHA256 {
		t.Fatalf("release = %d, %+v, %v", reply.Code, released, err)
	}
	repeated, err := s.releasePoolChild(first.ID, owner)
	if err != nil || repeated.Revision != released.Revision {
		t.Fatalf("duplicate release = %+v, %v", repeated, err)
	}
	conflicting := owner
	conflicting.ArchiveSHA256 = strings.Repeat("e", 64)
	if _, err := s.releasePoolChild(first.ID, conflicting); err == nil {
		t.Fatal("an existing receipt was overwritten")
	}
	assertPoll(worker, "root", third.ID)

	past := time.Now().Add(-time.Minute)
	expired := createQueued(create("expired", &past))
	assertPoll("worker-old", "root", "")
	result, err := s.getChild(expired.ID, owner)
	if err != nil || result.State != "failed" || result.MachineName != "" || result.CancellationFailure == nil {
		t.Fatalf("expired child = %+v, %v", result, err)
	}
}
