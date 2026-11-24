<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import type { RemoteAutomationField, RemoteAutomationRun } from '@/api'
import RemoteAutomationGraph from './RemoteAutomationGraph.vue'
import RemoteAutomationTriggers from './RemoteAutomationTriggers.vue'
import { AGENTLESS_LABEL, useRemoteAutomationStore } from '@/stores/remoteAutomation'

const props = defineProps<{ initialMachine?: string; initialTask?: string; pane?: string; active?: boolean }>()
const emit = defineEmits<{ started: [run: RemoteAutomationRun]; target: [value: string] }>()
const store = useRemoteAutomationStore()
// Empty = the server executes the graph itself without an Agent (API: empty machine_name).
const AGENTLESS_OPTION = '__agentless__'
const machineName = ref(props.initialMachine ?? '')
const taskId = ref(props.initialTask ?? '')
const draft = ref<Record<string, string>>({})
const content = ref<HTMLElement | null>(null)
const activeSection = ref('')
let timer: ReturnType<typeof setTimeout> | undefined
const serverMachineName = computed(() => store.tenants.find(row => row.id === store.tenantId)?.server_machine ?? '')
const executionMachineName = computed(() => machineName.value || serverMachineName.value)
const requestMachineName = computed(() => machineName.value === serverMachineName.value ? '' : machineName.value)
const machine = computed(() => store.machines.find(row => row.machine_name.toLowerCase() === executionMachineName.value.toLowerCase()))
const tasks = computed(() => store.tasks)
const task = computed(() => tasks.value.find(row => row.id === taskId.value))
const targetTask = computed(() => machine.value?.tasks.find(row => row.id === taskId.value))
const targetOptions = computed(() => [
  { value: AGENTLESS_OPTION, label: `${AGENTLESS_LABEL} (no Agent needed)` },
  ...store.machines.filter(row => row.machine_name !== serverMachineName.value).map(row => ({
    value: row.machine_name,
    label: [row.machine_name, store.online(row) ? '' : 'offline', row.tasks.some(item => item.id === taskId.value) ? '' : 'flow not registered'].filter(Boolean).join(' · '),
  })),
])
const inspectHint = computed(() => {
  if (!machineName.value && !serverMachineName.value) return `${AGENTLESS_LABEL} is not enabled for this tenant. Select a runner.`
  if (!machineName.value && !machine.value) return `${AGENTLESS_LABEL} is not ready yet.`
  if (!machine.value) return 'Select a connected runner.'
  if (!machineName.value && !machine.value.capabilities.includes('pregel.server.v1')) return `${AGENTLESS_LABEL} is not ready yet.`
  if (!store.online(machine.value)) return 'Runner is unreachable. Parameters and plan load once it reconnects.'
  if (!machine.value.capabilities.includes('pregel.inspect.v1')) return 'Update this runner\'s Agent to load the parameter form and plan.'
  if (!task.value) return 'Select a flow.'
  if (!targetTask.value) return 'The selected target hasn\'t registered this flow. Pick a target that has.'
  return ''
})
const targetHint = computed(() => inspectHint.value
  || (!targetTask.value?.can_run ? targetTask.value?.unavailable_reason || 'This flow can\'t be run remotely right now.' : ''))
const inspectionIssue = computed(() => store.inspectionError
  || (store.inspectionReady ? store.inspection?.validation_error : '') || '')
const runHint = computed(() => targetHint.value || inspectionIssue.value
  || (store.inspecting ? 'Loading parameters and plan from the target…' : !store.inspectionReady ? 'Waiting for the target to confirm parameters.' : ''))
