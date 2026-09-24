<script setup lang="ts">
import type { AppSettings, LoginMode, LoginStarted, Provider, ProviderLoginRequest, ProviderStatus, SpendReport, UsageItem } from '~/api/types'
import { isApiError, useApiClient } from '~/api/client'
import { errorText } from '~/api/errors'
import { providerLabel, fmtCost as fmtCostLabel, COST_TITLE } from '~/api/labels'

/**
 * Ayarlar: LLM baglantilari (giris durumu, tek tikla giris/cikis, modeller) ve kullanim.
 * Giris: runtime, saglayicinin kendi akisini kullanicinin makinesinde baslatir (yeni konsol + tarayici):
 * Anthropic → Claude Code (`claude auth login`), OpenAI → Codex CLI (`codex login`). Sifre/token bu ekrandan GECMEZ.
 * Istisna API anahtari: kullanici yapistirir, runtime dogrulayip kullanici profiline yazar; ekranda yalniz maskeli sonu
 * gorunur. Anahtar kayitliyken CLI oturumu kullanilmaz; "Anahtari sil" oturuma dondurur (docs/DOMAIN.md → Model, efor ve kimlik).
 * Kullanim: bizim kayitlarimiz (runs/ turlari). Saglayici abonelik limitini/kalan kotayi CLI'dan vermiyor.
 */
const emit = defineEmits<{ close: []; changed: [] }>()
const api = useApiClient()

const providers = ref<ProviderStatus[] | null>(null)
const loadError = ref<string | null>(null)
const busy = ref<string | null>(null)
const notice = ref<{ provider: Provider; kind: 'info' | 'err'; text: string } | null>(null)
const email = ref('')
/** Yalniz `apikey` modunda gonderilir; basarili giristen sonra silinir, hicbir yerde tutulmaz. */
const apiKey = ref<Record<string, string>>({})

const usage = ref<UsageItem[] | null>(null)
const usageError = ref<string | null>(null)

// ------------------------------------------------------------------ limit korumasi (GET/PUT /settings)
const settings = ref<AppSettings | null>(null)
const settingsError = ref<string | null>(null)
const settingsSaved = ref(false)
const savingSettings = ref(false)
async function loadSettings() {
  try { settings.value = await api.get<AppSettings>('/api/v1/settings'); settingsError.value = null } catch (e) { settingsError.value = errorText(e) }
}
async function saveSettings() {
  if (!settings.value || savingSettings.value) return
  savingSettings.value = true
  settingsError.value = null
  settingsSaved.value = false
  try {
    settings.value = await api.put<AppSettings>('/api/v1/settings', settings.value)
    settingsSaved.value = true
    setTimeout(() => { settingsSaved.value = false }, 2500)
  } catch (e) {
    settingsError.value = errorText(e)
  } finally {
    savingSettings.value = false
  }
}
/** Saglayici listesi: ayarlarda olanlar + baglantisi olanlar (yeni saglayici gelince satir kendi acilir). */
const guardRows = computed(() => {
  const keys = new Set<string>(Object.keys(settings.value?.limitGuards ?? {}))
  for (const p of providers.value ?? []) keys.add(p.provider)
  return [...keys]
})
function guardOf(p: string): number { return settings.value?.limitGuards[p] ?? 99 }
function setGuard(p: string, v: string) {
  if (!settings.value) return
  const n = Math.round(Number(v))
  settings.value = { ...settings.value, limitGuards: { ...settings.value.limitGuards, [p]: Number.isFinite(n) ? Math.min(100, Math.max(1, n)) : 99 } }
}
function setCacheTtl(v: string) {
  if (!settings.value) return
  settings.value = { ...settings.value, cacheTtl: v === '5m' || v === '1h' ? v : null }
}

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
  void loadSpend()
}

/** Kim ne harcadi (kullanici istegi 2026-09-23): haftalik pencerede ofis ajani / Claude Code oturumlari, CLI kayitlarindan. */
const spend = ref<SpendReport | null>(null)
const spendError = ref<string | null>(null)
const spendOpen = ref<string | null>(null)
async function loadSpend() {
  try {
    spend.value = await api.get<SpendReport>('/api/v1/usage/split')
    spendError.value = null
  } catch (e) {
    spend.value = null
    spendError.value = errorText(e)
  }
}
const spendTotal = computed(() => (spend.value?.sources ?? []).reduce((s, x) => s + (x.costUsd ?? 0), 0))

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

