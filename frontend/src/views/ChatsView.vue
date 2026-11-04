<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { SwitchRoot, SwitchThumb } from 'reka-ui'
import { api, refreshStatus, type Chat } from '../api'

const chats = ref<Chat[]>([])
const loading = ref(true)
const error = ref<string | null>(null)

async function load() {
  try { chats.value = await api.chats() } catch (e) { error.value = (e as Error).message }
  finally { loading.value = false }
}

async function toggle(chat: Chat, enabled: boolean) {
  const previous = chat.enabled
  chat.enabled = enabled
  try {
    await api.setChatEnabled(chat.jid, enabled)
    refreshStatus()
  } catch (e) {
    chat.enabled = previous
    error.value = (e as Error).message
  }
}

onMounted(load)
</script>

<template>
  <main class="page">
    <div class="page-head">
      <div>
        <h1>Chats</h1>
        <p>Foto’s en video’s worden automatisch opgehaald uit alle chats. Nieuwe groepen en privéchats verschijnen hier vanzelf en staan direct aan. Zet een chat uit om er niets meer uit op te halen.</p>
      </div>
    </div>

    <div v-if="error" class="toast error" role="alert">{{ error }}</div>

    <div v-if="!loading && chats.length === 0" class="empty">
      <strong>Nog geen chats</strong>
      <span>Voeg het WhatsApp-nummer toe aan een groep of stuur het een bericht; de chat verschijnt dan hier.</span>
    </div>

    <ul v-else class="list">
      <li v-for="c in chats" :key="c.jid" class="chat">
        <div style="display: flex; flex-direction: column; gap: 2px; min-width: 0">
          <span style="font-weight: 500">{{ c.name }}</span>
          <span style="font-size: 12px; color: var(--text-3)">
            {{ c.isGroup ? 'Groep' : 'Privéchat' }} · gezien sinds {{ new Date(c.firstSeenAt).toLocaleDateString('nl-NL', { dateStyle: 'medium' }) }}
          </span>
        </div>
        <label style="display: flex; align-items: center; gap: 10px; font-size: 13px; color: var(--text-2)">
          {{ c.enabled ? 'Ingeschakeld' : 'Uit' }}
          <SwitchRoot :model-value="c.enabled" class="switch" :aria-label="`Media ophalen uit ${c.name}`" @update:model-value="(v: boolean) => toggle(c, v)">
            <SwitchThumb class="switch-thumb" />
          </SwitchRoot>
        </label>
      </li>
    </ul>
  </main>
</template>

<style scoped>
.list { list-style: none; margin: 0; padding: 0; background: var(--surface); border: 1px solid var(--border); border-radius: var(--radius-lg); }
.chat { display: flex; align-items: center; justify-content: space-between; gap: 16px; padding: 14px 16px; border-bottom: 1px solid var(--muted); }
.chat:last-child { border-bottom: none; }
</style>
