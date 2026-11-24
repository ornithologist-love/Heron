<script setup lang="ts">
import type { RemoteAutomationArtifact } from '../api'

defineProps<{ artifacts: RemoteAutomationArtifact[]; legacyLocations: string[] }>()
const states: Record<string, string> = { uploading: 'Uploading', succeeded: 'Done', failed: 'Failed', canceled: 'Cancelled' }
const state = (value: string) => states[value] ?? value
const kinds: Record<string, string> = { report: 'Report', log: 'Log', stat: 'Test data', file: 'File' }
const size = (bytes: number | null | undefined) => bytes == null ? 'Size unknown'
  : bytes < 1024 ? `${bytes} B` : bytes < 1048576 ? `${(bytes / 1024).toFixed(1)} KB` : `${(bytes / 1048576).toFixed(1)} MB`
function webLink(location: string | null | undefined) {
  if (!location) return undefined
  try { const url = new URL(location); return ['http:', 'https:'].includes(url.protocol) ? url.href : undefined }
  catch { return undefined }
}
</script>

<template>
  <section class="artifact-list" aria-label="Run artifacts">
    <article v-for="artifact in artifacts" :key="artifact.id" class="artifact-card">
      <header><strong>{{ artifact.name }}</strong><span>{{ size(artifact.bytes) }}</span></header>
      <p class="artifact-meta">{{ kinds[artifact.kind] || artifact.kind }} · {{ artifact.machine_name }}</p>
      <dl>
        <dt>Runner file</dt><dd><code>{{ artifact.archived_path || artifact.source_path }}</code></dd>
        <template v-if="artifact.sha256"><dt>SHA-256</dt><dd><code>{{ artifact.sha256 }}</code></dd></template>
      </dl>
      <ul v-if="artifact.deliveries?.length" class="delivery-list">
        <li v-for="delivery in artifact.deliveries" :key="delivery.destination">
          <div class="delivery-heading"><strong>{{ delivery.destination }}</strong><span :class="{ failed: delivery.state === 'failed' }">{{ state(delivery.state) }}</span></div>
          <a v-if="webLink(delivery.location)" :href="webLink(delivery.location)" target="_blank" rel="noopener noreferrer">{{ delivery.location }}</a>
          <code v-else-if="delivery.location">{{ delivery.location }}</code>
          <p v-if="delivery.error" class="failed">{{ delivery.error }}</p>
        </li>
      </ul>
      <p v-else class="artifact-meta">No deliveries recorded</p>
    </article>
    <p v-if="!artifacts.length" class="artifact-meta">No artifacts recorded for this run.</p>
    <details v-if="legacyLocations.length" class="legacy-locations">
      <summary>Working directories and legacy locations</summary>
      <p class="artifact-meta">For troubleshooting only; these are not archived or uploaded artifacts.</p>
      <code v-for="location in legacyLocations" :key="location">{{ location }}</code>
    </details>
  </section>
</template>

<style scoped>
.artifact-list { display: grid; gap: 12px; }
.artifact-card { padding: 14px; border: 1px solid var(--border-color); border-radius: 8px; background: var(--bg-secondary); min-width: 0; }
.artifact-card header, .delivery-heading { display: flex; flex-wrap: wrap; align-items: baseline; justify-content: space-between; gap: 6px 16px; }
.artifact-card header strong { overflow-wrap: anywhere; }
.artifact-card header span, .artifact-meta { color: var(--text-secondary); font-size: 12px; }
.artifact-meta { margin: 7px 0; }
dl { display: grid; grid-template-columns: 88px minmax(0, 1fr); gap: 8px; margin: 12px 0; font-size: 12px; }
dt { color: var(--text-secondary); }
dd { margin: 0; min-width: 0; }
code, a { white-space: pre-wrap; overflow-wrap: anywhere; font-size: 12px; }
.delivery-list { list-style: none; padding: 10px 0 0; margin: 0; border-top: 1px solid var(--border-color); display: grid; gap: 10px; }
.delivery-list li { min-width: 0; }
.delivery-heading { font-size: 12px; margin-bottom: 4px; }
.failed { color: var(--td-error-color); }
.legacy-locations code { display: block; margin: 8px 0; }
summary { cursor: pointer; color: var(--text-secondary); font-size: 12px; }
@media (max-width: 540px) { dl { grid-template-columns: 1fr; gap: 4px; } dd { margin-bottom: 6px; } }
</style>
