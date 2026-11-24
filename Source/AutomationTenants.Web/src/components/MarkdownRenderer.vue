<script setup lang="ts">
import { computed, ref, onMounted, watch, nextTick, onBeforeUnmount } from 'vue'
import MarkdownIt from 'markdown-it'
import hljs from 'highlight.js/lib/core'
import bash from 'highlight.js/lib/languages/bash'
import c from 'highlight.js/lib/languages/c'
import cpp from 'highlight.js/lib/languages/cpp'
import csharp from 'highlight.js/lib/languages/csharp'
import css from 'highlight.js/lib/languages/css'
import diff from 'highlight.js/lib/languages/diff'
import go from 'highlight.js/lib/languages/go'
import graphql from 'highlight.js/lib/languages/graphql'
import ini from 'highlight.js/lib/languages/ini'
import java from 'highlight.js/lib/languages/java'
import javascript from 'highlight.js/lib/languages/javascript'
import json from 'highlight.js/lib/languages/json'
import kotlin from 'highlight.js/lib/languages/kotlin'
import lua from 'highlight.js/lib/languages/lua'
import makefile from 'highlight.js/lib/languages/makefile'
import markdown from 'highlight.js/lib/languages/markdown'
import python from 'highlight.js/lib/languages/python'
import rust from 'highlight.js/lib/languages/rust'
import sql from 'highlight.js/lib/languages/sql'
import typescript from 'highlight.js/lib/languages/typescript'
import xml from 'highlight.js/lib/languages/xml'
import yaml from 'highlight.js/lib/languages/yaml'

hljs.registerLanguage('bash', bash)
hljs.registerLanguage('c', c)
hljs.registerLanguage('cpp', cpp)
hljs.registerLanguage('csharp', csharp)
hljs.registerLanguage('css', css)
hljs.registerLanguage('diff', diff)
hljs.registerLanguage('go', go)
hljs.registerLanguage('graphql', graphql)
hljs.registerLanguage('ini', ini)
hljs.registerLanguage('java', java)
hljs.registerLanguage('javascript', javascript)
hljs.registerLanguage('json', json)
hljs.registerLanguage('kotlin', kotlin)
hljs.registerLanguage('lua', lua)
hljs.registerLanguage('makefile', makefile)
hljs.registerLanguage('markdown', markdown)
hljs.registerLanguage('python', python)
hljs.registerLanguage('rust', rust)
hljs.registerLanguage('sql', sql)
hljs.registerLanguage('typescript', typescript)
hljs.registerLanguage('xml', xml)
hljs.registerLanguage('yaml', yaml)
import anchor from 'markdown-it-anchor'
import DOMPurify from 'dompurify'
import type Token from 'markdown-it/lib/token.mjs'

import taskLists from 'markdown-it-task-lists'
import type { KsfThemeMode, MarkdownRendererLabels } from './i18n'
import { resolveMarkdownLabels } from './i18n'

// Slugify function - must match TableOfContents.vue parseHeadings
const slugify = (s: string) => {
  return s.toLowerCase().replace(/[^\w\u4e00-\u9fa5]+/g, '-').replace(/^-+|-+$/g, '')
}

interface Props {
  content: string
  locale?: string
  labels?: Partial<MarkdownRendererLabels>
  theme?: KsfThemeMode
}

const props = defineProps<Props>()
const labels = computed(() => resolveMarkdownLabels(props.locale, props.labels))
const resolvedTheme = ref<'light' | 'dark'>('light')
const renderEpoch = ref(0)

function readDocumentTheme(): 'light' | 'dark' {
  if (props.theme === 'light' || props.theme === 'dark') return props.theme
  if (typeof document !== 'undefined') {
    const theme = document.documentElement.dataset.theme
    if (theme === 'dark' || theme === 'light') return theme
  }
  if (typeof window !== 'undefined' && window.matchMedia?.('(prefers-color-scheme: dark)').matches) {
    return 'dark'
  }
  return 'light'
}

function syncResolvedTheme() {
  resolvedTheme.value = readDocumentTheme()
}

function mermaidTheme() {
  return resolvedTheme.value === 'dark' ? 'dark' : 'default'
}

const md: MarkdownIt = new MarkdownIt({
  html: true,
  linkify: true,
  typographer: true,
  highlight: (str: string, lang: string): string => {
    const langLabel = lang ? `<span class="code-lang">${md.utils.escapeHtml(lang)}</span>` : ''
    const safeCopy = md.utils.escapeHtml(labels.value.copyCode)
    const copyBtn = `<button class="code-copy" title="${safeCopy}" aria-label="${safeCopy}" type="button">
      <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
        <rect width="14" height="14" x="8" y="8" rx="2" ry="2"></rect>
        <path d="M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2"></path>
      </svg>
    </button>`
    const toolbar = `<div class="code-toolbar">${langLabel}${copyBtn}</div>`
    if (lang && hljs.getLanguage(lang)) {
      try {
        return `<pre class="hljs">${toolbar}<code>${hljs.highlight(str, { language: lang, ignoreIllegals: true }).value}</code></pre>`
      } catch (err) {
        console.error(err)
      }
    }
    return `<pre class="hljs">${toolbar}<code>${md.utils.escapeHtml(str)}</code></pre>`
  },
})

// Add anchor plugin for heading IDs
md.use(anchor, {
  slugify: (s: string) => slugify(s),
  permalink: anchor.permalink.ariaHidden({
    placement: 'before',
    class: 'header-anchor',
  }),
  callback: (token: Token, info: { slug: string; title: string }) => {
    // Ensure heading tokens have the correct id
    if (token.attrGet('id') === null) {
      token.attrSet('id', info.slug)
    }
  }
})

// Add task list (checkbox) plugin - readonly mode
md.use(taskLists, {
  enabled: false,
  label: true,
  labelAfter: true,
})

// Obsidian-style callouts: turn "> [!tip] Title" blockquotes into card divs.
// Tokens are rewritten in the core phase (blockquote_open/close -> div + class) with a bold
// title, so they survive DOMPurify, which only needs to keep the class attribute.
const CALLOUT_TYPES = new Set([
  'tip', 'note', 'abstract', 'info', 'success', 'question', 'warning',
  'failure', 'danger', 'bug', 'example', 'quote',
])

