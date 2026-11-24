<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { DialogPlugin, MessagePlugin } from 'tdesign-vue-next'
import type { RemoteAutomationRun } from '@/api'
import { AGENTLESS_LABEL, remoteAutomationState, remoteRunFinished, useRemoteAutomationStore, type RemoteAutomationTaskEntry } from '@/stores/remoteAutomation'
import { useSiteAccessStore } from '@/stores/siteAccess'
import { fullTime, relativeTime, runStartedAt, runTrigger, shortTime } from '@/utils/remoteAutomationFormat'
import RemoteAutomationInvocation from './RemoteAutomationInvocation.vue'
import RemoteAutomationRunDetail from './RemoteAutomationRunDetail.vue'
import WorkflowContributors from './WorkflowContributors.vue'

const route = useRoute(), router = useRouter(), store = useRemoteAutomationStore()
const access = useSiteAccessStore()
const canOrganize = computed(() => !!access.userName)
const props = defineProps<{ showTaskList: boolean }>()
type Pane = 'parameters' | 'plan' | 'runs'
type TaskFilter = 'all' | 'recent' | 'starred' | 'failed' | 'active'
const textQuery = (key: string) => typeof route.query[key] === 'string' ? route.query[key] as string : ''
// Empty target = the no-machine environment (no Agent), matching the API's empty machine_name.
// The URL spells it `agentless`; older `server` / `local` links are still accepted.
function parseTarget() {
  const value = textQuery('target') || textQuery('machine')
  return !value ? null : ['agentless', 'server', 'local'].includes(value) ? '' : value
}
const chosenTarget = ref<string | null>(parseTarget())
const taskId = ref(textQuery('task'))
const search = ref('')
const filter = ref<TaskFilter>('all')
const pane = ref<Pane>(route.query.view === 'detail' || route.query.view === 'history' ? 'runs' : route.query.pane === 'parameters' ? 'parameters' : 'plan')
const executor = ref<InstanceType<typeof RemoteAutomationInvocation> | null>(null)
const runId = computed(() => textQuery('run'))
const selected = computed(() => store.selectedRun?.id === runId.value ? store.selectedRun : null)
const serverName = computed(() => store.tenants.find(tenant => tenant.id === store.tenantId)?.server_machine ?? '')
const tasks = computed(() => store.tasks)
const latestByTask = computed(() => {
  const rows = new Map<string, RemoteAutomationRun>()
  for (const run of store.runs) if (!run.parent_run_id && !rows.has(run.task_id)) rows.set(run.task_id, run)
  return rows
})
function latest(id: string) { return latestByTask.value.get(id) }
function taskFailed(row: RemoteAutomationTaskEntry) { return latest(row.id)?.state === 'failed' }
function taskActive(row: RemoteAutomationTaskEntry) { const run = latest(row.id); return !!run && !remoteRunFinished(run.state) }
const RECENT_LIMIT = 15
const starred = computed(() => store.organization.starred)
const recentTasks = computed(() => tasks.value.filter(row => latest(row.id))
  .sort((a, b) => Date.parse(runStartedAt(latest(b.id)!)) - Date.parse(runStartedAt(latest(a.id)!))).slice(0, RECENT_LIMIT))
const starredTasks = computed(() => starred.value.map(id => tasks.value.find(row => row.id === id)).filter((row): row is RemoteAutomationTaskEntry => !!row))
const views = computed(() => [
  { value: 'all', label: 'All flows', icon: 'app', count: tasks.value.length },
  { value: 'recent', label: 'Recent', icon: 'time', count: recentTasks.value.length },
  ...(canOrganize.value ? [{ value: 'starred', label: 'Starred', icon: 'star', count: starredTasks.value.length }] : []),
])
const statusViews = computed(() => [
  { value: 'failed', label: 'Failed', state: 'failed', count: tasks.value.filter(taskFailed).length },
  { value: 'active', label: 'Running', state: 'running', count: tasks.value.filter(taskActive).length },
])
const viewLabel = computed(() => [...views.value, ...statusViews.value].find(item => item.value === filter.value)?.label ?? '')
const keyword = computed(() => search.value.trim().toLowerCase())
const visibleTasks = computed(() => {
  const pool = filter.value === 'recent' ? recentTasks.value : filter.value === 'starred' ? starredTasks.value
    : filter.value === 'failed' ? tasks.value.filter(taskFailed) : filter.value === 'active' ? tasks.value.filter(taskActive) : tasks.value
  return keyword.value ? pool.filter(row => row.name.toLowerCase().includes(keyword.value)) : pool
})

