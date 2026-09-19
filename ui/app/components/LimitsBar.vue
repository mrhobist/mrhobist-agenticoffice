<script setup lang="ts">
import type { ProviderLimits, UsageLimit } from '~/api/types'
import { isApiError, useApiClient } from '~/api/client'
import { providerLabel } from '~/api/labels'
import { errorText } from '~/api/errors'

/**
 * Ust bar: aktif saglayicilarin KALAN HAKKI (5 saat, hafta, modele ozel...). 2 dk'da bir yoklar;
 * runtime 90 s onbellekler. Tiklaninca Ayarlar acilir. Uc durum ayri soylenir (LESSONS: yanlis teshis saat yer):
 *  - down: runtime'a ulasilamiyor (503 runtime.unavailable)
 *  - broken: runtime ayakta, kota ucu hata verdi (502 runtime.error)
 *  - ready + available=false: saglayici vermiyor, neden yaninda (oturum yok, 429, ayristirilamadi...)
 */
const emit = defineEmits<{ open: [] }>()
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

/** kind → kisa etiket. Bilinmeyen kind oldugu gibi gosterilir; scope (model adi) varsa one gecer. */
const KIND_LABEL: Record<string, string> = {
  session: '5 saat',
  five_hour: '5 saat',
  weekly_all: 'hafta',
  seven_day: 'hafta',
  weekly_opus: 'Opus/hafta',
  seven_day_opus: 'Opus/hafta',
  weekly_sonnet: 'Sonnet/hafta',
  seven_day_sonnet: 'Sonnet/hafta',
  weekly_scoped: 'model/hafta',
}
function label(l: UsageLimit): string {
  if (l.scope) return `${l.scope}/${KIND_LABEL[l.kind]?.split('/').pop() ?? l.kind}`
  return KIND_LABEL[l.kind] ?? l.kind.replaceAll('_', ' ')
}
function remaining(l: UsageLimit): number { return Math.max(0, Math.min(100, Math.round(100 - l.percent))) }
function tone(l: UsageLimit): string {
  const r = remaining(l)
  if (l.severity === 'critical' || r <= 10) return 'crit'
  if (l.severity === 'warning' || r <= 30) return 'warn'
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
  return `${providerLabel(p.provider)} · ${label(l)}: %${remaining(l)} kaldı (kullanılan %${Math.round(l.percent)}). ${resetText(l)}`
}
const active = computed(() => (items.value ?? []).filter(p => p.available))
const unavailable = computed(() => (items.value ?? []).filter(p => !p.available))
</script>

<template>
  <div class="limits" role="status">
    <template v-if="state === 'ready' && items">
      <button v-for="p in active" :key="p.provider" type="button" class="prov" :title="p.subscription ? `${providerLabel(p.provider)} · ${p.subscription}` : providerLabel(p.provider)" @click="emit('open')">
        <span class="name">{{ providerLabel(p.provider) }}</span>
        <!-- isActive = su an baglayici pencere; digerleri de gosterilir, soluk. -->
        <span v-for="l in p.limits" :key="l.kind + (l.scope ?? '')" class="meter" :class="[tone(l), { dim: !l.isActive }]" :title="title(p, l)">
          <span class="lbl">{{ label(l) }}</span>
          <span class="bar"><span class="fill" :style="{ width: remaining(l) + '%' }" /></span>
          <span class="pct">%{{ remaining(l) }}</span>
        </span>
        <span v-if="!p.limits.length" class="sub">kalan hak bilgisi yok</span>
      </button>
      <button v-for="p in unavailable" :key="p.provider" type="button" class="prov off" :title="p.detail" @click="emit('open')">
        <span class="name">{{ providerLabel(p.provider) }}</span>
        <span class="sub">kalan hak alınamadı · {{ p.detail }}</span>
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
  display: flex; align-items: center; gap: 8px; font: inherit; cursor: pointer; color: var(--ink-2);
  background: var(--surface-2); border: 1px solid var(--rule); border-radius: 999px; padding: 3px 10px 3px 8px;
}
.prov:hover { border-color: #3d5a80; }
.prov.off { color: var(--ink-3); }
.prov.off.bad { border-color: #8a3a3a; }
.prov.off.bad .sub { color: #f0a0a0; }
.name { font-size: 11px; font-weight: 600; letter-spacing: 0.02em; }
.meter { display: inline-flex; align-items: center; gap: 5px; font-size: 11px; }
.lbl { color: var(--ink-3); }
.bar { width: 46px; height: 6px; border-radius: 3px; background: rgba(255,255,255,0.08); overflow: hidden; }
.fill { display: block; height: 100%; border-radius: 3px; background: #35b98a; }
.meter.warn .fill { background: #d99b3a; }
.meter.crit .fill { background: #e05252; }
.pct { font-variant-numeric: tabular-nums; min-width: 34px; text-align: right; }
.meter.warn .pct { color: #f0c26a; }
.meter.crit .pct { color: #f0a0a0; }
.meter.dim { opacity: 0.7; }
.sub { font-size: 11px; color: var(--ink-3); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; max-width: 320px; }
</style>