const canRun = computed(() => !!task.value && !runHint.value && store.inspectionReady && !store.saving)
const groups = computed(() => {
  const rows = new Map<string, RemoteAutomationField[]>()
  for (const field of store.inspection?.fields ?? []) {
    if (field.visible === false) continue
    const name = field.group || 'Run parameters'
    const group = rows.get(name) ?? []
    group.push(field)
    rows.set(name, group)
  }
  return Array.from(rows, ([name, fields]) => ({ id: `group-${name}`, name, fields }))
})
// Team schedules are visible to administrators only; the server omits them for team access.
const showTriggers = computed(() => !!task.value && store.schedules !== undefined)
const sections = computed(() => [...showTriggers.value ? [{ id: 'trigger', name: 'Triggers' }] : [], { id: 'target', name: 'Execution target' }, ...groups.value])
const inspectStatus = computed(() => store.inspecting ? 'Loading…' : store.inspectionReady ? 'Up to date with current parameters' : '')
watch(() => store.tenantId, (current, previous) => {
  if (previous && current !== previous) machineName.value = ''
})
watch(tasks, rows => {
  if (!rows.some(row => row.id === taskId.value))
    taskId.value = rows.find(row => row.id === props.initialTask)?.id ?? rows.find(row => row.targets.some(target => target.task.can_run))?.id ?? rows[0]?.id ?? ''
}, { immediate: true })
watch(() => props.initialMachine, value => { machineName.value = value ?? '' })
watch(() => props.initialTask, value => { if (value) taskId.value = value })
watch([() => store.tenantId, taskId], () => {
  // Only user edits travel between targets. Each host supplies its own saved defaults.
  draft.value = {}
  store.cancelInspection()
  scheduleInspection()
}, { immediate: true })
watch(executionMachineName, () => { store.cancelInspection(); scheduleInspection() })
watch(inspectHint, () => scheduleInspection())
watch(() => props.active, active => {
  if (active !== false) scheduleInspection()
  else { if (timer) clearTimeout(timer); timer = undefined; store.cancelInspection() }
})
function selectTarget(value: unknown) {
  machineName.value = value === AGENTLESS_OPTION ? '' : String(value)
  emit('target', machineName.value)
}
function scrollTo(id: string) {
  activeSection.value = id
  content.value?.querySelector(`[data-section="${CSS.escape(id)}"]`)?.scrollIntoView({ behavior: 'smooth', block: 'start' })
}

function scheduleInspection() {
  if (timer) clearTimeout(timer)
  store.cancelInspection(false)
  if (props.active === false || inspectHint.value) return
  timer = setTimeout(() => { void inspect() }, 350)
}
async function inspect() {
  if (timer) clearTimeout(timer)
  timer = undefined
  if (props.active === false || inspectHint.value) return
  const current = draft.value
  const result = await store.inspect({ tenant_id: store.tenantId, machine_name: requestMachineName.value, task_id: taskId.value, parameters: { ...current } })
  if (!result || draft.value !== current || !store.inspectionReady) return
}
function value(field: RemoteAutomationField) {
  return field.is_read_only ? field.value ?? '' : draft.value[field.key] ?? field.value ?? field.default_value ?? ''
}
function setValue(field: RemoteAutomationField, next: string) {
  const values = { ...draft.value, [field.key]: next }
  const changed = new Set([field.key])
  const pending = [field.key]
  for (let index = 0; index < pending.length; index++)
    for (const dependent of store.inspection?.fields ?? [])
      if (!dependent.is_read_only && !changed.has(dependent.key) && dependent.choice_dependencies?.includes(pending[index]!)) {
        values[dependent.key] = dependent.kind === 'multi_choice' ? '[]' : ''
        changed.add(dependent.key)
        pending.push(dependent.key)
      }
  draft.value = values
  scheduleInspection()
}
function choiceOptions(field: RemoteAutomationField) {
  const current = value(field)
  const options = field.choices.map(choice => ({ value: choice.value, label: choice.label, title: choice.tooltip }))
  return current && !field.choices.some(choice => choice.value === current) ? [{ value: current, label: `${current} (current)`, title: undefined }, ...options] : options
}
function selectedChoices(field: RemoteAutomationField): string[] {
  try {
    const result: unknown = JSON.parse(value(field) || '[]')
    return Array.isArray(result) ? result.filter((item): item is string => typeof item === 'string') : []
  } catch { return [] }
}
function selectionSummary(field: RemoteAutomationField) {
  const selected = selectedChoices(field)
  if (!selected.length) return field.empty_value || 'None selected'
  if (selected.length === field.choices.length) return `All (${selected.length})`
  return selected.map(item => field.choices.find(choice => choice.value === item)?.label ?? item).join(', ')
}
function setChoices(field: RemoteAutomationField, values: unknown) {
  setValue(field, JSON.stringify(Array.isArray(values) ? values.map(String) : []))
}
async function run() {
  if (!canRun.value || !store.inspection) return
  const result = await store.run({ tenant_id: store.tenantId, machine_name: requestMachineName.value,
    task_id: taskId.value, parameters: { ...store.inspection.parameters } })
  if (result) emit('started', result)
}
defineExpose({ run, canRun, runHint, inspect })
onBeforeUnmount(() => {
  if (timer) clearTimeout(timer)
  store.cancelInspection()
})
</script>