// Groups are a shared console arrangement (server-side); offline tasks always sink into their own section.
const UNGROUPED = '', OFFLINE = '__offline__'
type Section = { key: string; name: string; rows: RemoteAutomationTaskEntry[]; editable: boolean }
type ListItem = { kind: 'section'; section: Section } | { kind: 'task'; row: RemoteAutomationTaskEntry } | { kind: 'empty'; key: string }
const grouped = computed(() => filter.value === 'all' && !keyword.value)
const sections = computed<Section[]>(() => {
  const { groups, assignments } = store.organization
  const known = new Set(groups.map(group => group.id))
  const live = tasks.value.filter(row => row.targets.length)
  const groupOf = (row: RemoteAutomationTaskEntry) => known.has(assignments[row.id] ?? '') ? assignments[row.id] : UNGROUPED
  const result: Section[] = groups.map(group => ({ key: group.id, name: group.name, rows: live.filter(row => groupOf(row) === group.id), editable: true }))
  if (groups.length || !live.length) result.push({ key: UNGROUPED, name: 'Ungrouped', rows: live.filter(row => groupOf(row) === UNGROUPED), editable: false })
  else result.push({ key: UNGROUPED, name: '', rows: live, editable: false })
  const offline = tasks.value.filter(row => !row.targets.length)
  if (offline.length) result.push({ key: OFFLINE, name: 'Offline', rows: offline, editable: false })
  return result
})
// Collapsed sections are a per-viewer convenience; losing them only re-expands the list.
const COLLAPSED_KEY = 'ksf.automation.collapsed'
function loadCollapsed() {
  try { const value = JSON.parse(localStorage.getItem(COLLAPSED_KEY) ?? 'null'); return new Set<string>(Array.isArray(value) ? value : [OFFLINE]) } catch { return new Set([OFFLINE]) }
}
const collapsed = ref(loadCollapsed())
function toggleSection(key: string) {
  const next = new Set(collapsed.value)
  if (!next.delete(key)) next.add(key)
  collapsed.value = next
  try { localStorage.setItem(COLLAPSED_KEY, JSON.stringify([...next])) } catch { /* storage unavailable */ }
}
const listItems = computed<ListItem[]>(() => {
  if (!grouped.value) return visibleTasks.value.map(row => ({ kind: 'task', row }))
  const items: ListItem[] = []
  for (const section of sections.value) {
    if (section.name) items.push({ kind: 'section', section })
    if (section.name && collapsed.value.has(section.key)) continue
    if (!section.rows.length && section.editable) items.push({ kind: 'empty', key: section.key })
    for (const row of section.rows) items.push({ kind: 'task', row })
  }
  return items
})

