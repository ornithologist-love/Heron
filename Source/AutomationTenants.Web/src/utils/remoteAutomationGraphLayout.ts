import type { RemoteAutomationStep } from '@/api'

type Point = { x: number; y: number }
type Rect = Point & { width: number; height: number }
export interface AutomationGraphNode extends Rect {
  id: string
  path: string[]
  step: RemoteAutomationStep
  kind: 'step' | 'stage' | 'recovery'
  chip: string
  note: string
}
export interface AutomationGraphEdge {
  id: string
  source: string
  target: string
  points: Point[]
  conditional: boolean
  arrow: boolean
}
export interface AutomationGraphLayout {
  width: number
  height: number
  nodes: AutomationGraphNode[]
  edges: AutomationGraphEdge[]
  trays: (Rect & { id: string })[]
}
interface Item extends AutomationGraphLayout {
  key: string
  id: string
  incoming: number[]
  outgoing: number[]
}

const nodeWidth = 236, nodeHeight = 52, stageHeader = 40, stageGap = 8, laneGap = 28
export const automationGraphNodeId = (path: string[]) => JSON.stringify(path)
const point = (x: number, y: number): Point => ({ x, y })
const blank = (): AutomationGraphLayout => ({ width: 0, height: 0, nodes: [], edges: [], trays: [] })

// Match AutomationStepItemViewModel. Finally is drawn after the last body step;
// recovery uses an independent conditional lane, never an ordinary join.
function dependencies(steps: RemoteAutomationStep[], index: number): string[] {
  const step = steps[index]!
  const parents = step.is_finally
    ? step.recovery_from?.length ? [step.recovery_from[step.recovery_from.length - 1]!] : null
    : step.depends_on
  return parents ?? (index > 0 ? [steps[index - 1]!.key] : [])
}
function conditionalSources(step: RemoteAutomationStep) { return step.is_finally ? [] : step.recovery_from ?? [] }
// The node graph view reads the same dependency semantics.
export { dependencies as automationStepDependencies, conditionalSources as automationStepConditionalSources }

function topology(steps: RemoteAutomationStep[]) {
  const byKey = new Map(steps.map((step, index) => [step.key, index]))
  const parents = new Map(steps.map((step, index) => [step.key, dependencies(steps, index).filter(key => byKey.has(key))]))
  const next = new Map(steps.map(step => [step.key, steps.filter(child => parents.get(child.key)!.includes(step.key)).map(child => child.key)]))
  const ranks = new Map<string, number>()
  function rank(key: string, visiting = new Set<string>()): number {
    if (ranks.has(key)) return ranks.get(key)!
    if (visiting.has(key)) return 0
    visiting.add(key)
    const step = steps[byKey.get(key)!]!
    const sources = [...parents.get(key)!, ...conditionalSources(step).filter(source => byKey.has(source))]
    const result = Math.max(0, ...sources.filter(source => source !== key).map(source => rank(source, visiting) + 1))
    visiting.delete(key)
    ranks.set(key, result)
    return result
  }
  steps.forEach(step => rank(step.key))
  return { parents, next, ranks }
}

function append(target: AutomationGraphLayout, source: AutomationGraphLayout, x: number, y: number) {
  target.nodes.push(...source.nodes.map(node => ({ ...node, x: node.x + x, y: node.y + y })))
  target.trays.push(...source.trays.map(tray => ({ ...tray, x: tray.x + x, y: tray.y + y })))
  target.edges.push(...source.edges.map(edge => ({ ...edge, points: edge.points.map(p => point(p.x + x, p.y + y)) })))
}
function edge(layout: AutomationGraphLayout, source: string, target: string, points: Point[], conditional = false, arrow = true) {
  layout.edges.push({ id: `${source}>${target}:${layout.edges.length}`, source, target, points, conditional, arrow })
}

