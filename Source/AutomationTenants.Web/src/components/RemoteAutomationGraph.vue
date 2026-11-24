<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, useId, watch } from 'vue'
import type { RemoteAutomationStep } from '@/api'
import { remoteAutomationState, remoteStepProgress } from '@/stores/remoteAutomation'
import { automationGraphNodeId, layoutRemoteAutomationGraph, type AutomationGraphEdge, type AutomationGraphNode } from '@/utils/remoteAutomationGraphLayout'
import { layoutRemoteAutomationNodeGraph, nodeGraphHeaderHeight, nodeGraphTagHeight, type AutomationNodeGraphNode } from '@/utils/remoteAutomationNodeGraphLayout'
import { ellipsizeTitle, visualUnits } from '@/utils/graphAutoLayout'
import GraphCanvas, { type GraphCanvasEdge, type GraphCanvasNode, type GraphCanvasNodeMove, type GraphCanvasPin } from './GraphCanvas.vue'

const props = defineProps<{ steps: RemoteAutomationStep[]; preview?: boolean }>()
const emit = defineEmits<{ select: [step: RemoteAutomationStep | null]; logs: [] }>()
const history = ref<string[][]>([])
const selectedId = ref('')
const viewport = ref<HTMLElement | null>(null)
const canvasSize = ref<HTMLElement | null>(null)
const zoom = ref(1)
const dragging = ref(false)
const marker = `pregel-arrow-${useId().replace(/[^a-zA-Z0-9-]/g, '')}`
let pan: { pointerId: number; x: number; y: number; left: number; top: number; moved: boolean } | null = null

function resolve(path: string[]) {
  let steps = props.steps
  let found: RemoteAutomationStep | undefined
  for (const key of path) {
    found = steps.find(step => step.key === key)
    if (!found) return undefined
    steps = found.children ?? []
  }
  return found
}
const scope = computed(() => history.value[history.value.length - 1] ?? [])
const level = computed(() => scope.value.length ? resolve(scope.value)?.children ?? [] : props.steps)
const breadcrumbs = computed(() => history.value.map(path => ({ path, title: resolve(path)?.title ?? 'Subgraph' })))
const graph = computed(() => layoutRemoteAutomationGraph(level.value, scope.value))
const groups = computed(() => graph.value.nodes.filter(node => node.kind !== 'step'))
const nodes = computed(() => graph.value.nodes.filter(node => node.kind === 'step'))
const selected = computed(() => graph.value.nodes.find(node => automationGraphNodeId(node.path) === selectedId.value))
const selectedAncestors = computed(() => new Set(selected.value?.path.map((_, index, path) => automationGraphNodeId(path.slice(0, index + 1))) ?? []))
const height = computed(() => Math.max(240, Math.min(540, graph.value.height + 2)))

// Node graph view: stages dissolve into one layered DAG laid out by the restored
// Blackboard algorithm. Positions, sizes and the viewport are recomputed, never stored.
const mode = ref<'stage' | 'graph'>('stage')
// Dragging nodes stays in the canvas but is not offered to users; only the mock preview enables it.
const nodeDraggable = import.meta.env.VITE_KSF_MOCK === '1'
const nodeCanvas = ref<InstanceType<typeof GraphCanvas> | null>(null)
const nodeHost = ref<HTMLElement | null>(null)
const nodeHostWidth = ref(0)
const nodeScale = ref(1)
const moved = ref<Record<string, { x: number; y: number }>>({})
const nodeGraph = computed(() => layoutRemoteAutomationNodeGraph(level.value, scope.value, props.preview))
const nodeById = computed(() => new Map(nodeGraph.value.nodes.map(node => [node.id, node])))
// Remounting on a structural change refits the canvas; state updates keep the viewport.
const nodeGraphKey = computed(() => mode.value !== 'graph' ? '' : [JSON.stringify(scope.value), ...nodeGraph.value.nodes.map(node => node.id),
  ...nodeGraph.value.edges.map(edge => `${edge.from}>${edge.to}`)].join('|'))
