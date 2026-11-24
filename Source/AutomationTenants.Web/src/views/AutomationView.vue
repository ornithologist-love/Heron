<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import type { PrimaryTableCol } from 'tdesign-vue-next'
import type { RemoteAutomationRun } from '@/api'
import RemoteAutomationWorkspace from '@/components/RemoteAutomationWorkspace.vue'
import { remoteAutomationState, remoteRunFinished, remoteRunStage, remoteRunSource, useRemoteAutomationStore } from '@/stores/remoteAutomation'
import { useThemeStore } from '@/stores/theme'
import { durationText, fullTime, relativeTime, runStartedAt } from '@/utils/remoteAutomationFormat'

const route = useRoute(), router = useRouter(), store = useRemoteAutomationStore()
const theme = useThemeStore()

const view = computed(() => {
  if (route.query.view === 'history' && !route.query.task) return 'history'
  return route.query.view === 'fleet' ? 'fleet' : 'workspace'
})
const search = ref(''), machineFilter = ref(''), stateFilter = ref(''), sourceFilter = ref('')
const rootRuns = computed(() => store.runs.filter(run => !run.parent_run_id))
function family(run: RemoteAutomationRun) { return store.runs.filter(row => row.id === run.id || row.root_run_id === run.id) }
function stillRunning(run: RemoteAutomationRun) { return family(run).some(row => !remoteRunFinished(row.state)) }
const activeRuns = computed(() => rootRuns.value.filter(stillRunning))
const onlineMachines = computed(() => store.machines.filter(machine => store.online(machine)))
const visibleRuns = computed(() => {
  const keyword = search.value.trim().toLowerCase()
  return rootRuns.value.filter(row => (!machineFilter.value || family(row).some(member => member.machine_name === machineFilter.value))
    && (!stateFilter.value || (stateFilter.value === 'active' ? stillRunning(row) : row.state === stateFilter.value))
    && (!sourceFilter.value || (sourceFilter.value === 'local' ? row.source === 'local' : row.source !== 'local'))
    && (!keyword || [store.machineLabel(row.machine_name), row.task_name, remoteRunSource(row)].some(value => value.toLowerCase().includes(keyword))))
})
const last24h = computed(() => rootRuns.value.filter(run => store.now - Date.parse(runStartedAt(run)) < 86_400_000))
const successRate = computed(() => {
  const done = last24h.value.filter(run => remoteRunFinished(run.state) && run.state !== 'cancelled')
  return done.length ? `${Math.round(done.filter(run => run.state === 'succeeded').length / done.length * 100)}%` : '—'
})
const summary = computed(() => [
  { label: 'Runners online', value: `${onlineMachines.value.length} / ${store.machines.length}`, icon: 'server' },
  { label: 'Active runs', value: activeRuns.value.length, icon: 'play-circle' },
  { label: 'Awaiting review', value: activeRuns.value.filter(run => run.state === 'awaiting_review').length, icon: 'user-checked' },
  { label: 'Success rate (24h)', value: successRate.value, icon: 'chart-bar' },
])
const machineOptions = computed(() => store.machines.map(machine => ({ value: machine.machine_name, label: store.machineLabel(machine.machine_name) })))
const stateOptions = [
  { value: 'active', label: 'Active' },
  ...['running', 'queued', 'awaiting_review', 'succeeded', 'failed', 'cancelled'].map(state => ({ value: state, label: remoteAutomationState(state).label })),
]
const sourceOptions = [{ value: 'cloud', label: 'Cloud' }, { value: 'local', label: 'Local' }]
const columns: PrimaryTableCol[] = [
  { colKey: 'task', title: 'Flow', ellipsis: true, minWidth: 300 },
  { colKey: 'state', title: 'State', width: 150 },
  { colKey: 'stage', title: 'Current step', ellipsis: true, minWidth: 160 },
  { colKey: 'machine', title: 'Runner', ellipsis: true, width: 190 },
  { colKey: 'source', title: 'Source', width: 140 },
  { colKey: 'started', title: 'Started', width: 140 },
  { colKey: 'duration', title: 'Duration', width: 120 },
]
const filtered = computed(() => !!(search.value || machineFilter.value || stateFilter.value || sourceFilter.value))
watch(() => [route.query.tenant, store.tenants.length], () => {
  const tenant = route.query.tenant
  if (typeof tenant === 'string' && store.tenants.some(row => row.id === tenant) && tenant !== store.tenantId) store.selectTenant(tenant)
})
function switchView(next: string) {
  void router.push({ query: { tenant: store.tenantId || undefined, view: next === 'workspace' ? undefined : next } })
}
function clearFilters() { machineFilter.value = ''; stateFilter.value = ''; sourceFilter.value = ''; search.value = '' }
function selectView(next: string) {
  if (next === 'history') clearFilters()
  switchView(next)
}
const tabs = computed(() => [
  { label: 'Flows', value: 'workspace', icon: 'app' },
  { label: 'Run history', value: 'history', icon: 'history', count: rootRuns.value.length },
  { label: 'Runners', value: 'fleet', icon: 'server', count: `${onlineMachines.value.length}/${store.machines.length}` },
])
function openRun(row: RemoteAutomationRun) {
  void router.push({ query: { tenant: store.tenantId || undefined, view: 'detail', task: row.task_id, run: row.id } })
}
function machineRuns(name: string) { return store.runs.filter(run => run.machine_name === name) }
function machineActive(name: string) { return machineRuns(name).filter(run => !remoteRunFinished(run.state)) }