async function login(p: Provider, mode: LoginMode) {
  if (busy.value) return
  const body: ProviderLoginRequest = { mode, email: email.value.trim() || null }
  if (mode === 'apikey') {
    body.apiKey = (apiKey.value[p] ?? '').trim()
    if (!body.apiKey) { notice.value = { provider: p, kind: 'err', text: 'API anahtarı boş.' }; return }
  }
  busy.value = `${p}:${mode}`
  notice.value = null
  try {
    const r = await api.post<LoginStarted>(`/api/v1/providers/${p}/login`, body)
    notice.value = { provider: p, kind: r.started ? 'info' : 'err', text: r.detail }
    if (mode === 'apikey') {
      if (r.started) { apiKey.value = { ...apiKey.value, [p]: '' }; await load(true) }
    } else if (r.started) {
      watchLogin(p)
    }
  } catch (e) {
    notice.value = { provider: p, kind: 'err', text: errorText(e) }
  } finally {
    busy.value = null
  }
}

/** Saglayici basina giris secenekleri: hangi CLI oturumu, hangi etiket. `apikey` ikisinde de var. */
const SESSION_LOGIN: Record<string, { mode: LoginMode; label: string; alt?: { mode: LoginMode; label: string } }> = {
  anthropic: { mode: 'claudeai', label: 'Giriş yap (Claude aboneliği)', alt: { mode: 'console', label: 'Konsol ile giriş (API faturası)' } },
  openai: { mode: 'chatgpt', label: 'ChatGPT ile giriş (Codex)' },
}
function keyLabel(p: Provider): string { return p === 'openai' ? 'OpenAI API anahtarı' : 'Anthropic API anahtarı' }

async function logout(p: Provider) {
  const st = providers.value?.find(x => x.provider === p)
  const q = st?.method === 'apikey'
    ? `${providerLabel(p)} API anahtarı silinsin mi? Varsa CLI oturumuna dönülür.`
    : `${providerLabel(p)} oturumu kapatılsın mı? Çalışmalar model çağıramaz.`
  if (busy.value || !window.confirm(q)) return
  busy.value = `${p}:logout`
  notice.value = null
  try {
    await api.post(`/api/v1/providers/${p}/logout`)
    await load(true)
    notice.value = { provider: p, kind: 'info', text: st?.method === 'apikey' ? 'API anahtarı silindi.' : 'Oturum kapatıldı.' }
  } catch (e) {
    notice.value = { provider: p, kind: 'err', text: errorText(e) }
  } finally {
    busy.value = null
  }
}

onMounted(() => { void load(); void loadUsage(); void loadSettings() })
onBeforeUnmount(() => clearInterval(watchTimer))