const nodeHeightPx = computed(() => Math.max(240, Math.min(540, nodeGraph.value.height + 48 + (nodeGraph.value.edges.some(edge => edge.conditional) ? 72 : 0))))
const stateColors: Record<string, string> = {
  plan: 'var(--text-disabled)', pending: 'var(--text-meta)', running: 'var(--ksf-brand)', succeeded: 'var(--status-success-icon)',
  failed: 'var(--status-danger-icon)', cancelled: 'var(--status-warning-icon)', review: 'var(--status-warning-icon)', skipped: 'var(--text-disabled)',
}
const canvasNodes = computed<GraphCanvasNode[]>(() => {
  const { nodeWidth: width, nodeHeight } = nodeGraph.value
  const pins: GraphCanvasPin[] = [
    { handle: 'exec_in', side: 'left', x: 0, y: nodeGraphHeaderHeight / 2, role: 'target', category: 'exec' },
    { handle: 'exec_out', side: 'right', x: width, y: nodeGraphHeaderHeight / 2, role: 'source', category: 'exec' },
  ]
  return nodeGraph.value.nodes.map(node => ({
    id: node.id, x: moved.value[node.id]?.x ?? node.x, y: moved.value[node.id]?.y ?? node.y, width, height: nodeHeight,
    status: stateClass(node.step), color: stateColors[stateClass(node.step)], connectable: false, title: node.step.title, pins,
  }))
})
const canvasEdges = computed<GraphCanvasEdge[]>(() => nodeGraph.value.edges.map(edge => {
  const highlighted = !!selectedId.value && (edge.from === selectedId.value || edge.to === selectedId.value)
  // Recovery edges leave and enter through the bottom so they arc under the flow instead of
  // running along the same row as the ordinary edges.
  return { id: edge.id, from: edge.from, to: edge.to, removable: false, dashed: edge.conditional,
    sourceHandle: edge.conditional ? 'pin:bottom' : 'exec_out', targetHandle: edge.conditional ? 'pin:bottom' : 'exec_in', color: highlighted ? 'var(--color-link)' : undefined, classes: highlighted ? ['highlighted'] : [] }
}))
const hasConditional = computed(() => mode.value === 'graph' ? nodeGraph.value.edges.some(edge => edge.conditional) : graph.value.edges.some(edge => edge.conditional))
let resize: ResizeObserver | null = null
watch(nodeHost, host => {
  resize?.disconnect()
  resize = null
  nodeHostWidth.value = host?.clientWidth ?? 0
  if (!host) return
  resize = new ResizeObserver(() => { nodeHostWidth.value = host.clientWidth })
  resize.observe(host)
})
watch(nodeGraphKey, () => { moved.value = {} })
function setMode(next: 'stage' | 'graph') {
  if (mode.value === next) return
  mode.value = next
  moved.value = {}
}
function moveCanvasNode(move: GraphCanvasNodeMove) { moved.value = { ...moved.value, [move.id]: { x: move.x, y: move.y } } }
function openCanvasNode(id: string) { const node = nodeById.value.get(id); if (node) openSubgraph(node) }
// SVG text cannot ellipsize itself; ~6.8px per visual unit at 13px.
function clip(value: string, px: number, unit = 6.8) { const max = Math.max(4, px / unit); return visualUnits(value) > max ? ellipsizeTitle(value, max) : value }
function hasSubgraph(step: RemoteAutomationStep) { return !!step.children?.length && !step.is_stage }
function nodeActions(step: RemoteAutomationStep) { return (props.preview ? 0 : 1) + (hasSubgraph(step) ? 1 : 0) }
function nodeItems(id: string) { const node = nodeById.value.get(id); return node ? [node] : [] }
const tagMaxWidth = computed(() => nodeGraph.value.nodeWidth - 24)
function tagWidth(tag: string) { return Math.min(tagMaxWidth.value, visualUnits(tag) * 6 + 16) }

watch(selected, node => emit('select', node?.step ?? null), { immediate: true })
watch(() => props.steps, () => {
  if (scope.value.length && !resolve(scope.value)) history.value = []
  if (selectedId.value && !selected.value) selectedId.value = ''
})

