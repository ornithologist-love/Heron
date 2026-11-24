import { createRouter, createWebHashHistory } from 'vue-router'
import AutomationView from '@/views/AutomationView.vue'

export default createRouter({
  history: createWebHashHistory(),
  routes: [
    { path: '/', name: 'automation', component: AutomationView },
    { path: '/:pathMatch(.*)*', redirect: '/' },
  ],
})
