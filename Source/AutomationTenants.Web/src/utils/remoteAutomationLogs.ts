import type { RemoteAutomationLogLine, RemoteAutomationStep } from '@/api'

type EventObject = Record<string, unknown>
export type RemoteLogLevel = 'error' | 'warning' | 'output' | 'event'
export interface RemoteLogEntry {
  line: RemoteAutomationLogLine
  timestamp: string
  kind: string
  source: string
  nodeTitle: string
  nodePath: (string | null)[]
  level: RemoteLogLevel
  message: string
  raw: unknown
  structured: boolean
}

function object(value: unknown): EventObject | null {
  return value !== null && typeof value === 'object' && !Array.isArray(value) ? value as EventObject : null
}
function field(value: EventObject | null, name: string): unknown {
  if (!value) return undefined
  const key = Object.keys(value).find(key => key.replace(/_/g, '').toLowerCase() === name)
  return key === undefined ? undefined : value[key]
}
function text(value: unknown): string {
  if (typeof value === 'string') return value
  const wrapped = object(value)
  return typeof field(wrapped, 'value') === 'string' ? field(wrapped, 'value') as string : ''
}
function cleanOutput(value: string): string {
  // The native formatter removes terminal styling too; Vue still renders all content as text.
  return value.replace(/\x1b\[[0-?]*[ -/]*[@-~]/g, '').replace(/\r\n?/g, '\n')
}

/** Parse the retained PregelRunEvent, including its real subgraph envelope chain. */
export function parseRemoteLog(line: RemoteAutomationLogLine): RemoteLogEntry {
  let raw: unknown = line.raw
  if (typeof raw === 'string') {
    try { raw = JSON.parse(raw) as unknown } catch { /* Older/plain records retain their server text. */ }
  }
  let event = object(raw)
  const structured = !!event && !!text(field(event, 'kind'))
  const nodePath: (string | null)[] = []
  for (let depth = 0; event && depth < 32; depth++) {
    nodePath.push(text(field(event, 'nodeid')) || null)
    if (text(field(event, 'kind')) !== 'subgraph.event') break
    const child = object(field(event, 'data'))
    if (!child) break
    event = child
  }
  const kind = text(field(event, 'kind'))
  const source = text(field(event, 'source'))
  const failure = object(field(event, 'error'))
  const error = text(field(failure, 'message'))
  const errorCode = text(field(failure, 'code'))
  const outcome = text(field(event, 'message'))
  let message = outcome
  if (error && !message.includes(error)) message += `${message ? '\n' : ''}${error}${errorCode ? ` (${errorCode})` : ''}`
  if (!structured) message = line.text
  const warning = kind === 'node.retry' || kind === 'p4.sync_warning'
  const failed = (kind === 'node.finished' || kind === 'run.finished') && outcome === 'Failed'
  const level: RemoteLogLevel = warning ? 'warning' : (failure && outcome !== 'Cancelled') || failed ? 'error' : kind === 'node.output' ? 'output' : 'event'
  return {
    line, timestamp: text(field(event, 'timestamp')), kind, source,
    nodeTitle: text(field(event, 'nodetitle')) || text(field(event, 'nodeid')),
    nodePath, level, message: cleanOutput(message), raw, structured,
  }
}

export function findRemoteStepPath(steps: RemoteAutomationStep[], target: RemoteAutomationStep): RemoteAutomationStep[] | null {
  for (const step of steps) {
    if (step === target) return [step]
    const child = findRemoteStepPath(step.children ?? [], target)
    if (child) return [step, ...child]
  }
  return null
}

export function resolveRemoteStepPath(steps: RemoteAutomationStep[], keys: string[]): RemoteAutomationStep[] | null {
  const result: RemoteAutomationStep[] = []
  let siblings = steps
  for (const key of keys) {
    const step = siblings.find(step => step.key === key)
    if (!step) return null
    result.push(step)
    siblings = step.children ?? []
  }
  return result.length ? result : null
}

export function remoteLogNodeFilter(path: RemoteAutomationStep[] | null): ((entry: RemoteLogEntry) => boolean) | null {
  if (!path?.length) return null
  const constraints: { depth: number; ids: Set<string> }[] = []
  let depth = 0
  for (const step of path) {
    if (!step.node_ids?.length) return null
    constraints.push({ depth, ids: new Set(step.node_ids) })
    // Stages group nodes in the same graph; only an actual node's children are a nested graph.
    if (!step.is_stage) depth += 1
  }
  return entry => constraints.every(constraint => {
    const id = entry.nodePath[constraint.depth]
    return !!id && constraint.ids.has(id)
  })
}

const kindLabels: Record<string, string> = {
  'run.started': 'Run started', 'run.finished': 'Run finished', 'run.waiting': 'Waiting for resources', 'run.finalizing': 'Finalizing',
  'node.started': 'Node started', 'node.finished': 'Node finished', 'node.output': 'Output', 'node.progress': 'Progress',
  'node.retry': 'Retry', 'node.reused': 'Reused result', 'process.exited': 'Process exited', 'p4.sync_warning': 'Sync warning',
  'mutation.committed': 'Graph expanded',
}
export function remoteLogKind(kind: string): string { return (kindLabels[kind] ?? kind) || 'Log' }
