<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, shallowRef, triggerRef, watch } from 'vue'
import { useRoute } from 'vue-router'
import { MessagePlugin } from 'tdesign-vue-next'
import type { RemoteAutomationLogLine, RemoteAutomationRun, RemoteAutomationStep } from '@/api'
import { parseRemoteLog, remoteLogKind, remoteLogNodeFilter, type RemoteLogEntry, type RemoteLogLevel } from '@/utils/remoteAutomationLogs'

const props = defineProps<{
  run: RemoteAutomationRun
  lines: RemoteAutomationLogLine[]
  selectedPath: RemoteAutomationStep[] | null
  loading: boolean
  loaded: boolean
  drained: boolean
  hasMore: boolean
  loadingHistory: boolean
  error: string
  disconnected?: boolean
}>()
const emit = defineEmits<{
  more: []
  history: []
  'stop-history': []
  retry: []
  'clear-selection': []
}>()
const route = useRoute()

// Logs can reach hundreds of thousands of lines. Everything below is incremental and only the
// rows inside the viewport are rendered, so cost per poll is proportional to the new lines only.
const ROW = 20, OVERSCAN = 30
const entries = shallowRef<RemoteLogEntry[]>([])
const lowered: string[] = []
const sources = ref<string[]>([])
const unidentified = ref(0)
let ingested: RemoteAutomationLogLine[] = []

const input = ref(''), query = ref(''), onlyMatches = ref(false)
const category = ref('all'), source = ref('all')
const following = ref(true), pausedAt = ref(0)
const panel = ref<HTMLElement | null>(null), searchBox = ref<{ focus?: () => void } | null>(null)
const scrollTop = ref(0), viewportHeight = ref(320)
// Selection is kept as sequence numbers so it survives filtering and can be shared.
const anchor = ref<number | null>(null), selection = ref<{ from: number; to: number } | null>(null)
const pendingJump = ref<{ from: number; to: number } | null>(null)
const view = shallowRef<number[] | null>(null)
const matches = shallowRef<number[]>([])
const matchIndex = ref(-1)

const nodeFilter = computed(() => remoteLogNodeFilter(props.selectedPath))
const needle = computed(() => query.value.trim().toLocaleLowerCase())
const filtering = computed(() => !!nodeFilter.value || category.value !== 'all' || source.value !== 'all' || (onlyMatches.value && !!needle.value))
const total = computed(() => view.value ? view.value.length : entries.value.length)
const scope = computed(() => props.selectedPath?.map(step => step.title).join(' / ') ?? '')
const newCount = computed(() => following.value ? 0 : Math.max(0, entries.value.length - pausedAt.value))
const categoryOptions = [{ value: 'all', label: 'All types' }, ...(Object.entries({ error: 'Error', warning: 'Warning / retry', output: 'Output', event: 'Event' } satisfies Record<RemoteLogLevel, string>)).map(([value, label]) => ({ value, label }))]
const sourceOptions = computed(() => [{ value: 'all', label: 'All sources' }, ...sources.value.map(value => ({ value, label: sourceName(value) }))])
const status = computed(() => {
  if (!props.loaded) return props.loading ? 'Loading logs…' : 'Logs not loaded'
  if (props.loadingHistory) return `Loading more history · ${entries.value.length.toLocaleString()} lines read`
  if (props.hasMore) return `${entries.value.length.toLocaleString()} lines read; more history on server`
  if (props.drained) return 'All server logs loaded'
  if (props.run.logs_complete) return 'Runner synced; confirming end of log'
  if (props.disconnected) return 'Machine offline; keeping current logs until it reports again'
  return 'At the end; waiting for new logs'
})
const rendered = computed(() => {
  const start = Math.max(0, Math.floor(scrollTop.value / ROW) - OVERSCAN)
  const end = Math.min(total.value, Math.ceil((scrollTop.value + viewportHeight.value) / ROW) + OVERSCAN)
  const rows: { position: number; entry: RemoteLogEntry; index: number }[] = []
  for (let position = start; position < end; position++) {
    const index = view.value ? view.value[position]! : position
    rows.push({ position, entry: entries.value[index]!, index })
  }
  return { offset: start * ROW, rows }
})
const matchSet = computed(() => new Set(matches.value))
const selectedEntries = computed(() => {
  const range = selection.value
  if (!range) return []
  return entries.value.filter(entry => entry.line.sequence >= range.from && entry.line.sequence <= range.to)
})

