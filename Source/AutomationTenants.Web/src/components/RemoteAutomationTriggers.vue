<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { MessagePlugin } from 'tdesign-vue-next'
import type { RemoteAutomationScheduleEntry, RemoteAutomationTeamSchedule } from '@/api'
import { AGENTLESS_LABEL, remoteAutomationError, useRemoteAutomationStore } from '@/stores/remoteAutomation'

// The team schedule is the server's authority for this task: which machine runs it and when, in Beijing time.
const props = defineProps<{ taskId: string; preferredMachine: string }>()
const store = useRemoteAutomationStore()
const AGENTLESS_OPTION = '__agentless__'
const frequencies = [{ value: 'daily', label: 'Daily' }, { value: 'weekly', label: 'Weekly' }, { value: 'monthly', label: 'Monthly' }]
const hours = Array.from({ length: 24 }, (_, value) => String(value).padStart(2, '0'))
const minutes = Array.from({ length: 60 }, (_, value) => String(value).padStart(2, '0'))
const monthDays = Array.from({ length: 31 }, (_, index) => ({ value: index + 1, label: String(index + 1) }))
const DAY_NAMES = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']
const weekDays = [1, 2, 3, 4, 5, 6, 0].map(day => ({ day, label: DAY_NAMES[day]! }))

interface Draft { machine: string; enabled: boolean; entries: RemoteAutomationScheduleEntry[] }
const draft = ref<Draft>({ machine: '', enabled: false, entries: [] })
const baseline = ref('')
const expanded = ref(false)
const saving = ref(false)
const errorText = ref('')

const serverMachine = computed(() => store.tenants.find(row => row.id === store.tenantId)?.server_machine ?? '')
const entry = computed(() => store.tasks.find(row => row.id === props.taskId))
const schedule = computed<RemoteAutomationTeamSchedule | undefined>(() => store.schedules?.find(row => row.task_id === props.taskId))
const targets = computed(() => entry.value?.targets ?? [])
const reported = computed(() => {
  const preferred = targets.value.find(row => row.machine.machine_name.toLowerCase() === props.preferredMachine.toLowerCase())
  return (preferred ?? targets.value.find(row => row.task.triggers))?.task.triggers
})
const machineOptions = computed(() => {
  const options = targets.value.map(row => {
    const agentless = !!serverMachine.value && row.machine.machine_name.toLowerCase() === serverMachine.value.toLowerCase()
    return {
      value: agentless ? AGENTLESS_OPTION : row.machine.machine_name,
      label: agentless ? AGENTLESS_LABEL : [row.machine.machine_name, store.online(row.machine) ? '' : 'offline'].filter(Boolean).join(' · '),
    }
  })
  if (draft.value.machine && !options.some(option => option.value === draft.value.machine))
    options.unshift({ value: draft.value.machine, label: `${draft.value.machine} (task not reported)` })
  return options
})
const dirty = computed(() => JSON.stringify(draft.value) !== baseline.value)
const conflict = computed(() => {
  const rows = draft.value.entries
  for (let index = 1; index < rows.length; index++)
    for (let previous = 0; previous < index; previous++)
      if (same(rows[previous]!, rows[index]!)) return `Schedule ${index + 1} duplicates schedule ${previous + 1} (${summary([rows[index]!])}). Remove or change one of them.`
  return ''
})
const invalid = computed(() => draft.value.entries.findIndex(row =>
  row.frequency === 'weekly' && !row.week_days || row.frequency === 'monthly' && !row.last_day_of_month && !row.month_day))
const scheduleMeta = computed(() => draft.value.enabled ? summary(draft.value.entries) || 'No time set' : 'Disabled; times are kept')
const statusLine = computed(() => {
  const current = schedule.value
  if (!current) return reported.value?.schedule_enabled ? '' : 'No team schedule yet. Once enabled, the server dispatches runs to the selected runner on Beijing time.'
  const parts: string[] = []
  if (current.enabled && current.next_occurrence) parts.push(`Next run ${format(current.next_occurrence)}`)
  parts.push(`Runner ${current.machine_name || AGENTLESS_LABEL}`)
  parts.push(current.source === 'adopted' ? `Adopted local settings from ${current.updated_by}` : `Updated by ${current.updated_by} ${format(current.updated_at)}`)
  return parts.join(' · ')
})

