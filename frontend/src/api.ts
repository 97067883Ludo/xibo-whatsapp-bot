import { ref } from 'vue'

export type MediaKind = 'Photo' | 'Video'

export interface Item {
  id: string
  type: MediaKind
  status: string
  senderName: string
  senderJid: string
  chatJid: string
  chatName: string
  chatIsGroup: boolean
  caption: string
  receivedAt: string
  durationSeconds: number | null
  thumbUrl: string
  fileUrl: string
  screen: string | null
  showFrom: string | null
  showUntil: string | null
  displaySeconds: number | null
  reviewedBy: string | null
  error: string | null
}

export interface Chat {
  jid: string
  name: string
  isGroup: boolean
  enabled: boolean
  firstSeenAt: string
}

export interface Status {
  whatsApp: string
  pending: number
  live: number
  failedPlacements: number
  failedDownloads: number
  newChats: number
  enabledChats: number
}

export interface Config {
  defaultDisplaySeconds: number
  tag: string
}

export interface Settings {
  xiboFolderId: number | null
  xiboFolderName: string | null
}

export interface XiboFolder {
  id: number
  name: string
  path: string
  depth: number
}

export interface ApproveBody {
  showFrom: string
  showUntil?: string | null
  displaySeconds: number | null
  rotation: number
}

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const res = await fetch(url, { headers: { 'Content-Type': 'application/json' }, ...init })
  if (!res.ok) {
    const text = await res.text()
    let message = text
    try { message = JSON.parse(text) } catch { /* plain text */ }
    throw new Error(typeof message === 'string' && message ? message : `De server gaf fout ${res.status}.`)
  }
  return (res.status === 204 ? undefined : await res.json()) as T
}

const post = (body?: unknown): RequestInit => ({ method: 'POST', body: body === undefined ? undefined : JSON.stringify(body) })

export const api = {
  status: () => request<Status>('/api/status'),
  config: () => request<Config>('/api/config'),
  items: (status = 'Pending') => request<Item[]>(`/api/items?status=${status}`),
  approve: (id: string, body: ApproveBody) => request<void>(`/api/items/${id}/approve`, post(body)),
  reject: (id: string, reason: string) => request<void>(`/api/items/${id}/reject`, post({ reason })),
  live: () => request<Item[]>('/api/live'),
  remove: (id: string) => request<void>(`/api/live/${id}/remove`, post()),
  retry: (id: string) => request<void>(`/api/items/${id}/retry`, post()),
  chats: () => request<Chat[]>('/api/chats'),
  settings: () => request<Settings>('/api/settings'),
  saveSettings: (xiboFolderId: number | null) =>
    request<Settings>('/api/settings', { method: 'PUT', body: JSON.stringify({ xiboFolderId }) }),
  xiboFolders: () => request<XiboFolder[]>('/api/xibo/folders'),
  setChatEnabled: (jid: string, enabled: boolean) =>
    request<void>(`/api/chats/${encodeURIComponent(jid)}/enabled`, { method: 'PUT', body: JSON.stringify({ enabled }) })
}

/** Counts shown in the header and tabs, refreshed by App.vue and after each action. */
export const status = ref<Status | null>(null)

export async function refreshStatus() {
  try { status.value = await api.status() } catch { status.value = null }
}

export function relativeTime(iso: string) {
  const minutes = Math.round((Date.now() - new Date(iso).getTime()) / 60000)
  if (minutes < 1) return 'zojuist'
  if (minutes < 60) return `${minutes} min`
  const hours = Math.round(minutes / 60)
  if (hours < 24) return `${hours} uur`
  return new Date(iso).toLocaleDateString('nl-NL', { day: 'numeric', month: 'short' })
}

export function formatDateTime(iso: string | null) {
  if (!iso) return '–'
  return new Date(iso).toLocaleString('nl-NL', { day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' })
}

export function toDateInput(d: Date) {
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`
}

export function formatDuration(seconds: number | null) {
  if (seconds == null) return ''
  const s = Math.round(seconds)
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`
}

// Live updates: the backend sends { topics: [...] } over a WebSocket whenever something changes,
// so the views reload right away instead of waiting for their polling timer.
export type LiveTopic = 'queue' | 'live' | 'chats'
const ALL_TOPICS: LiveTopic[] = ['queue', 'live', 'chats']
const listeners = new Set<(topics: LiveTopic[]) => void>()

/** True while the WebSocket to the backend is open. */
export const liveConnected = ref(false)

/** Calls `callback` when one of the topics changes. Returns a function that stops listening. */
export function onLiveUpdate(topics: LiveTopic[], callback: () => void) {
  const listener = (changed: LiveTopic[]) => { if (changed.some((t) => topics.includes(t))) callback() }
  listeners.add(listener)
  return () => { listeners.delete(listener) }
}

function notify(topics: LiveTopic[]) {
  refreshStatus()
  listeners.forEach((l) => l(topics))
}

let socket: WebSocket | null = null
let attempts = 0
let wasConnected = false

export function connectLive() {
  if (socket) return
  const url = `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/ws`
  socket = new WebSocket(url)
  socket.onopen = () => {
    liveConnected.value = true
    attempts = 0
    if (wasConnected) notify(ALL_TOPICS) // catch up on what happened while disconnected
    wasConnected = true
  }
  socket.onmessage = (e) => {
    try {
      const { topics } = JSON.parse(e.data) as { topics: LiveTopic[] }
      if (Array.isArray(topics)) notify(topics)
    } catch { /* ignore malformed messages */ }
  }
  socket.onclose = () => {
    liveConnected.value = false
    socket = null
    window.setTimeout(connectLive, Math.min(30000, 1000 * 2 ** attempts++)) // 1s, 2s, 4s … max 30s
  }
}