function measure(step: RemoteAutomationStep, path: string[]): Item {
  const id = automationGraphNodeId(path)
  const graph = blank()
  if (step.is_stage) {
    const children = stageLayout(step.children ?? [], path)
    const entries = (step.children ?? []).filter((_, index, all) => dependencies(all, index).every(key => !all.some(item => item.key === key)))
    const chip = entries.length > 1 ? `${entries.length} parallel` : ''
    const note = step.is_finally ? 'Runs after success, failure or cancellation' : step.is_recovery ? 'Runs on failure or cancellation' : ''
    const contentTop = stageHeader + (chip || note ? 28 : 0)
    const width = Math.max(nodeWidth + 16, children.width + 16)
    const height = contentTop + children.height + 8
    graph.nodes.push({ id, path, step, kind: 'stage', x: 0, y: 0, width, height, chip, note })
    append(graph, children, 8, contentTop)
    if (children.incoming.length > 1) for (const y of children.incoming)
      edge(graph, id, id, [point(0, contentTop + y), point(8, contentTop + y)], false, false)
    if (children.outgoing.length > 1) for (const y of children.outgoing)
      edge(graph, id, id, [point(children.width + 8, contentTop + y), point(width, contentTop + y)], false, false)
    graph.width = width
    graph.height = height
    return { ...graph, key: step.key, id,
      incoming: children.incoming.length > 1 ? children.incoming.map(y => y + contentTop) : [stageHeader / 2],
      outgoing: children.outgoing.length > 1 ? children.outgoing.map(y => y + contentTop) : [stageHeader / 2] }
  }
  if (step.is_recovery) {
    const width = nodeWidth + 16, height = stageHeader + 28 + nodeHeight + 8
    graph.nodes.push({ id: `${id}:group`, path, step, kind: 'recovery', x: 0, y: 0, width, height, chip: '', note: 'Runs on failure or cancellation' })
    graph.nodes.push({ id, path, step, kind: 'step', x: 8, y: stageHeader + 28, width: nodeWidth, height: nodeHeight, chip: '', note: '' })
    return { ...graph, width, height, key: step.key, id, incoming: [stageHeader / 2], outgoing: [stageHeader / 2] }
  }
  graph.nodes.push({ id, path, step, kind: 'step', x: 0, y: 0, width: nodeWidth, height: nodeHeight, chip: '', note: '' })
  return { ...graph, width: nodeWidth, height: nodeHeight, key: step.key, id, incoming: [nodeHeight / 2], outgoing: [nodeHeight / 2] }
}

// DependencyGraphPanel.BuildBranches / LayoutBranches: preserve complete linear
// chains, split at forks and joins, and stack the branch trays inside each stage.
function stageLayout(steps: RemoteAutomationStep[], scope: string[]): AutomationGraphLayout & { incoming: number[]; outgoing: number[] } {
  const graph = blank()
  if (!steps.length) return { ...graph, incoming: [], outgoing: [] }
  const { parents, next, ranks } = topology(steps)
  const items = new Map(steps.map(step => [step.key, measure(step, [...scope, step.key])]))
  const branches: string[][] = [], assigned = new Set<string>(), owners = new Map<string, number>()
  function addBranch(head: string) {
    const chain: string[] = []
    let key = head
    while (!assigned.has(key)) {
      assigned.add(key)
      owners.set(key, branches.length)
      chain.push(key)
      const successors = next.get(key)!
      if (successors.length !== 1 || parents.get(successors[0]!)!.length !== 1) break
      key = successors[0]!
    }
    if (chain.length) branches.push(chain)
  }
  for (const step of [...steps].sort((a, b) => ranks.get(a.key)! - ranks.get(b.key)!))
    if (parents.get(step.key)!.length !== 1 || next.get(parents.get(step.key)![0]!)!.length !== 1) addBranch(step.key)
  steps.forEach(step => { if (!assigned.has(step.key)) addBranch(step.key) })
  const inset = branches.length > 1 ? stageGap * 1.5 : stageGap / 2
  const contentWidth = Math.max(nodeWidth, ...Array.from(items.values(), item => item.width))
  const positions = new Map<string, Rect>()
  let y = 0
  for (const [index, branch] of branches.entries()) {
    const top = y
    if (branches.length > 1) y += stageGap / 2
    for (const key of branch) {
      const item = items.get(key)!
      positions.set(key, { x: inset, y, width: item.width, height: item.height })
      append(graph, item, inset, y)
      y += item.height + stageGap
    }
    y -= stageGap
    if (branches.length > 1) {
      y += stageGap / 2
      graph.trays.push({ id: `${automationGraphNodeId(scope)}:branch:${index}`, x: stageGap, y: top, width: contentWidth + stageGap, height: y - top })
    }
    y += stageGap
  }
  graph.width = contentWidth + inset * 2
  graph.height = Math.max(0, y - stageGap)
  const entries = steps.filter(step => !parents.get(step.key)!.length)
  const exits = steps.filter(step => !next.get(step.key)!.length)
  const anchor = (key: string) => {
    const box = positions.get(key)!, item = items.get(key)!
    return box.y + Math.min(box.height / 2, item.nodes[0]?.kind === 'step' ? nodeHeight / 2 : stageHeader / 2)
  }
  if (branches.length > 1) {
    if (entries.length > 1) for (const entry of entries) {
      const box = positions.get(entry.key)!, item = items.get(entry.key)!
      edge(graph, automationGraphNodeId(scope), item.id, [point(0, anchor(entry.key)), point(box.x, anchor(entry.key))])
    }
    if (exits.length > 1) for (const exit of exits) {
      const box = positions.get(exit.key)!, item = items.get(exit.key)!
      edge(graph, item.id, automationGraphNodeId(scope), [point(box.x + box.width, anchor(exit.key)), point(graph.width, anchor(exit.key))], false, false)
    }
  }
  for (const step of steps) {
    const target = positions.get(step.key)!, targetItem = items.get(step.key)!
    for (const parent of parents.get(step.key)!) {
      const source = positions.get(parent)!, sourceItem = items.get(parent)!
      if (owners.get(parent) !== owners.get(step.key) || target.y <= source.y) {
        edge(graph, sourceItem.id, targetItem.id, [point(source.x, anchor(parent)), point(stageGap / 2, anchor(parent)), point(stageGap / 2, anchor(step.key)), point(target.x, anchor(step.key))])
      } else {
        const middle = target.y - stageGap / 2
        edge(graph, sourceItem.id, targetItem.id, [point(source.x + source.width / 2, source.y + source.height), point(source.x + source.width / 2, middle), point(target.x + target.width / 2, middle), point(target.x + target.width / 2, target.y)])
      }
    }
    for (const key of conditionalSources(step)) {
      const source = positions.get(key)
      if (!source) continue
      const side = graph.width - 2
      edge(graph, items.get(key)!.id, targetItem.id, [point(source.x + source.width, anchor(key)), point(side, anchor(key)), point(side, anchor(step.key)), point(target.x + target.width, anchor(step.key))], true)
    }
  }
  return { ...graph, incoming: entries.map(step => anchor(step.key)), outgoing: exits.map(step => anchor(step.key)) }
}