function toggleStar(id: string) {
  void store.organize(current => ({ ...current, starred: current.starred.includes(id) ? current.starred.filter(row => row !== id) : [...current.starred, id] }))
}
function moveTask(id: string, group: string) {
  void store.organize(current => {
    const assignments = { ...current.assignments }
    if (group) assignments[id] = group
    else delete assignments[id]
    return { ...current, assignments }
  })
}
const editing = ref<{ id: string; name: string } | null>(null)
let editInput: { focus: () => void } | null = null
function setEditInput(el: unknown) { if (el) editInput = el as { focus: () => void } }
function startEdit(id: string, name = '') {
  editing.value = { id, name }
  if (id && collapsed.value.has(id)) toggleSection(id)
  void nextTick(() => editInput?.focus())
}
function commitEdit() {
  const edit = editing.value
  editing.value = null
  const name = edit?.name.trim()
  if (!edit || !name) return
  void store.organize(current => edit.id === 'new'
    ? { ...current, groups: [...current.groups, { id: `g${Date.now().toString(36)}`, name }] }
    : { ...current, groups: current.groups.map(group => group.id === edit.id ? { ...group, name } : group) })
}
function deleteGroup(section: Section) {
  const dialog = DialogPlugin.confirm({
    header: `Delete group "${section.name}"`, body: section.rows.length ? `${section.rows.length} flow(s) in this group will move to "Ungrouped". The flows themselves are not affected.` : 'This group is empty.',
    confirmBtn: 'Delete', cancelBtn: 'Cancel', theme: 'warning',
    onConfirm: () => {
      dialog.hide()
      void store.organize(current => ({
        ...current, groups: current.groups.filter(group => group.id !== section.key),
        assignments: Object.fromEntries(Object.entries(current.assignments).filter(([, group]) => group !== section.key)),
      }))
    },
  })
}
function sectionActions() {
  return [{ content: 'Rename', value: 'rename' }, { content: 'Delete group', value: 'delete' }]
}
function onSectionAction(section: Section, item: { value?: string | number }) {
  if (item.value === 'rename') startEdit(section.key, section.name)
  else if (item.value === 'delete') deleteGroup(section)
}
function taskActions(row: RemoteAutomationTaskEntry) {
  const current = store.organization.assignments[row.id] ?? UNGROUPED
  const targets = [...store.organization.groups.map(group => ({ value: group.id, label: group.name })), { value: UNGROUPED, label: 'Ungrouped' }]
  return [
    { content: starred.value.includes(row.id) ? 'Unstar' : 'Star', value: 'star' },
    ...(row.targets.length && store.organization.groups.length ? [{ content: 'Move to', value: 'move',
      children: targets.map(target => ({ content: target.label, value: `move:${target.value}`, disabled: target.value === current })) }] : []),
    { content: 'New group…', value: 'new-group' },
  ]
}
function onTaskAction(row: RemoteAutomationTaskEntry, item: { value?: string | number }) {
  const value = String(item.value ?? '')
  if (value === 'star') toggleStar(row.id)
  else if (value.startsWith('move:')) moveTask(row.id, value.slice(5))
  else if (value === 'new-group') { filter.value = 'all'; search.value = ''; startEdit('new') }
}
const dragging = ref(''), dropTarget = ref<string | null>(null)
function dropOn(key: string) {
  if (dragging.value && key !== OFFLINE) moveTask(dragging.value, key)
  dragging.value = ''; dropTarget.value = null
}
function rowTitle(row: RemoteAutomationTaskEntry) {
  const run = latest(row.id)
  return [row.name, run ? `${remoteAutomationState(run.state).label} · ${fullTime(runStartedAt(run))} · ${store.machineLabel(run.machine_name)}` : 'Never run',
    `${runCount(row.id)} runs · ${machineCount(row.id)} runners`].join('\n')
}
const task = computed(() => tasks.value.find(row => row.id === taskId.value))
const archived = computed(() => !!task.value && !task.value.targets.length)
const defaultTarget = computed(() => {
  const targets = task.value?.targets ?? []
  const recent = latest(taskId.value)
  const previous = targets.find(row => row.machine.machine_name.toLowerCase() === recent?.machine_name.toLowerCase()
    && row.task.can_run && store.online(row.machine))
  if (previous) return previous.machine.machine_name === serverName.value ? '' : previous.machine.machine_name
  if (targets.some(row => row.machine.machine_name === serverName.value)) return ''
  return targets.find(row => row.task.can_run && store.online(row.machine))?.machine.machine_name
    ?? targets[0]?.machine.machine_name ?? ''
})
// Pick the initial machine once per task so polling cannot replace the target during editing.
const automaticTarget = computed<{ tenantId: string; taskId: string; machine: string } | undefined>(previous => {
  const tenantId = store.tenantId
  const id = taskId.value
  if (previous?.tenantId === tenantId && previous.taskId === id) return previous
  if (task.value?.targets.length) return { tenantId, taskId: id, machine: defaultTarget.value }
})
const target = computed(() => {
  const value = chosenTarget.value ?? automaticTarget.value?.machine ?? ''
  return value === serverName.value ? '' : value
})
const targetLabel = computed(() => target.value || AGENTLESS_LABEL)
const history = computed(() => store.runs.filter(run => !run.parent_run_id && run.task_id === taskId.value))
const lastRun = computed(() => history.value[0])
const canRun = computed(() => !!task.value && !archived.value && !!executor.value?.canRun)
const runHint = computed(() => archived.value ? 'This flow isn\'t registered on any execution target. Past runs are still available.' : executor.value?.runHint ?? 'Loading parameters and plan…')
const moreActions = computed(() => [
  { content: 'Reload parameters and plan', value: 'inspect', disabled: archived.value || store.inspecting },
  { content: 'View all runs', value: 'history' },
  { content: 'Refresh', value: 'refresh' },
])
const canRetry = computed(() => !archived.value && pane.value === 'runs' && !!selected.value
  && !selected.value.parent_run_id && remoteRunFinished(selected.value.state))
