<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { DialogPlugin, MessagePlugin } from 'tdesign-vue-next'
import type { RemoteAutomationReviewDecision, RemoteAutomationRun, RemoteAutomationStep } from '@/api'
import { remoteAutomationState, remoteRunFinished, useRemoteAutomationStore } from '@/stores/remoteAutomation'
import { useSiteAccessStore } from '@/stores/siteAccess'
import { durationText, fullTime, runStartedAt, runTrigger } from '@/utils/remoteAutomationFormat'
import { findRemoteStepPath, resolveRemoteStepPath } from '@/utils/remoteAutomationLogs'
import RemoteAutomationGraph from './RemoteAutomationGraph.vue'
import RemoteAutomationLogs from './RemoteAutomationLogs.vue'
import RemoteAutomationReview from './RemoteAutomationReview.vue'
import RemoteAutomationArtifacts from './RemoteAutomationArtifacts.vue'

const props = defineProps<{ run: RemoteAutomationRun }>()
const emit = defineEmits<{ openRun: [id: string] }>()
const store = useRemoteAutomationStore()
const access = useSiteAccessStore()
const graph = ref<InstanceType<typeof RemoteAutomationGraph> | null>(null)
const selectedKeys = ref<string[]>([])
const route = useRoute()
// A shared log link (?log=…) opens straight onto the logs.
const logsOpen = ref(typeof route.query.log === 'string')
const section = ref<'flow' | 'parameters' | 'artifacts'>('flow')
const children = computed(() => store.runs.filter(run => run.parent_run_id === props.run.id))
const selectedPath = computed(() => resolveRemoteStepPath(props.run.snapshot?.steps ?? [], selectedKeys.value))
const canControl = computed(() => !!store.runMachine(props.run)?.capabilities.includes('pregel.control.v1'))
const finished = computed(() => remoteRunFinished(props.run.state))
const canStop = computed(() => !finished.value && props.run.state !== 'awaiting_review'
  && (props.run.source !== 'local' || canControl.value) && !props.run.cancel_requested
  && props.run.control?.state !== 'pending' && !store.saving)
const state = computed(() => remoteAutomationState(props.run.state))
// AutomationPalette: a cancelled run reads as a warning; every other tone follows the shared state theme.
const stateTone = computed(() => props.run.state === 'cancelled' ? 'warning' : state.value.theme)
const artifacts = computed(() => props.run.snapshot?.artifacts ?? [])
const legacyLocations = computed(() => [...new Set([props.run.snapshot?.artifact_path, ...(props.run.snapshot?.evidence_directories ?? [])].filter((item): item is string => !!item))])
const launch = computed(() => props.run.snapshot?.launch)
const parameters = computed(() => {
  const values = launch.value?.parameters ?? props.run.snapshot?.task_snapshot?.parameters ?? props.run.parameters ?? {}
  const fields = new Map(launch.value?.fields.map(field => [field.key, field.label]) ?? [])
  const keys = [...new Set([...(launch.value?.fields.map(field => field.key) ?? []), ...Object.keys(values)])]
  return keys.filter(key => key in values).map(key => ({ key, label: fields.get(key) ?? key, value: values[key] }))
})
const parameterNotice = computed(() => launch.value
  ? "Parameters were frozen at launch, including the defaults and global settings in effect. Changing task settings won't alter this record."
  : props.run.snapshot?.task_snapshot ? 'This record only kept the parameters used; the full defaults snapshot is missing.'
    : props.run.snapshot ? 'This historical record has no full launch snapshot; showing submitted parameters only.'
      : "The runner hasn't reported its launch snapshot yet; showing submitted parameters.")
const notices = computed(() => {
  const rows: { theme: 'info' | 'warning' | 'error'; message: string }[] = []
  if (store.detailError) rows.push({ theme: 'error', message: `${store.detailError} Showing the last loaded record.` })
  if (store.runDisconnected(props.run)) rows.push({ theme: 'warning', message: 'Machine is offline; showing its last known state. Updates resume when it reconnects.' })
  if (props.run.cancel_requested && !finished.value) rows.push({ theme: 'info', message: 'Stop requested; waiting for the machine to cancel and clean up.' })
  if (props.run.control?.state === 'pending') rows.push({ theme: 'info', message: props.run.control.detail })
  else if (props.run.control?.state === 'failed') rows.push({ theme: 'error', message: `Action not applied: ${props.run.control.detail}` })
  if (props.run.snapshot?.warning) rows.push({ theme: 'warning', message: props.run.snapshot.warning })
  return rows
})
watch(() => props.run.id, () => { selectedKeys.value = []; section.value = 'flow' })
function selectStep(step: RemoteAutomationStep | null) {
  selectedKeys.value = step ? findRemoteStepPath(props.run.snapshot?.steps ?? [], step)?.map(item => item.key) ?? [] : []
  if (step) logsOpen.value = true
}
function clearSelection() { graph.value?.clearSelection(); selectedKeys.value = [] }
async function stop() {
  if (canStop.value && await store.requestStop(props.run)) MessagePlugin.success('Stop requested; waiting for the run to clean up')
}
// The way out when a stop or a decision cannot finish on the machine, e.g. its graph no longer loads.
const canDiscard = computed(() => !!access.userName && !finished.value && !store.saving
  && (props.run.state === 'awaiting_review' || props.run.cancel_requested))
