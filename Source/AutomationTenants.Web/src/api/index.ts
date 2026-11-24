export interface RemoteAutomationTenant {
  id: string
  name: string
  server_machine?: string
  pools?: { id: string; name: string }[]
}

export interface RemoteAutomationTask {
  id: string
  name: string
  local_nickname?: string
  parameters: Record<string, string>
  script_revision: string
  graph_fingerprint?: string
  can_run: boolean
  unavailable_reason: string
  triggers?: RemoteAutomationTriggers
}

export interface RemoteAutomationScheduleEntry {
  time: string
  frequency: 'daily' | 'weekly' | 'monthly'
  week_days: number
  month_day: number
  last_day_of_month: boolean
}

export interface RemoteAutomationTriggers {
  manual: boolean
  schedule_enabled: boolean
  schedules: (RemoteAutomationScheduleEntry & { time_zone: string })[]
  read_only_reason?: string
}

export interface RemoteAutomationTeamSchedule {
  task_id: string
  machine_name: string
  enabled: boolean
  entries: RemoteAutomationScheduleEntry[]
  source: 'adopted' | 'web'
  updated_by: string
  updated_at: string
  last_occurrence: string
  last_run_id?: string
  last_error?: string
  next_occurrence?: string
}

export interface RemoteAutomationScheduleDraft {
  tenant_id: string
  task_id: string
  machine_name: string
  enabled: boolean
  entries: RemoteAutomationScheduleEntry[]
}

export interface RemoteAutomationMachine {
  tenant_id: string
  pool_id: string
  machine_name: string
  host_version: string
  last_seen: string
  capabilities: string[]
  tasks: RemoteAutomationTask[]
}

export interface RemoteAutomationStep {
  key: string
  title: string
  node_ids?: string[]
  children?: RemoteAutomationStep[]
  depends_on?: string[] | null
  recovery_from?: string[]
  state?: string
  is_stage: boolean
  is_finally: boolean
  is_recovery: boolean
  is_reused: boolean
  started_at: string | null
  ended_at: string | null
  progress: number | null
  has_progress: boolean
  detail: string
  error: string | null
  attempt?: number
  retry_error: string | null
}

export interface RemoteAutomationArtifact {
  id: string
  name: string
  kind: string
  machine_name: string
  source_path: string
  archived_path?: string | null
  bytes?: number | null
  sha256?: string | null
  deliveries: { destination: string; state: string; location?: string | null; error?: string | null }[]
}

export interface RemoteAutomationReview {
  id: string
  revision: number
  title: string
  content: string
  content_hash: string
  created_at: string
  detail: string
  can_revise?: boolean
  approve_label?: string
  hint?: string
  revise_label?: string
  feedback_placeholder?: string
  continuation_detail?: string
  conversation?: { round: number; role: string; content: string; created_at: string }[]
}

export interface RemoteAutomationReviewDecision {
  action: 'approve' | 'revise' | 'abandon'
  review_id: string
  content_hash: string
  feedback: string
}

export interface RemoteAutomationSnapshot {
  run_id: string
  task_id: string
  trigger?: string
  status: string
  summary: string
  warning: string
  queued_at: string
  started_at: string | null
  ended_at: string | null
  steps: RemoteAutomationStep[]
  artifact_path?: string | null
  evidence_directories?: string[]
  artifacts?: RemoteAutomationArtifact[]
  review?: RemoteAutomationReview | null
  last_remote_action_id?: string | null
  task_snapshot?: { name?: string; template_id: string; parameters: Record<string, string> } | null
  launch?: {
    task_id?: string | null
    task_name?: string | null
    local_nickname?: string
    machine_name: string
    script_revision: string
    parameters: Record<string, string>
    fields: { key: string; label: string; group?: string | null }[]
    rendered_parameters?: string | null
  } | null
}

export interface RemoteAutomationControl {
  id: string
  action: string
  state: 'pending' | 'applied' | 'failed'
  detail: string
  created_by: string
  created_at: string
  updated_at: string
}

export interface RemoteAutomationRun {
  created_by?: string
  id: string
  tenant_id: string
  pool_id: string
  machine_name: string
  task_id: string
  task_name: string
  script_revision?: string
  parent_run_id?: string
  root_run_id?: string
  parent_node_id?: string
  child_run_id?: string
  subgraph?: { mutation_path: string[]; target: string }
  source?: 'cloud' | 'local'
  revision: number
  logs_complete?: boolean
  parameters: Record<string, string>
  state: string
  detail: string
  created_at: string
  updated_at: string
  cancel_requested: boolean
  cancellation_failure?: { code: string; message: string }
  local_run_id: string | null
  snapshot: RemoteAutomationSnapshot | null
  control?: RemoteAutomationControl | null
}

export interface RemoteAutomationInventory {
  tenants: RemoteAutomationTenant[]
  machines: RemoteAutomationMachine[]
  runs: RemoteAutomationRun[]
  tasks?: { tenant_id: string; id: string; name: string }[]
  organizations?: Record<string, RemoteAutomationOrganization>
  schedules?: Record<string, RemoteAutomationTeamSchedule[]>
}

export interface RemoteAutomationOrganization {
  groups: { id: string; name: string }[]
  assignments: Record<string, string>
  starred: string[]
}

export interface RemoteAutomationLogLine {
  sequence: number
  stream: string
  offset?: number
  text: string
  raw?: unknown
}

export interface RemoteAutomationLogs {
  lines: RemoteAutomationLogLine[]
  next_cursor: number
  has_more: boolean
}

export interface RemoteAutomationRunDraft {
  tenant_id: string
  machine_name: string
  task_id: string
  parameters: Record<string, string>
}

export interface RemoteAutomationField {
  key: string
  label: string
  kind: 'toggle' | 'text' | 'folder' | 'choice' | 'multiline' | 'multi_choice'
  default_value: string
  value: string
  hint?: string | null
  choices: { value: string; label: string; tooltip?: string }[]
  choice_dependencies?: string[] | null
  is_read_only: boolean
  group?: string | null
  visible: boolean
  empty_value?: string | null
  choices_error?: string | null
}

export interface RemoteAutomationInspection {
  machine_name?: string
  fields: RemoteAutomationField[]
  plan_steps: RemoteAutomationStep[]
  validation_error: string
  parameters: Record<string, string>
}