type GraphTarget = Pick<AutomationGraphNode | AutomationNodeGraphNode, 'path' | 'step'>
function selectNode(node: GraphTarget) { selectedId.value = automationGraphNodeId(node.path) }
function openLogs(node: GraphTarget) { selectNode(node); emit('logs') }
function clearSelection() { selectedId.value = '' }
defineExpose({ clearSelection })
async function navigate(index: number, next?: string[]) {
  selectedId.value = ''
  history.value = [...history.value.slice(0, index), ...(next ? [next] : [])]
  zoom.value = 1
  await nextTick()
  viewport.value?.scrollTo({ left: 0, top: 0 })
}
function openSubgraph(node: GraphTarget) {
  if (hasSubgraph(node.step)) void navigate(history.value.length, node.path)
}
function box(node: { x: number; y: number; width: number; height: number }) {
  return { left: `${node.x}px`, top: `${node.y}px`, width: `${node.width}px`, height: `${node.height}px` }
}
function route(edge: AutomationGraphEdge) { return edge.points.map((p, index) => `${index ? 'L' : 'M'} ${p.x} ${p.y}`).join(' ') }
function edgeSelected(edge: AutomationGraphEdge) { return selectedAncestors.value.has(edge.source) || selectedAncestors.value.has(edge.target) }
function state(step: RemoteAutomationStep) { return remoteAutomationState(props.preview ? undefined : step.state ?? 'not_run') }
// Same state classes as the Client's AutomationGraph.axaml; the colors follow from them in CSS.
function stateClass(step: RemoteAutomationStep) {
  if (props.preview) return 'plan'
  switch (step.state) {
    case 'running': case 'succeeded': case 'failed': case 'cancelled': return step.state
    case 'awaiting_review': return 'review'
    case 'skipped': case 'not_run': return 'skipped'
    default: return 'pending'
  }
}
// Line drawings of the Client's Fluent state icons (AutomationStepItemViewModel.StateIcon) on a 16px grid.
const ring = 'M8 1.5a6.5 6.5 0 1 0 0 13a6.5 6.5 0 1 0 0-13z'
const stateIcons: Record<string, string> = {
  plan: ring,
  pending: `${ring}M8 4.8V8l2.2 1.3`,
  running: 'M13.2 8a5.2 5.2 0 0 1-9 3.5M2.8 8a5.2 5.2 0 0 1 9-3.5M12.2 2.2v2.5H9.7M3.8 13.8v-2.5h2.5',
  review: 'M6.5 2a2.5 2.5 0 1 0 0 5a2.5 2.5 0 1 0 0-5zM2 13.5c0-2.6 2-4.4 4.5-4.4M11.5 8.5a3 3 0 1 0 0 6a3 3 0 1 0 0-6zM11.5 10v1.6l1 .6',
  succeeded: `${ring}M5 8.2l2.1 2.1L11 6`,
  failed: `${ring}M5.8 5.8l4.4 4.4M10.2 5.8l-4.4 4.4`,
  skipped: `${ring}M5.2 8h5.6`,
  cancelled: `${ring}M6.4 6.4h3.2v3.2H6.4zM7.4 8h1.2`,
  reused: 'M2.6 8a5.4 5.4 0 1 0 1.7-3.9L2.6 5.8M2.6 3v2.8h2.8M8 5.2V8l2 1.2',
}
const logIcon = 'M4.5 1.5h4.8l3.2 3.2v9.8h-8zM9.3 1.5v3.2h3.2M6.5 8.5h3.5M6.5 11h3.5'
function iconPath(step: RemoteAutomationStep) { return stateIcons[!props.preview && step.is_reused ? 'reused' : stateClass(step)]! }
function elapsed(step: RemoteAutomationStep) {
  if (!step.started_at || !step.ended_at) return ''
  const seconds = Math.max(0, Math.round((Date.parse(step.ended_at) - Date.parse(step.started_at)) / 1000))
  return Number.isFinite(seconds) ? seconds < 60 ? `${seconds}s` : `${Math.floor(seconds / 60)}m ${String(seconds % 60).padStart(2, '0')}s` : ''
}
// Same wording as AutomationStepItemViewModel.StatusLabel: success shows only its duration.
function status(step: RemoteAutomationStep) {
  if (props.preview) return ''
  if (step.is_reused) return step.state === 'skipped' ? 'Skipped (reused)' : step.state === 'failed' ? 'Failed (reused)' : 'Reused result'
  const duration = elapsed(step), attempt = step.attempt ?? 0
  if (step.state === 'running') {
    const progress = step.has_progress ? remoteStepProgress(step) : null
    return (progress === null ? 'In progress' : `In progress ${progress}%`) + (attempt > 1 ? ` · attempt ${attempt}` : '')
  }
  if (step.state === 'failed') return (duration ? `Failed · ${duration}` : 'Failed') + (attempt > 1 ? ` · ${attempt} attempts` : '')
  if (step.state === 'succeeded') return (duration || 'Completed') + (attempt > 1 ? ` · succeeded on attempt ${attempt}` : '')
  return state(step).label
}
function meta(step: RemoteAutomationStep) {
  return [status(step), props.preview ? '' : step.error?.split(/\r?\n/)[0] || step.detail,
    step.is_finally ? 'Finally: runs after success, failure or cancellation' : ''].filter(Boolean).join(' · ')
}
function dateText(value: string | null | undefined) {
  return value ? new Date(value).toLocaleString('en-GB', { hour12: false }) : '—'
}
async function setZoom(value: number, x?: number, y?: number) {
  const panel = viewport.value
  if (!panel) return
  const left = x ?? panel.clientWidth / 2, top = y ?? panel.clientHeight / 2
  const worldX = (panel.scrollLeft + left - (canvasSize.value?.offsetLeft ?? 0)) / zoom.value
  const worldY = (panel.scrollTop + top - (canvasSize.value?.offsetTop ?? 0)) / zoom.value
  zoom.value = Math.max(.2, Math.min(2, value))
  await nextTick()
  panel.scrollLeft = worldX * zoom.value + (canvasSize.value?.offsetLeft ?? 0) - left
  panel.scrollTop = worldY * zoom.value + (canvasSize.value?.offsetTop ?? 0) - top
}
const zoomValue = computed(() => mode.value === 'graph' ? nodeScale.value : zoom.value)
function zoomTo(value: number) {
  if (mode.value === 'graph') nodeCanvas.value?.zoomAt(value)
  else void setZoom(value)
}
async function fit() {
  if (mode.value === 'graph') { nodeCanvas.value?.fitViewportToVisibleNodes(0); return }
  const panel = viewport.value
  if (!panel || !graph.value.width) return
  zoom.value = Math.max(.2, Math.min(1, (panel.clientWidth - 16) / graph.value.width, (panel.clientHeight - 16) / graph.value.height))
  await nextTick()
  panel.scrollTo({ left: 0, top: 0 })
}
function wheel(event: WheelEvent) {
  if (!event.ctrlKey && !event.metaKey) return
  event.preventDefault()
  const bounds = viewport.value!.getBoundingClientRect()
  void setZoom(zoom.value * (event.deltaY > 0 ? .9 : 1.1), event.clientX - bounds.left, event.clientY - bounds.top)
}
function startPan(event: PointerEvent) {
  if (event.pointerType === 'touch' || !viewport.value || (event.button !== 0 && event.button !== 1)
    || (event.target as Element).closest('button, a, input')) return
  event.preventDefault()
  pan = { pointerId: event.pointerId, x: event.clientX, y: event.clientY, left: viewport.value.scrollLeft, top: viewport.value.scrollTop, moved: false }
  viewport.value.setPointerCapture(event.pointerId)
}
function movePan(event: PointerEvent) {
  if (!pan || pan.pointerId !== event.pointerId || !viewport.value) return
  const dx = event.clientX - pan.x, dy = event.clientY - pan.y
  if (Math.abs(dx) + Math.abs(dy) > 3) pan.moved = dragging.value = true
  viewport.value.scrollLeft = pan.left - dx
  viewport.value.scrollTop = pan.top - dy
}
function endPan(event: PointerEvent) {
  if (!pan || pan.pointerId !== event.pointerId) return
  if (!pan.moved && event.type === 'pointerup') selectedId.value = ''
  pan = null
  dragging.value = false
  if (viewport.value?.hasPointerCapture(event.pointerId)) viewport.value.releasePointerCapture(event.pointerId)
}
function keyboard(event: KeyboardEvent) {
  if (event.target !== event.currentTarget) return
  if (event.key === '+' || event.key === '=') { event.preventDefault(); zoomTo(zoomValue.value * 1.2) }
  if (event.key === '-') { event.preventDefault(); zoomTo(zoomValue.value / 1.2) }
  if (event.key === '0') { event.preventDefault(); void fit() }
  if (event.key === 'Escape') selectedId.value = ''
}
onBeforeUnmount(() => { pan = null; resize?.disconnect() })
</script>