// Same horizontal layering and parent-top alignment as Avalonia's LayoutHorizontal.
// Nested stage widths/heights participate in layout rather than becoming opaque cards.
export function layoutRemoteAutomationGraph(steps: RemoteAutomationStep[], scope: string[] = []): AutomationGraphLayout {
  const graph = blank()
  if (!steps.length) return graph
  const { parents, ranks } = topology(steps)
  const items = new Map(steps.map(step => [step.key, measure(step, [...scope, step.key])]))
  const hasOverhead = steps.some(step => conditionalSources(step).length)
    || steps.some(step => parents.get(step.key)!.some(parent => ranks.get(parent)! >= ranks.get(step.key)!))
  const overhead = hasOverhead ? laneGap : 0
  const positions = new Map<string, Rect>()
  const layers = [...new Set(ranks.values())].sort((a, b) => a - b)
  let x = 0, bottom = 0
  function parentTop(key: string) {
    const placed = parents.get(key)!.map(parent => positions.get(parent)).filter((box): box is Rect => !!box)
    return placed.length ? placed.reduce((sum, box) => sum + box.y, 0) / placed.length : overhead
  }
  for (const rank of layers) {
    let top = overhead, columnWidth = 0
    for (const step of steps.filter(step => ranks.get(step.key) === rank).sort((a, b) => parentTop(a.key) - parentTop(b.key))) {
      const item = items.get(step.key)!
      top = Math.max(top, parentTop(step.key))
      positions.set(step.key, { x, y: top, width: item.width, height: item.height })
      append(graph, item, x, top)
      columnWidth = Math.max(columnWidth, item.width)
      bottom = Math.max(bottom, top + item.height)
      top += item.height + laneGap
    }
    x += columnWidth + laneGap
  }
  for (const step of steps) {
    const to = positions.get(step.key)!, target = items.get(step.key)!
    for (const parent of parents.get(step.key)!) {
      const from = positions.get(parent)!, source = items.get(parent)!
      const sourceYs = source.outgoing.map(value => from.y + value), targetYs = target.incoming.map(value => to.y + value)
      if (to.x <= from.x) {
        edge(graph, source.id, target.id, [point(from.x + from.width, sourceYs[0]!), point(from.x + from.width + laneGap / 3, sourceYs[0]!), point(from.x + from.width + laneGap / 3, 8), point(to.x - laneGap / 3, 8), point(to.x - laneGap / 3, targetYs[0]!), point(to.x, targetYs[0]!)])
        continue
      }
      const middle = to.x - laneGap / 2
      for (const sourceY of sourceYs) edge(graph, source.id, target.id, [point(from.x + from.width, sourceY), point(middle, sourceY)], false, false)
      edge(graph, source.id, target.id, [point(middle, Math.min(...sourceYs, ...targetYs)), point(middle, Math.max(...sourceYs, ...targetYs))], false, false)
      for (const targetY of targetYs) edge(graph, source.id, target.id, [point(middle, targetY), point(to.x, targetY)], false, target.incoming.length < 2)
    }
    for (const sourceKey of conditionalSources(step)) {
      const from = positions.get(sourceKey)
      if (!from) continue
      const source = items.get(sourceKey)!, turn = from.x + from.width + laneGap / 2
      edge(graph, source.id, target.id, [point(from.x + from.width, from.y + 20), point(turn, from.y + 20), point(turn, laneGap / 4), point(to.x + to.width / 2, laneGap / 4), point(to.x + to.width / 2, to.y)], true)
    }
  }
  graph.width = Math.max(0, x - laneGap)
  graph.height = bottom
  const padded = blank()
  // Only room for the selection outline: the canvas itself is frameless, as in the Client.
  append(padded, graph, 4, 4)
  padded.width = graph.width + 8
  padded.height = graph.height + 8
  return padded
}
