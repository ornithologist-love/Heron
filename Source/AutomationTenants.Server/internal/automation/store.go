package automation

import (
	"encoding/json"
	"errors"
	"io"
	"os"
	"path/filepath"
	"slices"
	"sync"
	"time"
)

type tenantState struct {
	SchemaVersion int                     `json:"schema_version"`
	TenantID      string                  `json:"tenant_id"`
	Machines      map[string]Machine      `json:"machines"`
	Runs          map[string]RemoteRun    `json:"runs"`
	Organization  *Organization           `json:"organization,omitempty"`
	IgnoredRuns   map[string]string       `json:"ignored_runs,omitempty"`
	Outbox        []outboxEntry           `json:"outbox,omitempty"`
	Schedules     map[string]TeamSchedule `json:"schedules,omitempty"`
	Deployments   map[string]Deployment   `json:"deployments,omitempty"`
}

type store struct {
	mu          sync.Mutex
	directory   string
	states      map[string]*tenantState
	memberships map[string][]assignment
	logs        map[string]*runLogs
	lock        io.Closer
	hooks       map[string][]eventHookConfig
	wake        chan struct{}
	// When each machine's last-seen time last reached disk, keyed by tenant and machine.
	seenOnDisk map[string]time.Time
}

func openStore(c config) (*store, error) {
	if err := os.MkdirAll(c.Directory, 0700); err != nil {
		return nil, err
	}
	lock, err := lockState(filepath.Join(c.Directory, "writer.lock"))
	if err != nil {
		return nil, errors.New("Go 后台状态目录已被另一写入者占用或不可写")
	}
	s := &store{directory: c.Directory, states: map[string]*tenantState{}, logs: map[string]*runLogs{}, lock: lock,
		hooks: map[string][]eventHookConfig{}, wake: make(chan struct{}, 1), seenOnDisk: map[string]time.Time{}}
	if err := s.loadMemberships(c); err != nil {
		lock.Close()
		return nil, err
	}
	for _, tenant := range c.Tenants {
		state := &tenantState{SchemaVersion: 1, TenantID: tenant.ID, Machines: map[string]Machine{}, Runs: map[string]RemoteRun{}}
		path := s.stateFile(tenant.ID)
		data, err := os.ReadFile(path)
		if err == nil {
			if json.Unmarshal(data, state) != nil || state.SchemaVersion != 1 || state.TenantID != tenant.ID || state.Machines == nil || state.Runs == nil {
				lock.Close()
				return nil, errors.New("远程自动化持久状态格式无效")
			}
		} else if !errors.Is(err, os.ErrNotExist) {
			lock.Close()
			return nil, err
		}
		for id, run := range state.Runs {
			if run.Source == "" {
				run.Source = "cloud"
				state.Runs[id] = run
			}
		}
		// Events for a hook removed from the configuration have no receiver left.
		kept := state.Outbox[:0]
		for _, entry := range state.Outbox {
			if slices.ContainsFunc(tenant.EventHooks, func(hook eventHookConfig) bool { return hook.Name == entry.Hook }) {
				kept = append(kept, entry)
			}
		}
		state.Outbox = kept
		if len(tenant.EventHooks) > 0 {
			s.hooks[tenant.ID] = tenant.EventHooks
		}
		if err := s.applyTaskCatalog(tenant, state); err != nil {
			lock.Close()
			return nil, err
		}
		s.states[tenant.ID] = state
	}
	return s, nil
}

func (s *store) stateFile(tenant string) string {
	return filepath.Join(s.directory, "tenants", tenant, "state.json")
}

// The caller holds mu. Publish the new memory snapshot only after atomic persistence.
func (s *store) change(tenant string, apply func(*tenantState) error) error {
	data, err := json.Marshal(s.states[tenant])
	if err != nil {
		return err
	}
	var next tenantState
	if err := json.Unmarshal(data, &next); err != nil {
		return err
	}
	if err := apply(&next); err != nil {
		return err
	}
	s.recordEvents(tenant, s.states[tenant], &next)
	if err := writeAtomic(s.stateFile(tenant), &next); err != nil {
		return err
	}
	s.states[tenant] = &next
	return nil
}

func writeAtomic(path string, value any) error {
	directory := filepath.Dir(path)
	if err := os.MkdirAll(directory, 0700); err != nil {
		return err
	}
	f, err := os.CreateTemp(directory, ".state-*")
	if err != nil {
		return err
	}
	defer os.Remove(f.Name())
	if err = f.Chmod(0600); err == nil {
		err = json.NewEncoder(f).Encode(value)
	}
	if err == nil {
		err = f.Sync()
	}
	closed := f.Close()
	if err == nil {
		err = closed
	}
	if err != nil {
		return err
	}
	return os.Rename(f.Name(), path)
}
