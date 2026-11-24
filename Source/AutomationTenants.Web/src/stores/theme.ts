import { defineStore } from 'pinia'
import type { KsfThemeMode } from '../components/i18n'

const THEME_STORAGE_KEY = 'devkit-theme'

function isValidMode(value: string | null): value is KsfThemeMode {
  return value === 'light' || value === 'dark' || value === 'auto'
}

export const useThemeStore = defineStore('theme', {
  state: () => ({ mode: 'light' as KsfThemeMode }),
  getters: {
    resolved(state): 'light' | 'dark' {
      if (state.mode === 'auto') {
        return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'
      }
      return state.mode
    },
    isEmbedded(): boolean {
      return new URLSearchParams(window.location.search).get('embedded') === '1'
    },
  },
  actions: {
    init() {
      const urlTheme = new URLSearchParams(window.location.search).get('theme')
      const stored = this.isEmbedded ? null : localStorage.getItem(THEME_STORAGE_KEY)
      if (urlTheme === 'dark' || urlTheme === 'light') {
        this.mode = urlTheme
      } else if (isValidMode(stored)) {
        this.mode = stored
      } else {
        this.mode = 'light'
      }
      this.apply()

      if (!this.isEmbedded) {
        const media = window.matchMedia('(prefers-color-scheme: dark)')
        media.addEventListener('change', () => {
          if (this.mode === 'auto') this.apply()
        })
      }
      // Embedded pages also follow host postMessage (setMode does not persist to localStorage when embedded)
      window.addEventListener('message', (event: MessageEvent) => {
        if (typeof event.data === 'string' && event.data.startsWith('devkit-theme:')) {
          const value = event.data.slice('devkit-theme:'.length)
          if (value === 'dark' || value === 'light') this.setMode(value)
        }
      })
    },
    setMode(m: KsfThemeMode) {
      this.mode = m
      if (!this.isEmbedded) localStorage.setItem(THEME_STORAGE_KEY, m)
      this.apply()
    },
    toggle() {
      this.setMode(this.resolved === 'dark' ? 'light' : 'dark')
    },
    apply() {
      document.documentElement.dataset.theme = this.resolved
      document.documentElement.setAttribute('theme-mode', this.resolved)
    },
  },
})