// ---------------------------------------------------------------------------------------------
// In-browser demo backend.
//
// The real console talks to the Go tenant service. This portfolio build replaces every request
// with a deterministic, time-driven simulator: each run is a schedule computed from its graph
// template (stages, explicit dependencies, parallel branches, HumanReview gates, retries, recovery
// and Finally steps, cross-machine subgraphs). Reading a run at time `now` derives step states,
// progress and the structured PregelRunEvent log, so the 2s polling loop renders a live run.
// ---------------------------------------------------------------------------------------------

interface Tpl {
  key: string
  title: string
  dur?: number
  out?: string[]
  done?: string
  deps?: string[]
  stage?: Tpl[]
  isFinally?: boolean
  recovery?: string[]
  gate?: boolean
  remote?: string
  sub?: Tpl[]
  failOn?: boolean
  error?: string
  flaky?: string
  progress?: boolean
}

type Outcome = 'succeeded' | 'failed' | 'cancelled' | 'not_run'
interface Timing { start: number; end: number; outcome: Outcome; retryAt?: number; tpl: Tpl }

interface ArtifactTpl { after: string; id: string; name: string; kind: string; bytes: number; destination: string; state: string; remote?: string }

interface TaskDef {
  id: string
  name: string
  revision: string
  plan: Tpl[]
  fields: RemoteAutomationField[]
  artifacts: ArtifactTpl[]
  successDetail: string
  failureDetail?: string
  review?: (params: Record<string, string>, revision: number) => Omit<RemoteAutomationReview, 'id' | 'revision' | 'content_hash' | 'created_at' | 'conversation'>
}

const SECOND = 1000
const MINUTE = 60 * SECOND
const HOUR = 60 * MINUTE
const SERVER = 'server-host'
const TENANT = 'heron-demo'

function field(partial: Partial<RemoteAutomationField> & Pick<RemoteAutomationField, 'key' | 'label' | 'kind' | 'default_value'>): RemoteAutomationField {
  return { value: partial.default_value, choices: [], is_read_only: false, visible: true, ...partial }
}

function withSubDurations(list: Tpl[]): Tpl[] {
  for (const t of list) {
    if (t.stage) withSubDurations(t.stage)
    if (t.sub) { withSubDurations(t.sub); t.dur = t.sub.reduce((sum, s) => sum + (s.dur ?? 0), 0) + 600 }
  }
  return list
}

