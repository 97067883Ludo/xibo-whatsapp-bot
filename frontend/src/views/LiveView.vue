<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { api, formatDateTime, refreshStatus, type Item } from '../api'

const items = ref<Item[]>([])
const loading = ref(true)
const toast = ref<{ text: string; error?: boolean } | null>(null)
const removing = ref<string | null>(null)

function showToast(text: string, error = false) {
  toast.value = { text, error }
  window.setTimeout(() => (toast.value = null), 4000)
}

async function load() {
  try { items.value = await api.live() } catch (e) { showToast((e as Error).message, true) }
  finally { loading.value = false }
}

function endsSoon(i: Item) {
  return !!i.showUntil && new Date(i.showUntil).getTime() - Date.now() < 24 * 3600 * 1000
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

onMounted(load)
</script>

<template>
  <main class="page">
    <div class="page-head">
      <div>
        <h1>Live op schermen</h1>
        <p>Goedgekeurde items in de Xibo-afspeellijsten. Een verwijderd item verdwijnt bij de volgende synchronisatie van de schermen.</p>
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
            <th>Schermen</th>
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
                  <span v-if="i.status === 'Approved'" style="font-size: 12px; color: var(--text-3)">Wordt geplaatst…</span>
                  <span v-if="i.error" style="font-size: 12px; color: var(--danger)">{{ i.error }}</span>
                </div>
              </div>
            </td>
            <td>{{ i.senderName || i.senderJid }}<br /><span style="font-size: 12px; color: var(--text-3)">{{ i.chatName }}</span></td>
            <td><span class="tag">{{ i.screen }}</span></td>
            <td :style="{ color: endsSoon(i) ? 'var(--warn)' : undefined }">{{ formatDateTime(i.showUntil) }}</td>
            <td>{{ i.reviewedBy }}</td>
            <td style="text-align: right">
              <button class="btn sm danger-text" :disabled="removing === i.id" @click="remove(i)">Van schermen halen</button>
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </main>
</template>
