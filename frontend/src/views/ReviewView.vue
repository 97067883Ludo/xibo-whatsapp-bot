<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { SliderRange, SliderRoot, SliderThumb, SliderTrack, ToggleGroupItem, ToggleGroupRoot } from 'reka-ui'
import { api, formatDuration, refreshStatus, relativeTime, toDateInput, type Config, type Item } from '../api'

const items = ref<Item[]>([])
const config = ref<Config>({ defaultDisplaySeconds: 10, tag: 'onscreen' })
const loading = ref(true)
const selectedId = ref<string | null>(null)
const typeFilter = ref<string>('all')
const chatFilter = ref('all')
const sort = ref<'newest' | 'oldest' | 'sender'>('newest')
const toast = ref<{ text: string; error?: boolean } | null>(null)
const busy = ref(false)
const failed = ref<Item[]>([])
const failedBusy = ref<string | null>(null)

// Review form
const fromDate = ref('')
const untilDate = ref('')
const untilTime = ref('18:00')
const duration = ref<number[]>([10])
const rotation = ref(0)
const rejecting = ref(false)
const reason = ref('Niet geschikt voor de schermen')
const reasons = ['Niet geschikt voor de schermen', 'Slechte kwaliteit', 'Privacy: geen toestemming van mensen in beeld', 'Dubbel', 'Anders']

const chats = computed(() => {
  const map = new Map<string, string>()
  items.value.forEach((i) => map.set(i.chatJid, i.chatName))
  return [...map].map(([jid, name]) => ({ jid, name })).sort((a, b) => a.name.localeCompare(b.name))
})

const visible = computed(() => {
  const kind = typeFilter.value || 'all'
  const list = items.value.filter((i) =>
    (kind === 'all' || i.type === kind) && (chatFilter.value === 'all' || i.chatJid === chatFilter.value))
  if (sort.value === 'oldest') return [...list].reverse()
  if (sort.value === 'sender') return [...list].sort((a, b) => a.senderName.localeCompare(b.senderName))
  return list
})

const selected = computed(() => items.value.find((i) => i.id === selectedId.value) ?? null)
const pendingLabel = computed(() => {
  const n = items.value.length
  if (n === 0) return 'Er is niets te beoordelen'
  return n === 1 ? '1 item wacht op een beslissing' : `${n} items wachten op een beslissing`
})

function initials(name: string) {
  const parts = name.replace(/[^\p{L}\s]/gu, '').trim().split(/\s+/).filter(Boolean)
  return parts.length ? (parts[0][0] + (parts.length > 1 ? parts[parts.length - 1][0] : '')).toUpperCase() : '?'
}

function badge(i: Item) {
  return i.type === 'Video' ? `Video · ${formatDuration(i.durationSeconds)}` : 'Foto'
}

function resetForm() {
  const today = new Date()
  const until = new Date()
  until.setDate(today.getDate() + 14)
  fromDate.value = toDateInput(today)
  untilDate.value = toDateInput(until)
  untilTime.value = '18:00'
  duration.value = [config.value.defaultDisplaySeconds]
  rotation.value = 0
  rejecting.value = false
}

watch(selectedId, resetForm)

function showToast(text: string, error = false) {
  toast.value = { text, error }
  window.setTimeout(() => (toast.value = null), 4000)
}

async function load() {
  try {
    const [pending, failedDownloads] = await Promise.all([api.items('Pending'), api.items('Failed')])
    items.value = pending
    failed.value = failedDownloads
    if (!selected.value) selectedId.value = visible.value[0]?.id ?? null
  } catch (e) {
    showToast((e as Error).message, true)
  } finally {
    loading.value = false
  }
}

function next(removedId: string) {
  const index = visible.value.findIndex((i) => i.id === removedId)
  items.value = items.value.filter((i) => i.id !== removedId)
  selectedId.value = visible.value[Math.min(index, visible.value.length - 1)]?.id ?? null
}