const tasks: TaskDef[] = [
  {
    id: 'pr-code-review',
    name: 'PR Code Review Agent',
    revision: 'sha256:9f3a1c08',
    successDetail: 'Review approved and posted to the pull request.',
    fields: [
      field({ key: 'repo', label: 'Repository', kind: 'text', default_value: 'ornithologist-love/Heron', group: 'Source' }),
      field({ key: 'pr_number', label: 'Pull request', kind: 'text', default_value: '142', hint: 'Numeric PR id. The plan is validated on the runner before launch.', group: 'Source' }),
      field({ key: 'model', label: 'Agent model', kind: 'choice', default_value: 'deepseek-reasoner', group: 'Agent',
        choices: [{ value: 'deepseek-reasoner', label: 'DeepSeek Reasoner' }, { value: 'claude-sonnet', label: 'Claude Sonnet' }, { value: 'gpt-5', label: 'GPT-5' }] }),
      field({ key: 'sandbox', label: 'Sandbox access', kind: 'choice', default_value: 'read-only', group: 'Agent',
        choices: [{ value: 'read-only', label: 'Read-only workspace' }, { value: 'workspace-write', label: 'Workspace write' }] }),
      field({ key: 'focus', label: 'Review focus', kind: 'multi_choice', default_value: '["correctness","concurrency"]', group: 'Agent',
        choices: [{ value: 'correctness', label: 'Correctness' }, { value: 'concurrency', label: 'Concurrency' }, { value: 'security', label: 'Security' }, { value: 'performance', label: 'Performance' }] }),
    ],
    plan: withSubDurations([
      { key: 'stage-collect', title: 'Collect context', stage: [
        { key: 'fetch-diff', title: 'Fetch PR diff', dur: 2500, out: ['GET /repos/ornithologist-love/Heron/pulls/142/files', '24 files changed (+418 / -132)'], done: '24 files · +418 / -132' },
        { key: 'build-index', title: 'Resolve call graph', dur: 3200, progress: true, out: ['Indexing Heron.Pregel, Heron.Pregel.Automation …', 'Resolved 1,284 symbols across 18 impacted modules'], done: '18 impacted modules' },
      ] },
      { key: 'stage-audit', title: 'Parallel checks', stage: [
        { key: 'lint-check', title: 'Analyzers & layer boundaries', dur: 3500, deps: [], out: ['dotnet build -warnaserror /p:EnforceCodeStyleInBuild=true', 'PregelDsl.cs(212,17): warning CA1859'], done: '0 errors · 1 warning' },
        { key: 'security-scan', title: 'Secret & sandbox policy scan', dur: 4200, deps: [], out: ['Scanning 24 files with 61 secret patterns', 'Sandbox ACL policy unchanged'], done: 'No findings' },
        { key: 'llm-review', title: 'Agent code review', dur: 6500, deps: ['lint-check', 'security-scan'], progress: true,
          out: ['Agent Harness session started (access=read-only, tools=read,grep,glob)', 'Tool call: read Source/Pregel/PregelDsl.cs', 'Tool call: grep "MaxMutationInstances"', 'Drafted structured review with 3 findings'], done: '3 findings · review-summary.md' },
      ] },
      { key: 'stage-gate', title: 'HumanReview', stage: [
        { key: 'approve-comment', title: 'Maintainer sign-off', gate: true, out: ['Checkpoint persisted. Run is suspended until a maintainer decides.'] },
      ] },
      { key: 'stage-publish', title: 'Publish', stage: [
        { key: 'publish-comment', title: 'Post review comment', dur: 1800, out: ['POST /repos/ornithologist-love/Heron/pulls/142/reviews → 201'], done: 'Comment posted' },
      ] },
      { key: 'stage-finally', title: 'Finally', isFinally: true, recovery: ['stage-publish'], stage: [
        { key: 'archive-report', title: 'Archive run manifest', dur: 1200, out: ['Wrote manifest.json (SHA-256 verified)'], done: 'manifest.json' },
      ] },
    ]),
    artifacts: [
      { after: 'llm-review', id: 'review-md', name: 'review-summary.md', kind: 'report', bytes: 4280, destination: 'Archive', state: 'archived' },
      { after: 'archive-report', id: 'manifest', name: 'manifest.json', kind: 'manifest', bytes: 1942, destination: 'Archive', state: 'archived' },
    ],
    review: (params, revision) => ({
      title: `Approve review for PR #${params.pr_number || '142'}`,
      detail: `Approve to post this review to ${params.repo || 'the repository'}, or send feedback for another pass.`,
      content: [
        `### Summary${revision > 1 ? ` (revision ${revision})` : ''}`,
        '- **Scope**: consolidates the `Parallel` / `ForEach` collection branches in `PregelDsl.cs`.',
        '- **Checks**: analyzers clean (1 warning), no secret or sandbox-policy findings.',
        '',
        '### Findings',
        '1. **Medium** — an empty collection should short-circuit straight to the barrier join instead of scheduling zero child runs.',
        '2. **Low** — check `MaxMutationInstances` before allocating checkpoint entries.',
        revision > 1 ? '3. **Low** — suggested fix for #1:\n\n```csharp\nif (items.Count == 0)\n    return Barrier.CommitEmpty(step);\n```' : '3. **Info** — all 142 tests in `Heron.Pregel.Tests` pass.',
      ].join('\n'),
      can_revise: true,
      approve_label: 'Approve & post comment',
      revise_label: 'Revise with feedback',
      feedback_placeholder: 'e.g. Drop finding #3 and add a code snippet for finding #1',
    }),
  },
  {
    id: 'multi-target-ci',
    name: 'Multi-Target Build & Test',
    revision: 'sha256:4b82e110',
    successDetail: '180/180 tests passed on Windows and Linux; packages signed and archived.',
    failureDetail: 'Linux smoke test failed; recovery revoked partial artifacts and Finally released all leases.',
    fields: [
      field({ key: 'branch', label: 'Branch', kind: 'text', default_value: 'master', group: 'Source' }),
      field({ key: 'configuration', label: 'Configuration', kind: 'choice', default_value: 'Release', group: 'Build',
        choices: [{ value: 'Release', label: 'Release' }, { value: 'Debug', label: 'Debug' }] }),
      field({ key: 'run_smoke', label: 'Run startup smoke test', kind: 'toggle', default_value: 'true', group: 'Build' }),
      field({ key: 'inject_failure', label: 'Inject a Linux smoke failure', kind: 'toggle', default_value: 'false', group: 'Demo',
        hint: 'Watch the failure propagate: downstream steps are skipped, the Recovery branch runs and Finally still completes.' }),
    ],
    plan: withSubDurations([
      { key: 'stage-prepare', title: 'Prepare workspace', stage: [
        { key: 'git-sync', title: 'Sync source & submodules', dur: 2000, out: ['git fetch --depth=1 origin master', 'HEAD at 76f7847'], done: 'HEAD 76f7847' },
        { key: 'export-sdk', title: 'Export host contracts SDK', dur: 2500, out: ['dotnet run --project Source/CLI -- plugins sdk', 'Exported host-api v2'], done: 'host-api v2' },
      ] },
      { key: 'stage-targets', title: 'Cross-machine subgraphs', stage: [
        { key: 'win-build', title: 'Windows x64 build + tests', deps: [], remote: 'runner-win-01', done: '142/142 tests passed',
          sub: [
            { key: 'restore', title: 'Restore packages', dur: 2000, out: ['dotnet restore Heron.sln --locked-mode'], done: 'Restored 86 packages' },
            { key: 'compile', title: 'Compile Release', dur: 4500, progress: true, out: ['dotnet build -c Release --no-restore', 'Build succeeded. 0 Warning(s)'], done: '0 warnings' },
            { key: 'unit-tests', title: 'Unit tests', dur: 3500, progress: true, out: ['dotnet test --no-build --logger trx', 'Passed! 142 passed, 0 failed'], done: '142 passed' },
          ] },
        { key: 'linux-build', title: 'Linux container build + smoke', deps: [], remote: 'runner-linux-02', failOn: true, error: 'Smoke test exited with code 134 (SIGABRT) after 2.1s', done: '38/38 tests passed',
          sub: [
            { key: 'pull-image', title: 'Pull build image', dur: 2200, out: ['docker pull mcr.microsoft.com/dotnet/sdk:8.0'], done: 'sdk:8.0' },
            { key: 'container-build', title: 'Build in container', dur: 4000, progress: true, out: ['docker build -t heron-ci .', 'Successfully tagged heron-ci:latest'], done: 'heron-ci:latest' },
            { key: 'smoke', title: 'Startup smoke test', dur: 3200, failOn: true, error: 'Process exited with code 134 (SIGABRT)', out: ['./heron --smoke --timeout 30s', 'Host started, 12 plugins loaded'], done: '38/38 passed' },
          ] },
      ] },
      { key: 'stage-archive', title: 'Pack & archive', stage: [
        { key: 'pack-nuget', title: 'Pack NuGet packages', dur: 2500, out: ['pwsh -File ./Pack.ps1', 'Created Heron.Pregel.0.2.24.nupkg'], done: '4 packages' },
        { key: 'sign-manifest', title: 'SHA-256 manifest', dur: 1500, out: ['Hashed 4 artifacts', 'Manifest committed'], done: 'Verified' },
      ] },
      { key: 'rollback', title: 'Revoke partial artifacts', recovery: ['stage-targets'], dur: 2200, out: ['Recovery entered from stage-targets', 'Deleted 2 partial uploads; notified #ci-alerts'], done: 'Partial artifacts revoked' },
      { key: 'stage-finally', title: 'Finally', isFinally: true, recovery: ['stage-archive'], stage: [
        { key: 'release-leases', title: 'Release runner leases', dur: 1200, out: ['Released mutex leases on runner-win-01, runner-linux-02'], done: 'All leases released' },
      ] },
    ]),
    artifacts: [
      { after: 'win-build', id: 'trx', name: 'win-x64-tests.trx', kind: 'report', bytes: 61_204, destination: 'Archive', state: 'archived', remote: 'runner-win-01' },
      { after: 'pack-nuget', id: 'nupkg', name: 'Heron.Pregel.0.2.24.nupkg', kind: 'package', bytes: 318_420, destination: 'NuGet feed', state: 'delivered', remote: 'runner-win-01' },
      { after: 'sign-manifest', id: 'manifest', name: 'manifest.json', kind: 'manifest', bytes: 1_942, destination: 'Archive', state: 'archived' },
    ],
  },
  {
    id: 'daily-perf-audit',
    name: 'Daily Perf & Telemetry Audit',
    revision: 'sha256:7c19d442',
    successDetail: '3 scenes audited; ShadowDepths +1.8 ms regression flagged.',
    fields: [
      field({ key: 'window_hours', label: 'Lookback window (hours)', kind: 'text', default_value: '24' }),
      field({ key: 'scenes', label: 'Scenes', kind: 'multi_choice', default_value: '["UrbanCenter","DenseFoliage","LumenInterior"]',
        choices: [{ value: 'UrbanCenter', label: 'UrbanCenter' }, { value: 'DenseFoliage', label: 'DenseFoliage' }, { value: 'LumenInterior', label: 'LumenInterior' }] }),
      field({ key: 'max_concurrency', label: 'Agent mutex MaxConcurrency', kind: 'choice', default_value: '2',
        choices: [{ value: '1', label: '1' }, { value: '2', label: '2' }, { value: '5', label: '5' }], hint: 'Cross-process mutex shared by every flow that calls an LLM agent.' }),
    ],
    plan: withSubDurations([
      { key: 'stage-ingest', title: 'Ingest telemetry', stage: [
        { key: 'pull-metrics', title: 'Fetch frame, GPU & memory series', dur: 3000, progress: true, out: ['Querying 24h of PerfDog + UE stat captures', 'Loaded 3 scenes · 41,280 frames'], done: '41,280 frames' },
      ] },
      { key: 'stage-analyze', title: 'Scene attribution (mutex = 2)', stage: [
        { key: 'scene-city', title: 'UrbanCenter', dur: 4000, deps: [], out: ['MutexRule("AiAgent", 2): acquired slot 1/2', 'P95 frame time +0.4 ms (within budget)'], done: 'P95 +0.4 ms' },
        { key: 'scene-forest', title: 'DenseFoliage', dur: 5200, deps: [], flaky: 'Agent session timed out after 30s (transient); retrying with backoff', out: ['MutexRule("AiAgent", 2): acquired slot 2/2', 'ShadowDepths pass +1.8 ms vs 7-day baseline'], done: 'ShadowDepths +1.8 ms' },
        { key: 'scene-indoor', title: 'LumenInterior', dur: 3500, deps: ['scene-city'], out: ['MutexRule("AiAgent", 2): slot released by UrbanCenter, acquired', 'VRAM steady at 6.1 GB'], done: 'VRAM 6.1 GB' },
      ] },
      { key: 'stage-report', title: 'Report', stage: [
        { key: 'build-html', title: 'Render audit report', dur: 2500, out: ['Rendering daily-audit-report.html'], done: 'daily-audit-report.html' },
        { key: 'notify', title: 'Post to webhook', dur: 1200, out: ['POST https://hooks.example.com/perf → 200'], done: 'Delivered' },
      ] },
    ]),
    artifacts: [
      { after: 'build-html', id: 'html', name: 'daily-audit-report.html', kind: 'report', bytes: 84_120, destination: 'Webhook', state: 'delivered' },
    ],
  },
  {
    id: 'legacy-p4-sync',
    name: 'Legacy P4 Mirror Sync',
    revision: 'sha256:11aa02ce',
    successDetail: 'Mirror synced.',
    failureDetail: 'p4 sync failed: the depot path no longer exists. This flow has since been retired.',
    fields: [],
    plan: withSubDurations([
      { key: 'p4-sync', title: 'p4 sync //depot/legacy/...', dur: 3000, failOn: true, error: '//depot/legacy/... - no such file(s).', out: ['p4 -p ssl:p4.example.com:1666 sync //depot/legacy/...'] },
    ]),
    artifacts: [],
  },
]
const taskById = new Map(tasks.map(task => [task.id, task]))

