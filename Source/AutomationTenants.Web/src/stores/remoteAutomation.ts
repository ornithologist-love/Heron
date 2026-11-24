import { computed, ref, shallowRef, toRaw } from 'vue'
import { defineStore } from 'pinia'
import axios from 'axios'
import {
  createRemoteAutomationRun, fetchRemoteAutomation, fetchRemoteAutomationLogs,
  fetchRemoteAutomationRun, inspectRemoteAutomation, saveRemoteAutomationOrganization, saveRemoteAutomationSchedule, type RemoteAutomationOrganization,
  type RemoteAutomationScheduleDraft, type RemoteAutomationTeamSchedule, stopRemoteAutomationRun, discardRemoteAutomationRun, reviewRemoteAutomationRun,
  type RemoteAutomationInventory, type RemoteAutomationLogLine, type RemoteAutomationMachine,
  type RemoteAutomationRun, type RemoteAutomationRunDraft, type RemoteAutomationStep,
  type RemoteAutomationTenant, type RemoteAutomationInspection, type RemoteAutomationReviewDecision, type RemoteAutomationTask,
} from '@/api'

export interface RemoteAutomationTaskEntry {
  id: string
  name: string
  targets: { machine: RemoteAutomationMachine; task: RemoteAutomationTask }[]
}

/** The no-machine target: no build machine or Agent is chosen. Whatever hosts it today (the tenant's
 * server_machine) is an implementation detail, so the console never names that host as the target. */
export const AGENTLESS_LABEL = 'Serverless'

const finishedStates = new Set(['succeeded', 'skipped', 'failed', 'cancelled', 'unknown'])
export function remoteRunFinished(state: string) { return finishedStates.has(state) }

export function remoteAutomationState(state?: string): { label: string; theme: 'default' | 'primary' | 'success' | 'warning' | 'danger' } {
  switch (state) {
    case 'pending': case 'not_run': return { label: state === 'not_run' ? 'Not run' : 'Pending', theme: 'default' }
    case 'queued': return { label: 'Queued', theme: 'primary' }
    case 'accepted': return { label: 'Accepted', theme: 'primary' }
    case 'running': return { label: 'Running', theme: 'primary' }
    case 'awaiting_review': return { label: 'Awaiting review', theme: 'warning' }
    case 'finalization_pending': return { label: 'Finalizing', theme: 'warning' }
    case 'succeeded': return { label: 'Succeeded', theme: 'success' }
    case 'failed': return { label: 'Failed', theme: 'danger' }
    case 'cancelled': return { label: 'Cancelled', theme: 'default' }
    case 'skipped': return { label: 'Skipped', theme: 'default' }
    case 'unknown': return { label: 'Unknown', theme: 'warning' }
    default: return { label: state || 'Pending report', theme: 'default' }
  }
}

export function remoteStepProgress(step: RemoteAutomationStep): number | null {
  return typeof step.progress === 'number' && Number.isFinite(step.progress)
    ? Math.round(Math.max(0, Math.min(1, step.progress)) * 100) : null
}

export function remoteRunStage(run: RemoteAutomationRun): string {
  const active = new Set(['running', 'queued', 'awaiting_review', 'finalization_pending'])
  function find(steps: RemoteAutomationStep[]): string[] | undefined {
    for (const step of steps) {
      const child = find(step.children ?? [])
      if (child) return step.is_stage ? [step.title, ...child] : child
      if (active.has(step.state ?? '')) return [step.title]
    }
    return undefined
  }
  return find(run.snapshot?.steps ?? [])?.join(' / ') ?? '—'
}

export function remoteRunSource(run: RemoteAutomationRun): string {
  if (run.source !== 'local') return 'Cloud'
  const trigger = run.snapshot?.trigger?.toLowerCase() ?? ''
  if (trigger.includes('schedule') || trigger.includes('定时')) return 'Local · schedule'
  if (trigger.includes('cli')) return 'Local · CLI'
  if (trigger.includes('manual') || trigger.includes('手动')) return 'Local · manual'
  return 'Local'
}

export function remoteAutomationError(cause: unknown): string {
  if (axios.isAxiosError(cause)) {
    if (cause.response?.status === 401) return 'Your session has expired. Please sign in to the console again.'
    if (cause.response?.status === 403) return 'This account is not allowed to manage remote tasks for this tenant.'
    const body = cause.response?.data
    if (typeof body?.error === 'string') return body.error
    if (typeof body?.error?.message === 'string') return body.error.message
    if (typeof body?.detail === 'string') return body.detail
    return 'Could not load remote tasks. Check your connection and try again.'
  }
  return cause instanceof Error ? cause.message : 'The remote task operation failed. Please try again later.'
}