// Guided tour: short, concrete things a visitor can try in under a minute.
const TOUR_KEY = 'heron.demo.tour.hidden'
const tourHidden = ref(localStorage.getItem(TOUR_KEY) === '1')
function hideTour() { tourHidden.value = true; localStorage.setItem(TOUR_KEY, '1') }
function showTour() { tourHidden.value = false; localStorage.removeItem(TOUR_KEY) }
function latestRunOf(taskId: string, state?: string) {
  return rootRuns.value.find(run => run.task_id === taskId && (!state || run.state === state)) ?? rootRuns.value.find(run => run.task_id === taskId)
}
const tour = computed(() => [
  { step: '1', title: 'Approve a HumanReview gate', body: 'An agent run is suspended at a durable checkpoint. Approve it or send feedback for another revision.',
    go: () => { const run = latestRunOf('pr-code-review', 'awaiting_review'); if (run) openRun(run) } },
  { step: '2', title: 'Watch a cross-machine run', body: 'The CI graph fans out into Windows and Linux subgraphs. Switch to the node graph and open the live logs.',
    go: () => { const run = latestRunOf('multi-target-ci', 'running') ?? latestRunOf('multi-target-ci'); if (run) openRun(run) } },
  { step: '3', title: 'Inspect failure recovery', body: 'A failed smoke test skips downstream steps, enters the Recovery branch, and still runs Finally.',
    go: () => { const run = latestRunOf('multi-target-ci', 'failed'); if (run) openRun(run) } },
  { step: '4', title: 'Launch your own run', body: 'Edit typed parameters, preview the plan, pick a runner and hit Run now. Toggle "Inject a failure" to break it.',
    go: () => void router.push({ query: { tenant: store.tenantId || undefined, view: 'invoke', task: 'multi-target-ci', pane: 'parameters' } }) },
])
onMounted(() => { store.startPolling() })
onBeforeUnmount(() => { store.stopPolling() })
</script>