// ----- scheduling -----------------------------------------------------------------------------

interface Ctx { fail: boolean; approvedAt?: number; cancelAt: number; timings: Map<string, Timing> }

function leafKeys(t: Tpl): string[] { return t.stage ? t.stage.flatMap(leafKeys) : [t.key] }

function runLeaf(t: Tpl, start: number, ctx: Ctx): Timing {
  let end = start + (t.dur ?? 1000)
  let outcome: Outcome = t.failOn && ctx.fail ? 'failed' : 'succeeded'
  if (t.gate) end = ctx.approvedAt !== undefined ? Math.max(start, ctx.approvedAt) + 400 : Infinity
  if (ctx.cancelAt < end) {
    if (ctx.cancelAt <= start) return { start, end: start, outcome: 'not_run', tpl: t }
    end = ctx.cancelAt
    outcome = 'cancelled'
  }
  return { start, end, outcome, tpl: t, retryAt: t.flaky ? start + (t.dur ?? 1000) * 0.45 : undefined }
}

function markNotRun(t: Tpl, at: number, ctx: Ctx) {
  ctx.timings.set(t.key, { start: at, end: at, outcome: 'not_run', tpl: t })
  for (const child of t.stage ?? []) markNotRun(child, at, ctx)
}

function runItem(t: Tpl, start: number, ctx: Ctx): Timing {
  if (!t.stage) { const timing = runLeaf(t, start, ctx); ctx.timings.set(t.key, timing); return timing }
  const inner = runList(t.stage, start, ctx)
  const children = t.stage.map(child => ctx.timings.get(child.key)!)
  const ran = children.filter(child => child.outcome !== 'not_run')
  const outcome: Outcome = !ran.length ? 'not_run' : ran.some(c => c.outcome === 'failed') ? 'failed'
    : ran.some(c => c.outcome === 'cancelled') ? 'cancelled' : 'succeeded'
  const timing = { start, end: inner, outcome, tpl: t }
  ctx.timings.set(t.key, timing)
  return timing
}