<template>
  <div class="automation-graph">
    <div class="graph-toolbar">
      <nav class="breadcrumbs" aria-label="Graph level"><button v-if="history.length" type="button" class="back" title="Back to parent level" @click="navigate(history.length - 1)">← Back</button><button type="button" :disabled="!history.length" @click="navigate(0)">Full graph</button><template v-for="(crumb, index) in breadcrumbs" :key="automationGraphNodeId(crumb.path)"><span>/</span><button type="button" :disabled="index === breadcrumbs.length - 1" :title="crumb.title" @click="navigate(index + 1)">{{ crumb.title }}</button></template></nav>
      <div class="legend"><span><i />Dependency</span><span v-if="hasConditional"><i class="conditional" />Recovery</span><span v-if="nodes.some(node => node.step.is_finally)" class="finally-note">Finally: runs after success, failure or cancellation</span></div><div class="toolbar-actions"><slot name="actions" /></div><div class="mode-switch" role="group" aria-label="Graph style"><button type="button" :class="{ active: mode === 'stage' }" :aria-pressed="mode === 'stage'" title="Group by stage" @click="setMode('stage')">Stages</button><button type="button" :class="{ active: mode === 'graph' }" :aria-pressed="mode === 'graph'" title="Show all nodes by dependency" @click="setMode('graph')">Graph</button></div><div class="zoom-controls" aria-label="Canvas zoom" title="Drag empty space to pan · Ctrl + scroll to zoom"><button type="button" :disabled="zoomValue <= .2" aria-label="Zoom out" @click="zoomTo(zoomValue / 1.2)">−</button><button type="button" class="zoom-value" title="Reset to 100%" @click="zoomTo(1)">{{ Math.round(zoomValue * 100) }}%</button><button type="button" :disabled="zoomValue >= 2" aria-label="Zoom in" @click="zoomTo(zoomValue * 1.2)">+</button><button type="button" class="fit" @click="fit">Fit to view</button></div>
    </div>
    <p v-if="!level.length" class="empty">{{ preview ? 'This task has no steps to preview.' : 'Waiting for the machine to report the graph…' }}</p>
    <div v-else-if="mode === 'graph'" ref="nodeHost" class="node-graph" :style="{ height: `${nodeHeightPx}px` }" tabindex="0" aria-label="Node graph laid out by dependency; drag empty space to pan, Ctrl + scroll to zoom" @keydown="keyboard">
      <GraphCanvas v-if="nodeHostWidth" ref="nodeCanvas" :key="nodeGraphKey" class="node-canvas" :nodes="canvasNodes" :edges="canvasEdges"
        :canvas-size="{ width: nodeHostWidth, height: nodeHeightPx }" :default-node-width="nodeGraph.nodeWidth" :default-node-height="nodeGraph.nodeHeight"
        :focus-id="selectedId" :readonly="!nodeDraggable" :fit-padding="20" :fit-min-scale=".8" :min-scale=".2" :max-scale="2" wheel-zoom-modifier
        @node-select="selectedId = $event" @node-open="openCanvasNode" @node-move="moveCanvasNode" @node-move-end="moveCanvasNode" @viewport-change="nodeScale = $event.scale">
        <template #node="{ node, width, height, selected: active }">
          <template v-for="item in nodeItems(node.id)" :key="item.id">
            <g class="native-node" :class="[stateClass(item.step), { active, reused: !preview && item.step.is_reused }]">
              <title>{{ [item.step.title, meta(item.step)].filter(Boolean).join('\n') }}</title>
              <rect class="native-body" :width="width" :height="height" rx="6" />
              <path class="native-header" :d="`M0 6a6 6 0 0 1 6-6h${width - 12}a6 6 0 0 1 6 6v${nodeGraphHeaderHeight - 6}h-${width}z`" />
              <line v-if="height > nodeGraphHeaderHeight" class="native-divider" x1="0" :y1="nodeGraphHeaderHeight" :x2="width" :y2="nodeGraphHeaderHeight" />
              <g :transform="`translate(12 ${(nodeGraphHeaderHeight - 16) / 2})`"><g class="state-icon native-icon" :class="[stateClass(item.step), { reused: !preview && item.step.is_reused }]"><path :d="iconPath(item.step)" /></g></g>
              <text class="native-title" x="36" :y="nodeGraphHeaderHeight / 2 + 4.5">{{ clip(item.step.title, width - 44 - nodeActions(item.step) * 26) }}</text>
              <g v-if="hasSubgraph(item.step)" class="native-action" role="button" aria-label="Open subgraph" :transform="`translate(${width - 28 - (preview ? 0 : 26)} 6)`" @pointerdown.stop @click.stop="openSubgraph(item)">
                <title>Open subgraph</title><rect width="22" height="22" rx="4" /><g transform="translate(4 4) scale(.7)"><rect x="7" y="2" width="6" height="4" rx="1" /><rect x="1" y="13" width="6" height="4" rx="1" /><rect x="13" y="13" width="6" height="4" rx="1" /><path d="M 10 6 V 9 H 4 V 13 M 10 9 H 16 V 13" /></g>
              </g>
              <g v-if="!preview" class="native-action" role="button" aria-label="View node logs" :transform="`translate(${width - 28} 6)`" @pointerdown.stop @click.stop="openLogs(item)">
                <title>View node logs</title><rect width="22" height="22" rx="4" /><path transform="translate(4 4) scale(.875)" :d="logIcon" />
              </g>
              <text v-if="!preview && meta(item.step)" class="native-meta" x="12" :y="nodeGraphHeaderHeight + 16">{{ clip(meta(item.step), width - 24, 6.4) }}</text>
              <g v-if="nodeGraph.tagged && item.tag" class="native-tag" :transform="`translate(12 ${height - nodeGraphTagHeight + 5})`">
                <rect :width="tagWidth(item.tag)" height="16" rx="8" /><text x="8" y="11.5">{{ clip(item.tag, tagMaxWidth - 16, 6) }}</text>
              </g>
              <rect v-if="!preview && item.step.state === 'running' && item.step.has_progress" class="native-progress" x="3" :y="height - 2" :width="(width - 3) * (remoteStepProgress(item.step) ?? 0) / 100" height="2" />
            </g>
          </template>
        </template>
      </GraphCanvas>
    </div>
    <div v-else ref="viewport" class="graph-viewport" :class="{ dragging }" :style="{ height: `${height}px` }" tabindex="0" aria-label="Pregel graph with steps grouped by stage; scroll or drag the canvas to browse" @wheel="wheel" @pointerdown="startPan" @pointermove="movePan" @pointerup="endPan" @pointercancel="endPan" @lostpointercapture="pan = null; dragging = false" @keydown="keyboard">
      <div ref="canvasSize" class="canvas-size" :style="{ width: `${graph.width * zoom}px`, height: `${graph.height * zoom}px` }">
        <div class="graph-canvas" :style="{ width: `${graph.width}px`, height: `${graph.height}px`, transform: `scale(${zoom})` }">
          <section v-for="group in groups" :key="group.id" class="stage-group" :class="[stateClass(group.step), { selected: automationGraphNodeId(group.path) === selectedId }]" :style="box(group)">
            <div class="stage-heading"><button type="button" class="stage-main" :aria-pressed="automationGraphNodeId(group.path) === selectedId" :title="group.step.title" @click="selectNode(group)"><svg class="state-icon" :class="stateClass(group.step)" viewBox="0 0 16 16" aria-hidden="true"><path :d="iconPath(group.step)" /></svg><strong>{{ group.kind === 'recovery' ? 'Recovery' : group.step.title }}</strong><span v-if="!preview" class="stage-state">{{ status(group.step) }}</span></button><button v-if="!preview" type="button" class="graph-action" aria-label="View stage logs" title="View stage logs" @click="openLogs(group)"><svg viewBox="0 0 16 16" aria-hidden="true"><path :d="logIcon" /></svg></button></div>
            <div v-if="group.chip || group.note" class="stage-note"><span v-if="group.chip" class="graph-chip">{{ group.chip }}</span><span v-if="group.note">{{ group.note }}</span></div>
          </section>
          <svg class="connections" :width="graph.width" :height="graph.height" aria-hidden="true"><defs><marker :id="marker" markerWidth="7" markerHeight="7" refX="6.5" refY="3.5" orient="auto"><path d="M 1 1 L 6 3.5 L 1 6" fill="none" stroke="context-stroke" stroke-width="1.2" /></marker></defs>
            <rect v-for="tray in graph.trays" :key="tray.id" class="branch-tray" :x="tray.x" :y="tray.y" :width="tray.width" :height="tray.height" rx="6" />
            <path v-for="edge in graph.edges" :key="edge.id" :d="route(edge)" :class="{ conditional: edge.conditional, highlighted: edgeSelected(edge) }" :marker-end="edge.arrow ? `url(#${marker})` : undefined" />
          </svg>
          <article v-for="node in nodes" :key="node.id" class="graph-step" :class="[stateClass(node.step), { selected: automationGraphNodeId(node.path) === selectedId, reused: !preview && node.step.is_reused, retrying: !preview && node.step.state === 'running' && node.step.retry_error }]" :style="box(node)">
            <button type="button" class="step-main" :aria-pressed="automationGraphNodeId(node.path) === selectedId" :title="[node.step.title, meta(node.step)].filter(Boolean).join('\n')" @click="selectNode(node)"><svg class="state-icon" :class="[stateClass(node.step), { reused: !preview && node.step.is_reused }]" viewBox="0 0 16 16" aria-hidden="true"><path :d="iconPath(node.step)" /></svg><span class="step-copy"><strong>{{ node.step.title }}</strong><small v-if="meta(node.step)">{{ meta(node.step) }}</small></span><span v-if="node.step.is_finally" class="graph-chip">Finally</span></button>
            <span class="step-actions"><button v-if="node.step.children?.length && !node.step.is_stage" type="button" class="graph-action" :aria-label="`Open ${node.step.title} subgraph`" title="Open subgraph" @click="openSubgraph(node)"><svg viewBox="0 0 20 20" aria-hidden="true"><rect x="7" y="2" width="6" height="4" rx="1" /><rect x="1" y="13" width="6" height="4" rx="1" /><rect x="13" y="13" width="6" height="4" rx="1" /><path d="M 10 6 V 9 H 4 V 13 M 10 9 H 16 V 13" /></svg></button><button v-if="!preview" type="button" class="graph-action" aria-label="View node logs" title="View node logs" @click="openLogs(node)"><svg viewBox="0 0 16 16" aria-hidden="true"><path :d="logIcon" /></svg></button></span>
            <progress v-if="!preview && node.step.state === 'running' && node.step.has_progress" :value="remoteStepProgress(node.step) ?? undefined" max="100" :aria-label="`${node.step.title} progress`" />
          </article>
        </div>
      </div>
    </div>
    <section v-if="selected" class="node-detail" aria-label="Selected node details"><div class="detail-title"><strong>{{ selected.step.title }}</strong><t-tag v-if="!preview" size="small" variant="light" :theme="state(selected.step).theme">{{ state(selected.step).label }}</t-tag><button type="button" class="clear-selection" @click="selectedId = ''">Clear selection</button></div><p v-if="selected.step.is_finally" class="condition">Finally: runs after success, failure or cancellation.</p><p v-else-if="selected.step.is_recovery" class="condition">Recovery: runs on failure or cancellation.</p><p v-if="selected.step.detail">{{ selected.step.detail }}</p><p v-if="selected.step.error" class="error">{{ selected.step.error }}</p><p v-if="selected.step.retry_error && selected.step.retry_error !== selected.step.error">Previous attempt: {{ selected.step.retry_error }}</p><p v-if="selected.step.is_reused && !preview" class="times">Not run this time; the result from the original run was reused.</p><p v-if="!preview" class="times">Started {{ dateText(selected.step.started_at) }} · Ended {{ dateText(selected.step.ended_at) }}</p><p v-else class="times">Structure preview before execution. Actual node states come from the machine's reports.</p></section>
    <p v-else-if="level.length" class="selection-hint">Click a step or stage to see details{{ preview ? '' : ' and logs' }}. Steps with a graph icon open a subgraph.</p>
  </div>