// Normalize Obsidian aliases (hint -> tip, check -> success, ...) to match upstream behavior
const CALLOUT_ALIAS: Record<string, string> = {
  hint: 'tip', important: 'tip', check: 'success', done: 'success',
  help: 'question', faq: 'question', caution: 'warning', attention: 'warning',
  fail: 'failure', missing: 'failure', error: 'danger', cite: 'quote',
  summary: 'abstract', todo: 'info',
}

const CALLOUT_LABEL: Record<string, string> = {
  tip: 'Tip', note: 'Note', abstract: 'Abstract', info: 'Info',
  success: 'Success', question: 'Question', warning: 'Warning',
  failure: 'Failure', danger: 'Danger', bug: 'Bug', example: 'Example', quote: 'Quote',
}

md.core.ruler.after('block', 'obsidian-callout', (state) => {
  const tokens = state.tokens
  for (let i = 0; i < tokens.length; i++) {
    if (tokens[i].type !== 'blockquote_open') continue

    // Find the blockquote_close matching this blockquote_open (nesting-aware)
    let depth = 0
    let closeIdx = -1
    for (let j = i; j < tokens.length; j++) {
      if (tokens[j].type === 'blockquote_open') depth++
      else if (tokens[j].type === 'blockquote_close') {
        depth--
        if (depth === 0) {
          closeIdx = j
          break
        }
      }
    }
    if (closeIdx < 0) continue

    // The first inline token must start with [!type]; otherwise treat it as a plain quote
    let k = i + 1
    while (k < closeIdx && tokens[k].type !== 'inline') k++
    if (k >= closeIdx) continue
    const match = /^\[!(\w+)\]([+-]?)\s*(.*)$/.exec(tokens[k].content.trim())
    if (!match) continue
    const rawType = match[1].toLowerCase()
    if (!CALLOUT_TYPES.has(rawType)) continue
    const type = CALLOUT_ALIAS[rawType] ?? rawType
    const title = match[3].trim() || CALLOUT_LABEL[type] || rawType

    tokens[i].tag = 'div'
    tokens[i].attrJoin('class', `md-callout md-callout-${type}`)
    tokens[closeIdx].tag = 'div'

    // Key point: inline.children is still empty when core rules run (the later 'inline'
    // core rule builds it from content), so edits to children are discarded. The only
    // reliable place is inline.content itself: strip the "[!tip]" prefix and wrap the
    // title in bold so inline parsing naturally yields a <strong> title.
    const inline = tokens[k]
    const contentLines = inline.content.split('\n')
    const titleFromBody = /^\s*\[!\w+\][+-]?\s*(.*)$/.exec(contentLines[0] ?? '')?.[1]?.trim() ?? ''
    // Defensive cleanup: legacy titles may contain stray "[!tip]" or "(tip)" echoes
    const cleanedTitle = titleFromBody
      .replace(/\[!\w+\][+-]?/g, '')
      .replace(/\((?:tip|note|info|warning|danger|success|question|example|abstract|quote|bug|failure)\)/gi, '')
      .replace(/\s{2,}/g, ' ')
      .trim()
    const finalTitle = cleanedTitle || title

    if (contentLines.length === 1) {
      // First paragraph is just the title line: mark the whole paragraph as the title
      inline.content = `**${finalTitle}**`
      const paraOpenIdx = k - 1
      if (tokens[paraOpenIdx]?.type === 'paragraph_open') {
        tokens[paraOpenIdx].attrJoin('class', 'md-callout-title')
      }
    } else {
      // Title and body share a paragraph: bold the title, keep body lines (soft breaks) intact
      inline.content = `**${finalTitle}**\n${contentLines.slice(1).join('\n')}`
    }
  }
})

// Custom fence renderer: intercept mermaid code blocks
const defaultFence = md.renderer.rules.fence
md.renderer.rules.fence = (
  tokens: Token[],
  idx: number,
  options: any,
  env: unknown,
  self: any,
): string => {
  const token = tokens[idx]
  const langName = token.info.trim().toLowerCase()
  if (langName === 'mermaid') {
    // Confirmed by bisection: DOMPurify.sanitize() strips any attribute value containing
    // `--` (a built-in defense against HTML comment / conditional-comment escapes). Mermaid
    // arrows `-->` always contain `--`, so the block silently failed and stayed loading.
    // encodeURIComponent leaves hyphens alone, so instead the source is Base64-encoded
    // (letters, digits, + / = only, no `-`). Decoding uses atob symmetrically, with
    // TextEncoder/TextDecoder handling multi-byte UTF-8.
    const encoded = btoa(String.fromCharCode(...new TextEncoder().encode(token.content)))
    const loading = md.utils.escapeHtml(labels.value.mermaidLoading)
    return `<div class="mermaid-block" data-mermaid="${encoded}"><div class="mermaid-loading">${loading}</div></div>\n`
  }
  if (defaultFence) {
    return defaultFence(tokens, idx, options, env, self)
  }
  return self.renderToken(tokens, idx, options)
}

const markdownBodyRef = ref<HTMLDivElement>()

// Mermaid lightbox state
const showLightbox = ref(false)
const lightboxSvg = ref('')  // Original SVG string
const lightboxPngUrl = ref('')  // PNG data URL
const lightboxZoom = ref(1.0)
const lightboxSvgStyle = ref({ transform: 'scale(1)', transformOrigin: 'center center' })
let lightboxSvgEl: HTMLElement | null = null

// Drag state
const isDragging = ref(false)
const dragStartX = ref(0)
const dragStartY = ref(0)
const imagePosition = ref({ x: 0, y: 0 })