<template>
  <div class="console">
    <header class="topbar">
      <a class="brand" href="../" title="Back to portfolio">
        <svg viewBox="0 0 24 24" width="22" height="22" aria-hidden="true"><circle cx="5" cy="6" r="2.4" /><circle cx="5" cy="18" r="2.4" /><circle cx="19" cy="12" r="2.4" /><path d="M7.2 7.1 16.8 11M7.2 16.9 16.8 13" /></svg>
        <span><strong>Heron</strong> Pregel Console</span>
      </a>
      <nav class="views" aria-label="Console views">
        <button v-for="tab in tabs" :key="tab.value" type="button" :class="{ active: view === tab.value }" @click="selectView(tab.value)">
          <t-icon :name="tab.icon" /><span>{{ tab.label }}</span><em v-if="tab.count !== undefined">{{ tab.count }}</em>
        </button>
      </nav>
      <div class="topbar-actions">
        <span class="demo-badge" title="Every request is served by an in-browser simulator of the Go control plane"><i />Live demo · simulated backend</span>
        <t-tooltip v-if="tourHidden" content="Show the guided tour"><t-button variant="text" shape="square" aria-label="Show guided tour" @click="showTour"><template #icon><t-icon name="help-circle" /></template></t-button></t-tooltip>
        <t-tooltip :content="theme.resolved === 'dark' ? 'Light theme' : 'Dark theme'">
          <t-button variant="text" shape="square" aria-label="Toggle theme" @click="theme.toggle()"><template #icon><t-icon :name="theme.resolved === 'dark' ? 'sunny' : 'moon'" /></template></t-button>
        </t-tooltip>
        <a class="back" href="../"><t-icon name="arrow-left" />Portfolio</a>
      </div>
    </header>

    <section v-if="!tourHidden" class="tour" aria-label="Guided tour">
      <div class="tour-intro">
        <strong>Try it</strong>
        <span>Durable graph runs, live. Every control works.</span>
      </div>
      <button v-for="item in tour" :key="item.step" type="button" class="tour-card" @click="item.go()">
        <b>{{ item.step }}</b><span><strong>{{ item.title }}</strong><small>{{ item.body }}</small></span><t-icon name="chevron-right" />
      </button>
      <button type="button" class="tour-close" aria-label="Hide tour" @click="hideTour"><t-icon name="close" /></button>
    </section>

    <main class="stage">
      <div v-if="view === 'workspace'" class="frame">
        <RemoteAutomationWorkspace :show-task-list="true" />
      </div>

      <div v-else-if="view === 'history'" class="page">
        <div class="page-head">
          <div><h2>Run history</h2><p>Every root run across the tenant. Cross-machine subgraphs are grouped under their parent.</p></div>
        </div>
        <div class="toolbar">
          <t-input v-model="search" placeholder="Filter by flow or runner" clearable class="w-64"><template #prefix-icon><t-icon name="search" /></template></t-input>
          <t-select v-model="machineFilter" :options="machineOptions" placeholder="Runner" clearable class="w-48" />
          <t-select v-model="stateFilter" :options="stateOptions" placeholder="State" clearable class="w-40" />
          <t-select v-model="sourceFilter" :options="sourceOptions" placeholder="Source" clearable class="w-32" />
          <t-button v-if="filtered" variant="text" size="small" @click="clearFilters">Reset</t-button>
        </div>
        <div class="card table-card">
          <t-table :data="visibleRuns" :columns="columns" row-key="id" hover @row-click="({ row }: any) => openRun(row)">
            <template #task="{ row }"><span class="flow-cell"><strong>{{ row.task_name || row.task_id }}</strong><code>{{ row.id }}</code></span></template>
            <template #state="{ row }"><t-tag size="small" variant="light" :theme="remoteAutomationState(row.state).theme">{{ remoteAutomationState(row.state).label }}</t-tag></template>
            <template #stage="{ row }"><span class="muted">{{ remoteRunStage(row) }}</span></template>
            <template #machine="{ row }"><span class="runner-cell"><t-icon :name="store.machineLabel(row.machine_name) === row.machine_name ? 'desktop' : 'cloud'" />{{ store.machineLabel(row.machine_name) }}<em v-if="family(row).length > 1">+{{ family(row).length - 1 }}</em></span></template>
            <template #source="{ row }"><span class="muted">{{ remoteRunSource(row) }}</span></template>
            <template #started="{ row }"><span class="mono" :title="fullTime(runStartedAt(row))">{{ relativeTime(runStartedAt(row)) }}</span></template>
            <template #duration="{ row }"><span class="mono">{{ durationText(runStartedAt(row), row.snapshot?.ended_at, store.now) }}</span></template>
          </t-table>
        </div>
      </div>

      <div v-else class="page">
        <div class="page-head">
          <div><h2>Runners</h2><p>Agents connect outbound to the control plane, advertise capabilities and the flows they can run, and lease work.</p></div>
        </div>
        <div class="stats">
          <div v-for="item in summary" :key="item.label" class="card stat">
            <t-icon :name="item.icon" /><div><small>{{ item.label }}</small><strong>{{ item.value }}</strong></div>
          </div>
        </div>
        <div class="runners">
          <article v-for="m in store.machines" :key="m.machine_name" class="card runner" :class="{ offline: !store.online(m) }">
            <header>
              <t-icon :name="store.machineLabel(m.machine_name) === m.machine_name ? 'desktop' : 'cloud'" />
              <div><strong>{{ store.machineLabel(m.machine_name) }}</strong><small>{{ m.machine_name === store.tenants[0]?.server_machine ? 'Managed coordinator · no Agent needed' : `Agent v${m.host_version}` }} · seen {{ relativeTime(m.last_seen) }}</small></div>
              <span class="presence" :class="{ on: store.online(m) }">{{ store.online(m) ? 'Online' : 'Offline' }}</span>
            </header>
            <dl>
              <dt>Flows</dt><dd>{{ m.tasks.map(task => task.name).join(', ') }}</dd>
              <dt>Running now</dt><dd>{{ machineActive(m.machine_name).length ? machineActive(m.machine_name).map(run => run.task_name).join(', ') : 'Idle' }}</dd>
              <dt>Total runs</dt><dd>{{ machineRuns(m.machine_name).length }}</dd>
            </dl>
            <div class="caps"><code v-for="cap in m.capabilities" :key="cap">{{ cap }}</code></div>
            <p v-if="!m.capabilities.includes('pregel.inspect.v1')" class="warn">Outdated Agent: parameter preview and remote launch are unavailable until it updates.</p>
          </article>
        </div>
      </div>
    </main>
  </div>
