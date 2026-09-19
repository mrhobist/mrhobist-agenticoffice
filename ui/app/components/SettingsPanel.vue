<script setup lang="ts">
import type { LoginStarted, Provider, ProviderStatus, UsageItem } from '~/api/types'
import { isApiError, useApiClient } from '~/api/client'
import { errorText } from '~/api/errors'
import { providerLabel } from '~/api/labels'

/**
 * Ayarlar: LLM baglantilari (giris durumu, tek tikla giris/cikis, modeller) ve kullanim.
 * Giris: runtime, saglayicinin kendi akisini kullanicinin makinesinde baslatir (yeni konsol + tarayici);
 * sifre/token bu ekrandan GECMEZ (docs/DOMAIN.md → Model, efor ve kimlik).
 * Kullanim: bizim kayitlarimiz (runs/ turlari). Saglayici abonelik limitini/kalan kotayi CLI'dan vermiyor.
 */
const emit = defineEmits<{ close: []; changed: [] }>()
const api = useApiClient()

const providers = ref<ProviderStatus[] | null>(null)
const loadError = ref<string | null>(null)
const busy = ref<string | null>(null)
const notice = ref<{ provider: Provider; kind: 'info' | 'err'; text: string } | null>(null)
const email = ref('')

const usage = ref<UsageItem[] | null>(null)
const usageError = ref<string | null>(null)

async function load(refresh = false) {
  try {
    providers.value = await api.get<ProviderStatus[]>(`/api/v1/providers${refresh ? '?refresh=true' : ''}`)
    loadError.value = null
    emit('changed')
  } catch (e) {
    providers.value = null
    loadError.value = isApiError(e) && e.errorCode === 'runtime.unavailable'
      ? 'Runtime (5090) kapalı; sağlayıcı durumu alınamıyor.'
      : errorText(e)
  }
}

async function loadUsage() {
  try {
    usage.value = await api.get<UsageItem[]>('/api/v1/usage')
    usageError.value = null
  } catch (e) {
    usage.value = null
    usageError.value = errorText(e)
  }
}

/** Giris basladiktan sonra 5 s'de bir 3 dakika boyunca yeniden kontrol: kullanici tarayicida onaylayinca ekran kendi guncellenir. */
let watchTimer: ReturnType<typeof setInterval> | undefined
let watchUntil = 0
function watchLogin(p: Provider) {
  clearInterval(watchTimer)
  watchUntil = Date.now() + 3 * 60_000
  watchTimer = setInterval(async () => {
    await load(true)
    const st = providers.value?.find(x => x.provider === p)
    if (st?.loggedIn || Date.now() > watchUntil) {
      clearInterval(watchTimer)
      if (st?.loggedIn) notice.value = { provider: p, kind: 'info', text: `Giriş tamam${st.account ? `: ${st.account}` : ''}.` }
    }
  }, 5000)
}

async function login(p: Provider, mode: 'claudeai' | 'console') {
  if (busy.value) return
  busy.value = `${p}:${mode}`
  notice.value = null
  try {
    const r = await api.post<LoginStarted>(`/api/v1/providers/${p}/login`, { mode, email: email.value.trim() || null })
    notice.value = { provider: p, kind: r.started ? 'info' : 'err', text: r.detail }
    if (r.started) watchLogin(p)
  } catch (e) {
    notice.value = { provider: p, kind: 'err', text: errorText(e) }
  } finally {
    busy.value = null
  }
}

async function logout(p: Provider) {
  if (busy.value || !window.confirm(`${providerLabel(p)} oturumu kapatılsın mı? Çalışmalar model çağıramaz.`)) return
  busy.value = `${p}:logout`
  notice.value = null
  try {
    await api.post(`/api/v1/providers/${p}/logout`)
    await load(true)
    notice.value = { provider: p, kind: 'info', text: 'Oturum kapatıldı.' }
  } catch (e) {
    notice.value = { provider: p, kind: 'err', text: errorText(e) }
  } finally {
    busy.value = null
  }
}

onMounted(() => { void load(); void loadUsage() })
onBeforeUnmount(() => clearInterval(watchTimer))