const retryBlocked = computed(() => {
  const source = selected.value
  if (!source) return ''
  // Cloud runs always record what they started with; a local run without a snapshot recorded nothing.
  if (source.source === 'local' && !Object.keys(source.parameters ?? {}).length) return 'This run has no recorded parameters, so it can\'t be retried with them.'
  const entry = task.value?.targets.find(row => row.machine.machine_name.toLowerCase() === source.machine_name.toLowerCase())
  const machine = store.machineLabel(source.machine_name)
  if (!entry) return `${machine} no longer reports this flow, so it can't be retried there.`
  if (!store.online(entry.machine)) return `${machine} is offline, so it can't be retried there.`
  return entry.task.can_run ? '' : entry.task.unavailable_reason || 'This flow can\'t run right now.'
})
async function retry() {
  if (!selected.value || retryBlocked.value) return
  const run = await store.rerun(selected.value)
  if (!run) return
  MessagePlugin.success('Started a new run with the same parameters')
  openRun(run.id)
}
function machineCount(id: string) { return new Set(store.runs.filter(run => !run.parent_run_id && run.task_id === id).map(run => run.machine_name.toLowerCase())).size }
function runCount(id: string) { return store.runs.filter(run => !run.parent_run_id && run.task_id === id).length }
function navigate(mode: 'invoke' | 'detail' | 'history', id?: string) {
  void router.push({ query: { tenant: store.tenantId, view: mode, target: chosenTarget.value === null ? undefined : chosenTarget.value || 'agentless', task: taskId.value,
    run: id || undefined, pane: mode === 'invoke' && pane.value === 'parameters' ? 'parameters' : undefined } })
}
function selectTarget(value: string) {
  chosenTarget.value = value
  navigate('invoke', selected.value?.task_id === taskId.value ? selected.value.id : undefined)
}
function selectTask(id: string) {
  taskId.value = id
  if (archived.value || pane.value === 'runs') {
    pane.value = 'runs'
    if (history.value[0]) openRun(history.value[0].id)
    else navigate('history')
  } else navigate('invoke')
}
function selectPane(value: string | number) {
  const next = value as Pane
  pane.value = next
  if (next === 'runs') {
    const previous = history.value.find(run => run.id === runId.value) ?? history.value[0]
    if (previous) openRun(previous.id)
    else navigate('history')
  } else navigate('invoke', selected.value?.task_id === taskId.value ? selected.value.id : undefined)
}
function openRun(id: string) {
  const record = store.runs.find(run => run.id === id)
  if (record) taskId.value = record.task_id
  pane.value = 'runs'
  navigate('detail', id)
}
function onMore(item: { value?: string | number }) {
  if (item.value === 'inspect') void executor.value?.inspect()
  else if (item.value === 'history') void router.push({ query: { tenant: store.tenantId, view: 'history' } })
  else void store.refresh()
}
watch(() => [route.query.target, route.query.machine, route.query.task, route.query.view, route.query.pane], () => {
  chosenTarget.value = parseTarget()
  if (textQuery('task')) taskId.value = textQuery('task')
  pane.value = route.query.view === 'detail' || route.query.view === 'history' ? 'runs' : route.query.pane === 'parameters' ? 'parameters' : 'plan'
})
watch([runId, () => store.tenantId], ([id, tenant]) => { if (props.showTaskList && tenant) void store.selectRun(id) }, { immediate: true })
watch(() => selected.value?.id, () => {
  if (selected.value) taskId.value = selected.value.task_id
})
watch([tasks, runId], ([rows]) => {
  if (!rows.length) return
  if (!rows.some(row => row.id === taskId.value) && !runId.value)
    taskId.value = rows.find(row => row.id === textQuery('task'))?.id ?? rows.find(row => row.targets.some(item => item.task.can_run && store.online(item.machine)))?.id ?? rows[0]?.id ?? ''
}, { immediate: true })
</script>

