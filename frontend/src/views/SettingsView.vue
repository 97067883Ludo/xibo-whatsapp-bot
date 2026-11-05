<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { api, type Settings, type XiboFolder } from '../api'

const settings = ref<Settings | null>(null)
const folders = ref<XiboFolder[]>([])
const selected = ref<string>('') // '' = home folder of the API user
const loading = ref(true)
const saving = ref(false)
const folderError = ref<string | null>(null)
const toast = ref<{ text: string; error?: boolean } | null>(null)

const changed = computed(() => (settings.value?.xiboFolderId ?? '').toString() !== selected.value)

function showToast(text: string, error = false) {
  toast.value = { text, error }
  window.setTimeout(() => (toast.value = null), 4000)
}

/** Indents subfolders in the dropdown; options can't hold markup, so non-breaking spaces do it. */
function label(f: XiboFolder) {
  return `${'    '.repeat(f.depth)}${f.name}`
}

async function loadFolders() {
  folderError.value = null
  try { folders.value = await api.xiboFolders() } catch (e) { folderError.value = (e as Error).message }
}

async function load() {
  try {
    settings.value = await api.settings()
    selected.value = settings.value.xiboFolderId?.toString() ?? ''
  } catch (e) {
    showToast((e as Error).message, true)
  }
  await loadFolders()
  loading.value = false
}

async function save() {
  saving.value = true
  try {
    settings.value = await api.saveSettings(selected.value === '' ? null : Number(selected.value))
    showToast(settings.value.xiboFolderName
      ? `Opgeslagen. Nieuwe media komt in de map ‘${settings.value.xiboFolderName}’.`
      : 'Opgeslagen. Nieuwe media komt in de standaardmap.')
  } catch (e) {
    showToast((e as Error).message, true)
  } finally {
    saving.value = false
  }
}

onMounted(load)
</script>

<template>
  <main class="page">
    <div class="page-head">
      <div>
        <h1>Instellingen</h1>
        <p>Deze instellingen gelden voor iedereen die het dashboard gebruikt.</p>
      </div>
    </div>

    <div v-if="toast" class="toast" :class="{ error: toast.error }" role="status">{{ toast.text }}</div>

    <section class="panel" aria-labelledby="folder-title">
      <h2 id="folder-title">Map in Xibo</h2>
      <p class="hint">Goedgekeurde foto’s en video’s worden in deze map van de Xibo-bibliotheek gezet. Dit geldt voor media die vanaf nu wordt geplaatst; wat al in Xibo staat, blijft in de map waar het nu staat.</p>

      <div v-if="folderError" class="toast error" role="alert" style="margin: 0">
        {{ folderError }}
        <button class="btn sm" style="margin-left: 12px" @click="loadFolders">Opnieuw proberen</button>
      </div>

      <label class="field">Map
        <select v-model="selected" class="select" :disabled="loading || !!folderError">
          <option value="">Standaard (thuismap van de API-gebruiker)</option>
          <option v-for="f in folders" :key="f.id" :value="f.id.toString()">{{ label(f) }}</option>
          <option v-if="settings?.xiboFolderId && !folders.some((f) => f.id === settings!.xiboFolderId)" :value="settings.xiboFolderId.toString()">
            {{ settings.xiboFolderName ?? `Map ${settings.xiboFolderId}` }} (niet gevonden)
          </option>
        </select>
      </label>

      <div>
        <button class="btn primary" :disabled="!changed || saving || loading" @click="save">Opslaan</button>
      </div>
    </section>
  </main>
</template>

<style scoped>
.panel { max-width: 560px; padding: 20px; background: var(--surface); border: 1px solid var(--border); border-radius: var(--radius-lg); display: flex; flex-direction: column; gap: 16px; }
.panel h2 { margin: 0; font-size: 16px; }
.hint { margin: -8px 0 0; color: var(--text-3); font-size: 13px; }
</style>