async function approve() {
  const item = selected.value
  if (!item || busy.value) return
  busy.value = true
  try {
    await api.approve(item.id, {
      showFrom: `${fromDate.value}T00:00:00`,
      showUntil: `${untilDate.value}T${untilTime.value}:00`,
      displaySeconds: item.type === 'Photo' ? duration.value[0] : null,
      rotation: rotation.value
    })
    next(item.id)
    showToast(fromDate.value > toDateInput(new Date())
      ? `Goedgekeurd. Komt op de schermen vanaf ${new Date(fromDate.value).toLocaleDateString('nl-NL', { day: 'numeric', month: 'long' })}.`
      : 'Goedgekeurd. Komt binnen enkele seconden op de schermen.')
    refreshStatus()
  } catch (e) {
    showToast((e as Error).message, true)
  } finally {
    busy.value = false
  }
}

async function reject() {
  const item = selected.value
  if (!item || busy.value) return
  busy.value = true
  try {
    await api.reject(item.id, reason.value)
    next(item.id)
    showToast('Afgewezen. Het item komt niet op de schermen.')
    refreshStatus()
  } catch (e) {
    showToast((e as Error).message, true)
  } finally {
    busy.value = false
  }
}

async function retryDownload(i: Item) {
  failedBusy.value = i.id
  try {
    await api.retry(i.id)
    failed.value = failed.value.filter((x) => x.id !== i.id)
    showToast('Wordt opnieuw opgehaald. Lukt het, dan verschijnt het item hieronder.')
    refreshStatus()
    window.setTimeout(load, 6000)
  } catch (e) {
    showToast((e as Error).message, true)
  } finally {
    failedBusy.value = null
  }
}

async function dismissDownload(i: Item) {
  failedBusy.value = i.id
  try {
    await api.reject(i.id, 'Ophalen mislukt')
    failed.value = failed.value.filter((x) => x.id !== i.id)
    refreshStatus()
  } catch (e) {
    showToast((e as Error).message, true)
  } finally {
    failedBusy.value = null
  }
}

let timer: number | undefined
onMounted(async () => {
  try { config.value = await api.config() } catch { /* keep defaults */ }
  resetForm()
  await load()
  timer = window.setInterval(load, 20000)
})
onUnmounted(() => window.clearInterval(timer))
</script>