function discard() {
  const dialog = DialogPlugin.confirm({
    header: 'Force end this run',
    body: "The record ends immediately and cleanup steps on the machine won't run (e.g. closing started processes, removing temp files). Use only when Stop or Abandon can't complete.",
    confirmBtn: 'Force end', cancelBtn: 'Cancel', theme: 'warning',
    onConfirm: async () => {
      dialog.hide()
      if (await store.requestDiscard(props.run)) MessagePlugin.success('Run force-ended')
    },
  })
}
async function review(decision: RemoteAutomationReviewDecision) {
  if (await store.requestReview(props.run, decision)) MessagePlugin.success('Decision submitted; waiting for the machine to respond')
}
async function copyParameters() {
  try {
    await navigator.clipboard.writeText(JSON.stringify(Object.fromEntries(parameters.value.map(item => [item.key, item.value])), null, 2))
    MessagePlugin.success('Parameters copied')
  } catch { MessagePlugin.warning('Copy failed; select the parameters manually') }
}
</script>

<template>
  <section class="run-detail" aria-label="Run details">
    <header class="run-header">
      <div class="run-title">
        <span class="run-state" :class="stateTone">{{ state.label }}</span>
        <strong :title="fullTime(runStartedAt(run))">{{ fullTime(runStartedAt(run)) }}</strong>
        <span>{{ runTrigger(run) }} triggered</span>
        <span v-if="run.created_by" :title="run.created_by">by {{ run.created_by }}</span>
        <span>Duration {{ durationText(run.snapshot?.started_at, run.snapshot?.ended_at, store.now) }}</span>
        <span><t-icon :name="store.machineLabel(run.machine_name) === run.machine_name ? 'desktop' : 'cloud'" /> {{ store.machineLabel(run.machine_name) }}</span>
      </div>
      <div class="run-actions">
        <t-button v-if="access.userName && !finished && run.state !== 'awaiting_review'" theme="danger" variant="outline" size="small" :disabled="!canStop" @click="stop">
          <template #icon><t-icon name="stop-circle" /></template>{{ run.cancel_requested ? 'Stopping…' : 'Stop' }}
        </t-button>
        <t-button v-if="canDiscard" theme="danger" variant="text" size="small" title="End this record directly when Stop or Abandon can't complete" @click="discard">Force end</t-button>
        <t-button variant="outline" size="small" :aria-expanded="logsOpen" @click="logsOpen = !logsOpen"><template #icon><t-icon name="file" /></template>Logs</t-button>
      </div>
      <p v-if="run.detail" class="run-summary" :title="run.detail">{{ run.detail }}</p>
      <p v-if="run.snapshot?.summary && run.snapshot.summary !== run.detail" class="run-summary">{{ run.snapshot.summary }}</p>
      <t-link v-if="run.parent_run_id" class="parent-link" theme="primary" hover="color" @click="emit('openRun', run.parent_run_id)">← Back to parent run</t-link>
    </header>
    <div class="run-body">
      <t-alert v-for="(notice, index) in notices" :key="index" class="run-alert" :theme="notice.theme" :message="notice.message" />
      <RemoteAutomationReview v-if="access.userName" :key="run.id" :run="run" :can-control="canControl" :busy="store.saving" @submit="review" />
      <t-tabs v-model="section" class="section-tabs compact-tabs" size="medium">
        <t-tab-panel value="flow" label="Flow" />
        <t-tab-panel value="parameters" label="Parameters" />
        <t-tab-panel value="artifacts"><template #label>Artifacts <span v-if="artifacts.length" class="tab-count">{{ artifacts.length }}</span></template></t-tab-panel>
      </t-tabs>
      <div v-show="section === 'flow'">
        <div v-if="children.length" class="children" aria-label="Remote subgraphs">
          <button v-for="child in children" :key="child.id" type="button" class="child-run" @click="emit('openRun', child.id)">
            <t-icon name="share" /><span>{{ child.task_name }} · {{ store.machineLabel(child.machine_name) }}</span>
            <t-tag size="small" variant="light" :theme="remoteAutomationState(child.state).theme">{{ remoteAutomationState(child.state).label }}</t-tag>
            <t-icon name="chevron-right" />
          </button>
        </div>
        <RemoteAutomationGraph ref="graph" :key="run.id" :steps="run.snapshot?.steps ?? []" @select="selectStep" @logs="logsOpen = true" />
      </div>
      <section v-show="section === 'parameters'" class="launch-parameters" aria-label="Run launch parameters">
        <p class="empty-text">{{ parameterNotice }}</p>
        <dl class="parameter-list launch-context">
          <dt>Cloud task</dt><dd>{{ run.task_name }}</dd>
          <dt>taskId</dt><dd>{{ run.task_id }}</dd>
          <template v-if="launch?.local_nickname || run.snapshot?.task_snapshot?.name"><dt>Local nickname</dt><dd>{{ launch?.local_nickname || run.snapshot?.task_snapshot?.name }}</dd></template>
          <dt>Machine</dt><dd>{{ launch?.machine_name || run.machine_name }}</dd>
          <dt>Trigger</dt><dd>{{ runTrigger(run) }}</dd>
          <dt>Script revision</dt><dd>{{ launch?.script_revision || run.script_revision || 'Not saved' }}</dd>
          <template v-if="run.snapshot?.task_snapshot?.template_id"><dt>Template</dt><dd>{{ run.snapshot.task_snapshot.template_id }}</dd></template>
        </dl>
        <div class="parameter-heading"><h5>Parameters</h5><t-button variant="text" size="small" @click="copyParameters">Copy</t-button></div>
        <dl v-if="parameters.length" class="parameter-list"><template v-for="item in parameters" :key="item.key"><dt :title="item.key">{{ item.label }}</dt><dd>{{ item.value === '' ? '(empty)' : item.value }}</dd></template></dl>
        <p v-else class="empty-text">{{ run.snapshot ? 'No parameter values were saved for this record.' : 'No explicit parameters; waiting for the runner to report defaults.' }}</p>
        <template v-if="launch?.rendered_parameters"><h5>Command-line arguments</h5><pre class="launch-command">{{ launch.rendered_parameters }}</pre></template>
      </section>
      <div v-show="section === 'artifacts'" class="artifacts">
        <RemoteAutomationArtifacts :artifacts="artifacts" :legacy-locations="legacyLocations" />
      </div>
    </div>
    <section class="log-drawer" :class="{ open: logsOpen }">
      <button type="button" class="log-toggle" :aria-expanded="logsOpen" @click="logsOpen = !logsOpen">
        <strong>Logs</strong><span>{{ fullTime(runStartedAt(run)) }} · {{ store.machineLabel(run.machine_name) }}</span><t-icon :name="logsOpen ? 'chevron-down' : 'chevron-up'" />
      </button>
      <RemoteAutomationLogs v-show="logsOpen" :key="run.id" :run="run" :lines="store.logLines"
        :selected-path="selectedPath" :loading="store.logsLoading" :loaded="store.logsLoaded" :drained="store.logsDrained"
        :has-more="store.hasMoreLogs" :loading-history="store.loadingLogHistory" :error="store.logsError"
        :disconnected="store.runDisconnected(run)" @clear-selection="clearSelection"
        @more="store.loadMoreLogs()" @history="store.loadLogHistory()" @stop-history="store.stopLogHistory()" @retry="store.refreshLogs(true)" />
    </section>
  </section>
