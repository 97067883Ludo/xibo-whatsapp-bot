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
  newChats: number
  enabledChats: number
}

export interface Config {
  screens: string[]
  defaultDisplaySeconds: number
}

export interface ApproveBody {
  screen: string
  showFrom: string
  showUntil: string
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
  chats: () => request<Chat[]>('/api/chats'),
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