/** Schedules a sibling list; returns when the whole list (including recovery and Finally) ends. */
function runList(list: Tpl[], t0: number, ctx: Ctx): number {
  const body = list.filter(t => !t.isFinally && !t.recovery)
  const ends = new Map<string, number>()
  const failed: string[] = []
  let bodyEnd = t0
  body.forEach((t, index) => {
    const deps = t.deps ?? (index > 0 ? [body[index - 1]!.key] : [])
    const start = Math.max(t0, ...deps.map(d => ends.get(d) ?? t0))
    const blocked = deps.some(d => ctx.timings.get(d)?.outcome !== 'succeeded')
    if (blocked || start >= ctx.cancelAt) { markNotRun(t, Math.min(start, ctx.cancelAt), ctx); ends.set(t.key, Math.min(start, ctx.cancelAt)); return }
    const timing = runItem(t, start, ctx)
    ends.set(t.key, timing.end)
    if (timing.outcome === 'failed') failed.push(t.key)
    bodyEnd = Math.max(bodyEnd, timing.end)
  })
  for (const t of list.filter(t => t.recovery && !t.isFinally)) {
    const sources = t.recovery!.filter(key => failed.includes(key))
    if (!sources.length) { markNotRun(t, bodyEnd, ctx); continue }
    const timing = runItem(t, Math.max(...sources.map(key => ends.get(key)!)), ctx)
    bodyEnd = Math.max(bodyEnd, timing.end)
  }
  for (const t of list.filter(t => t.isFinally)) {
    const finallyCtx = { ...ctx, cancelAt: Infinity }
    const timing = runItem(t, Math.min(bodyEnd, Math.max(ctx.cancelAt, t0)), finallyCtx)
    bodyEnd = Math.max(bodyEnd, timing.end)
  }
  return bodyEnd
}

// ----- simulated runs -------------------------------------------------------------------------

interface ReviewMessage { round: number; role: string; content: string; created_at: string }

class Sim {
  approvedAt?: number
  cancelAt = Infinity
  reviewRevision = 1
  conversation: ReviewMessage[] = []
  constructor(
    readonly id: string,
    readonly task: TaskDef,
    readonly plan: Tpl[],
    readonly machine: string,
    readonly params: Record<string, string>,
    readonly t0: number,
    readonly fail: boolean,
    readonly options: { source?: 'cloud' | 'local'; trigger?: string; createdBy?: string; parent?: { sim: Sim; nodeId: string }; title?: string; taskId?: string } = {},
  ) {}

  schedule() {
    const parentCancel = this.options.parent?.sim.cancelAt ?? Infinity
    const ctx: Ctx = { fail: this.fail, approvedAt: this.approvedAt, cancelAt: Math.min(this.cancelAt, parentCancel), timings: new Map() }
    const end = runList(this.plan, this.t0, ctx)
    const top = this.plan.filter(t => !t.isFinally && !t.recovery).map(t => ctx.timings.get(t.key)!)
    const outcome: Outcome = ctx.cancelAt < Infinity && top.some(t => t.outcome === 'cancelled' || t.outcome === 'not_run') ? 'cancelled'
      : top.some(t => t.outcome === 'failed') ? 'failed' : 'succeeded'
    return { timings: ctx.timings, end, outcome }
  }

  gate(now: number, timings: Map<string, Timing>) {
    for (const timing of timings.values())
      if (timing.tpl.gate && timing.start <= now && now < timing.end) return timing
    return undefined
  }

  state(now: number) {
    const { timings, end, outcome } = this.schedule()
    if (now < this.t0) return { state: 'queued', timings, end, outcome }
    if (now >= end) return { state: outcome, timings, end, outcome }
    if (this.gate(now, timings)) return { state: 'awaiting_review', timings, end, outcome }
    return { state: 'running', timings, end, outcome }
  }

  step(t: Tpl, timings: Map<string, Timing>, now: number): RemoteAutomationStep {
    const timing = timings.get(t.key)
    const children = (t.stage ?? []).map(child => this.step(child, timings, now))
    let state = 'pending'
    if (timing) {
      if (now < timing.start) state = 'pending'
      else if (timing.outcome === 'not_run') state = 'not_run'
      else if (now < timing.end) state = t.stage
        ? children.some(c => c.state === 'awaiting_review') ? 'awaiting_review' : 'running'
        : t.gate ? 'awaiting_review' : 'running'
      else state = timing.outcome
    }
    const running = state === 'running' && !t.stage
    const span = timing ? timing.end - timing.start : 0
    const attempt = timing?.retryAt !== undefined && now >= timing.retryAt ? 2 : 1
    const emitted = (t.out ?? []).filter((_, i) => timing && now >= this.outputAt(timing, i))
    return {
      key: t.key,
      title: t.title,
      node_ids: leafKeys(t),
      children,
      depends_on: t.deps ?? null,
      recovery_from: t.recovery ?? [],
      state,
      is_stage: !!t.stage,
      is_finally: !!t.isFinally,
      is_recovery: !!t.recovery && !t.isFinally,
      is_reused: false,
      started_at: timing && state !== 'pending' && state !== 'not_run' ? iso(timing.start) : null,
      ended_at: timing && ['succeeded', 'failed', 'cancelled'].includes(state) ? iso(timing.end) : null,
      progress: running && t.progress && Number.isFinite(span) ? Math.min(0.99, (now - timing!.start) / span) : null,
      has_progress: !!t.progress,
      detail: state === 'succeeded' ? t.done ?? '' : running || state === 'awaiting_review' ? emitted.at(-1) ?? '' : '',
      error: state === 'failed' ? t.error ?? 'Step failed' : null,
      attempt,
      retry_error: attempt > 1 ? t.flaky ?? null : null,
    }
  }

  outputAt(timing: Timing, index: number) {
    const count = timing.tpl.out?.length ?? 0
    if (timing.tpl.gate) return timing.start + 200 * (index + 1)
    const span = Math.max(400, (timing.tpl.dur ?? 1000))
    return timing.start + span * (index + 1) / (count + 1)
  }

