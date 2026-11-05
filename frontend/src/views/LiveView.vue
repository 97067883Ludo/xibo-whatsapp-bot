<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue'
import { api, formatDateTime, onLiveUpdate, refreshStatus, type Item } from '../api'

const items = ref<Item[]>([])
const loading = ref(true)
const toast = ref<{ text: string; error?: boolean } | null>(null)
const removing = ref<string | null>(null)
const retrying = ref<string | null>(null)
let timer: number | undefined
const stopLive = onLiveUpdate(['live'], load)

function showToast(text: string, error = false) {
  toast.value = { text, error }
  window.setTimeout(() => (toast.value = null), 4000)
}

async function load() {
  try { items.value = await api.live() } catch (e) { showToast((e as Error).message, true) }
  finally { loading.value = false }
}

function scheduled(i: Item) {
  return i.status === 'Approved' && !!i.showFrom && new Date(i.showFrom).getTime() > Date.now()
}

function endsSoon(i: Item) {
  return !!i.showUntil && new Date(i.showUntil).getTime() - Date.now() < 24 * 3600 * 1000
}

async function retry(i: Item) {
  retrying.value = i.id
  try {
    await api.retry(i.id)
    i.status = 'Approved'
    i.error = null
    showToast(`‘${i.caption || 'Item'}’ wordt opnieuw geplaatst.`)
    refreshStatus()
  } catch (e) {
    showToast((e as Error).message, true)
  } finally {
    retrying.value = null
  }
}

async function remove(i: Item) {
  removing.value = i.id
  try {
    await api.remove(i.id)
    items.value = items.value.filter((x) => x.id !== i.id)
    showToast(`‘${i.caption || 'Item'}’ is van de schermen gehaald.`)
    refreshStatus()
  } catch (e) {
    showToast((e as Error).message, true)
  } finally {
    removing.value = null
  }
}

// Refresh while items are being placed, so the result shows up without reloading the page
onMounted(() => {
  load()
  timer = window.setInterval(() => {
    if (items.value.some((i) => i.status === 'Approved' && !scheduled(i))) { load(); refreshStatus() }
  }, 5000)
})
onUnmounted(() => { window.clearInterval(timer); stopLive() })
</script>

<template>
  <main class="page">
    <div class="page-head">
      <div>
        <h1>Live op schermen</h1>
        <p>Goedgekeurde items. Ze krijgen in Xibo de tag ‘onscreen’ en verschijnen zo op alle schermen; na ‘Te zien tot’ verwijdert Xibo ze zelf. Een verwijderd item verdwijnt bij de volgende synchronisatie van de schermen. Lukt plaatsen niet, dan kun je het hier opnieuw proberen.</p>
      </div>
    </div>

    <div v-if="toast" class="toast" :class="{ error: toast.error }" role="status">{{ toast.text }}</div>

    <div v-if="!loading && items.length === 0" class="empty">
      <strong>Er staat nu niets op de schermen</strong>
      <span>Keur een item goed onder ‘Te beoordelen’ om het te plaatsen.</span>
    </div>

    <div v-else class="table-wrap">
      <table>
        <thead>
          <tr>
            <th>Media</th>
            <th>Verzonden door</th>
            <th>Te zien tot</th>
            <th>Goedgekeurd door</th>
            <th style="text-align: right">Actie</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="i in items" :key="i.id">
            <td>
              <div style="display: flex; align-items: center; gap: 12px">
                <img :src="i.thumbUrl" alt="" style="width: 64px; height: 48px; object-fit: cover; border-radius: 6px; background: var(--muted)" />
                <div style="display: flex; flex-direction: column; gap: 2px">
                  <span style="font-weight: 500; color: var(--text)">{{ i.caption || 'Geen bijschrift' }}</span>
                  <span v-if="scheduled(i)" style="font-size: 12px; color: var(--text-3)">Gepland vanaf {{ formatDateTime(i.showFrom) }}</span>
                  <span v-else-if="i.status === 'Approved'" style="font-size: 12px; color: var(--text-3)">Wordt geplaatst…</span>
                  <span v-if="i.status === 'Failed'" class="pill bad" style="align-self: flex-start"><span class="dot"></span>Plaatsen mislukt</span>
                  <span v-if="i.error" style="font-size: 12px; color: var(--danger)">{{ i.error }}</span>
                </div>
              </div>
            </td>
            <td>{{ i.senderName || i.senderJid }}<br /><span style="font-size: 12px; color: var(--text-3)">{{ i.chatName }}</span></td>
            <td :style="{ color: endsSoon(i) ? 'var(--warn)' : undefined }">{{ formatDateTime(i.showUntil) }}</td>
            <td>{{ i.reviewedBy }}</td>
            <td style="text-align: right; white-space: nowrap">
              <button v-if="i.status === 'Failed'" class="btn sm primary" :disabled="retrying === i.id" @click="retry(i)" style="margin-right: 8px">Opnieuw proberen</button>
              <button class="btn sm danger-text" :disabled="removing === i.id" @click="remove(i)">{{ i.status === 'Failed' ? 'Verwijderen' : 'Van schermen halen' }}</button>
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </main>
</template>
