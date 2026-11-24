package automation

import (
	"encoding/json"
	"slices"
	"strings"
	"time"
)

func poolChildHoldsMachine(run RemoteRun) bool {
	return run.PullFromPool && run.AcceptedAt != nil &&
		(run.ArchiveSHA256 == "" || !run.LogsComplete || !terminal(run.State) || run.State == "unknown")
}

func poolChildExpired(run RemoteRun, now time.Time) bool {
	if !run.PullFromPool || run.AcceptedAt != nil || terminal(run.State) {
		return false
	}
	var header subgraphHeader
	return json.Unmarshal(run.Subgraph, &header) == nil && header.ResourceAdmission != nil &&
		header.ResourceAdmission.QueueDeadlineUTC != nil && !now.Before(*header.ResourceAdmission.QueueDeadlineUTC)
}

// The caller holds store.mu. The poll commits ownership and the delivery in the same state write.
func (s *service) poolClaim(tenant tenantConfig, pool poolConfig, machine Machine) (RemoteRun, error) {
	if !pool.PullSubgraphs || !slices.ContainsFunc(pool.MachineNames, func(name string) bool {
		return strings.EqualFold(name, machine.MachineName)
	}) || !slices.Contains(machine.Capabilities, subgraphCapability) || !slices.Contains(machine.Capabilities, mutexCapability) {
		return RemoteRun{}, nil
	}
	state := s.store.states[tenant.ID]
	var next RemoteRun
	for _, run := range state.Runs {
		if strings.EqualFold(run.MachineName, machine.MachineName) && poolChildHoldsMachine(run) {
			return RemoteRun{}, nil
		}
		if !run.PullFromPool || run.PoolID != pool.ID || run.AcceptedAt != nil || terminal(run.State) {
			continue
		}
		if next.ID != "" && (run.CreatedAt.After(next.CreatedAt) || run.CreatedAt.Equal(next.CreatedAt) && run.ID > next.ID) {
			continue
		}
		var header subgraphHeader
		if err := json.Unmarshal(run.Subgraph, &header); err != nil {
			return RemoteRun{}, err
		}
		if header.ResourceAdmission != nil && header.ResourceAdmission.QueueDeadlineUTC != nil &&
			!machine.LastSeen.Before(*header.ResourceAdmission.QueueDeadlineUTC) {
			continue
		}
		task := executionTask(machine, runConfiguration(run), true)
		if task == nil {
			task = executionTask(machine, run.TaskID, true)
		}
		if task == nil || task.GraphFingerprint != header.RootGraphFingerprint {
			continue
		}
		run.TaskID, run.TaskName = task.ID, task.Name
		run.ConfigurationID, run.ScriptRevision = taskConfiguration(*task), task.ScriptRevision
		next = run
	}
	if next.ID == "" {
		return RemoteRun{}, nil
	}
	// The caller's hash keeps the pool target. The worker receives its actual machine name
	// so the existing native target check and resource ownership continue to apply.
	var invocation map[string]json.RawMessage
	if err := json.Unmarshal(next.Subgraph, &invocation); err != nil {
		return RemoteRun{}, err
	}
	invocation["target"], _ = json.Marshal(machine.MachineName)
	subgraph, err := json.Marshal(invocation)
	if err != nil {
		return RemoteRun{}, err
	}
	next.Subgraph = subgraph
	next.MachineName = machine.MachineName
	next.AcceptedAt = &machine.LastSeen
	next.UpdatedAt = machine.LastSeen
	next.Revision++
	next.State = "accepted"
	next.Detail = "评测机已接单，等待本地执行记录"
	return next, nil
}

// A direct parent submits this receipt after verifying a durable archive and worker cleanup.
func (s *service) releasePoolChild(id string, request ChildAccessRequest) (RemoteRun, error) {
	return s.markPoolCleanup(id, request, false)
}

func (s *service) markPoolCleanup(id string, request ChildAccessRequest, worker bool) (RemoteRun, error) {
	archive, err := poolArchiveHash(request.ArchiveSHA256)
	if err != nil {
		return RemoteRun{}, err
	}
	s.store.mu.Lock()
	defer s.store.mu.Unlock()
	assigned, run, err := s.poolArchiveRun(id, request, worker)
	if err != nil {
		return RemoteRun{}, err
	}
	if run.ArchiveSHA256 != "" {
		if run.ArchiveSHA256 != archive {
			return RemoteRun{}, fault(409, "运行已经收到另一份归档回执。")
		}
		return run, nil
	}
	if !poolChildHoldsMachine(run) || (!worker && (!terminal(run.State) || run.State == "unknown" || !run.LogsComplete)) {
		return RemoteRun{}, fault(409, "运行尚未领单，或父图尝试在收到终态日志前释放机器。")
	}
	if run.ReceivedArchive == nil || run.ReceivedArchive.SHA256 != archive {
		return RemoteRun{}, fault(409, "服务器尚未校验并持久化这份归档。")
	}
	if err := s.store.checkPoolArchive(run); err != nil {
		return RemoteRun{}, err
	}
	err = s.store.change(assigned.TenantID, func(state *tenantState) error {
		run.ArchiveSHA256 = archive
		run.UpdatedAt = time.Now().UTC()
		run.Revision++
		state.Runs[run.ID] = run
		return nil
	})
	return run, err
}