function sourceName(value: string) {
  return value === 'stdout' ? 'stdout' : value === 'stderr' ? 'stderr' : value || 'event'
}
function timeText(value: string) {
  const timestamp = Date.parse(value)
  if (!Number.isFinite(timestamp)) return '--:--:--'
  const date = new Date(timestamp)
  return `${date.toLocaleTimeString('en-GB', { hour12: false })}.${String(date.getMilliseconds()).padStart(3, '0')}`
}
function message(entry: RemoteLogEntry) {
  if (!['node.finished', 'run.finished', 'node.reused'].includes(entry.kind)) return entry.message
  const states: Record<string, string> = { Succeeded: 'Succeeded', Completed: 'Completed', Failed: 'Failed', Cancelled: 'Cancelled', Skipped: 'Skipped', Interrupted: 'Awaiting review' }
  return states[entry.message] ?? entry.message
}
function firstLine(entry: RemoteLogEntry) {
  const text = message(entry)
  const index = text.indexOf('\n')
  return index < 0 ? text : text.slice(0, index)
}
function extraLines(entry: RemoteLogEntry) {
  let count = 0
  const text = entry.message
  for (let index = text.indexOf('\n'); index >= 0; index = text.indexOf('\n', index + 1)) count++
  return count
}
function origin(entry: RemoteLogEntry) { return entry.nodeTitle || sourceName(entry.source) }
/** Plain-text form used for copy, download and share; matches the on-screen columns. */
function plain(entry: RemoteLogEntry) {
  return `#${entry.line.sequence}\t${entry.timestamp || '-'}\t${remoteLogKind(entry.kind)}\t${origin(entry)}\t${message(entry)}`
}
function rawText(entry: RemoteLogEntry) {
  return typeof entry.raw === 'string' ? entry.raw : JSON.stringify(entry.raw ?? entry.line, null, 2)
}
function segments(text: string) {
  const term = needle.value
  if (!term) return [{ text, hit: false }]
  const lower = text.toLocaleLowerCase(), parts: { text: string; hit: boolean }[] = []
  let from = 0
  for (let index = lower.indexOf(term); index >= 0 && parts.length < 40; index = lower.indexOf(term, from)) {
    if (index > from) parts.push({ text: text.slice(from, index), hit: false })
    parts.push({ text: text.slice(index, index + term.length), hit: true })
    from = index + term.length
  }
  if (from < text.length) parts.push({ text: text.slice(from), hit: false })
  return parts
}

function searchable(index: number) {
  let value = lowered[index]
  if (value === undefined) {
    const entry = entries.value[index]!
    value = lowered[index] = `${entry.message}\n${entry.nodeTitle}\n${entry.kind}\n${entry.source}\n${entry.line.sequence}`.toLocaleLowerCase()
  }
  return value
}
function hits(index: number) { return !!needle.value && searchable(index).includes(needle.value) }
function accepts(index: number) {
  const entry = entries.value[index]!
  return (!nodeFilter.value || nodeFilter.value(entry))
    && (category.value === 'all' || entry.level === category.value)
    && (source.value === 'all' || entry.source === source.value)
    && (!onlyMatches.value || !needle.value || hits(index))
}
function indexAt(position: number) { return view.value ? view.value[position]! : position }