<template>
  <main class="page">
    <div class="page-head">
      <div>
        <h1>Te beoordelen</h1>
        <p>{{ pendingLabel }}</p>
      </div>
      <div class="tools">
        <ToggleGroupRoot v-model="typeFilter" type="single" class="segmented" aria-label="Filteren op type">
          <ToggleGroupItem value="all">Alles</ToggleGroupItem>
          <ToggleGroupItem value="Photo">Foto’s</ToggleGroupItem>
          <ToggleGroupItem value="Video">Video’s</ToggleGroupItem>
        </ToggleGroupRoot>
        <label class="field" style="flex-direction: row; align-items: center; gap: 8px; color: var(--text-3); font-weight: 400">
          Chat
          <select v-model="chatFilter" class="select compact">
            <option value="all">Alle chats</option>
            <option v-for="c in chats" :key="c.jid" :value="c.jid">{{ c.name }}</option>
          </select>
        </label>
        <label class="field" style="flex-direction: row; align-items: center; gap: 8px; color: var(--text-3); font-weight: 400">
          Sorteren
          <select v-model="sort" class="select compact">
            <option value="newest">Nieuwste eerst</option>
            <option value="oldest">Oudste eerst</option>
            <option value="sender">Op afzender</option>
          </select>
        </label>
      </div>
    </div>

    <div v-if="toast" class="toast" :class="{ error: toast.error }" role="status">{{ toast.text }}</div>

    <div class="review">
      <section aria-label="Media in afwachting" class="queue">
        <div v-if="failed.length" class="failed-box" role="region" aria-label="Niet opgehaald">
          <div class="failed-head">
            <strong>{{ failed.length === 1 ? '1 item kon niet worden opgehaald' : `${failed.length} items konden niet worden opgehaald` }}</strong>
            <span>Probeer het opnieuw. Lukt het weer niet, dan is het bestand waarschijnlijk beschadigd of in een formaat dat niet wordt ondersteund.</span>
          </div>
          <ul class="failed-list">
            <li v-for="i in failed" :key="i.id">
              <div class="failed-info">
                <span class="ellipsis" style="font-weight: 500">{{ i.type === 'Video' ? 'Video' : 'Foto' }} van {{ i.senderName || i.senderJid }}</span>
                <span class="ellipsis muted-sm">{{ i.chatName }} · {{ relativeTime(i.receivedAt) }}</span>
                <span v-if="i.error" class="ellipsis failed-error" :title="i.error">{{ i.error }}</span>
              </div>
              <div class="failed-actions">
                <button class="btn sm primary" :disabled="failedBusy === i.id" @click="retryDownload(i)">Opnieuw ophalen</button>
                <button class="btn sm" :disabled="failedBusy === i.id" @click="dismissDownload(i)">Negeren</button>
              </div>
            </li>
          </ul>
        </div>
        <div v-if="!loading && visible.length === 0" class="empty">
          <strong>Alles is beoordeeld</strong>
          <span>Nieuwe foto’s en video’s uit WhatsApp verschijnen hier.</span>
        </div>
        <div v-else class="grid">
          <button
            v-for="i in visible" :key="i.id" class="card" :aria-pressed="i.id === selectedId"
            @click="selectedId = i.id">
            <div class="thumb">
              <img :src="i.thumbUrl" alt="" loading="lazy" />
              <span class="badge" style="position: absolute; top: 10px; left: 10px">{{ badge(i) }}</span>
            </div>
            <div class="card-body">
              <div class="row">
                <span class="ellipsis" style="font-weight: 500">{{ i.senderName || i.senderJid }}</span>
                <span class="muted-sm">{{ relativeTime(i.receivedAt) }}</span>
              </div>
              <span class="ellipsis muted-sm">{{ i.chatName }}</span>
              <span class="ellipsis caption" :class="{ none: !i.caption }">{{ i.caption || 'Geen bijschrift' }}</span>
            </div>
          </button>
        </div>
      </section>

      <aside v-if="selected" class="panel" aria-label="Geselecteerd item">
        <div class="preview">
          <video v-if="selected.type === 'Video'" :src="selected.fileUrl" controls preload="metadata" />
          <img v-else :src="selected.fileUrl" alt="" :style="{ transform: `rotate(${rotation}deg)` }" />
          <span class="badge" style="position: absolute; top: 12px; left: 12px">{{ badge(selected) }}</span>
          <div v-if="selected.type === 'Photo'" class="rotate">
            <button class="icon-btn" aria-label="Linksom draaien" @click="rotation -= 90">
              <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M3 12a9 9 0 1 0 3-6.7L3 8" /><path d="M3 3v5h5" /></svg>
            </button>
            <button class="icon-btn" aria-label="Rechtsom draaien" @click="rotation += 90">
              <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M21 12a9 9 0 1 1-3-6.7L21 8" /><path d="M21 3v5h-5" /></svg>
            </button>
          </div>
        </div>

        <div class="panel-body">
          <div style="display: flex; align-items: center; gap: 12px">
            <span class="avatar">{{ initials(selected.senderName) }}</span>
            <div style="display: flex; flex-direction: column; min-width: 0">
              <span style="font-weight: 600">{{ selected.senderName || selected.senderJid }}</span>
              <span class="muted-sm">{{ selected.chatName }} · {{ new Date(selected.receivedAt).toLocaleString('nl-NL', { dateStyle: 'medium', timeStyle: 'short' }) }}</span>
            </div>
          </div>
          <p class="caption-box" :class="{ none: !selected.caption }">{{ selected.caption || 'Geen bijschrift' }}</p>

          <hr />

          <h2>Weergave-instellingen</h2>
          <label class="field">Tonen vanaf
            <input v-model="fromDate" type="date" class="input" />
          </label>
          <fieldset class="fieldset">
            <legend>Tonen tot</legend>
            <div class="two">
              <label class="field small">Datum <input v-model="untilDate" type="date" class="input" /></label>
              <label class="field small">Tijd <input v-model="untilTime" type="time" step="900" class="input" /></label>
            </div>
          </fieldset>
          <div v-if="selected.type === 'Photo'" class="field">
            <span style="display: flex; justify-content: space-between">
              Tijd op scherm <span style="font-weight: 400; color: var(--text-2)">{{ duration[0] }} seconden</span>
            </span>
            <SliderRoot v-model="duration" class="slider" :min="5" :max="30" :step="1">
              <SliderTrack class="slider-track"><SliderRange class="slider-range" /></SliderTrack>
              <SliderThumb class="slider-thumb" aria-label="Tijd op scherm in seconden" />
            </SliderRoot>
          </div>
          <p v-else class="muted-sm" style="margin: 0">Video’s worden één keer volledig afgespeeld.</p>

          <div v-if="!rejecting" class="actions">
            <button class="btn danger-text" style="flex: 1" :disabled="busy" @click="rejecting = true">Afwijzen</button>
            <button class="btn primary" style="flex: 2" :disabled="busy" @click="approve">
              <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M20 6 9 17l-5-5" /></svg>
              Goedkeuren en publiceren
            </button>
          </div>
          <div v-else class="reject-box">
            <label class="field" style="color: #7f1d1d">Reden (alleen zichtbaar voor beoordelaars)
              <select v-model="reason" class="select">
                <option v-for="r in reasons" :key="r">{{ r }}</option>
              </select>
            </label>
            <div class="actions">
              <button class="btn sm" style="flex: 1" @click="rejecting = false">Annuleren</button>
              <button class="btn sm danger" style="flex: 1" :disabled="busy" @click="reject">Afwijzing bevestigen</button>
            </div>
          </div>
        </div>
      </aside>
    </div>
  </main>