<template>
  <section class="invocation">
    <div v-show="pane === 'parameters'" class="parameters">
      <nav class="section-nav" aria-label="Parameter sections">
        <button v-for="section in sections" :key="section.id" type="button" :class="{ active: (activeSection || sections[0]?.id) === section.id }" @click="scrollTo(section.id)">{{ section.name }}</button>
      </nav>
      <form ref="content" class="parameter-form" @submit.prevent="run">
        <RemoteAutomationTriggers v-if="showTriggers" :task-id="taskId" :preferred-machine="executionMachineName" />
        <section class="settings-group" data-section="target">
          <h4>Execution target</h4>
          <div class="field">
            <label class="field-label" for="execution-target">Run this time on</label>
            <t-select id="execution-target" :value="!machineName || machineName === serverMachineName ? AGENTLESS_OPTION : machineName" :options="targetOptions"
              :disabled="store.saving" filterable @change="selectTarget" />
            <p class="field-hint">{{ AGENTLESS_LABEL }}: runs in the hosted environment with no Agent. Choosing a runner dispatches to its Agent. Applies to this run only; the flow is unchanged.</p>
            <p v-if="targetHint" class="field-hint">{{ targetHint }}</p>
          </div>
        </section>
        <section v-for="group in groups" :key="group.id" class="settings-group" :data-section="group.id">
          <h4>{{ group.name }}</h4>
          <template v-for="field in group.fields" :key="field.key">
            <div v-if="field.kind === 'toggle'" class="field toggle-field">
              <div><label class="toggle-label" :for="`parameter-${field.key}`">{{ field.label }}</label><p v-if="field.hint" class="field-hint">{{ field.hint }}</p></div>
              <t-switch :id="`parameter-${field.key}`" :value="value(field).toLowerCase() === 'true'" :disabled="field.is_read_only || store.saving"
                @change="(next: unknown) => setValue(field, String(!!next))" />
            </div>
            <div v-else class="field">
              <label class="field-label" :for="`parameter-${field.key}`">{{ field.label }}</label>
              <t-textarea v-if="field.kind === 'multiline'" :id="`parameter-${field.key}`" :value="value(field)" :readonly="field.is_read_only" :disabled="store.saving"
                :placeholder="field.empty_value ?? ''" :autosize="{ minRows: 4, maxRows: 12 }" @change="(next: unknown) => setValue(field, String(next ?? ''))" />
              <t-select v-else-if="field.kind === 'choice'" :id="`parameter-${field.key}`" :value="value(field)" :options="choiceOptions(field)"
                :disabled="field.is_read_only || store.saving" :placeholder="field.empty_value || 'Select'" clearable filterable
                @change="(next: unknown) => setValue(field, String(next ?? ''))" />
              <template v-else-if="field.kind === 'multi_choice'">
                <t-popup v-if="field.choices.length" trigger="click" placement="bottom-left" :disabled="field.is_read_only || store.saving" overlay-inner-class-name="choice-flyout">
                  <button :id="`parameter-${field.key}`" type="button" class="choice-summary" :disabled="field.is_read_only || store.saving" :title="selectionSummary(field)">
                    <span>{{ selectionSummary(field) }}</span><t-icon name="chevron-down" size="16px" />
                  </button>
                  <template #content>
                    <div class="choice-panel">
                      <div class="choice-panel-actions">
                        <t-button size="small" variant="outline" @click="setChoices(field, field.choices.map(choice => choice.value))">Select all</t-button>
                        <t-button size="small" variant="outline" @click="setChoices(field, [])">Clear</t-button>
                      </div>
                      <t-checkbox-group class="choice-list" :value="selectedChoices(field)" @change="(next: unknown) => setChoices(field, next)">
                        <t-checkbox v-for="choice in field.choices" :key="choice.value" :value="choice.value" :title="choice.tooltip">{{ choice.label }}</t-checkbox>
                      </t-checkbox-group>
                    </div>
                  </template>
                </t-popup>
                <p v-else class="field-hint">No options available</p>
              </template>
              <t-input v-else :id="`parameter-${field.key}`" :value="value(field)" :readonly="field.is_read_only" :disabled="store.saving"
                :placeholder="field.empty_value ?? (field.kind === 'folder' ? 'Folder path on the target' : '')" @change="(next: unknown) => setValue(field, String(next ?? ''))">
                <template v-if="field.kind === 'folder'" #prefix-icon><t-icon name="folder" /></template>
              </t-input>
              <p v-if="field.hint" class="field-hint">{{ field.hint }}</p>
              <p v-if="store.inspectionReady && draft[field.key] === '' && field.value" class="field-hint">Defaults to: {{ field.value }}</p>
              <p v-if="field.choices_error" class="field-hint choice-notice">Couldn't load options: {{ field.choices_error }}. Entered values are still validated by the flow.</p>
            </div>
          </template>
        </section>
        <p v-if="store.inspecting && !groups.length" class="field-hint">Loading parameters from {{ executionMachineName || 'the target' }}…</p>
        <p v-else-if="store.inspectionReady && !groups.length" class="field-hint">This flow has no other parameters.</p>
        <t-alert v-if="inspectionIssue" theme="error" :message="inspectionIssue">
          <template v-if="store.inspectionError" #operation><t-link theme="primary" @click="inspect">Reload</t-link></template>
        </t-alert>
      </form>
    </div>

    <div v-show="pane === 'plan'" class="plan">
      <t-alert v-if="inspectionIssue" class="plan-alert" theme="error" :message="inspectionIssue" />
      <div class="plan-graph">
        <RemoteAutomationGraph v-if="store.inspection" :key="`${executionMachineName}/${taskId}`" :steps="store.inspection.plan_steps" preview>
          <template #actions>
            <span class="plan-status" role="status" :title="inspectStatus">{{ inspectStatus }}</span>
            <t-button variant="text" size="small" :loading="store.inspecting" :disabled="!!inspectHint" @click="inspect"><template #icon><t-icon name="refresh" /></template>Reload</t-button>
          </template>
        </RemoteAutomationGraph>
        <div v-else class="plan-empty"><t-loading v-if="store.inspecting" size="small" text="Drawing the plan…" /><span v-else>Select a flow and an execution target to draw the plan.</span></div>
      </div>
    </div>
  </section>
