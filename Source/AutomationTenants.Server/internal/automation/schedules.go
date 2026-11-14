package automation

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"maps"
	"regexp"
	"slices"
	"strings"
	"time"
	_ "time/tzdata"
)

const triggerCapability = "pregel.triggers.v1"

// Team schedules are evaluated in one authoritative zone, whatever the machines use locally.
var teamZone = func() *time.Location {
	zone, err := time.LoadLocation("Asia/Shanghai")
	if err != nil {
		return time.FixedZone("UTC+8", 8*3600)
	}
	return zone
}()

var scheduleTime = regexp.MustCompile(`^([01][0-9]|2[0-3]):[0-5][0-9]$`)

// One calendar trigger. WeekDays is a bit mask with Sunday as bit 0, as in the Client.
type ScheduleEntry struct {
	Time           string `json:"time"`
	Frequency      string `json:"frequency"`
	WeekDays       int    `json:"week_days"`
	MonthDay       int    `json:"month_day"`
	LastDayOfMonth bool   `json:"last_day_of_month"`
}

// A machine's saved task triggers, reported with its task list.
type TaskTriggers struct {
	Manual          bool              `json:"manual"`
	ScheduleEnabled bool              `json:"schedule_enabled"`
	Schedules       []ReportedTrigger `json:"schedules"`
	ReadOnlyReason  string            `json:"read_only_reason,omitempty"`
}

type ReportedTrigger struct {
	ScheduleEntry
	TimeZone string `json:"time_zone"`
}

// The team's authoritative schedule for a registered task: which machine runs it and when.
// It starts as a copy of the first machine that reported the task with scheduling on.
type TeamSchedule struct {
	TaskID         string          `json:"task_id"`
	MachineName    string          `json:"machine_name"`
	Enabled        bool            `json:"enabled"`
	Entries        []ScheduleEntry `json:"entries"`
	Source         string          `json:"source"`
	UpdatedBy      string          `json:"updated_by"`
	UpdatedAt      time.Time       `json:"updated_at"`
	LastOccurrence time.Time       `json:"last_occurrence"`
	LastRunID      string          `json:"last_run_id,omitempty"`
	LastError      string          `json:"last_error,omitempty"`
	NextOccurrence *time.Time      `json:"next_occurrence,omitempty"`
}

// Delivered to the machine the team schedule targets, so its local scheduler leaves those
// occurrences to the server. TaskID is that machine's configuration ID.
type ServerSchedule struct {
	TaskID  string          `json:"task_id"`
	Entries []ScheduleEntry `json:"entries"`
}

type ScheduleRequest struct {
	TenantID    string          `json:"tenant_id"`
	TaskID      string          `json:"task_id"`
	MachineName string          `json:"machine_name"`
	Enabled     bool            `json:"enabled"`
	Entries     []ScheduleEntry `json:"entries"`
}

func validateEntries(entries []ScheduleEntry) error {
	if len(entries) == 0 || len(entries) > 20 {
		return fault(400, "定时触发至少保留一个时间，最多 20 个。")
	}
	for index, entry := range entries {
		prefix := ""
		if len(entries) > 1 {
			prefix = fmt.Sprintf("定时触发 %d：", index+1)
		}
		switch {
		case !scheduleTime.MatchString(entry.Time):
			return fault(400, prefix+"请选择有效的执行时间")
		case entry.Frequency != "daily" && entry.Frequency != "weekly" && entry.Frequency != "monthly":
			return fault(400, prefix+"请选择每天、每周或每月")
		case entry.Frequency == "weekly" && (entry.WeekDays <= 0 || entry.WeekDays > 127):
			return fault(400, prefix+"请至少选择一个星期")
		case entry.Frequency == "monthly" && !entry.LastDayOfMonth && (entry.MonthDay < 1 || entry.MonthDay > 31):
			return fault(400, prefix+"请选择 1 至 31 日或最后一天")
		}
		for previous := 0; previous < index; previous++ {
			if sameOccurrences(entries[previous], entry) {
				return fault(400, fmt.Sprintf("定时触发 %d 与定时触发 %d 重复，请删除或修改其中一个", index+1, previous+1))
			}
		}
	}
	return nil
}

