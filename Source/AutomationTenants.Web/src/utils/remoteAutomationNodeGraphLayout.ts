import type { RemoteAutomationStep } from '@/api'
import { autoPositions } from './graphAutoLayout'
import { automationGraphNodeId, automationStepConditionalSources, automationStepDependencies } from './remoteAutomationGraphLayout'

export interface AutomationNodeGraphNode {
  id: string
  path: string[]
  step: RemoteAutomationStep
  /** The stage the step was drawn in before stages dissolved, or its recovery/finally role. */
  tag: string
  x: number
  y: number
}
export interface AutomationNodeGraphEdge { id: string; from: string; to: string; conditional: boolean }
export interface AutomationNodeGraph {
  nodes: AutomationNodeGraphNode[]
  edges: AutomationNodeGraphEdge[]
  nodeWidth: number
  nodeHeight: number
  /** Whether nodes carry the tag row; the height is shared so the layered layout stays aligned. */
  tagged: boolean
  width: number
  height: number
}

export const nodeGraphWidth = 220
export const nodeGraphHeaderHeight = 34
export const nodeGraphMetaHeight = 24
export const nodeGraphTagHeight = 26
// The canvas draws edges as beziers whose handles reach at least 90px; a
// narrower lane would bend them back into the nodes.
const laneGap = 96, rowGap = 24, componentGap = 48

interface Ports { entries: string[]; exits: string[] }

// Stages dissolve into their steps: depending on a stage means depending on its
// exit steps, and a stage's dependencies lead into its entry steps. Recovery
// steps are entered conditionally, so they never stand for a stage's start or end.
function flatten(steps: RemoteAutomationStep[], scope: string[], tag: string,
  nodes: AutomationNodeGraphNode[], edges: AutomationNodeGraphEdge[], seen: Set<string>): Map<string, Ports> {
  const ports = new Map<string, Ports>()
  for (const step of steps) {
    const path = [...scope, step.key]
    const children = step.children ?? []
    if (step.is_stage && children.length) {
      const inner = flatten(children, path, step.title, nodes, edges, seen)
      const parents = new Map(children.map((child, index) => [child.key, automationStepDependencies(children, index).filter(key => inner.has(key))]))
      const body = children.filter(child => !child.is_recovery)
      const entries = body.filter(child => !parents.get(child.key)!.length)
      const exits = body.filter(child => !body.some(other => parents.get(other.key)!.includes(child.key)))
      ports.set(step.key, {
        entries: entries.flatMap(child => inner.get(child.key)!.entries),
        exits: exits.flatMap(child => inner.get(child.key)!.exits),
      })
      continue
    }
    const id = automationGraphNodeId(path)
    nodes.push({ id, path, step, tag: step.is_recovery ? 'Recovery' : tag || (step.is_finally ? 'Finally' : ''), x: 0, y: 0 })
    ports.set(step.key, { entries: [id], exits: [id] })
  }
  function connect(source: Ports, target: Ports, conditional: boolean) {
    for (const from of source.exits) for (const to of target.entries) {
      const key = `${from}>${to}`
      if (from === to || seen.has(key)) continue
      seen.add(key)
      edges.push({ id: `edge-${edges.length}`, from, to, conditional })
    }
  }
  steps.forEach((step, index) => {
    const target = ports.get(step.key)!
    for (const key of automationStepDependencies(steps, index)) {
      const source = ports.get(key)
      if (source) connect(source, target, false)
    }
    for (const key of automationStepConditionalSources(step)) {
      const source = ports.get(key)
      if (source) connect(source, target, true)
    }
  })
  return ports
}

/** Positions come from the restored layered layout on every call; nothing is stored. */
export function layoutRemoteAutomationNodeGraph(steps: RemoteAutomationStep[], scope: string[] = [], preview = false): AutomationNodeGraph {
  const nodes: AutomationNodeGraphNode[] = [], edges: AutomationNodeGraphEdge[] = []
  flatten(steps, scope, '', nodes, edges, new Set())
  const tagged = nodes.some(node => node.tag)
  const nodeHeight = nodeGraphHeaderHeight + (preview ? 0 : nodeGraphMetaHeight) + (tagged ? nodeGraphTagHeight : 0)
  const order = new Map(nodes.map((node, index) => [node.id, index]))
  const positions = autoPositions({ nodes, edges }, {
    nodeWidth: nodeGraphWidth, nodeHeight, laneGap, rowGap, componentGap, origin: { x: 0, y: 0 },
    // Declaration order, not id order: step keys are script names and would scramble the flow.
    compare: (a, b) => order.get(a.id)! - order.get(b.id)!,
  })
  for (const node of nodes) Object.assign(node, positions[node.id])
  return {
    nodes, edges, nodeWidth: nodeGraphWidth, nodeHeight, tagged,
    width: nodes.length ? Math.max(...nodes.map(node => node.x)) + nodeGraphWidth : 0,
    height: nodes.length ? Math.max(...nodes.map(node => node.y)) + nodeHeight : 0,
  }
}