</template>

<style scoped>
.graph-toolbar,.breadcrumbs,.zoom-controls,.graph-caption,.legend,.detail-title { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; }.graph-toolbar { flex-wrap: nowrap; gap: 14px; margin-bottom: 8px; }.breadcrumbs { min-width: 0; flex: 0 0 auto; max-width: 50%; flex-wrap: nowrap; }.toolbar-actions { display: flex; align-items: center; gap: 6px; min-width: 0; margin-left: auto; }.breadcrumbs button { border: 0; background: none; color: var(--color-link); cursor: pointer; font: inherit; padding: 3px 0; font-size: 13px; max-width: 220px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }.breadcrumbs button:disabled { color: var(--text-primary); cursor: default; }.breadcrumbs .back { margin-right: 7px; }.breadcrumbs > span { color: var(--text-secondary); }.zoom-controls { gap: 0; flex-wrap: nowrap; border: 1px solid var(--border-color); border-radius: 6px; overflow: hidden; }.zoom-controls button { font: inherit; font-size: 12px; height: 26px; min-width: 26px; padding: 0 8px; border: 0; background: var(--bg-card); color: var(--text-primary); cursor: pointer; }.zoom-controls button:disabled { color: var(--text-secondary); opacity: .4; cursor: default; }.zoom-controls .zoom-value { min-width: 52px; font-size: 12px; font-variant-numeric: tabular-nums; }.zoom-controls .fit { border-left: 1px solid var(--border-color); padding-inline: 10px; }.zoom-controls button:hover:not(:disabled) { background: var(--bg-primary); }
.legend { flex-wrap: nowrap; gap: 14px; color: var(--text-secondary); font-size: 11px; white-space: nowrap; min-width: 0; overflow: hidden; }.zoom-controls { flex-shrink: 0; }.legend > span { display: inline-flex; align-items: center; gap: 6px; }.legend i { display: inline-block; width: 19px; border-top: 1.5px solid var(--text-secondary); }.legend i.conditional { border-top-style: dashed; border-color: var(--border-strong); }.finally-note { opacity: .85; }
.graph-viewport { box-sizing: border-box; position: relative; overflow: auto; background: var(--bg-card); cursor: grab; overscroll-behavior: contain; }.graph-viewport.dragging { cursor: grabbing; user-select: none; }.graph-viewport:focus-visible { outline: 2px solid var(--color-link); outline-offset: 2px; }.canvas-size { position: relative; }.graph-canvas { position: absolute; inset: 0; transform-origin: left top; }.connections { position: absolute; inset: 0; z-index: 1; overflow: visible; pointer-events: none; }.connections > path { fill: none; stroke: var(--border-strong); stroke-width: 1; stroke-linejoin: round; }.connections > path.conditional { stroke-dasharray: 4 3; }.connections > path.highlighted { stroke: var(--color-link); stroke-width: 1.7; }.branch-tray { fill: var(--bg-pressed); stroke: var(--border-strong); stroke-width: 1; }
.stage-group { box-sizing: border-box; position: absolute; border: 1px solid var(--border-color); border-radius: 8px; background: var(--bg-secondary); }.stage-group.running { border-color: var(--ksf-brand); }.stage-group.failed { border-color: var(--status-danger-border); }.stage-group.review { border-color: var(--status-warning-icon); }.stage-group.selected { border-color: var(--color-link); box-shadow: 0 0 0 2px color-mix(in srgb, var(--color-link) 12%, transparent); }.stage-heading { position: relative; z-index: 3; display: flex; align-items: center; gap: 8px; height: 40px; padding-right: 8px; }.stage-main { display: flex; align-items: center; gap: 8px; flex: 1; min-width: 0; height: 100%; border: 0; border-radius: 8px 0 0; padding: 0 0 0 12px; background: transparent; color: var(--text-primary); cursor: pointer; text-align: left; font: inherit; }.stage-main strong { flex: 1; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; font-size: 13px; line-height: 18px; font-weight: 600; }.stage-group.skipped .stage-main strong,.stage-group.cancelled .stage-main strong { color: var(--text-secondary); }.stage-state { flex-shrink: 0; max-width: 120px; color: var(--text-meta); font-size: 12px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }.stage-group.running .stage-state { color: var(--color-link); }.stage-group.failed .stage-state { color: var(--status-danger-fg); }.stage-group.review .stage-state { color: var(--status-warning-fg); }.stage-note { display: flex; align-items: center; gap: 6px; box-sizing: border-box; height: 28px; padding: 0 12px 8px; color: var(--text-meta); font-size: 12px; line-height: 16px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }.graph-chip { flex-shrink: 0; height: 20px; padding: 0 6px; border-radius: 10px; background: var(--neutral-status-bg); color: var(--text-secondary); font-size: 11px; line-height: 20px; white-space: nowrap; }
.graph-step { box-sizing: border-box; position: absolute; z-index: 2; display: flex; align-items: center; border: 1px solid var(--border-color); border-radius: 6px; background: var(--bg-card); overflow: hidden; }.graph-step:hover { border-color: var(--border-strong); }.graph-step.running { border-color: var(--ksf-brand); }.graph-step.failed { border-color: var(--status-danger-border); }.graph-step.review { background: var(--status-warning-bg); border-color: var(--status-warning-icon); }.graph-step.skipped,.graph-step.cancelled { background: var(--bg-secondary); }.graph-step.reused { background: var(--bg-secondary); border-color: var(--border-subtle); }.graph-step.selected { outline: 2px solid var(--color-link); outline-offset: 1px; }.step-main { display: flex; align-items: center; gap: 10px; flex: 1; min-width: 0; height: 100%; border: 0; padding: 0 0 0 12px; color: var(--text-primary); background: transparent; cursor: pointer; text-align: left; font: inherit; }.step-copy { display: flex; flex-direction: column; gap: 2px; min-width: 0; flex: 1; }.step-copy strong { font-size: 13px; line-height: 18px; font-weight: 500; }.step-copy small { font-size: 12px; line-height: 16px; color: var(--text-meta); }.step-copy > * { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }.graph-step.skipped .step-copy strong,.graph-step.cancelled .step-copy strong,.graph-step.reused .step-copy strong { color: var(--text-secondary); }.graph-step.running .step-copy small { color: var(--color-link); }.graph-step.failed .step-copy small { color: var(--status-danger-fg); }.graph-step.review .step-copy small,.graph-step.retrying .step-copy small { color: var(--status-warning-fg); }.state-icon { flex: 0 0 16px; width: 16px; height: 16px; fill: none; stroke: currentColor; stroke-width: 1.2; stroke-linecap: round; stroke-linejoin: round; color: var(--text-disabled); }.state-icon.pending,.state-icon.reused { color: var(--text-meta); }.state-icon.running { color: var(--ksf-brand); animation: state-spin 1.4s linear infinite; }.state-icon.succeeded { color: var(--status-success-icon); }.state-icon.failed { color: var(--status-danger-icon); }.state-icon.cancelled { color: var(--status-warning-icon); }.state-icon.review { color: var(--status-warning-fg); }@keyframes state-spin { to { transform: rotate(360deg); } }@media (prefers-reduced-motion: reduce) { .state-icon.running { animation: none; } }.step-actions { display: flex; align-items: center; gap: 4px; flex-shrink: 0; padding: 0 8px 0 10px; }.step-actions:empty { display: none; }.graph-action { display: grid; place-items: center; flex: 0 0 24px; box-sizing: border-box; width: 24px; height: 24px; padding: 0; border: 1px solid var(--border-color); border-radius: 4px; color: var(--text-secondary); background: var(--bg-card); cursor: pointer; }.graph-action:hover { background: var(--bg-hover); border-color: var(--border-strong); color: var(--text-primary); }.graph-action:active { background: var(--bg-pressed); }.graph-action:focus-visible { border-color: var(--color-focus); outline: none; }.graph-action svg { width: 14px; height: 14px; fill: none; stroke: currentColor; stroke-width: 1.3; stroke-linecap: round; stroke-linejoin: round; }progress { position: absolute; inset: auto 0 0; display: block; width: 100%; height: 2px; border: 0; accent-color: var(--ksf-brand); }
.node-detail { border: 1px solid var(--border-color); border-radius: 7px; background: var(--bg-card); padding: 13px 15px; margin-top: 12px; font-size: 13px; }.detail-title strong { font-weight: 600; }.clear-selection { margin-left: auto; padding: 0; border: 0; background: transparent; color: var(--color-link); cursor: pointer; font-size: 12px; }.node-detail p { white-space: pre-wrap; overflow-wrap: anywhere; line-height: 1.7; margin: 9px 0 0; }.times,.condition,.selection-hint { color: var(--text-secondary); font-size: 12px; }.selection-hint { margin: 10px 0 0; line-height: 1.6; }.error { color: var(--td-error-color, #c74242); }.empty { padding: 24px; color: var(--text-secondary); text-align: center; font-size: 13px; }
.mode-switch { display: flex; flex-shrink: 0; border: 1px solid var(--border-color); border-radius: 6px; overflow: hidden; }.mode-switch button { font: inherit; font-size: 12px; height: 26px; min-width: 40px; padding: 0 10px; border: 0; background: var(--bg-card); color: var(--text-secondary); cursor: pointer; white-space: nowrap; }.mode-switch button + button { border-left: 1px solid var(--border-color); }.mode-switch button:hover:not(.active) { background: var(--bg-primary); color: var(--text-primary); }.mode-switch button.active { background: var(--ksf-brand-soft); color: var(--ksf-brand-text); font-weight: 600; }
.node-graph { box-sizing: border-box; position: relative; background: var(--bg-card); overscroll-behavior: contain; }.node-graph:focus-visible { outline: 2px solid var(--color-link); outline-offset: 2px; }.node-canvas { height: 100%; }.node-canvas :deep(.graph-edge path) { opacity: .75; }.node-canvas :deep(.graph-edge.highlighted path) { opacity: 1; stroke-width: 2.2; }
.native-body { fill: color-mix(in srgb, var(--graph-status-color) 4%, var(--bg-card)); stroke: color-mix(in srgb, var(--graph-status-color) 35%, var(--border-color)); stroke-width: 1; }.native-header { fill: color-mix(in srgb, var(--graph-status-color) 10%, var(--bg-card)); }.native-divider { stroke: color-mix(in srgb, var(--graph-status-color) 22%, var(--border-color)); }.native-node.review .native-body { fill: var(--status-warning-bg); }.native-node.active .native-body { stroke: var(--color-link); stroke-width: 2; }.native-title { fill: var(--text-primary); font-size: 13px; font-weight: 600; }.native-node.skipped .native-title,.native-node.cancelled .native-title,.native-node.reused .native-title { fill: var(--text-secondary); }.native-meta { fill: var(--text-meta); font-size: 12px; }.native-node.running .native-meta { fill: var(--color-link); }.native-node.failed .native-meta { fill: var(--status-danger-fg); }.native-node.review .native-meta { fill: var(--status-warning-fg); }.native-icon { transform-box: fill-box; transform-origin: center; }.native-tag rect { fill: var(--neutral-status-bg); }.native-tag text { fill: var(--text-secondary); font-size: 11px; }.native-action { cursor: pointer; }.native-action > rect { fill: var(--bg-card); stroke: var(--border-color); }.native-action:hover > rect { fill: var(--bg-hover); stroke: var(--border-strong); }.native-action > path,.native-action > g > * { fill: none; stroke: var(--text-secondary); stroke-width: 1.3; stroke-linecap: round; stroke-linejoin: round; vector-effect: non-scaling-stroke; }.native-progress { fill: var(--ksf-brand); }
/* The style switch takes the legend's room in a narrow pane; the line styles explain themselves. */
.automation-graph { container-type: inline-size; }@container (max-width: 560px) { .legend { display: none; } }
@media(max-width: 640px) { .graph-toolbar { align-items: flex-start; }.breadcrumbs { flex-basis: 100%; }.graph-caption > span { display: none; }.graph-toolbar .zoom-controls { margin-left: auto; } }
</style>