func sameOccurrences(a, b ScheduleEntry) bool {
	kind := func(entry ScheduleEntry) string {
		if entry.Frequency == "weekly" && entry.WeekDays == 127 {
			return "daily"
		}
		return entry.Frequency
	}
	if a.Time != b.Time || kind(a) != kind(b) {
		return false
	}
	switch kind(a) {
	case "weekly":
		return a.WeekDays == b.WeekDays
	case "monthly":
		return a.LastDayOfMonth == b.LastDayOfMonth && (a.LastDayOfMonth || a.MonthDay == b.MonthDay)
	}
	return true
}

func (entry ScheduleEntry) matches(date time.Time) bool {
	switch entry.Frequency {
	case "weekly":
		return entry.WeekDays&(1<<int(date.Weekday())) != 0
	case "monthly":
		if entry.LastDayOfMonth {
			return date.AddDate(0, 0, 1).Day() == 1
		}
		return date.Day() == entry.MonthDay
	}
	return true
}

func (entry ScheduleEntry) at(date time.Time) time.Time {
	var hour, minute int
	fmt.Sscanf(entry.Time, "%d:%d", &hour, &minute)
	return time.Date(date.Year(), date.Month(), date.Day(), hour, minute, 0, 0, teamZone)
}

// The latest occurrence at or before now, and the first one after it.
func occurrences(entries []ScheduleEntry, now time.Time) (previous, next time.Time) {
	local := now.In(teamZone)
	today := time.Date(local.Year(), local.Month(), local.Day(), 0, 0, 0, 0, teamZone)
	for _, entry := range entries {
		for offset := -370; offset <= 370; offset++ {
			date := today.AddDate(0, 0, offset)
			if !entry.matches(date) {
				continue
			}
			at := entry.at(date)
			if !at.After(now) && at.After(previous) {
				previous = at
			}
			if at.After(now) && (next.IsZero() || at.Before(next)) {
				next = at
			}
		}
	}
	return previous, next
}

// The zones a machine may report for a trigger that already runs on Beijing time.
func teamZoneID(id string) bool {
	return id == "China Standard Time" || id == "Asia/Shanghai"
}

func adoptSchedules(state *tenantState, machine Machine, now time.Time) {
	for _, task := range machine.Tasks {
		if task.Triggers == nil || !task.Triggers.ScheduleEnabled || task.Triggers.ReadOnlyReason != "" {
			continue
		}
		if _, exists := state.Schedules[task.ID]; exists {
			continue
		}
		// Adopt the configuration the server would dispatch, so the copied times are that configuration's.
		if picked := executionTask(machine, task.ID, false); picked == nil || taskConfiguration(*picked) != taskConfiguration(task) {
			continue
		}
		// A trigger in another zone keeps running locally; copying its clock time would move it.
		entries := make([]ScheduleEntry, 0, len(task.Triggers.Schedules))
		for _, reported := range task.Triggers.Schedules {
			if teamZoneID(reported.TimeZone) {
				entries = append(entries, reported.ScheduleEntry)
			}
		}
		if len(entries) != len(task.Triggers.Schedules) || validateEntries(entries) != nil {
			continue
		}
		if state.Schedules == nil {
			state.Schedules = map[string]TeamSchedule{}
		}
		// Adoption never replays the past: the first team occurrence is the next one.
		state.Schedules[task.ID] = TeamSchedule{TaskID: task.ID, MachineName: machine.MachineName, Enabled: true, Entries: entries,
			Source: "adopted", UpdatedBy: machine.MachineName, UpdatedAt: now, LastOccurrence: now}
	}
}

func (t tenantConfig) scheduleMachine(schedule TeamSchedule) string {
	if schedule.MachineName == "" {
		return t.ServerMachine
	}
	return schedule.MachineName
}