const totalCost = computed(() => (usage.value ?? []).reduce((s, u) => s + u.costUsd, 0))
function fmtCost(v: number): string { return `$${v.toFixed(4)}` }
function fmtNum(v: number): string { return v.toLocaleString('tr-TR') }
function fmtWhen(s: string | null): string { return s ? new Date(s).toLocaleString('tr-TR', { dateStyle: 'short', timeStyle: 'short' }) : '—' }
</script>

<template>
  <div class="wrap" @click.self="emit('close')">
    <section class="panel" role="dialog" aria-labelledby="settings-title">
      <header>
        <h2 id="settings-title">Ayarlar</h2>
        <button class="x" type="button" aria-label="Kapat" @click="emit('close')">×</button>
      </header>

      <!-- ---------------------------------------------------------- LLM baglantilari -->
      <section class="block">
        <div class="block-head">
          <h3>LLM bağlantıları</h3>
          <button type="button" class="small" :disabled="!!busy" @click="load(true)">Yeniden kontrol et</button>
        </div>
        <p class="sub">Modeller Claude Code oturumunu kullanır; oturum bu makinedeki Windows kullanıcısına bağlıdır. Giriş düğmesi kendi konsol penceresinde <code>claude auth login</code> başlatır; onayı tarayıcıda sen verirsin. Bu ekrandan şifre ya da anahtar geçmez.</p>

        <p v-if="loadError" class="err" role="alert">{{ loadError }}</p>
        <p v-else-if="!providers" class="sub">Yükleniyor…</p>

        <article v-for="p in providers" :key="p.provider" class="card">
          <div class="card-head">
            <strong>{{ providerLabel(p.provider) }}</strong>
            <span class="badge" :class="p.loggedIn ? 'ok' : 'off'">{{ p.loggedIn ? 'giriş var' : 'giriş yok' }}</span>
            <span v-if="p.account" class="sub">{{ p.account }}</span>
          </div>
          <p class="sub">{{ p.detail }}</p>

          <div v-if="!p.loggedIn" class="login">
            <label class="lbl" :for="`email-${p.provider}`">E-posta <span class="sub">(isteğe bağlı, giriş sayfasını doldurur)</span></label>
            <input :id="`email-${p.provider}`" v-model="email" type="email" autocomplete="off" placeholder="ad@ornek.com">
            <div class="actions">
              <button class="primary" type="button" :disabled="!!busy" @click="login(p.provider, 'claudeai')">Giriş yap (Claude aboneliği)</button>
              <button type="button" :disabled="!!busy" @click="login(p.provider, 'console')">Konsol ile giriş (API faturası)</button>
            </div>
          </div>
          <div v-else class="actions">
            <button type="button" class="ghost" :disabled="!!busy" @click="logout(p.provider)">Oturumu kapat</button>
          </div>

          <p v-if="notice && notice.provider === p.provider" :class="notice.kind === 'err' ? 'err' : 'ok'" role="status">{{ notice.text }}</p>

          <details class="models">
            <summary>Modeller ({{ p.models.length }})</summary>
            <ul>
              <li v-for="m in p.models" :key="m.model"><code>{{ m.model }}</code> <span class="sub">{{ m.reachable ? 'erişilebilir' : 'giriş gerekli' }}</span></li>
            </ul>
          </details>
        </article>
      </section>

      <!-- ---------------------------------------------------------- kullanim -->
      <section class="block">
        <div class="block-head">
          <h3>Kullanım</h3>
          <button type="button" class="small" @click="loadUsage">Yenile</button>
        </div>
        <p class="sub">Bu makinedeki çalışmaların kayıtlarından (<code>runs/</code>) toplanır: tur, token, sağlayıcının bildirdiği maliyet. Abonelik limiti ve kalan kota sağlayıcı tarafından CLI'a açılmıyor; o bilgi için hesap sayfasına bakılır.</p>
        <p v-if="usageError" class="err">{{ usageError }}</p>
        <p v-else-if="!usage" class="sub">Yükleniyor…</p>
        <p v-else-if="!usage.length" class="sub">Henüz kayıtlı bir model çağrısı yok.</p>
        <table v-else class="usage">
          <thead>
            <tr><th>Sağlayıcı</th><th>Model</th><th class="num">Tur</th><th class="num">Çalışma</th><th class="num">Giriş tk</th><th class="num">Çıkış tk</th><th class="num">Maliyet</th><th>Son</th></tr>
          </thead>
          <tbody>
            <tr v-for="u in usage" :key="u.provider + u.model">
              <td>{{ u.provider }}</td><td><code>{{ u.model }}</code></td>
              <td class="num">{{ fmtNum(u.turns) }}</td><td class="num">{{ fmtNum(u.runs) }}</td>
              <td class="num">{{ fmtNum(u.inputTokens) }}</td><td class="num">{{ fmtNum(u.outputTokens) }}</td>
              <td class="num">{{ fmtCost(u.costUsd) }}</td><td>{{ fmtWhen(u.lastAt) }}</td>
            </tr>
          </tbody>
          <tfoot><tr><td colspan="6">Toplam</td><td class="num">{{ fmtCost(totalCost) }}</td><td /></tr></tfoot>
        </table>
      </section>
    </section>
  </div>