<template>
  <section class="automation-workspace" aria-label="Automation workspace">
    <aside class="task-sidebar" :aria-label="showTaskList ? 'Automation flows' : 'Automation navigation'">
      <slot name="sidebar-header" />
      <t-input v-if="showTaskList" v-model="search" class="task-search" clearable placeholder="Search flows" aria-label="Search flows">
        <template #prefix-icon><t-icon name="search" /></template>
      </t-input>
      <nav v-if="showTaskList" class="task-views" aria-label="Filter flows">
        <button v-for="item in views" :key="item.value" type="button" class="view-row" :class="{ active: filter === item.value }"
          :aria-pressed="filter === item.value" @click="filter = item.value as TaskFilter">
          <t-icon :name="item.icon" /><span>{{ item.label }}</span><em>{{ item.count }}</em>
        </button>
        <div class="view-divider" />
        <button v-for="item in statusViews" :key="item.value" type="button" class="view-row" :class="{ active: filter === item.value }"
          :aria-pressed="filter === item.value" @click="filter = item.value as TaskFilter">
          <span class="view-dot"><span class="status-dot" :class="item.state" /></span><span>{{ item.label }}</span><em>{{ item.count }}</em>
        </button>
      </nav>
      <div v-if="showTaskList" class="list-heading">
        <span>{{ keyword ? `Search "${search.trim()}"` : viewLabel }}</span><small>{{ visibleTasks.length }}</small>
        <t-tooltip v-if="canOrganize && filter === 'all'" content="New group"><t-button class="heading-add" variant="text" shape="square" size="small" aria-label="New group" @click="startEdit('new')"><template #icon><t-icon name="folder-add" /></template></t-button></t-tooltip>
      </div>
      <div v-if="showTaskList" class="task-list" role="listbox" aria-label="Flows">
        <div v-if="editing?.id === 'new'" class="group-head editing"><t-icon name="folder" />
          <t-input :ref="setEditInput" v-model="editing.name" size="small" placeholder="Group name, Enter to save" :maxlength="40" @enter="commitEdit" @blur="commitEdit" @keydown.esc="editing = null" />
        </div>
        <template v-for="item in listItems" :key="item.kind === 'task' ? `t:${item.row.id}` : `${item.kind}:${item.kind === 'section' ? item.section.key : item.key}`">
          <div v-if="item.kind === 'section'" class="group-head" :class="{ dropping: dropTarget === item.section.key }" role="button" tabindex="0"
            :aria-expanded="!collapsed.has(item.section.key)" @click="toggleSection(item.section.key)" @keydown.enter="toggleSection(item.section.key)"
            @dragover.prevent="dragging && item.section.key !== OFFLINE && (dropTarget = item.section.key)" @dragleave="dropTarget = null" @drop.prevent="dropOn(item.section.key)">
            <t-icon class="caret" :name="collapsed.has(item.section.key) ? 'chevron-right' : 'chevron-down'" />
            <t-input v-if="editing?.id === item.section.key" :ref="setEditInput" v-model="editing.name" size="small" :maxlength="40"
              @click.stop @enter="commitEdit" @blur="commitEdit" @keydown.esc="editing = null" />
            <template v-else>
              <span class="group-name">{{ item.section.name }}</span><em>{{ item.section.rows.length }}</em>
              <span v-if="item.section.editable" class="row-menu" @click.stop @keydown.enter.stop>
                <t-dropdown v-if="canOrganize" :options="sectionActions()" trigger="click" placement="bottom-right" @click="onSectionAction(item.section, $event)">
                  <button type="button" class="row-action" aria-label="Group actions"><t-icon name="ellipsis" /></button>
                </t-dropdown>
              </span>
            </template>
          </div>
          <div v-else-if="item.kind === 'empty'" class="group-empty">Drag flows here or use the ⋯ menu</div>
          <div v-else class="task-row" :class="{ selected: !$slots.content && item.row.id === taskId, offline: !item.row.targets.length }" role="option" :aria-selected="!$slots.content && item.row.id === taskId"
            tabindex="0" :title="rowTitle(item.row)" :draggable="canOrganize && grouped && !!item.row.targets.length"
            @click="selectTask(item.row.id)" @keydown.enter="selectTask(item.row.id)" @dragstart="dragging = item.row.id" @dragend="dragging = ''; dropTarget = null">
            <span class="row-state"><span class="status-dot" :class="latest(item.row.id)?.state ?? 'never'" /></span>
            <span class="row-name">{{ item.row.name }}</span>
            <t-icon v-if="starred.includes(item.row.id)" class="row-starred" name="star-filled" />
            <span class="row-time">{{ latest(item.row.id) ? shortTime(runStartedAt(latest(item.row.id)!)) : '' }}</span>
            <span class="row-menu" @click.stop @keydown.enter.stop>
              <t-dropdown v-if="canOrganize" :options="taskActions(item.row)" trigger="click" placement="bottom-right" @click="onTaskAction(item.row, $event)">
                <button type="button" class="row-action" aria-label="Flow actions"><t-icon name="ellipsis" /></button>
              </t-dropdown>
            </span>
          </div>
        </template>
        <div v-if="!visibleTasks.length" class="list-empty">
          <strong>{{ store.loading && !tasks.length ? 'Loading flows…' : keyword ? 'No matching flows' : filter === 'starred' ? 'No starred flows' : filter === 'recent' ? 'No recent runs' : 'No flows' }}</strong>
          <span v-if="tasks.length && !keyword && filter === 'starred'">Star flows from their ⋯ menu to keep them here.</span>
        </div>
      </div>
      <t-alert v-if="showTaskList && store.organizationError" class="organize-error" theme="error" :message="store.organizationError" close />
    </aside>

    <div class="workspace-content">
      <slot name="notice" />
      <slot name="content">
        <main v-if="task" class="task-workspace">
          <header class="task-header">
            <div class="header-identity">
              <div class="title-line"><h3 :title="task.name">{{ task.name }}</h3><span class="meta">{{ history.length }} runs · {{ machineCount(task.id) }} runners</span></div>
              <div class="meta-line">
                <t-link v-if="!archived" theme="primary" hover="color" title="Execution target for new runs. Click to change." @click="selectPane('parameters')"><t-icon :name="target ? 'desktop' : 'cloud'" />Target: {{ targetLabel }}</t-link>
                <template v-if="lastRun"><span class="separator" /><span>Last run · {{ store.machineLabel(lastRun.machine_name) }}</span><span class="status-dot" :class="lastRun.state" /><span class="status-text" :class="lastRun.state">{{ remoteAutomationState(lastRun.state).label }}</span><span :title="fullTime(runStartedAt(lastRun))">{{ relativeTime(runStartedAt(lastRun)) }}</span></template>
                <span v-else-if="!archived" class="muted-inline">· Never run</span>
              </div>
              <WorkflowContributors :tenant-id="store.tenantId" :task-id="task.id" />
            </div>
            <div class="header-actions">
              <t-tooltip v-if="canRetry" :content="retryBlocked || 'Run again on the same runner with this run\'s parameters'" placement="bottom-right">
                <span><t-button variant="outline" :disabled="!!retryBlocked" :loading="store.saving" @click="retry"><template #icon><t-icon name="refresh" /></template>Retry with same params</t-button></span>
              </t-tooltip>
              <t-button v-if="!archived && pane === 'runs'" theme="primary" @click="selectPane('parameters')"><template #icon><t-icon name="play" /></template>New run</t-button>
              <t-tooltip v-else-if="!archived" :content="runHint" :disabled="canRun || !runHint" placement="bottom-right">
                <span><t-button theme="primary" :disabled="!canRun" :loading="store.saving" @click="executor?.run()"><template #icon><t-icon name="play" /></template>Run now</t-button></span>
              </t-tooltip>
              <t-dropdown :options="moreActions" trigger="click" placement="bottom-right" @click="onMore">
                <t-button variant="text" shape="square" aria-label="More actions"><template #icon><t-icon name="ellipsis" /></template></t-button>
              </t-dropdown>
            </div>
          </header>
          <t-tabs class="task-tabs compact-tabs" :value="pane" @change="selectPane">
            <t-tab-panel value="parameters" label="Parameters" :disabled="archived" />
            <t-tab-panel value="plan" label="Plan" :disabled="archived" />
            <t-tab-panel value="runs"><template #label>Runs <span class="tab-count">{{ history.length }}</span></template></t-tab-panel>
          </t-tabs>
          <t-alert v-if="archived" class="task-alert" theme="info" :message="runHint" />
          <RemoteAutomationInvocation v-if="!archived" v-show="pane !== 'runs'" ref="executor" class="task-body" :pane="pane" :active="pane !== 'runs'"
            :initial-machine="target" :initial-task="taskId" @started="run => openRun(run.id)" @target="selectTarget" />
          <section v-show="pane === 'runs'" class="run-workspace" aria-label="Flow runs">
            <aside class="history-sidebar" aria-label="Runs for this flow">
              <div class="history-heading"><span>Runs</span><small>last {{ history.length }}</small></div>
              <div class="history-list">
                <button v-for="run in history" :key="run.id" type="button" class="history-row" :class="{ selected: run.id === runId }" :aria-pressed="run.id === runId"
                  :title="`${fullTime(runStartedAt(run))} · ${remoteAutomationState(run.state).label} · ${store.machineLabel(run.machine_name)}`" @click="openRun(run.id)">
                  <span class="status-dot" :class="run.state" /><strong>{{ relativeTime(runStartedAt(run)) }}</strong><span class="trigger">{{ runTrigger(run) }}</span>
                  <small>{{ store.machineLabel(run.machine_name) }}</small>
                </button>
                <p v-if="!history.length" class="history-empty">No runs yet</p>
              </div>
            </aside>
            <RemoteAutomationRunDetail v-if="selected" :run="selected" @open-run="openRun" />
            <div v-else class="pane-empty">
              <t-icon name="history" size="32px" />
              <strong>{{ store.detailLoading ? 'Loading runs…' : history.length ? 'Select a run' : 'No runs yet' }}</strong>
              <span>{{ history.length ? 'Pick a time on the left to see that run\'s steps and logs.' : 'Click "Run now" above; the new run is selected automatically.' }}</span>
              <t-alert v-if="store.detailError" theme="error" :message="store.detailError" />
            </div>
          </section>
        </main>
        <main v-else class="pane-empty">
          <t-icon name="task" size="32px" />
          <strong>Select a flow to view its parameters, plan and runs</strong>
          <span>Every run is shown step by step: what is running, what failed, and why.</span>
        </main>
      </slot>
    </div>
  </section>