const renderedContent = computed(() => {
  const currentLabels = labels.value
  try {
    return DOMPurify.sanitize(md.render(props.content), {
      // Anyone can upload wiki content, so html: true requires sanitizing.
      // Keep mermaid containers (data-mermaid is read during rendering) and code toolbar buttons.
      ADD_ATTR: ['data-mermaid', 'target', 'aria-label'],
      FORBID_TAGS: ['style', 'iframe', 'form'],
      FORBID_ATTR: ['onerror', 'onload', 'onclick', 'onmouseover'],
    })
  } catch (err) {
    console.error('Failed to render markdown:', err)
    return `<div class="error">${md.utils.escapeHtml(currentLabels.renderError)}</div>`
  }
})

const renderKey = computed(() =>
  `${renderEpoch.value}-${resolvedTheme.value}-${props.locale ?? 'auto'}`,
)

// Mermaid rendering: dynamically load and render mermaid diagrams
let mermaidInstance: any = null
let mermaidIdCounter = 0

async function loadMermaid() {
  if (mermaidInstance) return mermaidInstance
  const mermaid = (await import('mermaid')).default
  mermaidInstance = mermaid
  return mermaid
}

function configureMermaid(mermaid: any) {
  mermaid.initialize({
    startOnLoad: false,
    theme: mermaidTheme(),
    fontFamily: 'system-ui, -apple-system, sans-serif',
    flowchart: { useMaxWidth: true, htmlLabels: true },
    sequence: { useMaxWidth: true },
    gantt: { useMaxWidth: true },
  })
}

async function renderMermaidBlocks() {
  const container = markdownBodyRef.value
  if (!container) return
  const blocks = container.querySelectorAll<HTMLElement>('.mermaid-block[data-mermaid]')
  if (blocks.length === 0) return

  let mermaid: any
  try {
    mermaid = await loadMermaid()
    configureMermaid(mermaid)
  } catch (loadErr) {
    console.error('Failed to load mermaid:', loadErr)
    // Fallback: show an inline load error inside every mermaid block so
    // the page doesn't just sit on "Loading diagram..." forever.
    for (const block of blocks) {
      block.innerHTML = renderMermaidErrorHtml(labels.value.mermaidUnavailable)
      block.classList.add('mermaid-error-block')
      block.removeAttribute('data-mermaid')
    }
    return
  }

  for (const block of blocks) {
    const decoded = decodeMermaidSource(block.getAttribute('data-mermaid'))
    if (!decoded) continue

    // Pre-parse so mermaid can report syntax errors *without* injecting
    // the giant red error <svg> into <body> that it normally does when
    // render() throws. `suppressErrors: true` makes parse return false
    // on failure instead of throwing, so we can fall through to our own
    // inline error box and never call render() on bad source.
    let parseError: unknown = null
    try {
      const ok = await mermaid.parse(decoded, { suppressErrors: true })
      if (ok === false) parseError = new Error('Mermaid syntax error')
    } catch (e) {
      parseError = e
    }
    if (parseError) {
      block.innerHTML = renderMermaidErrorHtml(
        labels.value.mermaidSyntaxError,
      )
      block.classList.add('mermaid-error-block')
      block.removeAttribute('data-mermaid')
      cleanupMermaidStrayNodes(undefined, container)
      continue
    }

    const id = `mermaid-svg-${++mermaidIdCounter}`
    try {
      const { svg } = await mermaid.render(id, decoded)
      block.innerHTML = svg
      block.classList.add('mermaid-rendered')
      block.removeAttribute('data-mermaid')
      block.style.cursor = 'pointer'
      block.title = labels.value.mermaidClickToEnlarge

      block.addEventListener('click', () => {
        openMermaidLightbox(svg)
      })
    } catch (renderErr: any) {
      console.error('Mermaid render error:', renderErr)
      block.innerHTML = renderMermaidErrorHtml(
        labels.value.mermaidSyntaxError,
      )
      block.classList.add('mermaid-error-block')
      block.removeAttribute('data-mermaid')
    } finally {
      // Defensive: mermaid >=10 sometimes leaves a measurement DOM node
      // attached to <body> with id `d${id}` or `${id}`. Remove them so
      // the page is never visually polluted by a stray diagram.
      cleanupMermaidStrayNodes(id, container)
    }
  }
}

function decodeMermaidSource(source: string | null): string {
  if (!source) return ''
  try {
    const binary = atob(source)
    const bytes = Uint8Array.from(binary, (ch) => ch.charCodeAt(0))
    return new TextDecoder().decode(bytes)
  } catch (err) {
    console.error('Failed to decode mermaid source:', err)
    return ''
  }
}

function renderMermaidErrorHtml(title: string): string {
  const safeTitle = md.utils.escapeHtml(title)
  return `<div class="mermaid-error">
    <span class="mermaid-error-icon" aria-hidden="true">⚠️</span>
    <span class="mermaid-error-title">${safeTitle}</span>
  </div>`
}

function cleanupMermaidStrayNodes(id?: string, owner?: HTMLElement) {
  // Mermaid occasionally attaches measurement artefacts directly to
  // <body> under ids derived from the one we passed in. On syntax
  // errors it can also leave a wrapper <div> containing the visible red
  // error SVG. Remove the wrapper too so the failure stays inside the
  // markdown renderer's own mermaid block.
  const selectors = id
    ? [`#${id}`, `#d${id}`, `[id^="${id}-"]`]
    : ['svg[id^="mermaid-svg-"]']
  for (const sel of selectors) {
    document.querySelectorAll(sel).forEach((node) => {
      if (owner?.contains(node)) return
      if ((node as Element).closest('.markdown-body, .mermaid-lightbox-overlay, #app')) return

      let removable: Element = node
      while (removable.parentElement && removable.parentElement !== document.body) {
        removable = removable.parentElement
      }

      if (removable.parentElement === document.body) {
        removable.remove()
      }
    })
  }
}

