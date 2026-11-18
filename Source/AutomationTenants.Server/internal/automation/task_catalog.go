package automation

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"path/filepath"
	"sort"
	"strings"
	"time"
)

// IDs belong to registered workflows. Aliases only migrate historical configuration
// IDs; a machine's nickname, parameters or graph fingerprint never register a task.
type TaskDefinition struct {
	ID                     string   `json:"id"`
	Name                   string   `json:"name"`
	Aliases                []string `json:"aliases,omitempty"`
	DefinitionIDs          []string `json:"definition_ids,omitempty"`
	ExcludedConfigurations []string `json:"excluded_configurations,omitempty"`
}

type CatalogTask struct {
	TenantID string `json:"tenant_id"`
	ID       string `json:"id"`
	Name     string `json:"name"`
}

func validateTaskDefinitions(tasks []TaskDefinition) error {
	ids, definitions := map[string]string{}, map[string]string{}
	for _, task := range tasks {
		if task.ID == "" || !clean(task.ID, 128) || strings.TrimSpace(task.Name) == "" || !clean(task.Name, 512) {
			return errors.New("任务目录需要稳定的 id 和云端名称")
		}
		for _, id := range append([]string{task.ID}, task.Aliases...) {
			if id == "" || !clean(id, 128) || ids[id] != "" {
				return errors.New("任务目录的 id 或迁移别名重复、无效")
			}
			ids[id] = task.ID
		}
		for _, id := range task.DefinitionIDs {
			if id == "" || !clean(id, 256) || definitions[id] != "" {
				return errors.New("任务目录的流程定义身份重复、无效")
			}
			definitions[id] = task.ID
		}
	}
	for _, task := range tasks {
		for _, id := range task.ExcludedConfigurations {
			if id == "" || !clean(id, 128) || ids[id] != "" {
				return errors.New("清理条目不能同时属于已登记任务")
			}
		}
	}
	return nil
}

func containsID(ids []string, value string) bool {
	for _, id := range ids {
		if id == value {
			return true
		}
	}
	return false
}

func (t tenantConfig) taskDefinition(id, definition string) (TaskDefinition, bool) {
	for _, task := range t.registeredTasks() {
		if containsID(task.ExcludedConfigurations, id) {
			return TaskDefinition{}, false
		}
	}
	for _, task := range t.registeredTasks() {
		if task.ID == id || containsID(task.Aliases, id) {
			return task, true
		}
	}
	if definition != "" {
		for _, task := range t.registeredTasks() {
			if containsID(task.DefinitionIDs, definition) {
				return task, true
			}
		}
	}
	return TaskDefinition{}, false
}

func taskConfiguration(task Task) string {
	if task.ConfigurationID != "" {
		return task.ConfigurationID
	}
	return task.ID
}

func runConfiguration(run RemoteRun) string {
	if run.ConfigurationID != "" {
		return run.ConfigurationID
	}
	return run.TaskID
}

func (t tenantConfig) normalizeTask(task Task) (Task, bool) {
	local := taskConfiguration(task)
	definition, ok := t.taskDefinition(local, task.DefinitionID)
	if !ok {
		return Task{}, false
	}
	if task.ConfigurationID != "" && task.ID != definition.ID {
		return Task{}, false
	}
	if task.LocalNickname == "" {
		task.LocalNickname = task.Name
	}
	task.ID, task.Name, task.ConfigurationID = definition.ID, definition.Name, local
	return task, true
}

func (t tenantConfig) normalizeTasks(tasks []Task) []Task {
	result := []Task{}
	for _, task := range tasks {
		if normalized, ok := t.normalizeTask(task); ok {
			result = append(result, normalized)
		}
	}
	return result
}

func executionTask(machine Machine, id string, runnable bool) *Task {
	var candidates []Task
	for _, task := range machine.Tasks {
		if (task.ID == id || taskConfiguration(task) == id) && (!runnable || task.CanRun) {
			candidates = append(candidates, task)
		}
	}
	if len(candidates) == 0 {
		return nil
	}
	sort.Slice(candidates, func(i, j int) bool {
		a, b := candidates[i], candidates[j]
		if (taskConfiguration(a) == id) != (taskConfiguration(b) == id) {
			return taskConfiguration(a) == id
		}
		if a.CanRun != b.CanRun {
			return a.CanRun
		}
		return taskConfiguration(a) < taskConfiguration(b)
	})
	return &candidates[0]
}