  events(now: number): RemoteAutomationLogLine[] {
    const { timings, end, outcome } = this.schedule()
    const rows: { at: number; order: number; event: Record<string, unknown> }[] = []
    let order = 0
    const push = (at: number, event: Record<string, unknown>) => { if (Number.isFinite(at)) rows.push({ at, order: order++, event: { timestamp: iso(at), source: 'event', ...event } }) }
    const host = this.machine === SERVER ? 'serverless coordinator' : this.machine
    push(this.t0, { kind: 'run.started', nodeId: '', message: `Run ${this.id} started on ${host} · fingerprint ${this.task.revision}` })
    for (const timing of timings.values()) {
      const t = timing.tpl
      if (t.stage || timing.outcome === 'not_run') continue
      const base = { nodeId: t.key, nodeTitle: t.title }
      push(timing.start, { ...base, kind: 'node.started', message: t.title })
      if (t.remote) push(timing.start + 50, { ...base, kind: 'node.output', source: 'stdout', message: `Dispatched subgraph to ${t.remote} as ${this.id}-${t.key} (lease held by parent)` })
      ;(t.out ?? []).forEach((line, index) => {
        const at = this.outputAt(timing, index)
        if (at < timing.end) push(at, { ...base, kind: 'node.output', source: 'stdout', message: line })
      })
      if (timing.retryAt !== undefined && timing.retryAt < timing.end)
        push(timing.retryAt, { ...base, kind: 'node.retry', message: 'Retrying (attempt 2 of 3)', error: { message: t.flaky, code: 'transient' } })
      if (t.gate && this.approvedAt !== undefined && this.approvedAt < timing.end)
        push(this.approvedAt, { ...base, kind: 'node.output', source: 'stdout', message: 'HumanReview approved by maintainer; resuming from checkpoint' })
      const finished = timing.outcome === 'failed' ? 'Failed' : timing.outcome === 'cancelled' ? 'Cancelled' : 'Succeeded'
      push(timing.end, { ...base, kind: 'node.finished', message: finished, ...(timing.outcome === 'failed' ? { error: { message: t.error ?? 'Step failed', code: 'exit_code' } } : {}) })
    }
    const closed = Number.isFinite(end)
    if (closed) push(end, { kind: 'run.finished', nodeId: '', message: outcome === 'failed' ? 'Failed' : outcome === 'cancelled' ? 'Cancelled' : 'Succeeded' })
    rows.sort((a, b) => a.at - b.at || a.order - b.order)
    return rows.filter(row => row.at <= now).map((row, index) => ({ sequence: index + 1, stream: String(row.event.source ?? 'event'), text: String(row.event.message ?? ''), raw: row.event }))
  }

  review(now: number, timings: Map<string, Timing>): RemoteAutomationReview | null {
    const gate = this.gate(now, timings)
    if (!gate || !this.task.review) return null
    return {
      ...this.task.review(this.params, this.reviewRevision),
      id: `rev-${this.id}-${this.reviewRevision}`,
      revision: this.reviewRevision,
      content_hash: `sha256:${this.id}:${this.reviewRevision}`,
      created_at: iso(gate.start),
      conversation: this.conversation.length ? this.conversation : [{ round: 1, role: 'assistant', content: 'Drafted the review after static checks, a secret scan and symbol indexing.', created_at: iso(gate.start) }],
    }
  }

  run(now: number): RemoteAutomationRun {
    const { state, timings, end } = this.state(now)
    const finished = ['succeeded', 'failed', 'cancelled'].includes(state)
    const steps = this.plan.map(t => this.step(t, timings, now))
    const active = [...timings.values()].filter(t => !t.tpl.stage && t.start <= now && now < t.end).map(t => t.tpl.title)
    const detail = state === 'queued' ? 'Waiting for a runner lease'
      : state === 'running' ? `Running: ${active.join(', ') || 'committing superstep barrier'}`
        : state === 'awaiting_review' ? 'Suspended at the HumanReview checkpoint; waiting for a maintainer decision'
          : state === 'succeeded' ? this.task.successDetail
            : state === 'failed' ? this.task.failureDetail ?? 'Run failed'
              : 'Cancelled by operator; Finally steps completed'
    const artifacts: RemoteAutomationArtifact[] = this.options.parent ? [] : this.task.artifacts
      .filter(a => { const t = timings.get(a.after); return t && t.outcome === 'succeeded' && now >= t.end })
      .map(a => ({
        id: `${this.id}-${a.id}`, name: a.name, kind: a.kind, machine_name: a.remote ?? this.machine,
        source_path: `runs/${this.id}/artifacts/${a.name}`, archived_path: `archive/${TENANT}/${this.id}/${a.name}`,
        bytes: a.bytes, sha256: fakeSha(`${this.id}/${a.name}`), deliveries: [{ destination: a.destination, state: a.state }],
      }))
    const fields = this.task.fields.map(f => ({ key: f.key, label: f.label, group: f.group ?? null }))
    const parent = this.options.parent
    const revision = this.events(now).length + this.reviewRevision * 1000 + (this.approvedAt ? 1 : 0)
    return {
      id: this.id,
      created_by: this.options.createdBy,
      tenant_id: TENANT,
      pool_id: 'default',
      machine_name: this.machine,
      task_id: this.options.taskId ?? this.task.id,
      task_name: this.options.title ?? this.task.name,
      script_revision: this.task.revision,
      parent_run_id: parent?.sim.id,
      root_run_id: parent?.sim.id,
      parent_node_id: parent?.nodeId,
      source: this.options.source ?? 'cloud',
      revision,
      logs_complete: finished,
      parameters: { ...this.params },
      state,
      detail,
      created_at: iso(this.t0 - 1500),
      updated_at: iso(Math.min(now, end)),
      cancel_requested: this.cancelAt < Infinity && !finished,
      local_run_id: this.options.source === 'local' ? `local-${this.id}` : null,
      snapshot: {
        run_id: this.id,
        task_id: this.task.id,
        trigger: this.options.trigger ?? 'manual',
        status: state,
        summary: '',
        warning: '',
        queued_at: iso(this.t0 - 1500),
        started_at: now >= this.t0 ? iso(this.t0) : null,
        ended_at: finished ? iso(end) : null,
        steps,
        artifacts,
        review: this.review(now, timings),
        launch: {
          task_id: this.task.id,
          task_name: this.task.name,
          machine_name: this.machine,
          script_revision: this.task.revision,
          parameters: { ...this.params },
          fields,
        },
      },
    }
  }