// Convert SVG string to PNG data URL (4K Ultra HD quality)
async function svgToPng(svgString: string, backgroundColor: string = '#ffffff'): Promise<string> {
  return new Promise((resolve, reject) => {
    // Parse SVG to get intrinsic dimensions from viewBox or width/height attributes
    const parser = new DOMParser()
    const svgDoc = parser.parseFromString(svgString, 'image/svg+xml')
    const svgEl = svgDoc.querySelector('svg')
    
    if (!svgEl) {
      reject(new Error('Invalid SVG'))
      return
    }
    
    // Determine the base dimensions from viewBox or explicit width/height
    let baseWidth = 800
    let baseHeight = 600
    
    const viewBox = svgEl.getAttribute('viewBox')
    if (viewBox) {
      const parts = viewBox.split(/[\s,]+/).map(Number)
      if (parts.length === 4 && parts[2] > 0 && parts[3] > 0) {
        baseWidth = parts[2]
        baseHeight = parts[3]
      }
    }
    
    // Check explicit width/height (may override viewBox)
    const widthAttr = svgEl.getAttribute('width')
    const heightAttr = svgEl.getAttribute('height')
    if (widthAttr && heightAttr) {
      const w = parseFloat(widthAttr)
      const h = parseFloat(heightAttr)
      if (w > 0 && h > 0) {
        baseWidth = w
        baseHeight = h
      }
    }
    
    // Force set explicit width/height on SVG for correct rendering
    const scale = 4  // 4x for 4K Ultra HD quality
    const renderWidth = baseWidth * scale
    const renderHeight = baseHeight * scale
    
    // Clone SVG and set explicit dimensions for high-res rendering
    svgEl.setAttribute('width', String(renderWidth))
    svgEl.setAttribute('height', String(renderHeight))
    
    const serializer = new XMLSerializer()
    const modifiedSvg = serializer.serializeToString(svgEl)
    
    const svgBlob = new Blob([modifiedSvg], { type: 'image/svg+xml;charset=utf-8' })
    const url = URL.createObjectURL(svgBlob)
    const img = new Image()
    img.onload = () => {
      const canvas = document.createElement('canvas')
      canvas.width = renderWidth
      canvas.height = renderHeight
      const ctx = canvas.getContext('2d')
      if (!ctx) {
        reject(new Error('Failed to get canvas context'))
        return
      }
      // Enable high-quality rendering
      ctx.imageSmoothingEnabled = true
      ctx.imageSmoothingQuality = 'high'
      // Fill white background
      ctx.fillStyle = backgroundColor
      ctx.fillRect(0, 0, canvas.width, canvas.height)
      // Draw SVG at full resolution
      ctx.drawImage(img, 0, 0, renderWidth, renderHeight)
      URL.revokeObjectURL(url)
      resolve(canvas.toDataURL('image/png', 1.0))
    }
    img.onerror = (err) => {
      URL.revokeObjectURL(url)
      reject(err)
    }
    img.src = url
  })
}

// Mermaid lightbox functionality
async function openMermaidLightbox(svg: string) {
  lightboxSvg.value = svg
  lightboxZoom.value = 1.0
  imagePosition.value = { x: 0, y: 0 }
  lightboxSvgStyle.value = { transform: 'scale(1) translate(0px, 0px)', transformOrigin: 'center center' }
  showLightbox.value = true
  document.body.style.overflow = 'hidden' // Prevent background scrolling
  
  // Convert SVG to PNG
  try {
    const pngUrl = await svgToPng(svg, resolvedTheme.value === 'dark' ? '#111827' : '#ffffff')
    lightboxPngUrl.value = pngUrl
  } catch (err) {
    console.error('Failed to convert SVG to PNG:', err)
    lightboxPngUrl.value = ''
  }
  
  // Bind wheel event after DOM update
  nextTick(() => {
    const container = document.querySelector('.mermaid-lightbox-svg-container') as HTMLElement
    if (container) {
      lightboxSvgEl = container
      container.addEventListener('wheel', handleLightboxWheel, { passive: false })
    }
  })
}

function closeMermaidLightbox() {
  showLightbox.value = false
  document.body.style.overflow = ''
  lightboxPngUrl.value = ''
  if (lightboxSvgEl) {
    lightboxSvgEl.removeEventListener('wheel', handleLightboxWheel)
    lightboxSvgEl = null
  }
}

// Mouse drag handlers
function handleMouseDown(e: MouseEvent) {
  if (e.button !== 0) return // Only left mouse button
  isDragging.value = true
  dragStartX.value = e.clientX - imagePosition.value.x
  dragStartY.value = e.clientY - imagePosition.value.y
}

function handleMouseMove(e: MouseEvent) {
  if (!isDragging.value) return
  e.preventDefault()
  const newX = e.clientX - dragStartX.value
  const newY = e.clientY - dragStartY.value
  imagePosition.value = { x: newX, y: newY }
  lightboxSvgStyle.value = {
    transform: `scale(${lightboxZoom.value}) translate(${newX}px, ${newY}px)`,
    transformOrigin: 'center center'
  }
}

function handleMouseUp() {
  isDragging.value = false
}

function handleLightboxWheel(e: WheelEvent) {
  e.preventDefault()
  const delta = e.deltaY > 0 ? -0.1 : 0.1
  lightboxZoom.value = Math.min(5.0, Math.max(0.1, lightboxZoom.value + delta))
  lightboxSvgStyle.value = {
    transform: `scale(${lightboxZoom.value}) translate(${imagePosition.value.x}px, ${imagePosition.value.y}px)`,
    transformOrigin: 'center center'
  }
}

function resetLightboxZoom() {
  lightboxZoom.value = 1.0
  imagePosition.value = { x: 0, y: 0 }
  lightboxSvgStyle.value = { 
    transform: 'scale(1) translate(0px, 0px)', 
    transformOrigin: 'center center' 
  }
}

function downloadPng() {
  const pngUrl = lightboxPngUrl.value
  if (!pngUrl) return
  
  const a = document.createElement('a')
  a.href = pngUrl
  a.download = `mermaid-diagram-${Date.now()}.png`
  document.body.appendChild(a)
  a.click()
  document.body.removeChild(a)
}

function downloadSvg() {
  const svgContent = lightboxSvg.value
  if (!svgContent) return
  
  // Create a Blob from SVG string
  const blob = new Blob([svgContent], { type: 'image/svg+xml' })
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = `mermaid-diagram-${Date.now()}.svg`
  document.body.appendChild(a)
  a.click()
  document.body.removeChild(a)
  URL.revokeObjectURL(url)
}

