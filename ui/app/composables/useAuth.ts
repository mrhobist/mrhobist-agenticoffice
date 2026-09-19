/**
 * Giris durumu (docs/DOMAIN.md → Giris). Belirtec tarayicida localStorage'da durur: tek makine, loopback, 12 saat omur.
 * Api istekleri `Authorization: Bearer` tasir; EventSource baslik gonderemediginden SSE'ye `?access_token=` eklenir.
 * 401 gelince oturum dusurulur ve giris ekrani acilir.
 */
import { ref, computed } from 'vue'

export interface AuthUser { id: string; name: string; role: string }

const KEY = 'aiteam.auth'
const token = ref<string | null>(null)
const user = ref<AuthUser | null>(null)
let loaded = false

function load() {
  if (loaded) return
  loaded = true
  try {
    const raw = localStorage.getItem(KEY)
    if (raw) {
      const v = JSON.parse(raw) as { token: string; user: AuthUser; expiresAt: string }
      if (new Date(v.expiresAt).getTime() > Date.now()) { token.value = v.token; user.value = v.user }
      else localStorage.removeItem(KEY)
    }
  } catch { /* localStorage yok ya da bozuk: giris ekrani */ }
}

export function getToken(): string | null { load(); return token.value }

export function setSession(v: { token: string; user: AuthUser; expiresAt: string } | null) {
  token.value = v?.token ?? null
  user.value = v?.user ?? null
  try { v ? localStorage.setItem(KEY, JSON.stringify(v)) : localStorage.removeItem(KEY) } catch { /* yalniz bellek */ }
}

/** Istek baslıklari: belirtec varsa Bearer. */
export function authHeaders(): Record<string, string> {
  const t = getToken()
  return t ? { authorization: `Bearer ${t}` } : {}
}

/** SSE ve resim gibi baslik tasiyamayan istekler icin sorgu eki. */
export function tokenQuery(): string {
  const t = getToken()
  return t ? `?access_token=${encodeURIComponent(t)}` : ''
}

export function useAuth() {
  load()
  return {
    user: computed(() => user.value),
    loggedIn: computed(() => !!token.value),
    logout: () => setSession(null),
  }
}