  children(now: number): Sim[] {
    const { timings } = this.schedule()
    const result: Sim[] = []
    for (const timing of timings.values()) {
      const t = timing.tpl
      if (!t.remote || !t.sub || timing.outcome === 'not_run' || now < timing.start) continue
      const id = `${this.id}-${t.key}`
      let child = childCache.get(id)
      if (!child) {
        child = new Sim(id, this.task, t.sub, t.remote, this.params, timing.start + 300, !!t.failOn && this.fail,
          { source: this.options.source, trigger: 'subgraph', parent: { sim: this, nodeId: t.key }, title: `${this.task.name} › ${t.title}` })
        childCache.set(id, child)
      }
      result.push(child)
    }
    return result
  }
}

const childCache = new Map<string, Sim>()
const iso = (ms: number) => new Date(ms).toISOString()
function fakeSha(seed: string) {
  let h = 2166136261
  let out = ''
  for (let round = 0; round < 8; round++) {
    for (const ch of seed + round) { h ^= ch.charCodeAt(0); h = Math.imul(h, 16777619) >>> 0 }
    out += h.toString(16).padStart(8, '0')
  }
  return out
}
function defaults(task: TaskDef) { return Object.fromEntries(task.fields.map(f => [f.key, f.default_value])) }

const boot = Date.now()
const sims: Sim[] = []
function seed(id: string, taskId: string, machine: string, startOffset: number, options: Sim['options'] & { params?: Record<string, string>; fail?: boolean; approveAfter?: number } = {}) {
  const task = taskById.get(taskId)!
  const params = { ...defaults(task), ...options.params }
  const sim = new Sim(id, task, task.plan, machine, params, boot - startOffset, options.fail ?? params.inject_failure === 'true', options)
  if (options.approveAfter !== undefined) sim.approvedAt = sim.t0 + options.approveAfter
  sims.push(sim)
  return sim
}
seed('run-0412', 'legacy-p4-sync', 'runner-win-01', 3 * 24 * HOUR, { source: 'local', trigger: 'manual', fail: true })
seed('run-0517', 'daily-perf-audit', 'runner-linux-02', 25 * HOUR, { source: 'local', trigger: 'schedule' })
seed('run-0521', 'pr-code-review', 'runner-win-01', 26 * HOUR, { createdBy: 'alice', approveAfter: 40 * SECOND, params: { pr_number: '138' } })
seed('run-0530', 'multi-target-ci', SERVER, 22 * HOUR, { createdBy: 'ci-bot' })
seed('run-0544', 'multi-target-ci', SERVER, 2 * HOUR, { createdBy: 'fanxiang', params: { branch: 'feature/foreach-barrier', inject_failure: 'true' } })
seed('run-0551', 'daily-perf-audit', 'runner-linux-02', HOUR, { source: 'local', trigger: 'schedule' })
seed('run-0558', 'pr-code-review', 'runner-win-01', 150 * SECOND, { createdBy: 'fanxiang' })
seed('run-0560', 'multi-target-ci', SERVER, 6 * SECOND, { createdBy: 'fanxiang' })
let nextRunNumber = 561

function allSims(now: number): Sim[] {
  const roots = sims.filter(sim => sim.t0 - 1500 <= now)
  return [...roots, ...roots.flatMap(sim => sim.children(now))]
}
function findSim(id: string) {
  const now = Date.now()
  const sim = allSims(now).find(row => row.id === id)
  if (!sim) throw new Error(`Run ${id} not found`)
  return sim
}

// ----- inventory ------------------------------------------------------------------------------

const organization: RemoteAutomationOrganization = {
  groups: [{ id: 'grp-agent', name: 'AI agent flows' }, { id: 'grp-ci', name: 'CI & telemetry' }],
  assignments: { 'pr-code-review': 'grp-agent', 'daily-perf-audit': 'grp-ci', 'multi-target-ci': 'grp-ci' },
  starred: ['pr-code-review'],
}
const schedules: RemoteAutomationTeamSchedule[] = [{
  task_id: 'daily-perf-audit',
  machine_name: 'runner-linux-02',
  enabled: true,
  entries: [{ time: '08:30', frequency: 'daily', week_days: 0, month_day: 1, last_day_of_month: false }],
  source: 'web',
  updated_by: 'fanxiang',
  updated_at: iso(boot - 3 * 24 * HOUR),
  last_occurrence: iso(boot - HOUR),
  last_run_id: 'run-0551',
  next_occurrence: iso(boot + 23 * HOUR),
}]

const CAPS = ['pregel.inspect.v1', 'pregel.control.v1', 'pregel.triggers.v1', 'pregel.discard.v1']
function machineTask(id: string): RemoteAutomationTask {
  const task = taskById.get(id)!
  return { id, name: task.name, parameters: defaults(task), script_revision: task.revision, can_run: true, unavailable_reason: '' }
}
function machines(now: number): RemoteAutomationMachine[] {
  return [
    { tenant_id: TENANT, pool_id: 'default', machine_name: SERVER, host_version: '0.2.24', last_seen: iso(now - 2000),
      capabilities: ['pregel.server.v1', ...CAPS, 'linux', 'go'], tasks: ['multi-target-ci', 'daily-perf-audit'].map(machineTask) },
    { tenant_id: TENANT, pool_id: 'default', machine_name: 'runner-win-01', host_version: '0.2.24', last_seen: iso(now - 4000),
      capabilities: [...CAPS, 'windows', 'dotnet8', 'gpu'], tasks: ['pr-code-review', 'multi-target-ci'].map(machineTask) },
    { tenant_id: TENANT, pool_id: 'default', machine_name: 'runner-linux-02', host_version: '0.2.24', last_seen: iso(now - 6000),
      capabilities: [...CAPS, 'linux', 'docker'], tasks: ['multi-target-ci', 'daily-perf-audit'].map(machineTask) },
    { tenant_id: TENANT, pool_id: 'default', machine_name: 'runner-mac-03', host_version: '0.2.19', last_seen: iso(boot - 2 * 24 * HOUR),
      capabilities: ['pregel.control.v1', 'macos'], tasks: ['multi-target-ci'].map(machineTask) },
  ]
}