func serverSchedules(tenant tenantConfig, state *tenantState, machine Machine, capabilities []string) []ServerSchedule {
	result := []ServerSchedule{}
	if !slices.Contains(capabilities, triggerCapability) {
		return result
	}
	for _, schedule := range state.Schedules {
		if !schedule.Enabled || !strings.EqualFold(tenant.scheduleMachine(schedule), machine.MachineName) {
			continue
		}
		// The server claims only what it can dispatch; anything else stays with the machine's own schedule.
		if task := executionTask(machine, schedule.TaskID, false); task != nil && task.CanRun {
			result = append(result, ServerSchedule{TaskID: taskConfiguration(*task), Entries: schedule.Entries})
		}
	}
	slices.SortFunc(result, func(a, b ServerSchedule) int { return strings.Compare(a.TaskID, b.TaskID) })
	return result
}

func scheduleViews(state *tenantState, now time.Time) []TeamSchedule {
	result := []TeamSchedule{}
	for _, schedule := range state.Schedules {
		if schedule.Enabled {
			if _, next := occurrences(schedule.Entries, now); !next.IsZero() {
				schedule.NextOccurrence = &next
			}
		}
		result = append(result, schedule)
	}
	slices.SortFunc(result, func(a, b TeamSchedule) int { return strings.Compare(a.TaskID, b.TaskID) })
	return result
}

func (s *service) saveSchedule(actor, selected string, request ScheduleRequest) (TeamSchedule, error) {
	if request.TenantID != "" {
		selected = request.TenantID
	}
	tenant, err := s.config.tenant(actor, selected)
	if err != nil {
		return TeamSchedule{}, err
	}
	if request.TaskID == "" || !clean(request.TaskID, 128) || !clean(request.MachineName, 256) {
		return TeamSchedule{}, fault(400, "任务或机器无效。")
	}
	if err := validateEntries(request.Entries); err != nil {
		return TeamSchedule{}, err
	}
	if _, registered := tenant.taskDefinition(request.TaskID, ""); !registered {
		return TeamSchedule{}, fault(404, "任务未在团队目录中登记。")
	}
	name := request.MachineName
	if strings.EqualFold(name, tenant.ServerMachine) {
		name = ""
	}
	// An occurrence already due belongs to the schedule being replaced; dispatch it before the save resets the clock.
	s.fireSchedules(time.Now())
	s.store.mu.Lock()
	defer s.store.mu.Unlock()
	var saved TeamSchedule
	err = s.store.change(tenant.ID, func(state *tenantState) error {
		target := tenant.scheduleMachine(TeamSchedule{MachineName: name})
		machine, exists := state.Machines[strings.ToLower(target)]
		if !exists || executionTask(machine, request.TaskID, false) == nil {
			return fault(409, "所选机器尚未上报此任务，无法由它定时执行。")
		}
		if state.Schedules == nil {
			state.Schedules = map[string]TeamSchedule{}
		}
		now := time.Now().UTC()
		previous, exists := state.Schedules[request.TaskID]
		saved = TeamSchedule{TaskID: request.TaskID, MachineName: name, Enabled: request.Enabled, Entries: request.Entries,
			Source: "web", UpdatedBy: actor, UpdatedAt: now, LastOccurrence: now, LastRunID: previous.LastRunID}
		if exists && previous.LastOccurrence.After(now) {
			saved.LastOccurrence = previous.LastOccurrence
		}
		state.Schedules[request.TaskID] = saved
		return nil
	})
	if err != nil {
		return TeamSchedule{}, err
	}
	if _, next := occurrences(saved.Entries, time.Now()); saved.Enabled && !next.IsZero() {
		saved.NextOccurrence = &next
	}
	return saved, nil
}

type dueSchedule struct {
	tenant     tenantConfig
	schedule   TeamSchedule
	occurrence time.Time
	problem    string
}

// A machine that stopped polling runs its own schedule again, so the server must not queue for it.
const machineOnline = 90 * time.Second

// Why the server cannot dispatch this schedule right now; empty when it can.
func scheduleProblem(tenant tenantConfig, state *tenantState, schedule TeamSchedule, now time.Time) string {
	name := tenant.scheduleMachine(schedule)
	machine, exists := state.Machines[strings.ToLower(name)]
	if !exists || now.Sub(machine.LastSeen) > machineOnline {
		return "执行机器 " + name + " 不在线，本次定时未下发"
	}
	task := executionTask(machine, schedule.TaskID, false)
	if task == nil {
		return "执行机器 " + name + " 未上报这个任务，本次定时未下发"
	}
	if !task.CanRun {
		return "任务暂不可运行，本次定时未下发：" + task.UnavailableReason
	}
	return ""
}

