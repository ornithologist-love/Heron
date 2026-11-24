import { defineStore } from 'pinia'

export const useSiteAccessStore = defineStore('siteAccess', () => ({
  userName: 'Heron Demo',
  isAdmin: true,
  canOpen: () => true,
}))