const clone = <T>(value: T): T => JSON.parse(JSON.stringify(value)) as T
const pause = (ms = 120) => new Promise(resolve => setTimeout(resolve, ms))

export async function fetchRemoteAutomation(_tenantId: string, _signal?: AbortSignal): Promise<RemoteAutomationInventory> {
  await pause(60)
  const now = Date.now()
  return {
    tenants: [{ id: TENANT, name: 'Heron demo cluster', server_machine: SERVER }],
    machines: machines(now),
    runs: allSims(now).map(sim => sim.run(now)),
    tasks: tasks.map(task => ({ tenant_id: TENANT, id: task.id, name: task.name })),
    organizations: { [TENANT]: clone(organization) },
    schedules: { [TENANT]: clone(schedules) },
  }
}

export async function saveRemoteAutomationOrganization(_tenantId: string, next: RemoteAutomationOrganization, _signal?: AbortSignal): Promise<RemoteAutomationOrganization> {
  Object.assign(organization, clone(next))
  return clone(organization)
}

export async function saveRemoteAutomationSchedule(draft: RemoteAutomationScheduleDraft, _signal?: AbortSignal): Promise<RemoteAutomationTeamSchedule> {
  const entry: RemoteAutomationTeamSchedule = {
    task_id: draft.task_id, machine_name: draft.machine_name, enabled: draft.enabled, entries: clone(draft.entries),
    source: 'web', updated_by: 'demo visitor', updated_at: new Date().toISOString(), last_occurrence: '',
    next_occurrence: new Date(Date.now() + 6 * HOUR).toISOString(),
  }
  const index = schedules.findIndex(row => row.task_id === draft.task_id)
  if (index >= 0) schedules[index] = entry
  else schedules.push(entry)
  return clone(entry)
}

function previewStep(t: Tpl): RemoteAutomationStep {
  return {
    key: t.key, title: t.title, node_ids: leafKeys(t), children: (t.stage ?? []).map(previewStep), depends_on: t.deps ?? null,
    recovery_from: t.recovery ?? [], state: 'pending', is_stage: !!t.stage, is_finally: !!t.isFinally, is_recovery: !!t.recovery && !t.isFinally,
    is_reused: false, started_at: null, ended_at: null, progress: null, has_progress: !!t.progress, detail: '', error: null, retry_error: null,
  }
}

export async function inspectRemoteAutomation(draft: RemoteAutomationRunDraft, _signal?: AbortSignal): Promise<RemoteAutomationInspection> {
  await pause(250)
  const task = taskById.get(draft.task_id)
  if (!task) throw new Error(`Unknown flow ${draft.task_id}`)
  const fields = clone(task.fields).map(f => ({ ...f, value: draft.parameters[f.key] ?? f.default_value }))
  const parameters = Object.fromEntries(fields.map(f => [f.key, f.value]))
  let validation_error = ''
  if (task.id === 'pr-code-review' && !/^\d+$/.test(parameters.pr_number ?? '')) validation_error = 'Pull request must be a number.'
  if (task.id === 'daily-perf-audit' && !(Number(parameters.window_hours) > 0)) validation_error = 'Lookback window must be a positive number of hours.'
  return { machine_name: draft.machine_name, fields, plan_steps: task.plan.map(previewStep), validation_error, parameters }
}

export async function createRemoteAutomationRun(draft: RemoteAutomationRunDraft, _signal?: AbortSignal): Promise<RemoteAutomationRun> {
  await pause()
  const task = taskById.get(draft.task_id)
  if (!task) throw new Error(`Unknown flow ${draft.task_id}`)
  const params = { ...defaults(task), ...draft.parameters }
  const now = Date.now()
  const sim = new Sim(`run-0${nextRunNumber++}`, task, task.plan, draft.machine_name || SERVER, params, now + 1200,
    params.inject_failure === 'true', { createdBy: 'demo visitor' })
  sims.push(sim)
  return sim.run(now)
}

export async function fetchRemoteAutomationRun(runId: string, _tenantId?: string, _signal?: AbortSignal): Promise<RemoteAutomationRun> {
  await pause(40)
  return findSim(runId).run(Date.now())
}

export async function stopRemoteAutomationRun(runId: string, _tenantId?: string, _signal?: AbortSignal): Promise<RemoteAutomationRun> {
  const sim = findSim(runId)
  sim.cancelAt = Date.now()
  return sim.run(Date.now() + 1)
}

export async function discardRemoteAutomationRun(runId: string, tenantId?: string, signal?: AbortSignal): Promise<RemoteAutomationRun> {
  return stopRemoteAutomationRun(runId, tenantId, signal)
}

export async function reviewRemoteAutomationRun(runId: string, _tenantId: string, decision: RemoteAutomationReviewDecision, _signal?: AbortSignal): Promise<RemoteAutomationRun> {
  await pause()
  const sim = findSim(runId)
  const now = Date.now()
  if (decision.action === 'approve') sim.approvedAt = now
  else if (decision.action === 'abandon') sim.cancelAt = now
  else {
    const at = new Date(now).toISOString()
    if (!sim.conversation.length) sim.conversation.push({ round: 1, role: 'assistant', content: 'Drafted the review after static checks, a secret scan and symbol indexing.', created_at: at })
    sim.reviewRevision += 1
    sim.conversation.push({ round: sim.reviewRevision, role: 'user', content: decision.feedback, created_at: at })
    sim.conversation.push({ round: sim.reviewRevision, role: 'assistant', content: `Revised the review: ${decision.feedback.slice(0, 160)}`, created_at: at })
  }
  return sim.run(now + 1)
}

export async function fetchRemoteAutomationLogs(runId: string, _tenantId: string, after: number, _signal?: AbortSignal): Promise<RemoteAutomationLogs> {
  await pause(40)
  const all = findSim(runId).events(Date.now())
  const lines = all.filter(line => line.sequence > after)
  return { lines, next_cursor: all.length ? all[all.length - 1]!.sequence : after, has_more: false }
}