</template>

<style scoped>
.console { display: flex; flex-direction: column; height: 100vh; height: 100dvh; min-height: 640px; background: var(--bg-secondary); }
.topbar { display: flex; align-items: center; gap: 20px; height: 52px; padding: 0 16px; flex-shrink: 0; border-bottom: 1px solid var(--border-color); background: var(--bg-card); }
.brand { display: flex; align-items: center; gap: 10px; color: var(--text-primary); text-decoration: none; font-size: 14px; white-space: nowrap; }
.brand svg { fill: var(--color-primary); stroke: var(--color-primary); stroke-width: 1.6; }
.brand strong { font-weight: 700; letter-spacing: -0.01em; }
.views { display: flex; gap: 2px; height: 100%; }
.views button { position: relative; display: flex; align-items: center; gap: 6px; padding: 0 12px; border: 0; background: transparent; color: var(--text-secondary); font: inherit; font-size: 13px; font-weight: 500; cursor: pointer; }
.views button:hover { color: var(--text-primary); }
.views button.active { color: var(--text-primary); }
.views button.active::after { content: ''; position: absolute; left: 10px; right: 10px; bottom: -1px; height: 2px; border-radius: 2px; background: var(--color-primary); }
.views em { padding: 0 6px; border-radius: 999px; background: var(--bg-hover); color: var(--text-secondary); font-size: 11px; font-style: normal; line-height: 18px; font-variant-numeric: tabular-nums; }
.topbar-actions { display: flex; align-items: center; gap: 4px; margin-left: auto; }
.demo-badge { display: inline-flex; align-items: center; gap: 6px; margin-right: 8px; padding: 3px 10px; border: 1px solid var(--border-color); border-radius: 999px; color: var(--text-secondary); font-size: 12px; white-space: nowrap; }
.demo-badge i { width: 7px; height: 7px; border-radius: 50%; background: var(--color-success); box-shadow: 0 0 0 3px color-mix(in srgb, var(--color-success) 22%, transparent); animation: pulse 2s ease-in-out infinite; }
@keyframes pulse { 50% { opacity: 0.45; } }
.back { display: inline-flex; align-items: center; gap: 4px; margin-left: 6px; padding: 5px 10px; border-radius: 6px; color: var(--text-secondary); font-size: 13px; text-decoration: none; }
.back:hover { background: var(--bg-hover); color: var(--text-primary); }