export const useRemoteAutomationStore = defineStore('remoteAutomation', () => {
  const inventory = ref<RemoteAutomationInventory>({ tenants: [], machines: [], runs: [] })
  // Keep the accessible tenant directory when a filtered inventory contains only one tenant.
  const tenants = ref<RemoteAutomationTenant[]>([])
  const tenantId = ref('')
  const selectedRunId = ref('')
  const selectedRun = ref<RemoteAutomationRun | null>(null)
  const logLines = shallowRef<RemoteAutomationLogLine[]>([])
  const logCursor = ref(0)
  const hasMoreLogs = ref(false)
  const logsLoading = ref(false)
  const logsLoaded = ref(false)
  const logsDrainedRevision = ref<number | null>(null)
  const loadingLogHistory = ref(false)
  const logsDrained = computed(() => !!selectedRun.value?.logs_complete
    && logsDrainedRevision.value === selectedRun.value.revision)
  const loading = ref(false)
  const detailLoading = ref(false)
  const saving = ref(false)
  const inventoryError = ref('')
  const detailError = ref('')
  const logsError = ref('')
  const actionError = ref('')
  const now = ref(Date.now())
  const inspection = ref<RemoteAutomationInspection | null>(null)
  const inspecting = ref(false)
  const inspectionReady = ref(false)
  const inspectionError = ref('')
  const error = computed(() => [actionError.value, inventoryError.value].filter(Boolean).join(' '))
  const machines = computed(() => inventory.value.machines.filter(machine => machine.tenant_id === tenantId.value))
  const runs = computed(() => inventory.value.runs.filter(run => run.tenant_id === tenantId.value)
    .slice().sort((a, b) => Date.parse(b.created_at) - Date.parse(a.created_at)))
  // Polls must not roll back an arrangement the user just changed, so writes bump a generation and
  // only polls started after the last write settled may replace it.
  const organizations = ref<Record<string, RemoteAutomationOrganization>>({})
  const organization = computed<RemoteAutomationOrganization>(() => organizations.value[tenantId.value] ?? { groups: [], assignments: {}, starred: [] })
  const organizationError = ref('')
  let organizationWrites = 0, organizationPending = 0
  async function organize(change: (current: RemoteAutomationOrganization) => RemoteAutomationOrganization) {
    const tenant = tenantId.value, previous = organization.value
    const next = change(JSON.parse(JSON.stringify(previous)) as RemoteAutomationOrganization)
    organizations.value = { ...organizations.value, [tenant]: next }
    organizationWrites += 1; organizationPending += 1
    try {
      const saved = await saveRemoteAutomationOrganization(tenant, next)
      if (toRaw(organizations.value[tenant]) === next) organizations.value = { ...organizations.value, [tenant]: saved }
      organizationError.value = ''
    } catch (cause) {
      if (toRaw(organizations.value[tenant]) === next) organizations.value = { ...organizations.value, [tenant]: previous }
      organizationError.value = remoteAutomationError(cause)
    } finally {
      organizationPending -= 1; organizationWrites += 1
    }
  }
  // Task identity owns navigation and history. Machines only advertise execution availability.
  /** Undefined when this viewer cannot see team schedules (team access or an older server). */
  const schedules = computed<RemoteAutomationTeamSchedule[] | undefined>(() => inventory.value.schedules?.[tenantId.value])
  async function saveSchedule(draft: RemoteAutomationScheduleDraft) {
    const saved = await saveRemoteAutomationSchedule(draft)
    const current = inventory.value.schedules ?? {}
    const rows = (current[draft.tenant_id] ?? []).filter(row => row.task_id !== saved.task_id)
    inventory.value = { ...inventory.value, schedules: { ...current, [draft.tenant_id]: [...rows, saved] } }
    return saved
  }

  const tasks = computed(() => {
    const entries = new Map<string, RemoteAutomationTaskEntry>()
    for (const task of inventory.value.tasks ?? [])
      if (task.tenant_id === tenantId.value) entries.set(task.id, { id: task.id, name: task.name, targets: [] })
    for (const machine of machines.value)
      for (const task of machine.tasks) {
        if (inventory.value.tasks && !entries.has(task.id)) continue
        const entry: RemoteAutomationTaskEntry = entries.get(task.id) ?? { id: task.id, name: task.name, targets: [] }
        entry.targets.push({ machine, task })
        entries.set(task.id, entry)
      }
    for (const run of runs.value)
      if (!inventory.value.tasks && !entries.has(run.task_id)) entries.set(run.task_id, { id: run.task_id, name: run.task_name, targets: [] })
    return [...entries.values()]
  })
  let active = false
  let lifecycle = 0
  let selection = 0
  let timer: ReturnType<typeof setTimeout> | undefined
  let inventoryRequest: AbortController | null = null
  let detailRequest: AbortController | null = null
  let logsRequest: AbortController | null = null
  let historyRevision = 0
  let actionRequest: AbortController | null = null
  let inspectionRequest: AbortController | null = null
  let inspectionRevision = 0

  function cancelInspection(clear = true) {
    inspectionRevision += 1
    inspectionRequest?.abort()
    inspectionRequest = null
    inspecting.value = false
    inspectionReady.value = false
    inspectionError.value = ''
    if (clear) inspection.value = null
  }
  async function inspect(draft: RemoteAutomationRunDraft) {
    cancelInspection(false)
    const revision = inspectionRevision
    const controller = inspectionRequest = new AbortController()
    inspecting.value = true
    try {
      const result = await inspectRemoteAutomation(draft, controller.signal)
      if (revision !== inspectionRevision || controller.signal.aborted || draft.tenant_id !== tenantId.value) return
      inspection.value = result
      inspectionReady.value = true
      return result
    } catch (cause) {
      if (revision === inspectionRevision && !controller.signal.aborted && !axios.isCancel(cause))
        inspectionError.value = remoteAutomationError(cause)
    } finally {
      if (inspectionRequest === controller) { inspectionRequest = null; inspecting.value = false }
    }
  }

  function online(machine: RemoteAutomationMachine): boolean {
    const time = Date.parse(machine.last_seen)
    return Number.isFinite(time) && now.value - time < 90_000
  }
  function machineLabel(name: string) {
    const host = tenants.value.find(tenant => tenant.id === tenantId.value)?.server_machine
    return host && name.toLowerCase() === host.toLowerCase() ? AGENTLESS_LABEL : name
  }
  function runMachine(run: RemoteAutomationRun) {
    return machines.value.find(machine => machine.machine_name.toLowerCase() === run.machine_name.toLowerCase())
  }
  function runDisconnected(run: RemoteAutomationRun) {
    const machine = runMachine(run)
    return !remoteRunFinished(run.state) && (!machine || !online(machine))
  }
  function upsertRun(run: RemoteAutomationRun) {
    const index = inventory.value.runs.findIndex(row => row.id === run.id && row.tenant_id === run.tenant_id)
    if (index < 0) inventory.value.runs.unshift(run)
    else inventory.value.runs[index] = latestRun(inventory.value.runs[index], run)
  }
  function latestRun(previous: RemoteAutomationRun | null | undefined, next: RemoteAutomationRun) {
    if (!previous || previous.id !== next.id || previous.tenant_id !== next.tenant_id) return next
    if ((previous.revision ?? 0) > (next.revision ?? 0)) return previous
    if ((previous.revision ?? 0) === (next.revision ?? 0) && Date.parse(previous.updated_at) > Date.parse(next.updated_at)) return previous
    return next
  }
  function resetSelection() {
    selection += 1
    detailRequest?.abort()
    detailRequest = null
    stopLogHistory()
    selectedRunId.value = ''
    selectedRun.value = null
    logLines.value = []
    logCursor.value = 0
    hasMoreLogs.value = false
    logsLoaded.value = false
    logsDrainedRevision.value = null
    detailLoading.value = false
    detailError.value = logsError.value = ''
  }

  async function refreshSelected() {
    if (!active || !selectedRunId.value || detailRequest) return
    const controller = detailRequest = new AbortController()
    const epoch = lifecycle, revision = selection, id = selectedRunId.value, tenant = tenantId.value
    detailLoading.value = true
    const current = () => active && epoch === lifecycle && revision === selection
      && tenant === tenantId.value && id === selectedRunId.value && !controller.signal.aborted
    try {
      const run = await fetchRemoteAutomationRun(id, tenant, controller.signal)
      if (!current()) return
      selectedRun.value = latestRun(selectedRun.value, run)
      upsertRun(run)
      detailError.value = ''
    } catch (cause) {
      if (current() && !axios.isCancel(cause)) detailError.value = remoteAutomationError(cause)
    } finally {
      if (detailRequest === controller) { detailRequest = null; detailLoading.value = false }
    }
  }

  async function refreshLogs(explicit = false, pages = 1) {
    if (!active || !selectedRunId.value || logsRequest) return
    // Long histories are read on demand. Polling never silently downloads an entire large run.
    if (!explicit && logsLoaded.value && (hasMoreLogs.value || (selectedRun.value?.logs_complete
      && logsDrainedRevision.value === selectedRun.value.revision && !logsError.value))) return
    const controller = logsRequest = new AbortController()
    const epoch = lifecycle, revision = selection, id = selectedRunId.value, tenant = tenantId.value
    const current = () => active && epoch === lifecycle && revision === selection
      && tenant === tenantId.value && id === selectedRunId.value && !controller.signal.aborted
    logsLoading.value = true
    try {
      for (let page = 0; page < pages; page++) {
        const cursor = logCursor.value
        const completionRevision = selectedRun.value?.logs_complete ? selectedRun.value.revision : null
        const logs = await fetchRemoteAutomationLogs(id, tenant, cursor, controller.signal)
        if (!current()) return
        if (logs.next_cursor < cursor || (logs.has_more && logs.next_cursor === cursor))
          throw new Error('The log cursor did not advance. Please retry; logs already loaded are kept.')
        // Retain every received event. The viewer renders only visible rows, never discards old history.
        logLines.value = logLines.value.concat(logs.lines.filter(line => line.sequence > cursor))
        logCursor.value = logs.next_cursor
        hasMoreLogs.value = logs.has_more
        logsLoaded.value = true
        // Only a read started after observing completion can confirm the final tail. The run
        // request is concurrent, so its later completion flag cannot certify an earlier log read.
        logsDrainedRevision.value = !logs.has_more ? completionRevision : null
        logsError.value = ''
        if (!logs.has_more) break
      }
    } catch (cause) {
      if (current() && !axios.isCancel(cause)) logsError.value = remoteAutomationError(cause)
    } finally {
      if (logsRequest === controller) { logsRequest = null; logsLoading.value = false }
    }
  }

  const loadMoreLogs = () => refreshLogs(true, 4)
  function stopLogHistory() {
    historyRevision += 1
    loadingLogHistory.value = false
    logsRequest?.abort()
    logsRequest = null
    logsLoading.value = false
  }
  async function loadLogHistory() {
    if (loadingLogHistory.value || logsLoading.value) return
    const revision = ++historyRevision
    loadingLogHistory.value = true
    try {
      do {
        await refreshLogs(true, 2)
        if (revision !== historyRevision || !hasMoreLogs.value || logsError.value) break
        await new Promise(resolve => setTimeout(resolve, 200))
      } while (active && revision === historyRevision)
    } finally {
      if (revision === historyRevision) loadingLogHistory.value = false
    }
  }

  async function selectRun(id: string) {
    if (id === selectedRunId.value) return
    resetSelection()
    selectedRunId.value = id
    selectedRun.value = runs.value.find(run => run.id === id) ?? null
    await Promise.all([refreshSelected(), refreshLogs()])
  }

  async function refreshInventory() {
    if (!active || inventoryRequest) return
    const controller = inventoryRequest = new AbortController()
    const epoch = lifecycle, tenant = tenantId.value, writes = organizationWrites
    loading.value = true
    try {
      const result = await fetchRemoteAutomation(tenant, controller.signal)
      if (!active || epoch !== lifecycle || tenant !== tenantId.value || controller.signal.aborted) return
      const previous = new Map(inventory.value.runs.map(run => [`${run.tenant_id}/${run.id}`, run]))
      result.runs = result.runs.map(run => latestRun(previous.get(`${run.tenant_id}/${run.id}`), run))
      inventory.value = result
      if (!organizationPending && writes === organizationWrites) organizations.value = result.organizations ?? {}
      for (const row of result.tenants) {
        const index = tenants.value.findIndex(item => item.id === row.id)
        if (index < 0) tenants.value.push(row)
        else tenants.value[index] = row
      }
      if (!tenantId.value) tenantId.value = result.tenants[0]?.id ?? ''
      now.value = Date.now()
      inventoryError.value = ''
    } catch (cause) {
      if (active && epoch === lifecycle && !controller.signal.aborted && !axios.isCancel(cause))
        inventoryError.value = remoteAutomationError(cause)
    } finally {
      if (inventoryRequest === controller) { inventoryRequest = null; loading.value = false }
    }
  }

  async function refresh() {
    now.value = Date.now()
    await Promise.all([refreshInventory(), refreshSelected(), refreshLogs()])
  }
  function stopPolling() {
    cancelInspection()
    active = false
    lifecycle += 1
    if (timer) clearTimeout(timer)
    timer = undefined
    inventoryRequest?.abort()
    detailRequest?.abort()
    stopLogHistory()
    actionRequest?.abort()
    inventoryRequest = detailRequest = actionRequest = null
    loading.value = detailLoading.value = saving.value = false
  }
  function startPolling() {
    stopPolling()
    active = true
    tenants.value = []
    tenantId.value = ''
    inventory.value = { tenants: [], machines: [], runs: [] }
    organizations.value = {}
    resetSelection()
    inventoryError.value = actionError.value = ''
    const epoch = lifecycle
    const poll = async () => {
      await refresh()
      if (active && epoch === lifecycle) timer = setTimeout(() => { void poll() }, 2000)
    }
    void poll()
  }
  function selectTenant(id: string) {
    if (id === tenantId.value || saving.value) return
    inventoryRequest?.abort()
    inventoryRequest = null
    tenantId.value = id
    cancelInspection()
    resetSelection()
    inventoryError.value = actionError.value = ''
    void refresh()
  }

  async function mutate(action: (signal: AbortSignal) => Promise<RemoteAutomationRun>, selectResult = false) {
    if (!active || saving.value) return
    const controller = actionRequest = new AbortController()
    const epoch = lifecycle, tenant = tenantId.value
    saving.value = true
    actionError.value = ''
    try {
      const run = await action(controller.signal)
      if (!active || epoch !== lifecycle || tenant !== tenantId.value || controller.signal.aborted) return
      // A pre-action read must not replace the accepted command or its cancellation result.
      inventoryRequest?.abort()
      inventoryRequest = null
      loading.value = false
      if (selectedRunId.value === run.id || selectResult) {
        selection += 1
        detailRequest?.abort()
        detailRequest = null
        detailLoading.value = false
        stopLogHistory()
      }
      upsertRun(run)
      if (selectedRunId.value === run.id) selectedRun.value = run
      else if (selectResult) await selectRun(run.id)
      return run
    } catch (cause) {
      if (active && epoch === lifecycle && !controller.signal.aborted && !axios.isCancel(cause))
        actionError.value = remoteAutomationError(cause)
    } finally {
      if (actionRequest === controller) { actionRequest = null; saving.value = false }
    }
  }
  const run = (draft: RemoteAutomationRunDraft) => mutate(signal => createRemoteAutomationRun(draft, signal), true)
  // A new run of the same task on the same machine, with the launch parameters recorded on the source run.
  const rerun = (source: RemoteAutomationRun) => run({ tenant_id: source.tenant_id, machine_name: source.machine_name,
    task_id: source.task_id, parameters: { ...source.parameters } })
  const requestStop = (run: RemoteAutomationRun) => mutate(signal => stopRemoteAutomationRun(run.id, run.tenant_id, signal))
  const requestDiscard = (run: RemoteAutomationRun) => mutate(signal => discardRemoteAutomationRun(run.id, run.tenant_id, signal))
  const requestReview = (run: RemoteAutomationRun, decision: RemoteAutomationReviewDecision) =>
    mutate(signal => reviewRemoteAutomationRun(run.id, run.tenant_id, decision, signal))

  return {
    tenants, tenantId, machines, tasks, runs, schedules, saveSchedule, organization, organizationError, organize, selectedRunId, selectedRun, logLines, logCursor,
    hasMoreLogs, logsLoading, logsLoaded, logsDrained, loadingLogHistory, loading, detailLoading, saving, error, detailError, logsError, now,
    inspection, inspecting, inspectionReady, inspectionError, inspect, cancelInspection,
    online, machineLabel, runMachine, runDisconnected, refresh, refreshLogs, loadMoreLogs, loadLogHistory, stopLogHistory,
    selectRun, selectTenant, startPolling, stopPolling, run, rerun, requestStop, requestDiscard, requestReview,
  }
})