</template>

<style scoped>
.automation-workspace { display: grid; grid-template-columns: 252px minmax(0, 1fr); grid-template-rows: minmax(0, 1fr); height: 100%; min-height: 560px; overflow: hidden; background: var(--bg-card); color: var(--text-primary); }

/* Task list */
.task-sidebar { display: flex; flex-direction: column; min-height: 0; padding: 12px 8px 8px; border-right: 1px solid var(--border-color); background: var(--bg-secondary); }
.task-search { margin: 0 2px 8px; width: auto; }
.task-views { display: flex; flex-direction: column; gap: 1px; padding-bottom: 8px; border-bottom: 1px solid var(--border-color); }
.view-row { display: grid; grid-template-columns: 16px minmax(0, 1fr) auto; align-items: center; gap: 8px; height: 30px; padding: 0 10px; border: 0; border-radius: 6px; background: transparent; color: var(--text-primary); font: inherit; font-size: 13px; text-align: left; cursor: pointer; }
.view-row > :deep(.t-icon) { color: var(--text-secondary); font-size: 15px; }
.view-row:hover { background: var(--bg-hover); }
.view-row.active { background: var(--color-primary-light); color: var(--color-link); font-weight: 500; }
.view-row.active > :deep(.t-icon) { color: var(--color-link); }
em { min-width: 22px; padding: 0 6px; border-radius: 999px; background: var(--bg-hover); color: var(--text-secondary); font-size: 11px; font-style: normal; font-weight: 400; line-height: 18px; text-align: center; font-variant-numeric: tabular-nums; }
.view-row.active em { background: var(--bg-card); color: var(--color-link); }
.view-dot { display: grid; place-items: center; }
.view-divider { height: 1px; margin: 4px 10px; background: var(--border-color); }
.list-heading { display: flex; align-items: center; gap: 6px; height: 34px; padding: 4px 4px 0 10px; color: var(--text-secondary); font-size: 12px; }
.list-heading small { font-size: 12px; }
.heading-add { margin-left: auto; color: var(--text-secondary); }
.task-list { flex: 1; min-height: 0; overflow-y: auto; display: flex; flex-direction: column; gap: 1px; }
.group-head { display: flex; align-items: center; gap: 6px; flex-shrink: 0; height: 30px; margin-top: 4px; padding: 0 6px 0 4px; border-radius: 6px; color: var(--text-secondary); font-size: 12px; font-weight: 500; cursor: pointer; user-select: none; outline: none; }
.group-head:hover, .group-head:focus-visible { background: var(--bg-hover); color: var(--text-primary); }
.group-head.dropping { background: var(--color-primary-light); box-shadow: inset 0 0 0 1px var(--color-primary-border); color: var(--color-link); }
.group-head.editing { padding-left: 10px; cursor: default; }
.group-head .caret { flex-shrink: 0; font-size: 14px; }
.group-head :deep(.t-input__wrap) { flex: 1; }
.group-name { flex: 1; min-width: 0; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; }
.group-empty { padding: 4px 10px 6px 30px; color: var(--text-disabled); font-size: 12px; }
.task-row { position: relative; display: flex; flex-shrink: 0; align-items: center; gap: 8px; height: 30px; padding: 0 4px 0 10px; border-radius: 6px; color: var(--text-primary); font-size: 13px; cursor: pointer; outline: none; }
.task-row:hover, .task-row:focus-visible { background: var(--bg-hover); }
.task-row.selected { background: var(--color-primary-light); color: var(--color-link); font-weight: 500; }
.task-row.selected::before { content: ''; position: absolute; left: 0; top: 7px; bottom: 7px; width: 3px; border-radius: 2px; background: var(--color-primary); }
.task-row.offline .row-name { color: var(--text-secondary); }
.row-state { display: grid; place-items: center; flex: 0 0 14px; }
.status-dot.never { background: transparent; box-shadow: inset 0 0 0 1.5px var(--text-disabled); }
.row-name { flex: 1; min-width: 0; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; }
.row-starred { flex-shrink: 0; color: var(--color-accent); font-size: 12px; }
.row-badge { flex-shrink: 0; }
.row-time { flex-shrink: 0; color: var(--text-secondary); font-size: 11px; font-weight: 400; font-variant-numeric: tabular-nums; }
.row-menu { display: contents; }
.row-action { display: none; place-items: center; flex: 0 0 22px; height: 22px; padding: 0; border: 0; border-radius: 4px; background: transparent; color: var(--text-secondary); font-size: 14px; cursor: pointer; }
.row-action:hover { background: var(--bg-card); color: var(--text-primary); }
.task-row:hover .row-action, .task-row:focus-within .row-action, .group-head:hover .row-action, .group-head:focus-within .row-action { display: grid; }
.task-row:hover .row-time, .task-row:focus-within .row-time { display: none; }
.list-empty { display: grid; gap: 6px; padding: 28px 12px; text-align: center; font-size: 12px; color: var(--text-secondary); }
.list-empty strong { color: var(--text-primary); font-weight: 500; font-size: 13px; }
.organize-error { margin-top: 8px; }