.tour { position: relative; display: grid; grid-template-columns: minmax(170px, 0.8fr) repeat(4, minmax(0, 1.3fr)); gap: 8px; padding: 8px 44px 8px 16px; flex-shrink: 0; border-bottom: 1px solid var(--border-color); background: var(--bg-card); }
.tour-intro { display: grid; align-content: center; gap: 2px; font-size: 12px; color: var(--text-secondary); line-height: 1.5; }
.tour-intro strong { color: var(--text-primary); font-size: 14px; }
.tour-card { display: flex; align-items: flex-start; gap: 10px; padding: 8px 8px 8px 10px; border: 1px solid var(--border-color); border-radius: 10px; background: var(--bg-secondary); color: var(--text-primary); font: inherit; text-align: left; cursor: pointer; transition: border-color .15s, background-color .15s, transform .15s; }
.tour-card:hover { border-color: var(--color-primary-border); background: var(--color-primary-light); transform: translateY(-1px); }
.tour-card b { display: grid; place-items: center; flex: 0 0 22px; height: 22px; border-radius: 50%; background: var(--color-primary); color: #fff; font-size: 12px; }
.tour-card span { display: grid; gap: 2px; min-width: 0; }
.tour-card strong { font-size: 13px; font-weight: 600; }
.tour-card small { display: -webkit-box; overflow: hidden; -webkit-line-clamp: 2; -webkit-box-orient: vertical; color: var(--text-secondary); font-size: 12px; line-height: 1.4; }
.tour-card > .t-icon { align-self: center; flex-shrink: 0; color: var(--text-secondary); }
.tour-close { position: absolute; top: 10px; right: 12px; display: grid; place-items: center; width: 26px; height: 26px; border: 0; border-radius: 6px; background: transparent; color: var(--text-secondary); cursor: pointer; }
.tour-close:hover { background: var(--bg-hover); color: var(--text-primary); }

.stage { flex: 1; min-height: 0; display: flex; flex-direction: column; }
.frame { flex: 1; min-height: 0; }
.page { flex: 1; min-height: 0; overflow: auto; padding: 24px; display: flex; flex-direction: column; gap: 16px; }
.page > * { width: 100%; max-width: 1320px; margin-inline: auto; }
.page-head h2 { margin: 0 0 4px; font-size: 20px; font-weight: 650; letter-spacing: -0.01em; }
.page-head p { margin: 0; color: var(--text-secondary); font-size: 13px; }
.toolbar { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
.card { border: 1px solid var(--border-color); border-radius: 12px; background: var(--bg-card); }
.table-card { overflow: hidden; }
.table-card :deep(.t-table__row) { cursor: pointer; }
.flow-cell { display: flex; align-items: baseline; gap: 8px; min-width: 0; }
.flow-cell strong { font-weight: 550; }
.flow-cell code, .mono { color: var(--text-secondary); font-size: 12px; font-variant-numeric: tabular-nums; }
.muted { color: var(--text-secondary); font-size: 12px; }
.runner-cell { display: inline-flex; align-items: center; gap: 6px; font-size: 12px; }
.runner-cell em { padding: 0 5px; border-radius: 4px; background: var(--bg-hover); color: var(--text-secondary); font-size: 11px; font-style: normal; }
.stats { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 12px; }
.stat { display: flex; align-items: center; gap: 14px; padding: 16px 18px; }
.stat > .t-icon { display: grid; place-items: center; width: 36px; height: 36px; padding: 8px; border-radius: 10px; background: var(--color-primary-light); color: var(--color-primary); font-size: 20px; box-sizing: border-box; }
.stat small { display: block; color: var(--text-secondary); font-size: 12px; }
.stat strong { font-size: 22px; font-weight: 650; font-variant-numeric: tabular-nums; }
.runners { display: grid; grid-template-columns: repeat(auto-fill, minmax(380px, 1fr)); gap: 12px; }
.runner { display: grid; gap: 14px; padding: 16px 18px; }
.runner.offline { opacity: 0.72; }
.runner header { display: flex; align-items: center; gap: 12px; }
.runner header > .t-icon { font-size: 20px; color: var(--text-secondary); }
.runner header div { flex: 1; display: grid; min-width: 0; }
.runner header strong { font-size: 14px; }
.runner header small { color: var(--text-secondary); font-size: 12px; }
.presence { display: inline-flex; align-items: center; gap: 6px; padding: 2px 10px; border-radius: 999px; background: var(--bg-hover); color: var(--text-secondary); font-size: 12px; }
.presence::before { content: ''; width: 6px; height: 6px; border-radius: 50%; background: var(--text-disabled); }
.presence.on { background: var(--status-success-bg); color: var(--status-success-fg); }
.presence.on::before { background: var(--color-success); }
.runner dl { display: grid; grid-template-columns: 96px 1fr; gap: 6px 12px; margin: 0; font-size: 12px; }
.runner dt { color: var(--text-secondary); }
.runner dd { margin: 0; }
.caps { display: flex; flex-wrap: wrap; gap: 6px; }
.caps code { padding: 2px 8px; border: 1px solid var(--border-color); border-radius: 6px; background: var(--bg-secondary); color: var(--text-secondary); font-size: 11px; }
.warn { margin: 0; padding: 8px 10px; border-radius: 8px; background: var(--status-warning-bg); color: var(--status-warning-fg); font-size: 12px; }

@media (max-width: 1200px) {
  .tour { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  .tour-intro { grid-column: 1 / -1; }
  .demo-badge { display: none; }
}
@media (max-width: 760px) {
  .stats { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  .views span { display: none; }
  .tour { grid-template-columns: 1fr; }
}
</style>