</template>

<style scoped>
.review { display: flex; flex-wrap: wrap; gap: 24px; align-items: flex-start; }
.queue { flex: 999 1 560px; min-width: 0; }
.failed-box { margin-bottom: 16px; padding: 16px; border: 1px solid var(--danger-border); border-radius: var(--radius-lg); background: var(--danger-bg); display: flex; flex-direction: column; gap: 12px; }
.failed-head { display: flex; flex-direction: column; gap: 2px; }
.failed-head strong { color: var(--danger); }
.failed-head span { color: var(--text-2); font-size: 13px; }
.failed-list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 8px; }
.failed-list li { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 12px; padding: 10px 12px; border-radius: var(--radius); background: var(--surface); }
.failed-info { display: flex; flex-direction: column; gap: 2px; min-width: 0; flex: 1 1 240px; }
.failed-error { font-size: 12px; color: var(--danger); }
.failed-actions { display: flex; gap: 8px; }
.grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 16px; }
.card { text-align: left; padding: 0; background: var(--surface); border: 1px solid var(--border); border-radius: var(--radius-lg); cursor: pointer; overflow: hidden; display: flex; flex-direction: column; }
.card[aria-pressed='true'] { border-color: var(--accent); box-shadow: 0 0 0 2px var(--accent); }
.thumb { position: relative; aspect-ratio: 4 / 3; background: var(--muted); width: 100%; }
.thumb img { width: 100%; height: 100%; object-fit: cover; display: block; }
.card-body { padding: 12px 14px 14px; display: flex; flex-direction: column; gap: 4px; width: 100%; }
.row { display: flex; justify-content: space-between; gap: 8px; }
.ellipsis { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.muted-sm { color: var(--text-3); font-size: 12px; }
.caption { font-size: 13px; color: var(--text-2); margin-top: 2px; }
.caption.none, .caption-box.none { color: var(--text-3); }

.panel { flex: 1 1 380px; max-width: 440px; background: var(--surface); border: 1px solid var(--border); border-radius: var(--radius-lg); overflow: hidden; position: sticky; top: 16px; }
.preview { position: relative; aspect-ratio: 16 / 10; background: var(--text); display: grid; place-items: center; overflow: hidden; }
.preview img, .preview video { max-width: 100%; max-height: 100%; object-fit: contain; transition: transform 0.2s; }
.rotate { position: absolute; bottom: 12px; right: 12px; display: flex; gap: 6px; }
.panel-body { padding: 20px; display: flex; flex-direction: column; gap: 14px; }
.panel-body h2 { margin: 0; font-size: 14px; font-weight: 600; }
.panel-body hr { border: none; height: 1px; background: var(--border); margin: 2px 0; width: 100%; }
.caption-box { margin: 0; padding: 12px; border-radius: var(--radius); background: var(--muted); color: var(--text-2); line-height: 1.5; }
.fieldset { margin: 0; padding: 0; border: none; min-width: 0; }
.fieldset legend { padding: 0; margin-bottom: 6px; font-size: 13px; font-weight: 500; }
.two { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 12px; }
.actions { display: flex; gap: 10px; padding-top: 4px; }
.reject-box { display: flex; flex-direction: column; gap: 10px; padding: 14px; border-radius: var(--radius); border: 1px solid var(--danger-border); background: var(--danger-bg); }
@media (max-width: 860px) { .panel { position: static; max-width: none; } }
</style>