let themeObserver: MutationObserver | null = null
let mediaQuery: MediaQueryList | null = null

function bumpRenderEpoch() {
  renderEpoch.value += 1
}

function handleThemeChange() {
  const previous = resolvedTheme.value
  syncResolvedTheme()
  if (resolvedTheme.value !== previous) bumpRenderEpoch()
}

watch(
  () => props.theme,
  handleThemeChange,
  { immediate: true },
)

watch(
  () => props.locale,
  bumpRenderEpoch,
)

// Clean up event listeners on unmount
onBeforeUnmount(() => {
  if (lightboxSvgEl) {
    lightboxSvgEl.removeEventListener('wheel', handleLightboxWheel)
  }
  themeObserver?.disconnect()
  if (mediaQuery) {
    mediaQuery.removeEventListener?.('change', handleThemeChange)
  }
  document.body.style.overflow = ''
})

// Bind copy buttons for code blocks
function bindCopyButtons() {
  const container = markdownBodyRef.value
  if (!container) return
  const buttons = container.querySelectorAll<HTMLButtonElement>('.code-copy')
  buttons.forEach((btn) => {
    // Avoid re-binding
    if (btn.getAttribute('data-bound')) return
    btn.setAttribute('data-bound', 'true')
    btn.addEventListener('click', async () => {
      const pre = btn.closest('pre')
      if (!pre) return
      const code = pre.querySelector('code')
      if (!code) return
      try {
        await navigator.clipboard.writeText(code.textContent || '')
        const originalTitle = btn.title
        const originalLabel = btn.getAttribute('aria-label') || labels.value.copyCode
        btn.title = labels.value.copied
        btn.setAttribute('aria-label', labels.value.copied)
        btn.classList.add('copied')
        setTimeout(() => {
          btn.title = originalTitle
          btn.setAttribute('aria-label', originalLabel)
          btn.classList.remove('copied')
        }, 2000)
      } catch (err) {
        console.error('Copy failed:', err)
      }
    })
  })
}

onMounted(() => {
  syncResolvedTheme()
  if (typeof document !== 'undefined') {
    themeObserver = new MutationObserver(handleThemeChange)
    themeObserver.observe(document.documentElement, {
      attributes: true,
      attributeFilter: ['data-theme'],
    })
  }
  if (typeof window !== 'undefined') {
    mediaQuery = window.matchMedia?.('(prefers-color-scheme: dark)') ?? null
    mediaQuery?.addEventListener?.('change', handleThemeChange)
  }
  nextTick(() => {
    renderMermaidBlocks()
    bindCopyButtons()
  })
})

watch(
  [renderedContent, resolvedTheme, labels],
  () => {
    nextTick(() => {
      renderMermaidBlocks()
      bindCopyButtons()
    })
  },
)
</script>

<template>
 <div
   :key="renderKey"
   ref="markdownBodyRef"
   class="markdown-body"
   :data-ksf-theme="resolvedTheme"
   v-html="renderedContent"
 ></div>
  
  <!-- Mermaid Lightbox Modal -->
  <Teleport to="body">
    <div v-if="showLightbox" class="mermaid-lightbox-overlay" @click.self="closeMermaidLightbox">
      <div class="mermaid-lightbox-toolbar">
        <span class="mermaid-lightbox-zoom">{{ Math.round(lightboxZoom * 100) }}%</span>
        <button class="mermaid-lightbox-btn" @click="resetLightboxZoom" :title="labels.mermaidResetTitle">{{ labels.mermaidReset }}</button>
        <button class="mermaid-lightbox-btn" @click="downloadPng" :title="labels.downloadPng">PNG</button>
        <button class="mermaid-lightbox-btn" @click="downloadSvg" :title="labels.downloadSvg">SVG</button>
        <button class="mermaid-lightbox-close" @click="closeMermaidLightbox" :title="labels.close">✕</button>
      </div>
      <div 
        class="mermaid-lightbox-content"
        @mousedown="handleMouseDown"
        @mousemove="handleMouseMove"
        @mouseup="handleMouseUp"
        @mouseleave="handleMouseUp"
      >
        <div 
          class="mermaid-lightbox-svg-container" 
          :style="lightboxSvgStyle"
          v-if="lightboxPngUrl"
        >
          <img 
            :src="lightboxPngUrl" 
            :alt="labels.mermaidImageAlt" 
            style="display: block;"
            @dragstart.prevent
          />
        </div>
        <!-- Fallback to SVG if PNG conversion fails -->
        <div 
          class="mermaid-lightbox-svg-container" 
          :style="lightboxSvgStyle" 
          v-html="lightboxSvg"
          v-else
        ></div>
      </div>
      <div class="mermaid-lightbox-hint">{{ labels.mermaidZoomHint }}</div>
    </div>
  </Teleport>
</template>

<style>
/* Markdown body styles */
.markdown-body {
  color: var(--dk-md-text, var(--text-primary));
  line-height: 1.8;
  font-size: 15px;
  max-width: 860px;
  margin: 0 auto;
  word-wrap: break-word;
}

.markdown-body h1 {
  font-size: 28px;
  font-weight: 700;
  margin: 40px 0 20px;
  padding-bottom: 10px;
  border-bottom: 2px solid var(--dk-md-border, var(--border-color));
  letter-spacing: 0;
  position: relative;
}

.markdown-body h2 {
  font-size: 22px;
  font-weight: 600;
  margin: 32px 0 16px;
  padding-bottom: 8px;
  border-bottom: 1px dashed var(--dk-md-border, var(--border-color));
  position: relative;
}

.markdown-body h3 {
  font-size: 18px;
  font-weight: 600;
  margin: 24px 0 12px;
  position: relative;
}

.markdown-body h4 {
  font-size: 16px;
  font-weight: 600;
  margin: 20px 0 10px;
  position: relative;
}

.markdown-body h5 {
  font-size: 15px;
  font-weight: 600;
  margin: 16px 0 8px;
  position: relative;
}