func catalogMachine(machine Machine) Machine {
	ids := map[string]bool{}
	result := []Task{}
	for _, task := range machine.Tasks {
		if ids[task.ID] {
			continue
		}
		ids[task.ID] = true
		if selected := executionTask(machine, task.ID, false); selected != nil {
			result = append(result, *selected)
		}
	}
	machine.Tasks = result
	return machine
}

func (s *store) applyTaskCatalog(tenant tenantConfig, state *tenantState) error {
	before, err := json.Marshal(state)
	if err != nil {
		return err
	}
	if state.IgnoredRuns == nil {
		state.IgnoredRuns = map[string]string{}
	}
	for key, machine := range state.Machines {
		machine.Tasks = tenant.normalizeTasks(machine.Tasks)
		state.Machines[key] = machine
	}
	for id, run := range state.Runs {
		local := runConfiguration(run)
		definitionID := ""
		if header, present, err := snapshotInfo(run.Snapshot); err == nil && present && header.TaskSnapshot != nil {
			definitionID = header.TaskSnapshot.TemplateID
		}
		definition, ok := tenant.taskDefinition(local, definitionID)
		if !ok {
			// A previously migrated run keeps its registered task ID even if its
			// historical configuration is no longer advertised by the machine.
			definition, ok = tenant.taskDefinition(run.TaskID, "")
		}
		if !ok {
			state.IgnoredRuns[id] = run.MachineName
			delete(state.Runs, id)
			continue
		}
		run.TaskID, run.TaskName, run.ConfigurationID = definition.ID, definition.Name, local
		state.Runs[id] = run
	}
	if state.Organization != nil {
		assignments := map[string]string{}
		keys := make([]string, 0, len(state.Organization.Assignments))
		for id := range state.Organization.Assignments {
			keys = append(keys, id)
		}
		sort.Strings(keys)
		for _, id := range keys {
			group := state.Organization.Assignments[id]
			if task, ok := tenant.taskDefinition(id, ""); ok {
				if assignments[task.ID] == "" || id == task.ID {
					assignments[task.ID] = group
				}
			}
		}
		starred := []string{}
		for _, id := range state.Organization.Starred {
			if task, ok := tenant.taskDefinition(id, ""); ok && !containsID(starred, task.ID) {
				starred = append(starred, task.ID)
			}
		}
		state.Organization.Assignments, state.Organization.Starred = assignments, starred
	}
	after, err := json.Marshal(state)
	if err != nil || bytes.Equal(before, after) {
		return err
	}
	// Archive the complete original state before publishing the migrated ledger.
	archive := filepath.Join(s.directory, "archive", "task-catalog-"+time.Now().UTC().Format("20060102T150405.000000000Z"), tenant.ID+".json")
	if err := writeAtomic(archive, json.RawMessage(before)); err != nil {
		return err
	}
	return writeAtomic(s.stateFile(tenant.ID), state)
}

func ignoredObservationID(machine, run string) string {
	hash := sha256.Sum256([]byte(strings.ToLower(machine) + "\x00" + run))
	return "ignored." + hex.EncodeToString(hash[:16])
}

// Old Agents may still finish transmitting quarantined observations. Acknowledge
// their transport without recreating a cloud task, run, or log file.
func (s *service) ignoredRun(machine, id string) bool {
	assignments := s.machineAssignments(machine)
	if len(assignments) == 0 {
		return false
	}
	if len(id) == len("ignored.")+32 && strings.HasPrefix(id, "ignored.") && runID.MatchString(strings.TrimPrefix(id, "ignored.")) {
		return true
	}
	for _, assigned := range assignments {
		if strings.EqualFold(s.store.states[assigned.TenantID].IgnoredRuns[id], machine) {
			return true
		}
	}
	return false
}