function rebuildView() {
  if (!filtering.value) view.value = null
  else {
    const rows: number[] = []
    for (let index = 0; index < entries.value.length; index++) if (accepts(index)) rows.push(index)
    view.value = rows
  }
  rebuildMatches()
}
function rebuildMatches() {
  const rows: number[] = []
  if (needle.value && !onlyMatches.value)
    for (let position = 0; position < total.value; position++) if (hits(indexAt(position))) rows.push(position)
  matches.value = rows
  matchIndex.value = -1
}
function ingest(lines: RemoteAutomationLogLine[]) {
  const reset = lines.length < ingested.length || (ingested.length > 0 && lines[0] !== ingested[0])
  if (reset) {
    entries.value = []
    lowered.length = 0
    sources.value = []
    unidentified.value = 0
  }
  const start = entries.value.length
  if (lines.length === start) { ingested = lines; return }
  const list = entries.value
  const known = new Set(sources.value)
  let unknown = 0
  for (let index = start; index < lines.length; index++) {
    const entry = parseRemoteLog(lines[index]!)
    list.push(entry)
    if (!entry.structured) unknown++
    if (!known.has(entry.source)) { known.add(entry.source); sources.value = [...known].sort() }
  }
  unidentified.value += unknown
  ingested = lines
  if (reset) { rebuildView(); triggerRef(entries); return }
  // Append-only path: test only the new entries.
  const firstNew = total.value
  if (view.value) {
    const rows = view.value
    for (let index = start; index < list.length; index++) if (accepts(index)) rows.push(index)
    triggerRef(view)
  }
  triggerRef(entries)
  if (needle.value && !onlyMatches.value) {
    const rows = matches.value
    for (let position = firstNew; position < total.value; position++) if (hits(indexAt(position))) rows.push(position)
    triggerRef(matches)
  }
}

function scrollToPosition(position: number, center = true) {
  const element = panel.value
  if (!element) return
  element.scrollTop = Math.max(0, position * ROW - (center ? element.clientHeight / 2 - ROW : 0))
}
function pauseFollowing() {
  if (following.value) pausedAt.value = entries.value.length
  following.value = false
}
let programmatic = false
async function scrollToEnd() {
  programmatic = true
  await nextTick()
  if (panel.value) panel.value.scrollTop = panel.value.scrollHeight
  requestAnimationFrame(() => { programmatic = false })
}
function toggleFollowing() {
  if (following.value) pauseFollowing()
  else { following.value = true; void scrollToEnd() }
}
let frame = 0
function onScroll() {
  if (frame) return
  frame = requestAnimationFrame(() => {
    frame = 0
    const element = panel.value
    if (!element) return
    scrollTop.value = element.scrollTop
    if (!programmatic && following.value && element.scrollHeight - element.scrollTop - element.clientHeight > ROW * 3) pauseFollowing()
  })
}
function jumpMatch(step: 1 | -1) {
  if (!matches.value.length) return
  pauseFollowing()
  matchIndex.value = (matchIndex.value + step + matches.value.length) % matches.value.length
  const position = matches.value[matchIndex.value]!
  const sequence = entries.value[indexAt(position)]!.line.sequence
  selection.value = { from: sequence, to: sequence }
  anchor.value = sequence
  scrollToPosition(position)
}
function onSearchKey(event: KeyboardEvent) {
  if (event.key === 'Enter') { event.preventDefault(); jumpMatch(event.shiftKey ? -1 : 1) }
}
function selectLine(entry: RemoteLogEntry, event: MouseEvent) {
  const sequence = entry.line.sequence
  if (event.shiftKey && anchor.value !== null)
    selection.value = { from: Math.min(anchor.value, sequence), to: Math.max(anchor.value, sequence) }
  else if (selection.value?.from === sequence && selection.value.to === sequence) { selection.value = null; anchor.value = null; return }
  else { selection.value = { from: sequence, to: sequence }; anchor.value = sequence }
  pauseFollowing()
}
function selected(entry: RemoteLogEntry) {
  const range = selection.value
  return !!range && entry.line.sequence >= range.from && entry.line.sequence <= range.to
}

