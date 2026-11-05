import { createApp } from 'vue'
import { createRouter, createWebHistory } from 'vue-router'
import App from './App.vue'
import ReviewView from './views/ReviewView.vue'
import LiveView from './views/LiveView.vue'
import ChatsView from './views/ChatsView.vue'
import SettingsView from './views/SettingsView.vue'
import './style.css'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', component: ReviewView, meta: { title: 'Te beoordelen' } },
    { path: '/live', component: LiveView, meta: { title: 'Live op schermen' } },
    { path: '/chats', component: ChatsView, meta: { title: 'Chats' } },
    { path: '/instellingen', component: SettingsView, meta: { title: 'Instellingen' } }
  ]
})

router.afterEach((to) => {
  document.title = `${to.meta.title ?? ''} – Schermpostvak`
})

createApp(App).use(router).mount('#app')