// A scheduled run the machine did not accept within the grace period is void: running it late
// would repeat or misplace the occurrence.
func (s *service) expireScheduledRuns(now time.Time) {
	s.store.mu.Lock()
	defer s.store.mu.Unlock()
	for id, state := range s.store.states {
		stale := func(run RemoteRun) bool {
			return run.ScheduledFor != nil && run.AcceptedAt == nil && run.LocalRunID == "" &&
				!terminal(run.State) && now.Sub(run.CreatedAt) > scheduleGrace
		}
		if !slices.ContainsFunc(slices.Collect(maps.Values(state.Runs)), stale) {
			continue
		}
		_ = s.store.change(id, func(state *tenantState) error {
			for runID, run := range state.Runs {
				if !stale(run) {
					continue
				}
				run.State, run.LogsComplete, run.UpdatedAt = "failed", true, now.UTC()
				run.Revision++
				run.Detail = "机器未在 5 分钟内接单，本次定时已作废"
				state.Runs[runID] = run
				if schedule, exists := state.Schedules[run.TaskID]; exists && schedule.LastRunID == runID {
					schedule.LastError = run.Detail
					state.Schedules[run.TaskID] = schedule
				}
			}
			return nil
		})
	}
}

// Fires each team schedule once per occurrence. The request ID makes a repeated tick or a
// restart idempotent; an occurrence missed by more than the grace period is skipped, like the Client.
func (s *service) runSchedules(ctx context.Context) {
	ticker := time.NewTicker(15 * time.Second)
	defer ticker.Stop()
	for {
		s.fireSchedules(time.Now())
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
		}
	}
}

const scheduleGrace = 5 * time.Minute

func scheduleRequestID(task string, occurrence time.Time) string {
	hash := sha256.Sum256([]byte(fmt.Sprintf("%s|%d", task, occurrence.Unix())))
	return "schedule-" + hex.EncodeToString(hash[:16])
}

func (s *service) fireSchedules(now time.Time) {
	s.expireScheduledRuns(now)
	var due []dueSchedule
	s.store.mu.Lock()
	for id, state := range s.store.states {
		tenant, configured := s.config.tenantByID[id]
		if !configured {
			continue
		}
		for _, schedule := range state.Schedules {
			if !schedule.Enabled {
				continue
			}
			previous, _ := occurrences(schedule.Entries, now)
			if previous.IsZero() || !previous.After(schedule.LastOccurrence) {
				continue
			}
			due = append(due, dueSchedule{tenant, schedule, previous, scheduleProblem(tenant, state, schedule, now)})
		}
	}
	s.store.mu.Unlock()
	for _, item := range due {
		runID, failure := "", ""
		if now.Sub(item.occurrence) > scheduleGrace {
			failure = "服务不在线，错过计划时间 " + item.occurrence.In(teamZone).Format("2006-01-02 15:04") + "，已跳过"
		} else if item.problem != "" {
			failure = item.problem
		} else {
			run, err := s.createRun(item.tenant, "schedule", CreateRequest{TenantID: item.tenant.ID, MachineName: item.schedule.MachineName,
				TaskID: item.schedule.TaskID, RequestID: scheduleRequestID(item.schedule.TaskID, item.occurrence), ScheduledFor: &item.occurrence})
			if err != nil {
				failure = err.Error()
			}
			runID = run.ID
		}
		s.store.mu.Lock()
		_ = s.store.change(item.tenant.ID, func(state *tenantState) error {
			current, exists := state.Schedules[item.schedule.TaskID]
			if !exists || current.UpdatedAt != item.schedule.UpdatedAt || !item.occurrence.After(current.LastOccurrence) {
				return nil
			}
			current.LastOccurrence = item.occurrence
			if runID != "" {
				current.LastRunID = runID
			}
			current.LastError = failure
			state.Schedules[item.schedule.TaskID] = current
			return nil
		})
		s.store.mu.Unlock()
	}
}
