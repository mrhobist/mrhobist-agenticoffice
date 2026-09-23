<script setup lang="ts">
import type { ProviderLimits, UsageLimit } from '~/api/types'
import { isApiError, useApiClient } from '~/api/client'
import { providerLabel } from '~/api/labels'
import { errorText } from '~/api/errors'

/**
 * Ust bar: aktif saglayicilarin KULLANDIGI kota (saatlik, haftalik, modele ozel). 2 dk'da bir yoklar;
 * runtime 90 s onbellekler. Tiklaninca Ayarlar acilir. Uc durum ayri soylenir (LESSONS: yanlis teshis saat yer):
 *  - down: runtime'a ulasilamiyor (503 runtime.unavailable)
 *  - broken: runtime ayakta, kota ucu hata verdi (502 runtime.error)
 *  - ready + available=false: saglayici vermiyor, neden yaninda (oturum yok, 429, ayristirilamadi...)
 *
 * Halka KULLANILANI gosterir (kullanici karari 2026-09-20): saglayicinin kendi `/usage` ekrani da
 * boyle okur, "%87 doldu" ile "%13 kaldi" arasinda gidip gelmek karisikligi buyutuyordu.
 */
const emit = defineEmits<{ open: [] }>()
/** Girisi olmayan saglayicilar (app.vue → GET /providers). Kota alinamama sebebi bunlarda "giris yok"tur. */
const props = withDefaults(defineProps<{ signedOut?: string[] }>(), { signedOut: () => [] })
const api = useApiClient()

const items = ref<ProviderLimits[] | null>(null)
const state = ref<'loading' | 'ready' | 'down' | 'broken' | 'error'>('loading')
const failText = ref('')
let timer: ReturnType<typeof setInterval> | undefined

async function load(refresh = false) {
  try {
    items.value = await api.get<ProviderLimits[]>(`/api/v1/limits${refresh ? '?refresh=true' : ''}`)
    state.value = 'ready'
  } catch (e) {
    if (isApiError(e) && (e.errorCode === 'runtime.unavailable' || e.endpointMissing)) state.value = 'down'
    else if (isApiError(e) && e.errorCode === 'runtime.error') state.value = 'broken'
    else state.value = 'error'
    failText.value = errorText(e)
  }
}
defineExpose({ reload: () => load(true) })

// Saglayicinin kullanim ucu sik sorguya 429 doner: 2 dk'da bir yeter, runtime da 90 s onbellekler.
onMounted(() => { void load(); timer = setInterval(() => { void load() }, 120_000) })
onBeforeUnmount(() => clearInterval(timer))

/** kind → uzun etiket (ipucunda). Bilinmeyen kind oldugu gibi gosterilir; scope (model adi) varsa one gecer. */
const KIND_LABEL: Record<string, string> = {
  session: 'saatlik',
  five_hour: 'saatlik',
  weekly_all: 'haftalık',
  seven_day: 'haftalık',
  weekly_opus: 'Opus/hafta',
  seven_day_opus: 'Opus/hafta',
  weekly_sonnet: 'Sonnet/hafta',
  seven_day_sonnet: 'Sonnet/hafta',
  weekly_scoped: 'modele özel/hafta',
}
function label(l: UsageLimit): string {
  if (l.scope) return `${l.scope}/hafta`
  return KIND_LABEL[l.kind] ?? l.kind.replaceAll('_', ' ')
}
/**
 * Halka altindaki tek kelimelik etiket: saatlik · haftalik · model adi (Fable, Opus...).
 * `scope` ust uctan nesne gelir ve runtime adi cikarir; bos gelirse modele ozel pencere
 * "haftalik" ile ayni etiketi tasiyip iki halkayi ayirt edilemez yapiyordu (2026-09-20).
 */
function shortLabel(l: UsageLimit): string {
  if (l.scope) return l.scope.replace(/^claude-/, '').split('-')[0] ?? l.scope
  const k = l.kind
  if (k === 'session' || k === 'five_hour') return 'saatlik'
  if (k.includes('opus')) return 'Opus'
  if (k.includes('sonnet')) return 'Sonnet'
  if (k === 'weekly_scoped') return 'model'
  if (k.includes('week') || k.includes('seven')) return 'haftalık'
  return k.slice(0, 7)
}
/** Kota alinamadiginda tek kelimelik neden: API anahtari · oturum yok · 429 · yok. */
function shortReason(provider: string, detail: string): string {
  // Giris yoksa kota zaten alinamaz; kullaniciya kok sebep soylenir, tureyen degil.
  if (props.signedOut.includes(provider)) return 'giriş yok'
  const d = detail.toLowerCase()
  if (d.includes('api anahtar')) return 'API anahtarı · kota yok'
  if (d.includes('oturum')) return 'giriş yok'
  if (d.includes('429')) return 'kota ucu meşgul'
  if (d.includes('dışa vermiyor') || d.includes('vermiyor')) return 'kota bilgisi yok'
  return 'alınamadı'
}
/** Halkada yazan sayi: KULLANILAN yuzde (saglayicinin `/usage` ekraniyla ayni okuma). */
function used(l: UsageLimit): number { return Math.max(0, Math.min(100, Math.round(l.percent))) }
function remaining(l: UsageLimit): number { return 100 - used(l) }
function tone(l: UsageLimit): string {
  const u = used(l)
  if (l.severity === 'critical' || u >= 90) return 'crit'
  if (l.severity === 'warning' || u >= 70) return 'warn'
  return 'ok'
}
function resetText(l: UsageLimit): string {
  if (!l.resetsAt) return ''
  const ms = new Date(l.resetsAt).getTime() - Date.now()
  if (ms <= 0) return 'sıfırlanıyor'
  const h = Math.floor(ms / 3_600_000)
  const m = Math.floor((ms % 3_600_000) / 60_000)
  const d = Math.floor(h / 24)
  return d >= 1 ? `${d} g ${h % 24} s sonra sıfırlanır` : h >= 1 ? `${h} s ${m} dk sonra sıfırlanır` : `${m} dk sonra sıfırlanır`
}
function title(p: ProviderLimits, l: UsageLimit): string {
  return `${providerLabel(p.provider)} · ${label(l)}: kullanılan %${used(l)} (kalan %${remaining(l)}). ${resetText(l)}`
}
const active = computed(() => (items.value ?? []).filter(p => p.available))
const unavailable = computed(() => (items.value ?? []).filter(p => !p.available))
</script>