</template>

<style scoped>
.invocation { min-height: 100%; }
.parameters { display: grid; grid-template-columns: 160px minmax(0, 640px); gap: 28px; padding: 18px 20px 20px 24px; }
.section-nav { position: sticky; top: 0; align-self: start; display: grid; gap: 4px; }
.section-nav button { padding: 8px 10px; border: 0; border-radius: 6px; background: transparent; color: var(--text-primary); font: inherit; font-size: 14px; line-height: 22px; text-align: left; cursor: pointer; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; }
.section-nav button:hover { background: var(--bg-hover); }
.section-nav button.active { background: var(--color-primary-light); color: var(--color-link); font-weight: 500; }
.parameter-form { display: grid; gap: 24px; align-content: start; min-width: 0; padding-bottom: 24px; }
.settings-group { display: grid; gap: 16px; padding-bottom: 24px; border-bottom: 1px solid var(--border-subtle); scroll-margin-top: 12px; }
.settings-group:last-of-type { border-bottom: 0; padding-bottom: 0; }
.settings-group h4 { margin: 0 0 -4px; padding-bottom: 8px; border-bottom: 1px solid var(--border-subtle); color: var(--text-primary); font-size: 15px; font-weight: 600; line-height: 22px; }
.field { display: grid; gap: 4px; min-width: 0; }
.field-label { color: var(--text-meta); font-size: 11px; font-weight: 500; line-height: 16px; }
.field-hint { margin: 0; color: var(--text-meta); font-size: 11px; font-weight: 500; line-height: 16px; }
.choice-notice { color: var(--color-accent); }
.toggle-field { grid-template-columns: minmax(0, 1fr) auto; align-items: center; gap: 12px; }
.toggle-field > div { display: grid; gap: 2px; }
.toggle-label { color: var(--text-default); font-size: 14px; line-height: 22px; }
.choice-summary { display: flex; align-items: center; justify-content: space-between; gap: 12px; width: 100%; height: 32px; padding: 0 10px 0 12px; border: 1px solid var(--border-color); border-radius: 6px; background: var(--bg-card); color: var(--text-default); font: inherit; font-size: 14px; text-align: left; cursor: pointer; }
.choice-summary span { min-width: 0; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; }
.choice-summary:hover:not(:disabled) { border-color: var(--border-strong); }
.choice-summary:disabled { background: var(--bg-disabled); color: var(--text-disabled); cursor: not-allowed; }
.choice-panel { display: grid; gap: 8px; min-width: 280px; max-width: 440px; padding: 4px; }
.choice-panel-actions { display: flex; gap: 8px; }
.choice-list { display: grid; gap: 4px; max-height: 280px; overflow-y: auto; }
.plan { display: flex; flex-direction: column; min-height: 100%; }
.plan-status { min-width: 0; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; color: var(--text-secondary); font-size: 12px; }
.plan-alert { margin: 10px 16px 0; }
.plan-graph { flex: 1; padding: 8px 16px 12px; min-width: 0; }
.plan-empty { display: grid; place-items: center; min-height: 260px; color: var(--text-secondary); font-size: 12px; }
@media (max-width: 900px) { .parameters { grid-template-columns: 1fr; gap: 16px; padding: 12px 16px; } .section-nav { position: static; display: flex; overflow-x: auto; } }
</style>