const draftTarget = computed(() => {
  const name = (draft.value.machine === AGENTLESS_OPTION ? serverMachine.value : draft.value.machine).toLowerCase()
  return targets.value.find(row => row.machine.machine_name.toLowerCase() === name)
})
const machineOffline = computed(() => !!draftTarget.value && !store.online(draftTarget.value.machine))
// Local schedules the server would not take over with the settings on screen: they keep running on their own machines.
const localNote = computed(() => {
  const owner = draft.value.enabled ? draftTarget.value?.machine.machine_name.toLowerCase() ?? '' : ''
  const parts = targets.value.flatMap(row => {
    const local = row.task.triggers
    if (!local?.schedule_enabled) return []
    const taken = row.machine.machine_name.toLowerCase() === owner ? draft.value.entries : []
    const rest = local.schedules.filter(item => !taken.some(entry => same(entry, item)))
    return rest.length ? [`${store.machineLabel(row.machine.machine_name)} ${summary(rest)}`] : []
  })
  return parts.length ? `Local runner schedules are unaffected by the team schedule and keep running: ${parts.join('; ')}` : ''
})
watch(() => props.taskId, () => { baseline.value = ''; reset() }, { immediate: true })
// Polling refreshes the server copy; an edit in progress is kept until it is saved or discarded.
watch([schedule, reported], () => { if (!dirty.value) reset() })