<template>
  <div class="limits" role="status">
    <template v-if="state === 'ready' && items">
      <!-- Minimal (kullanici istegi 2026-09-20): saglayici adi + her pencere icin yuvarlak yuzde halkasi; ayrinti ipucunda. -->
      <button v-for="p in active" :key="p.provider" type="button" class="prov" :title="p.subscription ? `${providerLabel(p.provider)} · ${p.subscription}` : providerLabel(p.provider)" @click="emit('open')">
        <span class="name">{{ providerLabel(p.provider) }}<Ico v-if="p.detail.startsWith('son bilinen')" class="stale" name="clock" :size="11" title="Son bilinen değer; kota ucu şu an yanıt vermiyor" /></span>
        <span v-for="l in p.limits" :key="l.kind + (l.scope ?? '')" class="gauge" :class="{ dim: !l.isActive }" :title="title(p, l)">
          <span class="ring" :class="tone(l)" :style="{ '--p': used(l) }"><span class="pct">{{ used(l) }}</span></span>
          <span class="tag">{{ shortLabel(l) }}</span>
        </span>
        <span v-if="!p.limits.length" class="sub">—</span>
      </button>
      <button v-for="p in unavailable" :key="p.provider" type="button" class="prov off quiet" :title="props.signedOut.includes(p.provider) ? `${providerLabel(p.provider)}: giriş yok. Kullanmıyorsanız gerekmez; kullanacaksanız Ayarlar'dan giriş yapın.` : p.detail" @click="emit('open')">
        <span class="name">{{ providerLabel(p.provider) }}</span>
        <span class="sub short">{{ shortReason(p.provider, p.detail) }}</span>
      </button>
    </template>
    <span v-else-if="state === 'loading'" class="sub">kalan hak…</span>
    <button v-else-if="state === 'down'" type="button" class="prov off" :title="`Runtime'a ulaşılamıyor; kalan hak alınamıyor. ${failText}`" @click="emit('open')">
      <span class="name">kalan hak</span><span class="sub">runtime kapalı</span>
    </button>
    <button v-else-if="state === 'broken'" type="button" class="prov off bad" :title="`Runtime ayakta ama kota ucu hata verdi. ${failText}`" @click="emit('open')">
      <span class="name">kalan hak</span><span class="sub">kota ucu hata verdi (runtime)</span>
    </button>
    <button v-else type="button" class="prov off" :title="failText" @click="emit('open')">
      <span class="name">kalan hak</span><span class="sub">alınamadı</span>
    </button>
  </div>
</template>

<style scoped>
.limits { display: flex; align-items: center; gap: 8px; min-width: 0; overflow: hidden; }
.prov {
  display: flex; align-items: center; gap: 9px; font: inherit; cursor: pointer; color: var(--ink-2);
  background: var(--surface-2); border: 1px solid var(--rule); border-radius: 999px; padding: 4px 12px 4px 10px; min-height: 38px;
}
.prov:hover { border-color: #3d5a80; }
.prov.off { color: var(--ink-3); }
/* Kullanilmayan saglayici (orn. Codex kurulu degil) surekli goz almasin: kucuk, soluk, hover'da acilir. */
.prov.off.quiet { opacity: 0.6; padding: 3px 10px; min-height: 30px; }
.prov.off.quiet:hover { opacity: 1; }
.prov.off.bad { border-color: #8a3a3a; }
.prov.off.bad .sub { color: #f0a0a0; }
.name { font-size: 11px; font-weight: 600; letter-spacing: 0.02em; }
/* Gosterge: halka + ALTINDA kisa etiket. Etiket eskiden halkaya `bottom: -9px` ile asiliydi;
   cipin icine sigmadigi icin kirpiliyordu (kullanici 2026-09-20). Artik ikisi alt alta akiyor. */
.gauge { display: inline-flex; flex-direction: column; align-items: center; gap: 1px; flex: none; }
.gauge.dim { opacity: 0.55; }
.ring {
  --p: 0; position: relative; width: 26px; height: 26px; border-radius: 50%; flex: none;
  background: conic-gradient(var(--c, #35b98a) calc(var(--p) * 1%), rgba(255,255,255,0.1) 0);
  display: inline-grid; place-items: center;
}
.ring::before { content: ''; position: absolute; inset: 3px; border-radius: 50%; background: var(--surface-2); }
.ring .pct { position: relative; font-size: 9px; font-weight: 700; font-variant-numeric: tabular-nums; color: var(--ink); line-height: 1; }
.tag { font-size: 7.5px; line-height: 1; color: var(--ink-3); white-space: nowrap; letter-spacing: 0.02em; }
.ring.warn { --c: #d99b3a; }
.ring.crit { --c: #e05252; }
.sub.short { max-width: 140px; }
.stale { margin-left: 4px; color: var(--ink-3); }
.sub { font-size: 11px; color: var(--ink-3); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; max-width: 320px; }
</style>