</template>

<style scoped>
.run-detail { display: flex; flex-direction: column; min-width: 0; min-height: 0; color: var(--text-primary); }
/* Header, tabs and spacing follow the run view in AutomationPageView.axaml and AutomationWorkspace.axaml. */
.run-header { display: flex; align-items: flex-start; flex-wrap: wrap; gap: 6px 12px; padding: 14px 20px; border-bottom: 1px solid var(--border-subtle); }
.run-title { display: flex; align-items: center; flex-wrap: wrap; gap: 4px 10px; flex: 1; min-width: 0; min-height: 32px; color: var(--text-muted); font-size: 11px; font-weight: 500; line-height: 16px; }
.run-title strong { color: var(--text-default); font-size: 14px; font-weight: 600; line-height: 22px; font-variant-numeric: tabular-nums; }
.run-title span { display: inline-flex; align-items: center; gap: 4px; }
.run-title .run-state { padding: 3px 10px; border-radius: 12px; font-size: 12px; font-weight: 600; line-height: 18px; color: var(--status-muted-fg); background: var(--status-muted-bg); }
.run-state.success { color: var(--status-success-fg); background: var(--status-success-bg); }
.run-state.danger { color: var(--status-danger-fg); background: var(--status-danger-bg); }
.run-state.primary { color: var(--status-info-fg); background: var(--status-info-bg); }
.run-state.warning { color: var(--status-warning-fg); background: var(--status-warning-bg); }
.run-actions { display: flex; gap: 8px; }
.run-actions :deep(.t-button) { height: 32px; padding: 0 10px; border-radius: 6px; font-size: 12px; font-weight: 500; }
.run-actions :deep(.t-button--variant-outline.t-button--theme-default) { background: var(--bg-card); border-color: var(--border-strong); color: var(--text-default); }
.run-summary { flex-basis: 100%; margin: 0; color: var(--text-muted); font-size: 12px; line-height: 18px; overflow: hidden; display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; overflow-wrap: anywhere; }
.parent-link { font-size: 12px; }
.run-body { flex: 1; min-height: 140px; overflow: auto; padding: 4px 20px 16px; }
.run-alert { margin-top: 8px; }
.section-tabs { margin: 0 0 16px; background: transparent; }
.section-tabs :deep(.t-tabs__header) { background: transparent; }
.section-tabs :deep(.t-tabs__content) { display: none; }
.section-tabs :deep(.t-tabs__nav-container::after) { display: none; }
.section-tabs :deep(.t-tabs__nav-item.t-size-m) { height: 32px; line-height: 32px; font-size: 13px; color: var(--text-secondary); }
.section-tabs :deep(.t-tabs__nav-item-wrapper) { height: 32px; margin: 0 22px 0 0; padding: 0; }
.section-tabs :deep(.t-tabs__nav-item:first-of-type .t-tabs__nav-item-wrapper) { margin-left: 0; }
.section-tabs :deep(.t-tabs__nav-item:hover) { color: var(--text-primary); }
.section-tabs :deep(.t-tabs__nav-item:hover .t-tabs__nav-item-wrapper) { background: transparent; }
.section-tabs :deep(.t-tabs__nav-item.t-is-active) { color: var(--text-primary); font-weight: 600; }
.section-tabs :deep(.t-tabs__bar) { background: var(--ksf-brand); }
.tab-count { margin-left: 4px; color: var(--text-secondary); font-size: 12px; font-weight: 400; }
.children { display: grid; gap: 4px; margin-bottom: 8px; }
.child-run { display: flex; align-items: center; gap: 10px; padding: 6px 10px; border: 1px solid var(--border-color); border-radius: 8px; background: var(--bg-secondary); color: var(--text-primary); font: inherit; font-size: 13px; text-align: left; cursor: pointer; }
.child-run:hover { border-color: var(--border-strong); background: var(--bg-hover); }
.child-run > span { flex: 1; min-width: 0; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; }
.artifacts h5 { margin: 4px 0 10px; font-size: 13px; font-weight: 600; }
.artifacts h5:not(:first-child) { margin-top: 22px; }
.path-list { display: grid; gap: 6px; margin: 0; padding: 0; list-style: none; }
.path-list li { display: flex; align-items: center; gap: 8px; padding: 6px 6px 6px 10px; border: 1px solid var(--border-color); border-radius: 6px; background: var(--bg-secondary); }
.path-list code { flex: 1; min-width: 0; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; font: 12px/1.6 ui-monospace, 'Cascadia Code', Consolas, monospace; }
.parameter-list { display: grid; grid-template-columns: minmax(120px, max-content) minmax(0, 1fr); gap: 8px 20px; margin: 0; font-size: 12px; }
.parameter-list dt { color: var(--text-secondary); }
.parameter-list dd { margin: 0; overflow-wrap: anywhere; font-family: ui-monospace, 'Cascadia Code', Consolas, monospace; }
.empty-text { margin: 0; color: var(--text-secondary); font-size: 12px; }
.launch-parameters { padding: 4px 0 12px; }
.launch-context { margin: 16px 0; padding-bottom: 16px; border-bottom: 1px solid var(--border-color); }
.parameter-heading { display: flex; align-items: center; justify-content: space-between; margin-bottom: 8px; }
.launch-parameters h5 { margin: 12px 0 8px; font-size: 13px; font-weight: 600; }
.parameter-heading h5 { margin: 0; }
.launch-command { white-space: pre-wrap; overflow-wrap: anywhere; font: 12px/1.7 ui-monospace, Consolas, monospace; }
.log-drawer { flex-shrink: 0; border-top: 1px solid var(--border-color); }
.log-drawer.open { display: flex; flex-direction: column; height: 60%; min-height: 0; }
.log-drawer.open > :deep(.run-logs) { flex: 1; min-height: 0; }
.log-toggle { display: flex; align-items: center; gap: 12px; width: 100%; height: 40px; padding: 0 14px 0 20px; border: 0; border-radius: 0; background: transparent; color: var(--text-primary); font: inherit; font-size: 13px; cursor: pointer; flex-shrink: 0; }
.log-toggle:hover { background: var(--bg-hover); }
.log-toggle:active { background: var(--bg-pressed); }
.log-toggle strong { font-weight: 500; }
.log-toggle > span { flex: 1; color: var(--text-secondary); font-size: 12px; text-align: left; }
@media (max-width: 760px) { .run-header, .run-body { padding-left: 12px; padding-right: 12px; } }
</style>