const totalCost = computed(() => (usage.value ?? []).reduce((s, u) => s + u.costUsd, 0))
function fmtCost(v: number): string { return fmtCostLabel(v, 4) }
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
        <p class="sub">Varsayılan kimlik CLI oturumudur: Anthropic için Claude Code (<code>claude auth login</code>), OpenAI için Codex (<code>codex login</code>); kota aboneliğinden düşer. Oturum bu makinedeki Windows kullanıcısına bağlıdır; giriş düğmesi kendi konsol penceresinde başlar, onayı tarayıcıda sen verirsin. İstersen API anahtarı da girebilirsin: doğrulanıp kullanıcı profiline yazılır, burada yalnız maskeli sonu görünür ve o sürede CLI oturumu kullanılmaz.</p>

        <p v-if="loadError" class="err" role="alert">{{ loadError }}</p>
        <p v-else-if="!providers" class="sub">Yükleniyor…</p>

        <article v-for="p in providers" :key="p.provider" class="card">
          <div class="card-head">
            <strong>{{ providerLabel(p.provider) }}</strong>
            <span class="badge" :class="p.loggedIn ? 'ok' : 'off'">{{ !p.loggedIn ? 'giriş yok' : p.method === 'apikey' ? 'API anahtarı' : 'giriş var' }}</span>
            <span v-if="p.account" class="sub">{{ p.account }}</span>
          </div>
          <p class="sub">{{ p.detail }}</p>

          <div v-if="!p.loggedIn && SESSION_LOGIN[p.provider]" class="login">
            <template v-if="p.provider === 'anthropic'">
              <label class="lbl" :for="`email-${p.provider}`">E-posta <span class="sub">(isteğe bağlı, giriş sayfasını doldurur)</span></label>
              <input :id="`email-${p.provider}`" v-model="email" type="email" autocomplete="off" placeholder="ad@ornek.com">
            </template>
            <div class="actions">
              <button class="primary" type="button" :disabled="!!busy" @click="login(p.provider, SESSION_LOGIN[p.provider]!.mode)">{{ SESSION_LOGIN[p.provider]!.label }}</button>
              <button v-if="SESSION_LOGIN[p.provider]!.alt" type="button" :disabled="!!busy" @click="login(p.provider, SESSION_LOGIN[p.provider]!.alt!.mode)">{{ SESSION_LOGIN[p.provider]!.alt!.label }}</button>
            </div>
          </div>
          <div v-else-if="p.loggedIn" class="actions">
            <button type="button" class="ghost" :disabled="!!busy" @click="logout(p.provider)">{{ p.method === 'apikey' ? 'Anahtarı sil' : 'Oturumu kapat' }}</button>
          </div>

          <!-- API anahtari: oturum olsa da girilebilir (anahtar one gecer). Alan gonderimden sonra bosaltilir. -->
          <details v-if="p.provider === 'anthropic' || p.provider === 'openai'" class="keybox" :open="!p.loggedIn && !SESSION_LOGIN[p.provider]">
            <summary>{{ p.method === 'apikey' ? 'Anahtarı değiştir' : 'API anahtarı ile kullan' }}</summary>
            <div class="login">
              <label class="lbl" :for="`key-${p.provider}`">{{ keyLabel(p.provider) }} <span class="sub">(doğrulanır, kullanıcı profiline yazılır; fatura sağlayıcı hesabına)</span></label>
              <div class="keyrow">
                <input :id="`key-${p.provider}`" :value="apiKey[p.provider] ?? ''" type="password" autocomplete="off" spellcheck="false" :placeholder="p.provider === 'openai' ? 'sk-…' : 'sk-ant-…'" @input="apiKey = { ...apiKey, [p.provider]: ($event.target as HTMLInputElement).value }">
                <button type="button" :disabled="!!busy || !(apiKey[p.provider] ?? '').trim()" @click="login(p.provider, 'apikey')">Kaydet</button>
              </div>
            </div>
          </details>

          <p v-if="notice && notice.provider === p.provider" :class="notice.kind === 'err' ? 'err' : 'ok'" role="status">{{ notice.text }}</p>

          <details class="models">
            <summary>Modeller ({{ p.models.length }})</summary>
            <ul>
              <li v-for="m in p.models" :key="m.model"><code>{{ m.model }}</code> <span class="sub">{{ m.reachable ? 'erişilebilir' : 'giriş gerekli' }}</span></li>
            </ul>
          </details>
        </article>
      </section>

      <!-- ---------------------------------------------------------- limit korumasi -->
      <section class="block">
        <div class="block-head">
          <h3>Limit koruması</h3>
          <button type="button" class="small primary" :disabled="savingSettings || !settings" @click="saveSettings">{{ savingSettings ? '…' : settingsSaved ? 'Kaydedildi ✓' : 'Kaydet' }}</button>
        </div>
        <p class="sub">Sağlayıcının aktif kota penceresi (5 saat, hafta) bu yüzdeye ulaşınca yeni LLM turu başlamaz: çalışma bekler, pencere sıfırlanınca kendisi sürer; bildirim zilinde görünür. Eşik platform bazında.</p>
        <p v-if="settingsError" class="err">{{ settingsError }}</p>
        <div v-else-if="settings" class="guards">
          <label v-for="p in guardRows" :key="p" class="guard">
            <span>{{ providerLabel(p as Provider) }}</span>
            <input type="number" min="1" max="100" step="1" :value="guardOf(p)" @change="setGuard(p, ($event.target as HTMLInputElement).value)">
            <span class="sub">%</span>
          </label>
        </div>
        <p v-else class="sub">Yükleniyor…</p>
      </section>

      <!-- ---------------------------------------------------------- istem onbellegi (deneme) -->
      <section class="block">
        <div class="block-head">
          <h3>İstem önbelleği</h3>
          <button type="button" class="small primary" :disabled="savingSettings || !settings" @click="saveSettings">{{ savingSettings ? '…' : settingsSaved ? 'Kaydedildi ✓' : 'Kaydet' }}</button>
        </div>
        <p class="sub">Anthropic ajanlarının bağlamı önbelleğe kaç süreliğine yazılsın. Ölçüm: önbelleğe yazma maliyetin ~%40'ı ve bugün hepsi 1 saatlik (2×). 5 dakikalık yazma 1,25× — hesapta ~%15 ucuz; ama 5 dakikayı aşan bir araç çağrısından (npm install, uzun test) sonra bağlam yeniden yazılır. Etki <code>scripts/context-report.py</code> ile okunur.</p>
        <label v-if="settings" class="guard">
          <span>Önbellek süresi</span>
          <select :value="settings.cacheTtl ?? ''" @change="setCacheTtl(($event.target as HTMLSelectElement).value)">
            <option value="">Varsayılan (1 saat)</option>
            <option value="5m">5 dakika (deneme)</option>
            <option value="1h">1 saat (açıkça)</option>
          </select>
        </label>
      </section>

      <!-- ---------------------------------------------------------- kim ne harcadi -->
      <section class="block">
        <div class="block-head">
          <h3>Kim ne harcadı</h3>
          <button type="button" class="small" @click="loadSpend">Yenile</button>
        </div>
        <p class="sub">Haftalık kota ortak: ofis ajanı ile Claude Code oturumların aynı hesaptan harcar. Makinedeki her çağrının kaydından (<code>~/.claude/projects</code>) okunur; kota payı eşdeğer $ oranıyla bölünür.</p>
        <p v-if="spendError" class="err">{{ spendError }}</p>
        <p v-else-if="!spend" class="sub">Yükleniyor…</p>
        <template v-else>
          <p class="sub">Pencere: {{ fmtWhen(spend.since) }} →<template v-if="spend.weeklyResetsAt"> sıfırlanma {{ fmtWhen(spend.weeklyResetsAt) }}</template><template v-if="spend.weeklyPercent !== null"> · haftalık <strong>%{{ Math.round(spend.weeklyPercent) }}</strong></template></p>
          <div class="spendbar" role="img" :aria-label="spend.sources.map(s => `${s.label} ${s.quotaPoints ?? '?'} puan`).join(', ')">
            <span v-for="s in spend.sources" :key="s.key" :class="s.key" :style="{ flexGrow: s.costUsd ?? 0 }" :title="s.label" />
          </div>
          <table class="usage">
            <thead><tr><th>Kaynak</th><th class="num">Mesaj</th><th class="num">Giriş tk</th><th class="num">Çıkış tk</th><th class="num" :title="COST_TITLE">≈ Maliyet</th><th class="num">Kota payı</th></tr></thead>
            <tbody>
              <template v-for="s in spend.sources" :key="s.key">
                <tr class="src" @click="spendOpen = spendOpen === s.key ? null : s.key">
                  <td><span class="dot" :class="s.key" /> {{ s.label }} <span class="sub">{{ spendOpen === s.key ? '▾' : '▸' }}</span></td>
                  <td class="num">{{ fmtNum(s.messages) }}</td><td class="num">{{ fmtNum(s.inputTokens) }}</td><td class="num">{{ fmtNum(s.outputTokens) }}</td>
                  <td class="num" :title="s.unpricedModels?.length ? `Fiyatsız: ${s.unpricedModels.join(', ')} (hariç)` : undefined">{{ s.costUsd === null ? 'ölçülemedi' : (s.unpricedModels?.length ? '≥ ' : '') + fmtCost(s.costUsd) }}</td>
                  <td class="num">{{ s.quotaPoints === null ? '—' : `${s.unpricedModels?.length ? '≥' : '~'}${s.quotaPoints} puan` }}</td>
                </tr>
                <template v-if="spendOpen === s.key">
                  <tr v-for="l in s.lines" :key="l.source + l.project + l.model" class="line">
                    <td :title="l.project"><code>{{ l.project.replace(/^C--/, '').slice(-42) }}</code> <span class="sub">{{ l.source }} · {{ l.model }}</span></td>
                    <td class="num">{{ fmtNum(l.messages) }}</td><td class="num">{{ fmtNum(l.inputTokens) }}</td><td class="num">{{ fmtNum(l.outputTokens) }}</td>
                    <td class="num">{{ l.costUsd === null ? '—' : fmtCost(l.costUsd) }}</td><td />
                  </tr>
                </template>
              </template>
            </tbody>
            <tfoot><tr><td colspan="4">Toplam</td><td class="num">{{ fmtCost(spendTotal) }}</td><td class="num">{{ spend.weeklyPercent === null ? '' : `%${Math.round(spend.weeklyPercent)}` }}</td></tr></tfoot>
          </table>
          <p class="sub">Ofisin kendi tur kaydı aynı pencerede: {{ spend.officeRecordedTurns }} tur · {{ fmtCost(spend.officeRecordedUsd) }} (kesilen turlar dahil).</p>
          <p v-for="(n, i) in spend.notes" :key="i" class="sub note">{{ n }}</p>
        </template>
      </section>

      <!-- ---------------------------------------------------------- kullanim -->
      <section class="block">
        <div class="block-head">
          <h3>Kullanım</h3>
          <button type="button" class="small" @click="loadUsage">Yenile</button>
        </div>
        <p class="sub" :title="COST_TITLE">Bu makinedeki çalışmaların kayıtlarından (<code>runs/</code>) toplanır: tur, token, <strong>eşdeğer maliyet</strong> (≈$: API liste fiyatına göre; Claude Code aboneliğiyle ücret kesilmez, kota penceresi tükenir). Kalan kota üst barda.</p>
        <p v-if="usageError" class="err">{{ usageError }}</p>
        <p v-else-if="!usage" class="sub">Yükleniyor…</p>
        <p v-else-if="!usage.length" class="sub">Henüz kayıtlı bir model çağrısı yok.</p>
        <table v-else class="usage">
          <thead>
            <tr><th>Sağlayıcı</th><th>Model</th><th class="num">Tur</th><th class="num">Çalışma</th><th class="num">Giriş tk</th><th class="num">Çıkış tk</th><th class="num" :title="COST_TITLE">≈ Maliyet</th><th>Son</th></tr>
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
  background: #ede9dc; color: #23283a; border-left: 6px solid #6b4a2b;
  width: min(640px, 100%); height: 100%; overflow: auto; padding: 16px 18px;
  box-shadow: -20px 0 60px rgba(0,0,0,0.5); display: flex; flex-direction: column; gap: 16px;
}
.panel > header { display: flex; align-items: center; gap: 10px; }
h2 { margin: 0; font-size: 16px; letter-spacing: 0.04em; text-transform: uppercase; }
h3 { margin: 0; font-size: 13px; text-transform: uppercase; letter-spacing: 0.06em; color: #4a5068; }
/* Kapat: 28x28 tiklama alani, isaret tam ortada, ustune gelince hafif zemin (tum panellerde ayni). */
.x {
  margin-left: auto; width: 28px; height: 28px; flex: none; display: inline-flex; align-items: center; justify-content: center;
  background: none; border: none; border-radius: 6px; padding: 0; font-size: 20px; line-height: 1; color: #23283a; cursor: pointer;
}
.x:hover { background: rgba(35,40,58,0.10); }
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
input[type="email"], input[type="password"] { font: inherit; font-size: 13px; background: #fff; color: #23283a; border: 1px solid #c9c3b3; border-radius: 4px; padding: 6px 8px; width: 100%; }
.keybox summary { cursor: pointer; font-size: 12px; font-weight: 600; color: #4a5068; }
.keybox .login { margin-top: 6px; }
.keyrow { display: flex; gap: 6px; }
.keyrow input { flex: 1; min-width: 0; }
input:focus { outline: 2px solid #4f8ef7; outline-offset: 0; }
.actions { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; margin-top: 4px; }
button { font: inherit; cursor: pointer; border-radius: 4px; padding: 7px 14px; border: 1px solid #c9c3b3; background: #fff; color: #23283a; }
button:disabled { opacity: 0.5; cursor: default; }
.primary { background: #23283a; color: #fff; border-color: #23283a; }
.guards { display: flex; flex-direction: column; gap: 6px; }
.guard { display: grid; grid-template-columns: 140px 90px auto; align-items: center; gap: 8px; font-size: 13px; }
.guard select { font: inherit; font-size: 13px; background: #fff; color: #23283a; border: 1px solid #c9c3b3; border-radius: 4px; padding: 5px 8px; grid-column: span 2; }
.guard input { font: inherit; font-size: 13px; background: #fff; color: #23283a; border: 1px solid #c9c3b3; border-radius: 4px; padding: 5px 8px; }
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
.usage tr.src { cursor: pointer; }
.usage tr.src:hover { background: #f6f3ea; }
.usage tr.line td { font-size: 11px; color: #4a5068; padding-left: 18px; }
.spendbar { display: flex; height: 10px; border-radius: 999px; overflow: hidden; background: #e5e7ee; border: 1px solid #c9c3b3; margin: 4px 0 6px; }
.spendbar span { flex-basis: 0; }
.spendbar .office, .dot.office { background: #4fa3e0; }
.spendbar .sessions, .dot.sessions { background: #ef9b4f; }
.dot { display: inline-block; width: 8px; height: 8px; border-radius: 50%; vertical-align: middle; }
.note { font-style: italic; }
</style>