async function writeClipboard(text: string, done: string) {
  try { await navigator.clipboard.writeText(text); MessagePlugin.success(done) } catch { MessagePlugin.warning('Browser blocked clipboard access; use Download instead') }
}
function copySelection() {
  if (selectedEntries.value.length) void writeClipboard(selectedEntries.value.map(plain).join('\n'), `Copied ${selectedEntries.value.length} lines`)
}
function copyView() {
  const rows: string[] = []
  for (let position = 0; position < total.value; position++) rows.push(plain(entries.value[indexAt(position)]!))
  void writeClipboard(rows.join('\n'), `Copied ${rows.length.toLocaleString()} lines${filtering.value ? ' (filtered)' : ''}`)
}
function download() {
  const text = entries.value.map(plain).join('\n')
  const url = URL.createObjectURL(new Blob([text], { type: 'text/plain;charset=utf-8' }))
  const link = document.createElement('a')
  link.href = url
  link.download = `${props.run.task_name}-${props.run.id}.log`.replace(/[\\/:*?"<>|\s]+/g, '_')
  link.click()
  URL.revokeObjectURL(url)
  if (props.hasMore) MessagePlugin.info('Downloaded the loaded portion only; more history on server')
}
function shareLink() {
  const range = selection.value
  if (!range) return
  const url = new URL(location.href)
  url.searchParams.set('log', range.from === range.to ? String(range.from) : `${range.from}-${range.to}`)
  void writeClipboard(url.toString(), 'Log link copied')
}
function onKeydown(event: KeyboardEvent) {
  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'f') { event.preventDefault(); searchBox.value?.focus?.() }
  else if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'c' && selection.value && !document.getSelection()?.toString()) { event.preventDefault(); copySelection() }
}
function clearFilters() { input.value = query.value = ''; category.value = source.value = 'all'; onlyMatches.value = false }
function parseShared(value: unknown) {
  const match = typeof value === 'string' ? value.match(/^(\d+)(?:-(\d+))?$/) : null
  if (!match) return null
  const from = Number(match[1]), to = Number(match[2] ?? match[1])
  return { from: Math.min(from, to), to: Math.max(from, to) }
}
function applyPendingJump() {
  const range = pendingJump.value
  if (!range) return
  const index = entries.value.findIndex(entry => entry.line.sequence >= range.from)
  if (index < 0) return
  let position = index
  if (view.value) { position = view.value.findIndex(row => row >= index); if (position < 0) return }
  pendingJump.value = null
  selection.value = range
  anchor.value = range.from
  pauseFollowing()
  void nextTick(() => scrollToPosition(position))
}

let debounce: ReturnType<typeof setTimeout> | undefined
watch(input, value => {
  if (debounce) clearTimeout(debounce)
  debounce = setTimeout(() => { query.value = value }, 180)
})
watch([nodeFilter, category, source, onlyMatches], () => { pauseFollowing(); rebuildView(); applyPendingJump() })
watch(needle, () => { if (onlyMatches.value) rebuildView(); else rebuildMatches() })
watch(() => props.lines, lines => { ingest(lines); applyPendingJump() }, { immediate: true })
watch(total, () => { if (following.value) void scrollToEnd() })
watch(() => route.query.log, value => { pendingJump.value = parseShared(value); applyPendingJump() }, { immediate: true })
// A shared line beyond what has been read keeps reading forward, and stops once it is reached.
watch(() => [pendingJump.value, props.hasMore, props.loading, props.loadingHistory, entries.value.length] as const, ([pending, more, loading, history]) => {
  if (pending && more && !loading && !history) emit('more')
})

