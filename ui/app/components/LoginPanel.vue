<script setup lang="ts">
import { useApiClient } from '~/api/client'
import { errorText } from '~/api/errors'
import { setSession, type AuthUser } from '~/composables/useAuth'

/**
 * Giris (docs/DOMAIN.md → Giris): bugun kodda gomulu tek kullanici (admin/admin), JWT tek sema.
 * Ileride LDAP ya da kullanici deposu ayni formu kullanir; ekran degismez.
 */
const emit = defineEmits<{ done: [] }>()
const api = useApiClient()
const username = ref('')
const password = ref('')
const busy = ref(false)
const error = ref<string | null>(null)

async function submit() {
  if (busy.value) return
  busy.value = true
  error.value = null
  try {
    const r = await api.post<{ token: string; expiresAt: string; user: AuthUser }>('/api/v1/auth/login', { username: username.value.trim(), password: password.value })
    setSession(r)
    emit('done')
  } catch (e) {
    error.value = errorText(e)
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="login">
    <section class="intro">
      <div class="brand">
        <span class="mark" aria-hidden="true"><svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="#141413" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 21h18"/><path d="M5 21V8l7-5 7 5v13"/><path d="M9 21v-6h6v6"/></svg></span>
        MrHobist.AITeam
      </div>
      <h1>Projelerin var, ofis çalışıyor.</h1>
      <p>Her proje kendi işlerini ve akışını taşır. Analist planlar, sen onaylarsın, ekip piksel ofiste işi bitirir.</p>
      <div class="chips"><span>Plan onayı zorunlu</span><span>Bütçe sınırı</span><span>Tam günlük</span></div>
    </section>
    <section class="card">
      <h2>Giriş</h2>
      <p class="sub">Çalışma alanına gir; LLM oturumları Ayarlar'da ayrı bağlanır.</p>
      <form @submit.prevent="submit">
        <label class="lbl" for="login-user">Kullanıcı adı</label>
        <input id="login-user" v-model="username" type="text" autocomplete="username" required autofocus>
        <label class="lbl" for="login-pass">Şifre</label>
        <input id="login-pass" v-model="password" type="password" autocomplete="current-password" required>
        <button class="primary" type="submit" :disabled="busy || !username || !password">{{ busy ? 'Giriş yapılıyor…' : 'Giriş yap' }}</button>
        <p v-if="error" class="err" role="alert">{{ error }}</p>
      </form>
      <p class="note"><strong>Bugün:</strong> tek yerel kullanıcı (<code>admin</code>). İleride LDAP ya da tam kullanıcı mimarisi aynı kapıdan gelir; yalnız <code>127.0.0.1</code>, JWT tek şema, roller claim ile.</p>
    </section>
  </div>
</template>

<style scoped>
.login { position: absolute; inset: 0; display: grid; grid-template-columns: minmax(0, 1fr) 460px; background: #151820; color: #e8ecf5; }
.intro { position: relative; padding: 56px 64px; display: flex; flex-direction: column; justify-content: flex-end; gap: 14px;
  background: repeating-linear-gradient(0deg, rgba(255,255,255,0.03) 0 1px, transparent 1px 32px), repeating-linear-gradient(90deg, rgba(255,255,255,0.03) 0 1px, transparent 1px 32px), #151820; }
.brand { position: absolute; left: 64px; top: 48px; display: flex; align-items: center; gap: 10px; font-weight: 700; font-size: 16px; }
.mark { width: 30px; height: 30px; border-radius: 8px; background: #d9a13a; display: inline-flex; align-items: center; justify-content: center; }
h1 { margin: 0; font-size: 38px; line-height: 1.15; font-weight: 600; max-width: 620px; }
.intro p { margin: 0; font-size: 15px; color: var(--ink-2); line-height: 1.6; max-width: 620px; }
.chips { display: flex; gap: 8px; }
.chips span { font-size: 12px; padding: 5px 11px; border-radius: 999px; background: var(--surface-2); border: 1px solid var(--rule); color: var(--ink-2); }
.card { padding: 64px 48px; background: var(--surface); border-left: 1px solid var(--rule); display: flex; flex-direction: column; justify-content: center; gap: 14px; }
h2 { margin: 0; font-size: 24px; font-weight: 600; }
.sub { margin: 0; font-size: 13px; color: var(--ink-3); }
form { display: flex; flex-direction: column; gap: 6px; }
.lbl { font-size: 11px; font-weight: 700; text-transform: uppercase; letter-spacing: 0.06em; color: var(--ink-2); margin-top: 8px; }
input { font: inherit; font-size: 14px; padding: 11px 13px; background: var(--bg); color: var(--ink); border: 1px solid var(--rule); border-radius: 8px; }
input:focus { outline: 2px solid #4f8ef7; }
.primary { margin-top: 14px; font: inherit; font-size: 14px; font-weight: 700; padding: 12px 18px; border-radius: 8px; background: #d9a13a; color: #141413; border: none; cursor: pointer; }
.primary:disabled { opacity: 0.5; cursor: default; }
.err { margin: 6px 0 0; color: #f0a0a0; font-size: 12px; }
.note { margin: 8px 0 0; padding: 12px 14px; border-radius: 10px; background: var(--surface-2); border: 1px solid var(--rule); font-size: 12px; color: var(--ink-3); line-height: 1.5; }
.note code { font-size: 11px; background: rgba(0,0,0,0.3); padding: 1px 5px; border-radius: 3px; color: var(--ink); }
</style>