function reset() {
  const current = schedule.value
  // A new team schedule starts on a machine that reports the task, so it can be saved as is.
  const names = targets.value.map(row => row.machine.machine_name)
  const first = names.find(name => name.toLowerCase() === props.preferredMachine.toLowerCase()) ?? names[0] ?? ''
  const machine = current ? current.machine_name || AGENTLESS_OPTION
    : first && first.toLowerCase() !== serverMachine.value.toLowerCase() ? first : AGENTLESS_OPTION
  const entries = current?.entries ?? reported.value?.schedules.map(({ time_zone: _, ...row }) => row) ?? []
  draft.value = { machine, enabled: current?.enabled ?? false,
    entries: entries.length ? entries.map(row => ({ ...row })) : [{ time: '10:00', frequency: 'daily', week_days: 62, month_day: 1, last_day_of_month: false }] }
  baseline.value = JSON.stringify(draft.value)
  errorText.value = ''
}
function same(a: RemoteAutomationScheduleEntry, b: RemoteAutomationScheduleEntry) {
  const kind = (row: RemoteAutomationScheduleEntry) => row.frequency === 'weekly' && row.week_days === 127 ? 'daily' : row.frequency
  if (a.time !== b.time || kind(a) !== kind(b)) return false
  if (kind(a) === 'weekly') return a.week_days === b.week_days
  if (kind(a) === 'monthly') return a.last_day_of_month === b.last_day_of_month && (a.last_day_of_month || a.month_day === b.month_day)
  return true
}
function calendar(row: RemoteAutomationScheduleEntry) {
  if (row.frequency === 'weekly') return 'Weekly on ' + [1, 2, 3, 4, 5, 6, 0].filter(day => row.week_days & (1 << day)).map(day => DAY_NAMES[day]).join(', ')
  if (row.frequency === 'monthly') return row.last_day_of_month ? 'Monthly on the last day' : `Monthly on day ${row.month_day}`
  return 'Daily'
}
// Same wording as the Client's schedule summary.
function summary(rows: RemoteAutomationScheduleEntry[]) {
  if (!rows.length) return ''
  return new Set(rows.map(calendar)).size === 1 ? `${calendar(rows[0]!)} ${rows.map(row => row.time).join(', ')}` : rows.map(row => `${calendar(row)} ${row.time}`).join('; ')
}
function format(value: string) {
  return new Date(value).toLocaleString('en-GB', { timeZone: 'Asia/Shanghai', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hour12: false })
}
function setTime(row: RemoteAutomationScheduleEntry, part: 0 | 1, value: unknown) {
  const pieces = row.time.split(':')
  pieces[part] = String(value)
  row.time = pieces.join(':')
}
function toggleDay(row: RemoteAutomationScheduleEntry, day: number, checked: boolean) {
  row.week_days = checked ? row.week_days | (1 << day) : row.week_days & ~(1 << day)
}
function setEnabled(value: unknown) {
  draft.value.enabled = !!value
  if (draft.value.enabled) expanded.value = true
}
function addEntry() {
  const last = draft.value.entries[draft.value.entries.length - 1]
  draft.value.entries.push(last ? { ...last } : { time: '10:00', frequency: 'daily', week_days: 62, month_day: 1, last_day_of_month: false })
  expanded.value = true
}
async function save() {
  if (conflict.value || invalid.value >= 0 || saving.value) return
  saving.value = true
  errorText.value = ''
  try {
    await store.saveSchedule({ tenant_id: store.tenantId, task_id: props.taskId,
      machine_name: draft.value.machine === AGENTLESS_OPTION ? '' : draft.value.machine, enabled: draft.value.enabled, entries: draft.value.entries })
    baseline.value = ''
    reset()
    void MessagePlugin.success('Team schedule saved. The server dispatches runs on Beijing time.')
  } catch (cause) {
    errorText.value = remoteAutomationError(cause)
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <section class="settings-group triggers" data-section="trigger">
    <h4>Triggers</h4>
    <div>
      <div class="trigger-row">
        <t-icon name="play" size="16px" class="trigger-icon" />
        <div class="trigger-text"><span class="trigger-title">Manual</span><span class="meta">Start from the web, MCP, or locally; follows the runner's task settings</span></div>
        <t-switch :value="reported?.manual ?? true" disabled aria-label="Manual trigger" />
      </div>
      <div class="divider" />
      <div class="trigger-row">
        <t-icon name="time" size="16px" class="trigger-icon" />
        <div class="trigger-text"><span class="trigger-title">Schedule</span><span class="meta" :title="scheduleMeta">{{ scheduleMeta }}</span></div>
        <t-switch :value="draft.enabled" :disabled="saving" aria-label="Enable schedule" @change="setEnabled" />
      </div>
    </div>
    <p v-if="statusLine" class="meta">{{ statusLine }}</p>
    <p v-if="schedule?.last_error" class="meta warning">Last scheduled run not dispatched: {{ schedule.last_error }}</p>
    <p v-if="localNote" class="meta" :title="localNote">{{ localNote }}</p>

    <div v-if="draft.enabled" class="expander" :class="{ open: expanded }">
      <button type="button" class="expander-header" :aria-expanded="expanded" @click="expanded = !expanded">
        <span>Schedule settings</span><t-icon :name="expanded ? 'chevron-up' : 'chevron-down'" size="16px" />
      </button>
      <div v-show="expanded" class="expander-body">
        <div class="field">
          <span class="field-label">Runner</span>
          <t-select :value="draft.machine" :options="machineOptions" :disabled="saving" filterable @change="(value: unknown) => draft.machine = String(value)" />
          <span v-if="machineOffline" class="meta warning">This runner is offline. If still offline at the scheduled time, the run is skipped and resumes at the next slot.</span>
          <span v-else class="meta">The server dispatches to this runner on schedule. Local flows with the same name on other runners are unaffected.</span>
        </div>
        <div v-for="(row, index) in draft.entries" :key="index" class="entry">
          <div v-if="draft.entries.length > 1" class="entry-head">
            <span class="entry-title">Schedule {{ index + 1 }}</span>
            <t-button variant="text" size="small" :disabled="saving" @click="draft.entries.splice(index, 1)">Remove</t-button>
          </div>
          <div class="entry-fields">
            <label class="field">
              <span class="field-label">Frequency</span>
              <t-select class="frequency" :value="row.frequency" :options="frequencies" :disabled="saving" @change="(value: unknown) => row.frequency = value as RemoteAutomationScheduleEntry['frequency']" />
            </label>
            <div class="field">
              <span class="field-label">Time</span>
              <div class="time">
                <t-select :value="row.time.split(':')[0]" :options="hours.map(value => ({ value, label: value }))" :disabled="saving" aria-label="Hour" @change="(value: unknown) => setTime(row, 0, value)" />
                <span>:</span>
                <t-select :value="row.time.split(':')[1]" :options="minutes.map(value => ({ value, label: value }))" :disabled="saving" aria-label="Minute" @change="(value: unknown) => setTime(row, 1, value)" />
              </div>
            </div>
          </div>
          <div v-if="row.frequency === 'weekly'" class="weekly">
            <div class="days">
              <t-checkbox v-for="item in weekDays" :key="item.day" :checked="!!(row.week_days & (1 << item.day))" :disabled="saving"
                @change="(checked: boolean) => toggleDay(row, item.day, checked)">{{ item.label }}</t-checkbox>
            </div>
            <t-button variant="text" size="small" :disabled="saving" @click="row.week_days = 62">Weekdays</t-button>
          </div>
          <div v-else-if="row.frequency === 'monthly'" class="monthly">
            <span>Day</span>
            <t-select class="month-day" :value="row.month_day" :options="monthDays" :disabled="saving || row.last_day_of_month" @change="(value: unknown) => row.month_day = Number(value)" />
            <span>of each month</span>
            <t-checkbox :checked="row.last_day_of_month" :disabled="saving" @change="(checked: boolean) => row.last_day_of_month = checked">Last day</t-checkbox>
          </div>
          <div class="zone"><span class="field-label">Time zone</span><span class="zone-value">Beijing time (team-wide)</span></div>
        </div>
        <p v-if="conflict" class="meta danger">{{ conflict }}</p>
        <p v-else-if="invalid >= 0" class="meta danger">Schedule {{ invalid + 1 }}: {{ draft.entries[invalid]?.frequency === 'weekly' ? 'Select at least one day' : 'Select a day from 1 to 31 or Last day' }}</p>
        <t-button class="add" variant="outline" size="small" :disabled="saving || draft.entries.length >= 20" @click="addEntry">
          <template #icon><t-icon name="add" /></template>Add time
        </t-button>
      </div>
    </div>

    <div v-if="dirty || errorText" class="save-bar">
      <span v-if="errorText" class="meta danger">{{ errorText }}</span>
      <span v-else class="meta">Team schedule has unsaved changes</span>
      <t-button size="small" :loading="saving" :disabled="!dirty || !!conflict || invalid >= 0" @click="save">Save</t-button>
      <t-button size="small" variant="outline" :disabled="saving" @click="baseline = ''; reset()">Discard</t-button>
    </div>
  </section>
</template>

<style scoped>
.triggers { gap: 10px; }
.triggers h4 { margin: 0 0 -4px; padding-bottom: 8px; border-bottom: 1px solid var(--border-subtle); color: var(--text-primary); font-size: 15px; font-weight: 600; line-height: 22px; }
.trigger-row { display: grid; grid-template-columns: auto minmax(0, 1fr) auto; align-items: center; gap: 8px; padding: 10px 0; }
.trigger-icon { color: var(--text-secondary); }
.trigger-text { display: grid; gap: 2px; min-width: 0; }
.trigger-title { color: var(--text-default); font-size: 14px; line-height: 22px; }
.meta { margin: 0; overflow: hidden; color: var(--text-meta); font-size: 11px; font-weight: 500; line-height: 16px; text-overflow: ellipsis; }
.trigger-text .meta { white-space: nowrap; }
.meta.warning { color: var(--status-warning-fg); }
.meta.danger { color: var(--status-danger-fg); }
.divider { height: 1px; background: var(--border-subtle); }
.expander { border: 1px solid var(--border-color); border-radius: 6px; background: var(--bg-card); }
.expander-header { display: flex; align-items: center; justify-content: space-between; width: 100%; height: 40px; padding: 0 12px; border: 0; background: transparent; color: var(--text-default); font: inherit; font-size: 14px; cursor: pointer; }
.expander.open .expander-header { border-bottom: 1px solid var(--border-subtle); }
.expander-body { display: grid; gap: 16px; padding: 12px; }
.field { display: grid; gap: 4px; min-width: 0; }
.field-label { color: var(--text-meta); font-size: 11px; font-weight: 500; line-height: 16px; }
.entry { display: grid; gap: 8px; }
.entry-head { display: flex; align-items: center; justify-content: space-between; }
.entry-title { color: var(--text-default); font-size: 14px; font-weight: 600; }
.entry-fields { display: flex; flex-wrap: wrap; gap: 0 12px; }
.frequency { width: 120px; }
.time { display: flex; align-items: center; gap: 4px; }
.time :deep(.t-select__wrap) { width: 72px; }
.weekly { display: grid; justify-items: start; gap: 6px; }
.days { display: flex; flex-wrap: wrap; gap: 4px 12px; }
.monthly { display: flex; flex-wrap: wrap; align-items: center; gap: 10px; color: var(--text-default); font-size: 14px; }
.month-day { width: 80px; }
.zone { display: flex; align-items: center; gap: 10px; }
.zone-value { color: var(--text-default); font-size: 14px; }
.add { justify-self: start; }
.save-bar { display: flex; align-items: center; gap: 8px; padding: 8px 12px; border-radius: 6px; background: var(--bg-secondary); }
.save-bar .meta { flex: 1; }
</style>