let observer: ResizeObserver | null = null
onMounted(() => {
  if (!panel.value) return
  observer = new ResizeObserver(() => { viewportHeight.value = panel.value?.clientHeight ?? viewportHeight.value })
  observer.observe(panel.value)
  if (following.value) void scrollToEnd()
})
onBeforeUnmount(() => { observer?.disconnect(); if (frame) cancelAnimationFrame(frame); if (debounce) clearTimeout(debounce) })
</script>

<template>
  <section class="run-logs" aria-label="Logs" @keydown="onKeydown">
    <div class="log-toolbar">
      <t-input ref="searchBox" v-model="input" class="log-search" size="small" clearable placeholder="Search loaded logs (Enter next, Shift+Enter previous)" aria-label="Search logs" @keydown="onSearchKey">
        <template #prefix-icon><t-icon name="search" /></template>
        <template #suffix><span v-if="needle && !onlyMatches" class="match-count">{{ matches.length ? `${matchIndex + 1 || '-'}/${matches.length.toLocaleString()}` : 'No matches' }}</span></template>
      </t-input>
      <t-button size="small" variant="outline" shape="square" :disabled="!matches.length" title="Previous match" @click="jumpMatch(-1)"><template #icon><t-icon name="chevron-up" /></template></t-button>
      <t-button size="small" variant="outline" shape="square" :disabled="!matches.length" title="Next match" @click="jumpMatch(1)"><template #icon><t-icon name="chevron-down" /></template></t-button>
      <t-checkbox v-model="onlyMatches" :disabled="!needle">Matches only</t-checkbox>
      <t-select v-model="category" class="log-select" size="small" :options="categoryOptions" aria-label="Filter by type" />
      <t-select v-model="source" class="log-select" size="small" :options="sourceOptions" aria-label="Filter by source" />
      <span class="toolbar-spacer" />
      <t-button size="small" :variant="following ? 'base' : 'outline'" :theme="following ? 'primary' : 'default'" :aria-pressed="following" @click="toggleFollowing">
        {{ following ? 'Following' : 'Paused' }}<span v-if="newCount">&nbsp;· {{ newCount.toLocaleString() }} new</span>
      </t-button>
      <t-button size="small" variant="outline" @click="copyView">{{ filtering ? 'Copy filtered' : 'Copy all' }}</t-button>
      <t-button size="small" variant="outline" @click="download"><template #icon><t-icon name="download" /></template>Download</t-button>
    </div>
    <div class="log-status-line" role="status">
      <span>{{ status }}</span>
      <span>· Showing {{ total.toLocaleString() }}<template v-if="filtering"> / {{ entries.length.toLocaleString() }}</template> lines</span>
      <span v-if="scope">· Node: {{ scope }} <t-link theme="primary" hover="color" @click="emit('clear-selection')">Show all</t-link></span>
      <span v-if="scope && !nodeFilter" class="warn">· Node has no identifier; showing all logs</span>
      <span v-else-if="scope && unidentified" class="warn">· {{ unidentified.toLocaleString() }} lines without node identifier excluded</span>
      <span v-if="pendingJump" class="warn">· {{ hasMore ? `Loading up to linked line ${pendingJump.from}…` : `Linked line ${pendingJump.from} is not in this log` }}</span>
      <t-link v-if="filtering || input" theme="primary" hover="color" @click="clearFilters">Clear filters</t-link>
    </div>
    <div v-if="error" class="log-error" role="alert"><span>{{ error }}</span><t-link theme="primary" :disabled="loading" @click="emit('retry')">Retry</t-link></div>
    <div v-if="hasMore || loadingHistory" class="history-banner">
      <span>Search and filters cover only the {{ entries.length.toLocaleString() }} lines loaded.</span>
      <template v-if="!loadingHistory"><t-link theme="primary" :disabled="loading" @click="emit('more')">Load 4,000 more lines</t-link><t-link theme="primary" :disabled="loading" @click="emit('history')">Load full history</t-link></template>
      <t-link v-else theme="primary" @click="emit('stop-history')">Pause loading</t-link>
    </div>
    <div class="log-body">
    <div ref="panel" class="log-panel" tabindex="0" aria-label="Log content" @scroll="onScroll">
      <p v-if="!total" class="logs-empty">{{ loading && !loaded ? 'Loading logs…' : entries.length ? 'No matching log lines.' : drained ? 'This run reported no logs.' : 'No logs yet; waiting for the machine to report.' }}</p>
      <div v-else class="log-spacer" :style="{ height: `${total * ROW}px` }">
        <div class="log-window" :style="{ transform: `translateY(${rendered.offset}px)` }">
          <div v-for="row in rendered.rows" :key="row.entry.line.sequence" class="log-line"
            :class="[`level-${row.entry.level}`, { selected: selected(row.entry), hit: matchSet.has(row.position), current: matches[matchIndex] === row.position }]">
            <button type="button" class="gutter" :title="`Select line ${row.entry.line.sequence} (Shift to extend)`" @click="selectLine(row.entry, $event)">{{ row.entry.line.sequence }}</button>{{ ' ' }}<span class="time">{{ timeText(row.entry.timestamp) }}</span>{{ ' ' }}<span class="kind" :title="row.entry.kind">{{ remoteLogKind(row.entry.kind) }}</span>{{ ' ' }}<span class="origin" :title="`${sourceName(row.entry.source)}${row.entry.nodeTitle ? ` · ${row.entry.nodeTitle}` : ''}`">{{ origin(row.entry) }}</span>{{ ' ' }}<span class="text"><template v-for="(part, index) in segments(firstLine(row.entry))" :key="index"><mark v-if="part.hit">{{ part.text }}</mark><template v-else>{{ part.text }}</template></template><span v-if="extraLines(row.entry)" class="more"> ↵ +{{ extraLines(row.entry) }} lines</span></span>
          </div>
        </div>
      </div>
    </div>
    <div v-if="selectedEntries.length" class="selection-pane">
      <div class="selection-bar">
        <strong>{{ selectedEntries.length === 1 ? `Line ${selection!.from}` : `Lines ${selection!.from}–${selection!.to} · ${selectedEntries.length} entries` }}</strong>
        <t-button size="small" variant="outline" @click="copySelection"><template #icon><t-icon name="file-copy" /></template>Copy</t-button>
        <t-button size="small" variant="outline" @click="shareLink"><template #icon><t-icon name="link" /></template>Copy link</t-button>
        <t-button size="small" variant="text" @click="selection = null; anchor = null">Clear selection</t-button>
      </div>
      <pre v-if="selectedEntries.length === 1" class="selection-text">{{ message(selectedEntries[0]!) || '(no message body)' }}

