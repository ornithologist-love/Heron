import type { RemoteAutomationRun } from '@/api'

// Labels follow the Client automation page so both ends read the same run the same way.
export function runStartedAt(run: RemoteAutomationRun) {
  return run.snapshot?.started_at || run.created_at
}

export function relativeTime(value?: string | null) {
  if (!value) return '—'
  const date = new Date(value)
  const today = new Date()
  const hm = date.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit', hour12: false })
  if (date.toDateString() === today.toDateString()) return hm
  const yesterday = new Date(today)
  yesterday.setDate(today.getDate() - 1)
  if (date.toDateString() === yesterday.toDateString()) return `Yesterday ${hm}`
  const md = `${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`
  return date.getFullYear() === today.getFullYear() ? `${md} ${hm}` : `${date.getFullYear()}-${md} ${hm}`
}

/** One short token for dense lists: time today, "Yesterday", otherwise the date. */
export function shortTime(value?: string | null) {
  if (!value) return ''
  const date = new Date(value), today = new Date()
  if (date.toDateString() === today.toDateString()) return date.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit', hour12: false })
  const yesterday = new Date(today)
  yesterday.setDate(today.getDate() - 1)
  if (date.toDateString() === yesterday.toDateString()) return 'Yesterday'
  const md = `${date.getMonth() + 1}-${date.getDate()}`
  return date.getFullYear() === today.getFullYear() ? md : `${date.getFullYear()}-${md}`
}

export function fullTime(value?: string | null) {
  return value ? new Date(value).toLocaleString('en-GB', { hour12: false }) : '—'
}

export function durationText(start?: string | null, end?: string | null, now = Date.now()) {
  if (!start) return '—'
  const seconds = Math.max(0, Math.floor(((end ? Date.parse(end) : now) - Date.parse(start)) / 1000))
  if (!Number.isFinite(seconds)) return '—'
  if (seconds < 1) return '<1s'
  if (seconds < 60) return `${seconds}s`
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ${String(seconds % 60).padStart(2, '0')}s`
  return `${Math.floor(seconds / 3600)}h ${Math.floor(seconds % 3600 / 60)}m`
}

/** Short trigger label shown beside a run, matching the Client's Schedule / Manual / CLI / Tray. Chinese literals below match trigger data, not display text. */
export function runTrigger(run: RemoteAutomationRun) {
  if (run.source !== 'local') return 'Cloud'
  const trigger = run.snapshot?.trigger?.toLowerCase() ?? ''
  if (trigger.includes('schedule') || trigger.includes('定时')) return 'Schedule'
  if (trigger.includes('cli')) return 'CLI'
  if (trigger.includes('tray') || trigger.includes('托盘')) return 'Tray'
  return 'Manual'
}
