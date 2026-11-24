// Layered auto layout restored from Blackboard's dependency graph (bb_web
// TicketDependencyGraph.vue autoPositions(), commit 2717340). The algorithm is
// unchanged; only its inputs became parameters so any node/edge list can use it.

export interface GraphAutoLayoutNode { id: string }
export interface GraphAutoLayoutEdge { from: string; to: string }
export interface GraphAutoLayoutOptions<T extends GraphAutoLayoutNode> {
  nodeWidth?: number
  nodeHeight?: number
  laneGap?: number
  rowGap?: number
  componentGap?: number
  origin?: { x: number; y: number }
  /** Initial order inside a level and tie-break; Blackboard sorted by id. */
  compare?: (a: T, b: T) => number
}

export function autoPositions<T extends GraphAutoLayoutNode>(
  graph: { nodes: T[]; edges: GraphAutoLayoutEdge[] },
  options: GraphAutoLayoutOptions<T> = {},
): Record<string, { x: number; y: number }> {
  const nodeWidth = options.nodeWidth ?? 320
  const nodeHeight = options.nodeHeight ?? 102
  const laneGap = options.laneGap ?? 170
  const rowGap = options.rowGap ?? 42
  const componentGap = options.componentGap ?? 110
  const originX = options.origin?.x ?? 72
  const originY = options.origin?.y ?? 84
  const compare = options.compare ?? ((a: T, b: T) => a.id.localeCompare(b.id))

  const deps = new Map<string, string[]>()
  const children = new Map<string, string[]>()
  for (const node of graph.nodes) {
    deps.set(node.id, [])
    children.set(node.id, [])
  }
  for (const edge of graph.edges) {
    deps.get(edge.to)?.push(edge.from)
    children.get(edge.from)?.push(edge.to)
  }

  const memo = new Map<string, number>()
  function levelFor(id: string, seen = new Set<string>()): number {
    if (memo.has(id)) return memo.get(id) ?? 0
    if (seen.has(id)) return 0
    seen.add(id)
    const level = Math.max(0, ...((deps.get(id) ?? []).map((dep) => levelFor(dep, seen) + 1)))
    seen.delete(id)
    memo.set(id, level)
    return level
  }

  const byLevel = new Map<number, T[]>()
  const levelById = new Map<string, number>()
  for (const node of graph.nodes) {
    const level = levelFor(node.id)
    levelById.set(node.id, level)
    const list = byLevel.get(level) ?? []
    list.push(node)
    byLevel.set(level, list)
  }

  const levels = [...byLevel.keys()].sort((a, b) => a - b)
  const order = new Map<string, number>()
  for (const level of levels) {
    const nodes = byLevel.get(level) ?? []
    nodes.sort(compare)
    nodes.forEach((node, index) => order.set(node.id, index))
  }

  const neighborAverage = (ids: string[]) => {
    const values = ids.map((id) => order.get(id)).filter((value): value is number => value !== undefined)
    if (values.length === 0) return null
    return values.reduce((sum, value) => sum + value, 0) / values.length
  }

  for (let sweep = 0; sweep < 3; sweep += 1) {
    for (const level of levels) {
      if (level === levels[0]) continue
      const nodes = byLevel.get(level) ?? []
      nodes.sort((a, b) => {
        const aPressure = neighborAverage(deps.get(a.id) ?? []) ?? order.get(a.id) ?? 0
        const bPressure = neighborAverage(deps.get(b.id) ?? []) ?? order.get(b.id) ?? 0
        return aPressure - bPressure || compare(a, b)
      })
      nodes.forEach((node, index) => order.set(node.id, index))
    }

    const lastLevel = levels[levels.length - 1]
    for (const level of [...levels].reverse()) {
      if (level === lastLevel) continue
      const nodes = byLevel.get(level) ?? []
      nodes.sort((a, b) => {
        const aPressure = neighborAverage(children.get(a.id) ?? []) ?? order.get(a.id) ?? 0
        const bPressure = neighborAverage(children.get(b.id) ?? []) ?? order.get(b.id) ?? 0
        return aPressure - bPressure || compare(a, b)
      })
      nodes.forEach((node, index) => order.set(node.id, index))
    }
  }

  const yById = new Map<string, number>()
  const desiredY = new Map<string, number>()
  const yForRow = (index: number) => originY + index * (nodeHeight + rowGap)
  for (const level of levels) {
    const nodes = [...(byLevel.get(level) ?? [])].sort((a, b) => (order.get(a.id) ?? 0) - (order.get(b.id) ?? 0))
    nodes.forEach((node, index) => yById.set(node.id, yForRow(index)))
  }

  const averageChildCenter = (id: string) => {
    const childCenters = (children.get(id) ?? [])
      .map((childId) => yById.get(childId))
      .filter((value): value is number => value !== undefined)
      .map((y) => y + nodeHeight / 2)
    if (childCenters.length === 0) return null
    return childCenters.reduce((sum, value) => sum + value, 0) / childCenters.length
  }

  for (const level of [...levels].reverse()) {
    const nodes = [...(byLevel.get(level) ?? [])].sort((a, b) => (order.get(a.id) ?? 0) - (order.get(b.id) ?? 0))
    for (const node of nodes) {
      const centered = averageChildCenter(node.id)
      desiredY.set(node.id, centered === null ? yById.get(node.id) ?? originY : centered - nodeHeight / 2)
    }
    let previous = -Infinity
    for (const node of [...nodes].sort((a, b) => (desiredY.get(a.id) ?? 0) - (desiredY.get(b.id) ?? 0))) {
      const desired = desiredY.get(node.id) ?? originY
      const y = Math.max(desired, previous + nodeHeight + rowGap)
      yById.set(node.id, y)
      previous = y
    }
  }

  const isSoleSuccessor = (id: string) => {
    const parentIds = deps.get(id) ?? []
    return parentIds.length > 0 && parentIds.every((parentId) => (children.get(parentId) ?? []).length === 1)
  }

  for (const level of levels) {
    const nodes = [...(byLevel.get(level) ?? [])].sort((a, b) => (order.get(a.id) ?? 0) - (order.get(b.id) ?? 0))
    for (const node of nodes) {
      const parentCenters = (deps.get(node.id) ?? [])
        .map((parentId) => yById.get(parentId))
        .filter((value): value is number => value !== undefined)
        .map((y) => y + nodeHeight / 2)
      const centered = parentCenters.length > 0
        ? parentCenters.reduce((sum, value) => sum + value, 0) / parentCenters.length
        : null
      desiredY.set(node.id, isSoleSuccessor(node.id) && centered !== null ? centered - nodeHeight / 2 : yById.get(node.id) ?? originY)
    }
    let previous = -Infinity
    for (const node of [...nodes].sort((a, b) => (desiredY.get(a.id) ?? 0) - (desiredY.get(b.id) ?? 0))) {
      const desired = desiredY.get(node.id) ?? originY
      const y = Math.max(desired, previous + nodeHeight + rowGap)
      yById.set(node.id, y)
      previous = y
    }
  }

  const componentIds = new Map<string, number>()
  const components: string[][] = []
  const byId = new Map(graph.nodes.map((node) => [node.id, node]))
  for (const node of graph.nodes) {
    if (componentIds.has(node.id)) continue
    const index = components.length
    const queue = [node.id]
    const ids: string[] = []
    componentIds.set(node.id, index)
    while (queue.length > 0) {
      const id = queue.shift()
      if (!id) continue
      ids.push(id)
      const linked = [...(deps.get(id) ?? []), ...(children.get(id) ?? [])]
      for (const linkedId of linked) {
        if (!byId.has(linkedId) || componentIds.has(linkedId)) continue
        componentIds.set(linkedId, index)
        queue.push(linkedId)
      }
    }
    components.push(ids)
  }

  const compactComponent = (ids: string[]) => {
    const localLevels = [...new Set(ids.map((id) => levelById.get(id) ?? 0))].sort((a, b) => a - b)
    const nodesAtLevel = (level: number) => ids
      .filter((id) => (levelById.get(id) ?? 0) === level)
      .map((id) => byId.get(id))
      .filter((node): node is T => node !== undefined)

    const placeCompact = (nodes: T[], desiredTop: Map<string, number> | null) => {
      const sorted = [...nodes].sort((a, b) => {
        const aDesired = desiredTop?.get(a.id)
        const bDesired = desiredTop?.get(b.id)
        if (aDesired !== undefined || bDesired !== undefined) {
          return (aDesired ?? yById.get(a.id) ?? originY) - (bDesired ?? yById.get(b.id) ?? originY)
            || (order.get(a.id) ?? 0) - (order.get(b.id) ?? 0)
            || compare(a, b)
        }
        return (order.get(a.id) ?? 0) - (order.get(b.id) ?? 0) || compare(a, b)
      })
      const groupHeight = sorted.length * nodeHeight + Math.max(0, sorted.length - 1) * rowGap
      const desiredCenters = sorted
        .map((node) => desiredTop?.get(node.id))
        .filter((value): value is number => value !== undefined)
        .map((y) => y + nodeHeight / 2)
      const center = desiredCenters.length > 0
        ? desiredCenters.reduce((sum, value) => sum + value, 0) / desiredCenters.length
        : originY + groupHeight / 2
      const desiredTops = sorted.map((node) => desiredTop?.get(node.id) ?? yById.get(node.id) ?? originY)
      const start = desiredCenters.length === sorted.length
        ? center - groupHeight / 2
        : desiredCenters.length > 0
          ? Math.min(...desiredTops)
          : originY
      sorted.forEach((node, index) => yById.set(node.id, start + index * (nodeHeight + rowGap)))
    }

    for (const level of localLevels) placeCompact(nodesAtLevel(level), null)

    for (let sweep = 0; sweep < 3; sweep += 1) {
      for (const level of [...localLevels].reverse()) {
        const desired = new Map<string, number>()
        for (const node of nodesAtLevel(level)) {
          const childIds = children.get(node.id) ?? []
          if (childIds.length === 1 && (deps.get(childIds[0]!) ?? []).length > 1) continue
          const childCenters = childIds
            .map((childId) => yById.get(childId))
            .filter((value): value is number => value !== undefined)
            .map((y) => y + nodeHeight / 2)
          if (childCenters.length > 0) {
            desired.set(node.id, childCenters.reduce((sum, value) => sum + value, 0) / childCenters.length - nodeHeight / 2)
          }
        }
        if (desired.size > 0) placeCompact(nodesAtLevel(level), desired)
      }

      for (const level of localLevels) {
        const desired = new Map<string, number>()
        for (const node of nodesAtLevel(level)) {
          const parentCenters = (deps.get(node.id) ?? [])
            .map((parentId) => yById.get(parentId))
            .filter((value): value is number => value !== undefined)
            .map((y) => y + nodeHeight / 2)
          if (parentCenters.length > 0) {
            desired.set(node.id, parentCenters.reduce((sum, value) => sum + value, 0) / parentCenters.length - nodeHeight / 2)
          }
        }
        if (desired.size > 0) placeCompact(nodesAtLevel(level), desired)
      }
    }
  }

  for (const ids of components) compactComponent(ids)

  let nextComponentY = originY
  for (const ids of components.sort((a, b) => compare(byId.get(a[0]!)!, byId.get(b[0]!)!))) {
    const ys = ids.map((id) => yById.get(id) ?? originY)
    const minY = Math.min(...ys)
    const maxY = Math.max(...ys.map((y) => y + nodeHeight))
    const offset = nextComponentY - minY
    for (const id of ids) yById.set(id, (yById.get(id) ?? originY) + offset)
    nextComponentY += maxY - minY + componentGap
  }

  const next: Record<string, { x: number; y: number }> = {}
  for (const [level, nodes] of [...byLevel.entries()].sort((a, b) => a[0] - b[0])) {
    nodes.forEach((node) => {
      next[node.id] = {
        x: originX + level * (nodeWidth + laneGap),
        y: yById.get(node.id) ?? originY,
      }
    })
  }
  return next
}

// Width estimate for SVG text, which cannot ellipsize itself: CJK glyphs count
// as 1.9 units, a space as 0.55 and everything else as 1.
export function visualUnits(value: string) {
  return [...value].reduce((sum, char) => {
    if (char === ' ') return sum + 0.55
    return sum + (/[㐀-鿿＀-￯]/.test(char) ? 1.9 : 1)
  }, 0)
}

export function ellipsizeTitle(value: string, maxUnits: number) {
  let result = value
  while (result.length > 0 && visualUnits(`${result}...`) > maxUnits) {
    result = result.slice(0, -1)
  }
  return `${result.trimEnd()}...`
}