—— Raw event · stream {{ selectedEntries[0]!.line.stream }}{{ selectedEntries[0]!.line.offset !== undefined ? ` · byte offset ${selectedEntries[0]!.line.offset}` : '' }}
{{ rawText(selectedEntries[0]!) }}</pre>
      <pre v-else class="selection-text">{{ selectedEntries.slice(0, 500).map(plain).join('\n') }}{{ selectedEntries.length > 500 ? `\n… ${selectedEntries.length - 500} more lines; use "Copy"` : '' }}</pre>
    </div>
    </div>
  </section>
</template>

<style scoped>
.run-logs { display: flex; flex-direction: column; gap: 8px; min-width: 0; padding: 12px 16px; background: var(--bg-card); }
.log-toolbar { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; }
.log-search { flex: 1 1 320px; min-width: 240px; }
.match-count { color: var(--text-secondary); font-size: 12px; font-variant-numeric: tabular-nums; white-space: nowrap; }
.log-select { width: 130px; }
.toolbar-spacer { flex: 1 0 0; }
.log-status-line { display: flex; align-items: center; flex-wrap: wrap; gap: 4px 6px; color: var(--text-secondary); font-size: 12px; }
.log-status-line .warn { color: var(--color-accent); }
.log-error, .history-banner { display: flex; align-items: center; flex-wrap: wrap; gap: 10px; padding: 6px 10px; border-radius: 6px; font-size: 12px; }
.log-error { color: var(--color-danger); background: color-mix(in srgb, var(--color-danger) 7%, transparent); }
.history-banner { color: var(--text-secondary); background: var(--bg-secondary); border: 1px solid var(--border-color); }
.log-panel { position: relative; flex: 1; min-height: 140px; overflow: auto; border: 1px solid var(--border-color); border-radius: 6px; background: var(--bg-secondary); overscroll-behavior: contain; font: 12px/20px ui-monospace, 'Cascadia Code', Consolas, monospace; }
.log-panel:focus-visible { outline: 2px solid var(--color-focus); outline-offset: 1px; }
.log-spacer { position: relative; min-width: max-content; }
.log-window { position: absolute; top: 0; left: 0; min-width: 100%; }
.log-line { height: 20px; padding-right: 12px; white-space: pre; color: var(--text-primary); }
.log-line:hover { background: var(--bg-hover); }
.log-line.level-error { color: var(--color-danger); background: color-mix(in srgb, var(--color-danger) 6%, transparent); }
.log-line.level-warning { color: color-mix(in srgb, var(--color-accent) 80%, var(--text-primary)); }
.log-line.hit { background: color-mix(in srgb, var(--color-accent) 10%, transparent); }
.log-line.selected { background: var(--color-primary-light); }
.log-line.current { box-shadow: inset 3px 0 0 var(--color-accent); }
.gutter { display: inline-block; width: 64px; padding: 0 8px 0 0; border: 0; border-right: 1px solid var(--border-color); background: transparent; color: var(--text-secondary); font: inherit; text-align: right; cursor: pointer; user-select: none; }
.gutter:hover { color: var(--color-link); }
.log-line.selected .gutter { color: var(--color-link); font-weight: 600; }
.time { display: inline-block; width: 92px; padding-left: 6px; color: var(--text-secondary); }
.kind { display: inline-block; width: 64px; overflow: hidden; text-overflow: ellipsis; vertical-align: top; color: var(--text-secondary); }
.origin { display: inline-block; width: 120px; overflow: hidden; text-overflow: ellipsis; vertical-align: top; color: var(--text-secondary); }
mark { padding: 0; border-radius: 2px; background: color-mix(in srgb, var(--color-accent) 45%, transparent); color: inherit; }
.more { color: var(--text-secondary); }
.logs-empty { margin: 0; padding: 32px 16px; color: var(--text-secondary); font-family: inherit; text-align: center; }
.log-body { flex: 1; min-height: 0; display: flex; gap: 8px; }
.log-body > .log-panel { min-width: 0; }
.selection-pane { display: flex; flex-direction: column; flex: 0 0 380px; min-height: 0; border: 1px solid var(--border-color); border-radius: 6px; background: var(--bg-card); }
.selection-bar { display: flex; align-items: center; gap: 8px; padding: 6px 10px; border-bottom: 1px solid var(--border-color); font-size: 12px; }
.selection-bar strong { margin-right: auto; font-weight: 500; }
.selection-bar { flex-wrap: wrap; }
.selection-text { flex: 1; min-height: 0; margin: 0; padding: 10px 12px; overflow: auto; white-space: pre-wrap; overflow-wrap: anywhere; font: 12px/1.6 ui-monospace, 'Cascadia Code', Consolas, monospace; user-select: text; }
@media (max-width: 1100px) { .log-body { flex-direction: column; } .selection-pane { flex-basis: auto; max-height: 200px; } }
@media (max-width: 640px) { .run-logs { padding: 10px; } .time, .kind { display: none; } .origin { width: 80px; } }
</style>
