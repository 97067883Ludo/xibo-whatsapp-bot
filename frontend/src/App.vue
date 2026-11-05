<script setup lang="ts">
import { computed, onMounted, onUnmounted } from 'vue'
import { connectLive, refreshStatus, status } from './api'

const connected = computed(() => status.value?.whatsApp === 'WORKING')
const chatsLabel = computed(() => {
  const n = status.value?.enabledChats ?? 0
  return n === 1 ? '1 chat actief' : `${n} chats actief`
})

let timer: number | undefined
onMounted(() => {
  refreshStatus()
  connectLive()
  timer = window.setInterval(refreshStatus, 60000) // fallback; live updates do the rest
})
onUnmounted(() => window.clearInterval(timer))
</script>

<template>
  <header class="topbar">
    <div class="brand">
      <div class="brand-mark">
        <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="#fff" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><rect x="3" y="4" width="18" height="12" rx="2" /><path d="M8 20h8M12 16v4" /></svg>
      </div>
      <div style="display: flex; flex-direction: column">
        <span class="brand-name">Schermpostvak</span>
        <span class="brand-sub">WhatsApp · {{ chatsLabel }}</span>
      </div>
    </div>
    <span class="pill" :class="connected ? 'ok' : 'bad'" role="status">
      <span class="dot" />{{ connected ? 'WhatsApp verbonden' : 'WhatsApp niet verbonden' }}
    </span>
  </header>

  <nav class="tabs" aria-label="Onderdelen">
    <RouterLink to="/" class="tab">Te beoordelen <span class="count">{{ status?.pending ?? 0 }}</span><span v-if="status?.failedDownloads" class="count failed">{{ status.failedDownloads }} mislukt</span></RouterLink>
    <RouterLink to="/live" class="tab">Live op schermen <span class="count">{{ status?.live ?? 0 }}</span><span v-if="status?.failedPlacements" class="count failed">{{ status.failedPlacements }} mislukt</span></RouterLink>
    <RouterLink to="/chats" class="tab">
      Chats <span v-if="status?.newChats" class="count">{{ status.newChats }} nieuw</span>
    </RouterLink>
    <RouterLink to="/instellingen" class="tab">Instellingen</RouterLink>
  </nav>

  <RouterView />
</template>
