<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type { RemoteAutomationReviewDecision, RemoteAutomationRun } from '@/api'
import MarkdownRenderer from './MarkdownRenderer.vue'

const props = defineProps<{ run: RemoteAutomationRun; canControl: boolean; busy: boolean }>()
const emit = defineEmits<{ submit: [decision: RemoteAutomationReviewDecision] }>()
const review = computed(() => props.run.snapshot?.review)
const feedback = ref('')
const canSubmit = computed(() => props.canControl && !props.busy && props.run.control?.state !== 'pending')
watch(() => `${review.value?.id ?? ''}:${review.value?.content_hash ?? ''}`, () => { feedback.value = '' })
function submit(action: RemoteAutomationReviewDecision['action']) {
  if (!canSubmit.value || !review.value || action === 'revise' && !feedback.value.trim()) return
  emit('submit', { action, review_id: review.value.id, content_hash: review.value.content_hash, feedback: feedback.value.trim() })
}
</script>

<template>
  <section v-if="review && run.state === 'awaiting_review'" class="review" aria-label="Review this run">
    <div class="review-heading"><h4>{{ review.title || 'Review this run' }}</h4><span>Revision {{ review.revision }}</span></div>
    <p v-if="review.detail" class="detail">{{ review.detail }}</p>
    <p v-if="review.hint" class="detail">{{ review.hint }}</p>
    <div v-if="review.content" class="review-content"><MarkdownRenderer :content="review.content" /></div>
    <p v-if="review.continuation_detail" class="detail">{{ review.continuation_detail }}</p>
    <details v-if="review.conversation?.length" class="conversation"><summary>Show previous conversation</summary><article v-for="(message, index) in review.conversation" :key="index"><strong>Round {{ message.round }} · {{ message.role === 'user' ? 'Your request' : 'Agent result' }}</strong><MarkdownRenderer :content="message.content" /></article></details>
    <details v-if="Object.keys(run.parameters).length" class="parameters"><summary>Run parameters</summary><dl><template v-for="(value, key) in run.parameters" :key="key"><dt>{{ key }}</dt><dd>{{ value }}</dd></template></dl></details>
    <label v-if="review.can_revise !== false" class="feedback">Feedback<t-textarea v-model="feedback" :autosize="{ minRows: 4, maxRows: 10 }" :maxlength="12000" :placeholder="review.feedback_placeholder || 'Add requirements, point out issues, or ask a follow-up'" :disabled="!canSubmit" /></label>
    <p v-if="!canControl" class="detail">Update the Agent on this runner to review this run from the web.</p>
    <div class="review-actions">
      <t-button theme="primary" :disabled="!canSubmit || run.cancel_requested" @click="submit('approve')">{{ review.approve_label || 'Approve & continue' }}</t-button>
      <t-button v-if="review.can_revise !== false" variant="outline" :disabled="!canSubmit || run.cancel_requested || !feedback.trim()" @click="submit('revise')">{{ review.revise_label || 'Revise with feedback' }}</t-button>
      <t-button theme="danger" variant="outline" :disabled="!canSubmit" @click="submit('abandon')">Abandon run</t-button>
    </div>
  </section>
</template>

<style scoped>
.review { border: 1px solid var(--color-accent-border); border-radius: var(--radius-card); padding: 18px; margin: 14px 0; background: var(--color-accent-light); }
.review-heading,.review-actions { display: flex; align-items: center; flex-wrap: wrap; gap: 10px; }.review-heading { justify-content: space-between; }.review-heading h4 { margin: 0; font-size: 15px; }.review-heading span,.detail { color: var(--text-secondary); font-size: 12px; }.detail { white-space: pre-wrap; overflow-wrap: anywhere; line-height: 1.7; }
.review-content :deep(.markdown-body) { max-width: none; margin: 0; font-size: 13px; line-height: 1.65; }.review-content :deep(.markdown-body h3) { margin: 4px 0 6px; font-size: 14px; }.review-content :deep(.markdown-body h3 + ul), .review-content :deep(.markdown-body h3 + ol) { margin-top: 0; }.review-content :deep(.markdown-body :is(ul, ol)) { margin: 0 0 10px; }.review-content { max-height: 420px; overflow: auto; margin: 12px 0; padding: 12px 16px; background: var(--bg-card); border: 1px solid var(--border-color); border-radius: 6px; }.conversation,.parameters { margin: 14px 0; font-size: 12px; }.conversation summary,.parameters summary { cursor: pointer; color: var(--text-secondary); }.conversation article { margin-top: 14px; }.parameters dl { display: grid; grid-template-columns: minmax(100px, 180px) 1fr; gap: 8px 12px; }.parameters dt { color: var(--text-secondary); }.parameters dd { margin: 0; white-space: pre-wrap; overflow-wrap: anywhere; }
.feedback { display: grid; gap: 8px; margin: 16px 0; font-size: 13px; }
@media(max-width: 640px) { .review { padding: 14px; }.parameters dl { grid-template-columns: 1fr; }.parameters dd { margin-bottom: 8px; } }
</style>