.markdown-body h6 {
  font-size: 14px;
  font-weight: 600;
  margin: 14px 0 6px;
  color: var(--dk-md-text-muted, var(--text-secondary));
  position: relative;
}

.markdown-body p {
  margin-bottom: 16px;
  line-height: 1.8;
}

.markdown-body ul,
.markdown-body ol {
  margin-bottom: 16px;
  padding-left: 28px;
}

.markdown-body ul {
  list-style: disc;
}

.markdown-body ul ul {
  list-style: circle;
}

.markdown-body ul ul ul {
  list-style: square;
}

.markdown-body ol {
  list-style: decimal;
}

.markdown-body li {
  margin-bottom: 6px;
  line-height: 1.7;
}

.markdown-body li > p {
  margin-bottom: 8px;
}

.markdown-body a {
  color: var(--dk-md-link, var(--color-primary));
  text-decoration: none;
  border-bottom: 1px solid transparent;
  transition: border-color 0.2s ease, color 0.2s ease;
}

.markdown-body a:hover {
  color: var(--dk-md-link-hover, #2563eb);
  border-bottom-color: var(--dk-md-link-hover, #2563eb);
}

.markdown-body blockquote {
  border-left: 4px solid var(--dk-md-link, var(--color-primary, #3b82f6));
  padding: 12px 20px;
  margin: 20px 0;
  background-color: var(--dk-md-blockquote-bg, #f0f7ff);
  color: var(--dk-md-text-muted, var(--text-secondary));
  font-style: italic;
  border-radius: 0 6px 6px 0;
}

.markdown-body blockquote blockquote {
  border-left-color: var(--dk-md-border-strong, #94a3b8);
  background-color: var(--dk-md-surface-muted, #f1f5f9);
  margin: 12px 0;
}

.markdown-body blockquote blockquote blockquote {
  border-left-color: var(--dk-md-border, #cbd5e1);
  background-color: var(--dk-md-surface-soft, #f8fafc);
}

.markdown-body blockquote p:last-child {
  margin-bottom: 0;
}

/* Obsidian-style callouts: tinted background plus a colored type bar on the left */
.markdown-body .md-callout {
  padding: 16px 20px;
  margin: 20px 0;
  background-color: var(--dk-md-blockquote-bg, #f0f7ff);
  border-radius: 4px;
  border-left: 4px solid var(--dk-md-link, var(--color-primary, #3b82f6));
  color: var(--dk-md-text, var(--text-primary));
}

.markdown-body .md-callout > .md-callout-title {
  margin: 0 0 10px;
  font-weight: 600;
  font-size: 15px;
}

.markdown-body .md-callout > .md-callout-title:only-child {
  margin-bottom: 0;
}

.markdown-body .md-callout > p:last-child {
  margin-bottom: 0;
}

.markdown-body .md-callout-tip,
.markdown-body .md-callout-important {
  border-left-color: #0ea5e9;
}

.markdown-body .md-callout-tip > .md-callout-title,
.markdown-body .md-callout-important > .md-callout-title {
  color: #0284c7;
}

.markdown-body .md-callout-note,
.markdown-body .md-callout-info {
  border-left-color: #3b82f6;
}

.markdown-body .md-callout-note > .md-callout-title,
.markdown-body .md-callout-info > .md-callout-title {
  color: #2563eb;
}

.markdown-body .md-callout-abstract,
.markdown-body .md-callout-example {
  border-left-color: #14b8a6;
}

.markdown-body .md-callout-abstract > .md-callout-title,
.markdown-body .md-callout-example > .md-callout-title {
  color: #0d9488;
}

.markdown-body .md-callout-success {
  border-left-color: #22c55e;
}

.markdown-body .md-callout-success > .md-callout-title {
  color: #16a34a;
}

.markdown-body .md-callout-question {
  border-left-color: #a855f7;
}

.markdown-body .md-callout-question > .md-callout-title {
  color: #9333ea;
}

.markdown-body .md-callout-warning,
.markdown-body .md-callout-failure {
  border-left-color: #f59e0b;
}

.markdown-body .md-callout-warning > .md-callout-title,
.markdown-body .md-callout-failure > .md-callout-title {
  color: #d97706;
}

.markdown-body .md-callout-danger,
.markdown-body .md-callout-bug {
  border-left-color: #ef4444;
}

.markdown-body .md-callout-danger > .md-callout-title,
.markdown-body .md-callout-bug > .md-callout-title {
  color: #dc2626;
}

.markdown-body .md-callout-quote {
  border-left-color: var(--dk-md-border-strong, #94a3b8);
}

.markdown-body .md-callout-quote > .md-callout-title {
  color: var(--dk-md-text-muted, var(--text-secondary));
}

.markdown-body code:not(.hljs code) {
  background-color: var(--dk-md-inline-code-bg, #f1f5f9);
  color: var(--dk-md-inline-code-text, #e11d48);
  padding: 2px 6px;
  border-radius: 4px;
  font-size: 0.9em;
  font-family: 'Consolas', 'Monaco', 'Courier New', monospace;
  border: 1px solid var(--dk-md-inline-code-border, #e2e8f0);
}

/* Image styles */
.markdown-body img {
  max-width: 100%;
  height: auto;
  display: block;
  margin: 20px auto;
  border-radius: 8px;
  box-shadow: var(--dk-md-shadow-soft, 0 2px 12px rgba(0, 0, 0, 0.08));
}

/* Horizontal rule styles */
.markdown-body hr {
  border: none;
  height: 1px;
  background: linear-gradient(to right, transparent, var(--dk-md-border, var(--border-color, #d1d5db)), transparent);
  margin: 32px 0;
}

.markdown-body pre {
  margin: 16px 0;
  border-radius: 8px;
  overflow: hidden;
  position: relative;
}

/* Code block toolbar */
.markdown-body .code-toolbar {
  position: absolute;
  top: 8px;
  left: 12px;
  right: 12px;
  z-index: 1;
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 12px;
  padding: 0;
  background: transparent;
  min-height: 16px;
  pointer-events: none;
}

.markdown-body .code-lang {
  font-size: 11px;
  line-height: 1;
  color: var(--dk-md-code-toolbar, #a8b3c5);
  text-transform: uppercase;
  font-weight: 600;
  letter-spacing: 0;
  font-family: 'Consolas', 'Monaco', 'Courier New', monospace;
}

.markdown-body .code-copy {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 16px;
  height: 16px;
  color: var(--dk-md-code-toolbar, #a8b3c5);
  background: transparent;
  border: 0;
  border-radius: 0;
  padding: 0;
  cursor: pointer;
  transition: color 0.15s ease;
  font-family: system-ui, -apple-system, sans-serif;
  pointer-events: auto;
}

.markdown-body .code-copy svg {
  width: 14px;
  height: 14px;
  fill: none;
  stroke: currentColor;
  stroke-width: 2;
  stroke-linecap: round;
  stroke-linejoin: round;
}

.markdown-body .code-copy:hover {
  color: #f8fafc;
}

.markdown-body .code-copy.copied {
  color: #86efac;
}

/* Highlight.js theme */
.hljs {
  background-color: var(--dk-md-code-bg, #1e293b);
  color: var(--dk-md-code-text, #e2e8f0);
  padding: 32px 16px 16px;
  overflow-x: auto;
  font-family: 'Consolas', 'Monaco', 'Courier New', monospace;
  font-size: 13px;
  line-height: 1.6;
}

.hljs-comment,
.hljs-quote {
  color: #94a3b8;
}

.hljs-keyword,
.hljs-selector-tag,
.hljs-addition {
  color: #f87171;
}

.hljs-number,
.hljs-string,
.hljs-meta .hljs-meta-string,
.hljs-literal,
.hljs-doctag,
.hljs-regexp {
  color: #4ade80;
}

.hljs-title,
.hljs-section,
.hljs-name,
.hljs-selector-id,
.hljs-selector-class {
  color: #60a5fa;
}

.hljs-attribute,
.hljs-attr,
.hljs-variable,
.hljs-template-variable,
.hljs-class .hljs-title,
.hljs-type {
  color: #facc15;
}

.hljs-symbol,
.hljs-bullet,
.hljs-subst,
.hljs-meta,
.hljs-meta .hljs-keyword,
.hljs-selector-attr,
.hljs-selector-pseudo,
.hljs-link {
  color: #22d3ee;
}

.hljs-built_in,
.hljs-deletion {
  color: #fb923c;
}

.hljs-formula {
  color: #cbd5e1;
}

.hljs-emphasis {
  font-style: italic;
}

.hljs-strong {
  font-weight: bold;
}

/* Table styles */
.markdown-body table {
  width: 100%;
  border-collapse: collapse;
  margin: 20px 0;
  border-radius: 8px;
  overflow: hidden;
  border: 1px solid var(--dk-md-border, var(--border-color));
}

.markdown-body th,
.markdown-body td {
  border: 1px solid var(--dk-md-border, var(--border-color));
  padding: 10px 16px;
  text-align: left;
  font-size: 14px;
}

.markdown-body th {
  background-color: var(--dk-md-table-head-bg, #f1f5f9);
  font-weight: 600;
  color: var(--dk-md-text-strong, var(--text-primary));
}

.markdown-body tbody tr:nth-child(even) {
  background-color: var(--dk-md-table-row-alt, #f8fafc);
}

.markdown-body tbody tr:hover {
  background-color: var(--dk-md-table-row-hover, #e8f0fe);
  transition: background-color 0.15s ease;
}

/* Anchor link styles */
.markdown-body .header-anchor {
  color: var(--dk-md-text-muted, var(--text-secondary));
  text-decoration: none;
  opacity: 0;
  visibility: hidden;
  transition: opacity 0.2s, visibility 0.2s;
  position: absolute;
  left: -20px;
  padding-right: 4px;
}

.markdown-body .header-anchor:hover {
  color: var(--dk-md-link, var(--color-primary));
}

.markdown-body h1:hover .header-anchor,
.markdown-body h2:hover .header-anchor,
.markdown-body h3:hover .header-anchor,
.markdown-body h4:hover .header-anchor,
.markdown-body h5:hover .header-anchor,
.markdown-body h6:hover .header-anchor {
  opacity: 1;
  visibility: visible;
}

/* Task list (checkbox) styles */
.markdown-body .task-list-item {
  list-style: none;
  position: relative;
  padding-left: 6px;
  margin-bottom: 8px;
}

.markdown-body .task-list-item-checkbox {
  appearance: none;
  -webkit-appearance: none;
  width: 18px;
  height: 18px;
  border: 2px solid var(--dk-md-border, var(--border-color, #d1d5db));
  border-radius: 4px;
  background-color: transparent;
  cursor: default;
  position: relative;
  top: 3px;
  margin-right: 8px;
  transition: all 0.2s ease;
  pointer-events: none;
}

.markdown-body .task-list-item-checkbox:checked {
  background-color: var(--dk-md-link, var(--color-primary, #3b82f6));
  border-color: var(--dk-md-link, var(--color-primary, #3b82f6));
}

.markdown-body .task-list-item-checkbox:checked::after {
  content: '';
  position: absolute;
  left: 4px;
  top: 1px;
  width: 6px;
  height: 10px;
  border: solid white;
  border-width: 0 2px 2px 0;
  transform: rotate(45deg);
}

.markdown-body .task-list-item-checkbox:checked + .task-list-item-label {
  text-decoration: line-through;
  color: var(--dk-md-text-muted, var(--text-secondary, #94a3b8));
}

.markdown-body ul.contains-task-list {
  padding-left: 8px;
}

.markdown-body ul.contains-task-list ul.contains-task-list {
  padding-left: 24px;
}

/* Mermaid diagram styles */
.markdown-body .mermaid-block {
  margin: 20px 0;
  padding: 20px;
  background-color: var(--dk-md-surface-soft, var(--section-bg, #f8fafc));
  border: 1px solid var(--dk-md-border, var(--border-color, #e2e8f0));
  border-radius: 8px;
  overflow: auto;
  cursor: pointer;
  transition: box-shadow 0.2s ease, border-color 0.2s ease;
  display: flex;
  justify-content: center;
  align-items: center;
  min-height: 100px;
}

.markdown-body .mermaid-block:hover {
  box-shadow: 0 4px 16px rgba(0, 0, 0, 0.1);
  border-color: var(--dk-md-link, var(--color-primary, #3b82f6));
}

.markdown-body .mermaid-block.mermaid-rendered {
  background-color: transparent;
  border-color: var(--dk-md-border, var(--border-color, #e2e8f0));
  display: block;
  text-align: center;
}

.markdown-body .mermaid-block.mermaid-rendered:hover {
  border-color: var(--dk-md-link, var(--color-primary, #3b82f6));
}

.markdown-body .mermaid-block svg {
  height: auto;
  max-width: none;
}

/* Mermaid Lightbox Overlay */
.mermaid-lightbox-overlay {
  position: fixed;
  top: 0;
  left: 0;
  width: 100vw;
  height: 100vh;
  background-color: rgba(0, 0, 0, 0.85);
  z-index: 9999;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  animation: lightboxFadeIn 0.2s ease;
}

@keyframes lightboxFadeIn {
  from { opacity: 0; }
  to { opacity: 1; }
}

/* Lightbox Toolbar */
.mermaid-lightbox-toolbar {
  position: absolute;
  top: 0;
  left: 0;
  right: 0;
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 10px;
  padding: 12px 20px;
  background: linear-gradient(to bottom, rgba(0,0,0,0.5), transparent);
  z-index: 10;
}

.mermaid-lightbox-zoom {
  color: white;
  font-size: 14px;
  font-weight: 500;
  min-width: 50px;
  text-align: center;
}

.mermaid-lightbox-btn {
  padding: 6px 16px;
  border: 1px solid rgba(255, 255, 255, 0.3);
  border-radius: 6px;
  background-color: rgba(255, 255, 255, 0.1);
  color: white;
  font-size: 14px;
  cursor: pointer;
  transition: all 0.15s ease;
  backdrop-filter: blur(10px);
}

.mermaid-lightbox-btn:hover {
  background-color: rgba(255, 255, 255, 0.2);
  border-color: rgba(255, 255, 255, 0.5);
}

.mermaid-lightbox-close {
  width: 36px;
  height: 36px;
  border: 1px solid rgba(255, 255, 255, 0.3);
  border-radius: 50%;
  background-color: rgba(255, 255, 255, 0.1);
  color: white;
  font-size: 18px;
  cursor: pointer;
  display: flex;
  align-items: center;
  justify-content: center;
  transition: all 0.15s ease;
  backdrop-filter: blur(10px);
}

.mermaid-lightbox-close:hover {
  background-color: rgba(255, 255, 255, 0.25);
  border-color: rgba(255, 255, 255, 0.5);
}

/* Lightbox Content */
.mermaid-lightbox-content {
  flex:1;
  display: flex;
  align-items: center;
  justify-content: center;
  width: 100%;
  height: 100%;
  overflow: hidden;
  padding: 60px 20px 40px;
}

.mermaid-lightbox-svg-container {
  transition: transform 0.1s ease-out;
  display: flex;
  justify-content: center;
  align-items: center;
  transform-origin: center center;
  cursor: grab;
}

.mermaid-lightbox-svg-container:active {
  cursor: grabbing;
}

.mermaid-lightbox-svg-container svg {
  max-width: 90vw;
  max-height: 85vh;
  width: auto;
  height: auto;
}

.mermaid-lightbox-svg-container img {
  max-width: 90vw;
  max-height: 85vh;
  width: auto;
  height: auto;
  object-fit: contain;
}

/* Lightbox Hint */
.mermaid-lightbox-hint {
  position: absolute;
  bottom: 20px;
  left: 50%;
  transform: translateX(-50%);
  color: rgba(255, 255, 255, 0.5);
  font-size: 13px;
  padding: 6px 16px;
  background-color: rgba(0, 0, 0, 0.3);
  border-radius: 20px;
  pointer-events: none;
  animation: hintFade 3s ease 2s forwards;
}

@keyframes hintFade {
  to { opacity: 0; }
}

.markdown-body .mermaid-loading {
  color: var(--dk-md-text-muted, var(--text-secondary, #94a3b8));
  font-size: 13px;
  padding: 24px 0;
  animation: mermaidPulse 1.5s ease-in-out infinite;
}

@keyframes mermaidPulse {
  0%, 100% { opacity: 0.5; }
  50% { opacity: 1; }
}

.markdown-body .mermaid-error-block {
  border-color: var(--dk-md-error-border, #fca5a5) !important;
  background-color: var(--dk-md-error-bg, #fef2f2) !important;
  text-align: left;
  justify-content: flex-start;
  align-items: center;
  min-height: 0;
  padding: 12px 16px;
}

.markdown-body .mermaid-error {
  display: flex;
  align-items: center;
  justify-content: flex-start;
  gap: 8px;
}

.markdown-body .mermaid-error-icon {
  flex-shrink: 0;
  font-size: 15px;
  line-height: 1;
}

.markdown-body .mermaid-error-title {
  color: var(--dk-md-error-text, #dc2626);
  font-weight: 600;
  font-size: 14px;
  line-height: 1.4;
  margin: 0;
}

.markdown-body .mermaid-error-message {
  color: var(--dk-md-error-text, #7f1d1d);
  font-size: 12px;
  line-height: 1.5;
  margin-bottom: 12px;
  white-space: pre-wrap;
  word-break: break-word;
}

.markdown-body .mermaid-error-source {
  margin: 0;
  background-color: var(--dk-md-code-bg, #1e293b);
  border-radius: 6px;
  overflow-x: auto;
}

.markdown-body .mermaid-error-source code {
  display: block;
  padding: 12px 16px;
  color: var(--dk-md-code-text, #e2e8f0);
  font-size: 12px;
  font-family: 'Consolas', 'Monaco', 'Courier New', monospace;
  line-height: 1.5;
  white-space: pre;
}
</style>