/* Status vocabulary shared by list, header and history */
.status-dot { display: inline-block; flex-shrink: 0; width: 7px; height: 7px; border-radius: 50%; background: var(--text-disabled); }
.status-dot.succeeded { background: var(--color-success); }
.status-dot.failed { background: var(--color-danger); }
.status-dot.running, .status-dot.accepted, .status-dot.queued { background: var(--color-link); }
.status-dot.awaiting_review, .status-dot.finalization_pending, .status-dot.unknown { background: var(--color-accent); }
.status-text { color: var(--text-secondary); white-space: nowrap; }
.status-text.succeeded { color: var(--color-success); }
.status-text.failed { color: var(--color-danger); }
.status-text.running, .status-text.accepted, .status-text.queued { color: var(--color-link); }
.status-text.awaiting_review { color: var(--color-accent); }

/* Task workspace */
.workspace-content, .task-workspace { display: flex; flex-direction: column; min-width: 0; min-height: 0; }
.workspace-content > .task-workspace, .workspace-content > .pane-empty { flex: 1; }
.task-header { display: flex; align-items: center; gap: 12px; padding: 10px 16px 2px; }
.header-identity { flex: 1; min-width: 0; display: grid; gap: 2px; }
.title-line { display: flex; align-items: baseline; gap: 10px; min-width: 0; }
.title-line h3 { margin: 0; font-size: 15px; font-weight: 600; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; max-width: 480px; }
.meta, .meta-line, .muted-inline { color: var(--text-secondary); font-size: 12px; }
.title-line .meta { white-space: nowrap; }
.meta-line { display: flex; align-items: center; flex-wrap: wrap; gap: 2px 6px; }
.meta-line :deep(.t-link) { gap: 4px; font-size: 12px; }
.separator { width: 1px; height: 12px; margin: 0 4px; background: var(--border-strong); }
.header-actions { display: flex; align-items: center; gap: 6px; }
.task-tabs { padding: 0 16px; border-bottom: 1px solid var(--border-color); background: transparent; }
.task-tabs :deep(.t-tabs__nav-wrap::after), .task-tabs :deep(.t-tabs__nav-container::after) { display: none; }
.task-tabs :deep(.t-tabs__header) { background: transparent; }
.task-tabs :deep(.t-tabs__content) { display: none; }
.tab-count { margin-left: 4px; color: var(--text-secondary); font-size: 12px; }
.task-alert { margin: 10px 16px 0; }
.task-body { flex: 1; min-height: 0; overflow: auto; }

