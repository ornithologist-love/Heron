<script setup lang="ts">
import { useThemeStore } from '@/stores/theme'

// Applies both `data-theme` (console tokens) and `theme-mode` (TDesign) before the first paint.
useThemeStore().init()
</script>

<template>
  <router-view />
</template>