</template>

<style scoped>
.wrap { position: absolute; inset: 0; background: rgba(10, 12, 18, 0.55); display: flex; justify-content: flex-end; }
.panel {
  background: #ede9dc; color: #23283a; border-left: 6px solid #5b6b3a;
  width: min(640px, 100%); height: 100%; overflow: auto; padding: 16px 18px;
  box-shadow: -20px 0 60px rgba(0,0,0,0.5); display: flex; flex-direction: column; gap: 16px;
}
.panel > header { display: flex; align-items: center; gap: 10px; }
h2 { margin: 0; font-size: 16px; letter-spacing: 0.04em; text-transform: uppercase; }
h3 { margin: 0; font-size: 13px; text-transform: uppercase; letter-spacing: 0.06em; color: #4a5068; }
.x { background: none; border: none; font-size: 22px; cursor: pointer; color: #23283a; line-height: 1; margin-left: auto; padding: 0 4px; }
.block { display: flex; flex-direction: column; gap: 8px; }
.block-head { display: flex; align-items: center; justify-content: space-between; }
.sub { font-size: 11px; color: #6b7285; line-height: 1.45; margin: 0; }
.card { background: #fff; border: 1px solid #c9c3b3; border-radius: 6px; padding: 10px 12px; display: flex; flex-direction: column; gap: 6px; }
.card-head { display: flex; align-items: center; gap: 8px; font-size: 14px; }
.badge { font-size: 10px; font-weight: 700; padding: 2px 8px; border-radius: 999px; text-transform: uppercase; letter-spacing: 0.04em; }
.badge.ok { background: #7cc46b; }
.badge.off { background: #f3c34a; }
.login { display: flex; flex-direction: column; gap: 4px; }
.lbl { font-size: 11px; font-weight: 700; text-transform: uppercase; letter-spacing: 0.04em; color: #4a5068; }
input[type="email"] { font: inherit; font-size: 13px; background: #fff; color: #23283a; border: 1px solid #c9c3b3; border-radius: 4px; padding: 6px 8px; width: 100%; }
input:focus { outline: 2px solid #4f8ef7; outline-offset: 0; }
.actions { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; margin-top: 4px; }
button { font: inherit; cursor: pointer; border-radius: 4px; padding: 7px 14px; border: 1px solid #c9c3b3; background: #fff; color: #23283a; }
button:disabled { opacity: 0.5; cursor: default; }
.primary { background: #23283a; color: #fff; border-color: #23283a; }
.ghost { background: transparent; }
.small { padding: 3px 8px; font-size: 11px; }
.err { color: #b3261e; font-size: 12px; margin: 0; }
.ok { color: #2f7a4a; font-size: 12px; margin: 0; }
.models summary { cursor: pointer; font-size: 12px; font-weight: 600; color: #4a5068; }
.models ul { margin: 4px 0 0; padding-left: 16px; font-size: 12px; }
code { font-size: 10px; background: rgba(0,0,0,0.06); padding: 1px 4px; border-radius: 3px; }
.usage { width: 100%; border-collapse: collapse; font-size: 12px; background: #fff; border: 1px solid #c9c3b3; border-radius: 6px; }
.usage th, .usage td { padding: 5px 8px; border-bottom: 1px solid rgba(0,0,0,0.08); text-align: left; }
.usage th { font-size: 10px; text-transform: uppercase; letter-spacing: 0.04em; color: #6b7285; }
.usage .num { text-align: right; font-variant-numeric: tabular-nums; }
.usage tfoot td { font-weight: 700; border-bottom: none; }
</style>