/* Runs */
.run-workspace { flex: 1; min-height: 0; display: grid; grid-template-columns: 200px minmax(0, 1fr); }
.history-sidebar { display: flex; flex-direction: column; min-height: 0; border-right: 1px solid var(--border-color); }
.history-heading { display: flex; align-items: baseline; gap: 6px; padding: 10px 14px 6px; font-size: 13px; font-weight: 500; }
.history-heading small { color: var(--text-secondary); font-size: 12px; font-weight: 400; }
.history-list { flex: 1; min-height: 0; overflow-y: auto; display: flex; flex-direction: column; gap: 1px; padding: 0 6px 8px; }
.history-row { position: relative; display: grid; grid-template-columns: 7px minmax(0, 1fr) auto; align-items: center; gap: 0 10px; width: 100%; padding: 5px 8px 5px 10px; border: 0; border-radius: 6px; background: transparent; color: var(--text-primary); text-align: left; font: inherit; cursor: pointer; }
.history-row:hover { background: var(--bg-hover); }
.history-row.selected { background: var(--color-primary-light); }
.history-row.selected::before { content: ''; position: absolute; left: 0; top: 8px; bottom: 8px; width: 3px; border-radius: 2px; background: var(--color-primary); }
.history-row strong { overflow: hidden; font-size: 13px; font-weight: 500; font-variant-numeric: tabular-nums; white-space: nowrap; text-overflow: ellipsis; }
.history-row .trigger { color: var(--text-secondary); font-size: 12px; }
.history-row small { grid-column: 2 / -1; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; color: var(--text-secondary); font-size: 11px; }
.history-empty { margin: 8px 10px; color: var(--text-secondary); font-size: 12px; }
.pane-empty { display: grid; align-content: center; justify-items: center; gap: 10px; padding: 32px; text-align: center; color: var(--text-secondary); font-size: 12px; }
.pane-empty strong { color: var(--text-primary); font-size: 15px; font-weight: 500; }
.pane-empty span { max-width: 380px; line-height: 1.7; }

@media (max-width: 1280px) { .automation-workspace { grid-template-columns: 228px minmax(0, 1fr); } .run-workspace { grid-template-columns: 188px minmax(0, 1fr); } }
@media (max-width: 1000px) {
  .run-workspace { grid-template-columns: minmax(0, 1fr); grid-template-rows: auto minmax(0, 1fr); }
  .task-header { flex-wrap: wrap; }
  .header-identity { flex-basis: 220px; }
  .history-sidebar { border-right: 0; border-bottom: 1px solid var(--border-color); }
  .history-list { flex-direction: row; overflow-x: auto; }
  .history-row { min-width: 150px; }
}
@media (max-width: 640px) {
  .automation-workspace { grid-template-columns: clamp(124px, 28vw, 180px) minmax(0, 1fr); }
  .task-sidebar { padding-inline: 4px; }
  .task-header { padding: 10px 12px 2px; }
  .task-tabs { padding: 0 12px; }
}
</style>
