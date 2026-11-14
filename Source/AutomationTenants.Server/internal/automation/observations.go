package automation

import (
	"bytes"
	"crypto/rand"
	"encoding/hex"
	"strings"
	"time"
)

func (s *service) observe(request ObservationRequest) (ObservationReceipt, error) {
	name, err := machineName(request.MachineName)
	if err != nil {
		return ObservationReceipt{}, err
	}
	assigned, err := s.config.assign(name)
	if err != nil {
		return ObservationReceipt{}, err
	}
	header, present, err := snapshotInfo(request.Snapshot)
	if err != nil {
		return ObservationReceipt{}, err
	}
	configuration := request.ConfigurationID
	if configuration == "" {
		configuration = request.TaskID
	}
	if !present || header.RunID == "" || !clean(header.RunID, 128) || request.TaskID == "" ||
		!clean(request.TaskID, 128) || !clean(configuration, 128) || header.TaskID != configuration || !clean(request.TaskName, 512) || !clean(request.LocalNickname, 512) || !clean(request.ScriptRevision, 1024) {
		return ObservationReceipt{}, fault(400, "需要对应本机任务的完整运行快照。")
	}
	values := map[string]string{}
	if header.TaskSnapshot != nil {
		values = parameters(header.TaskSnapshot.Parameters)
		if err := validateParameters(values); err != nil {
			return ObservationReceipt{}, err
		}
	}

	s.store.mu.Lock()
	tenant := s.config.tenantByID[assigned.TenantID]
	definitionID := ""
	if header.TaskSnapshot != nil {
		definitionID = header.TaskSnapshot.TemplateID
	}
	for _, task := range s.store.states[assigned.TenantID].Machines[strings.ToLower(name)].Tasks {
		if taskConfiguration(task) == configuration && task.DefinitionID != "" {
			definitionID = task.DefinitionID
			break
		}
	}
	definition, known := tenant.taskDefinition(configuration, definitionID)
	if request.ConfigurationID != "" {
		registered, exists := tenant.taskDefinition(request.TaskID, "")
		if !exists || registered.ID != request.TaskID || known && definition.ID != registered.ID {
			known = false
		} else {
			// New Agents explicitly bind a local configuration to a registered
			// workflow. Its first run can arrive before the next catalog poll.
			definition, known = registered, true
			for _, task := range tenant.registeredTasks() {
				if containsID(task.ExcludedConfigurations, configuration) {
					known = false
					break
				}
			}
		}
	}
	if !known {
		// A native configuration is not a workflow registration.
		s.store.mu.Unlock()
		return ObservationReceipt{ID: ignoredObservationID(name, header.RunID)}, nil
	}
	var id string
	err = s.store.change(assigned.TenantID, func(state *tenantState) error {
		machine, exists := state.Machines[strings.ToLower(name)]
		if !exists {
			return fault(404, "机器尚未上报任务目录。")
		}
		// A cloud invocation is still one execution when it is also observed locally.
		if header.RemoteRequestID != "" {
			run, err := requireRun(state, header.RemoteRequestID)
			if err != nil {
				return err
			}
			if !strings.EqualFold(run.MachineName, name) || run.PoolID != assigned.PoolID || runConfiguration(run) != configuration {
				return fault(409, "本地运行与云端下发记录不一致。")
			}
			id = run.ID
			return nil
		}
		for _, run := range state.Runs {
			if strings.EqualFold(run.MachineName, name) && run.LocalRunID == header.RunID {
				if run.PoolID != assigned.PoolID || runConfiguration(run) != configuration {
					return fault(409, "本地运行已归属其他任务或机器池。")
				}
				id = run.ID
				return nil
			}
		}
		key := make([]byte, 16)
		if _, err := rand.Read(key); err != nil {
			return err
		}
		id = hex.EncodeToString(key)
		now := time.Now().UTC()
		created := header.QueuedAt
		if created.IsZero() {
			created = now
		}
		title := definition.Name
		if title == "" {
			title = request.TaskID
		}
		state.Runs[id] = RemoteRun{ID: id, TenantID: assigned.TenantID, PoolID: assigned.PoolID,
			MachineName: machine.MachineName, TaskID: definition.ID, ConfigurationID: configuration, TaskName: title, LocalNickname: request.LocalNickname, Parameters: values,
			ScriptRevision: request.ScriptRevision, Source: "local", State: header.Status, LocalRunID: header.RunID,
			Snapshot: bytes.Clone(request.Snapshot), Revision: 1, Detail: header.Summary, CreatedAt: created, UpdatedAt: now}
		return nil
	})
	s.store.mu.Unlock()
	if err != nil {
		return ObservationReceipt{}, err
	}
	// Reuse the same native revision and terminal-state handling as dispatch reports.
	_, err = s.report(id, ReportRequest{MachineName: name, LocalRunID: header.RunID, Snapshot: request.Snapshot})
	return ObservationReceipt{ID: id}, err
}
