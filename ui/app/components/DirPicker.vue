<script setup lang="ts">
import type { DirectoryListing } from '~/api/types'
import { useApiClient } from '~/api/client'
import { errorText } from '~/api/errors'

/**
 * Hedef dizin secici (kullanici karari 2026-09-20: serbest metin yalniz ad ve aciklamada). Depo icindeki klasorler
 * `GET /projects/dirs?path=` ile bir seviye bir seviye gezilir; deger her zaman depo kokune gore ileri bolulu yoldur.
 * Iki secim yolu: var olan bir klasoru "burayi sec" ya da gezilen klasorun icinde `<key>` adli yeni klasor.
 * Bos deger = varsayilan (`projects/<key>`); sunucu bosu oyle yorumlar.
 * Ucuncu yol (2026-09-23): depo DISI suruculu tam yol (ör. `D:/Work/Anket`) elle yazilir.
 * 2026-09-26 (iceri alma): secici surucu koklerinden baslayarak depo disini da gezer; surucu kokunun kendisi secilemez.
 * Sunucu dogrular (surucu koku ve `..` reddedilir); depo disi klasor uygulamadan hic silinmez.
 *
 * `mode="import"`: var olan bir klasor secilir -- varsayilan yok, "yeni klasor" yok; bos deger "secilmedi" demek.
 */
const props = withDefaults(defineProps<{ modelValue: string; projectKey: string; id?: string; mode?: 'target' | 'import' }>(), { id: undefined, mode: 'target' })
const emit = defineEmits<{ 'update:modelValue': [v: string] }>()
const api = useApiClient()

const open = ref(false)
const path = ref('')
const listing = ref<DirectoryListing | null>(null)
const error = ref<string | null>(null)
const busy = ref(false)
const external = ref('')
/** Suruculu tam yol mu (sunucudaki Project.IsExternalDir ile ayni kural; son karar sunucunun). */
const externalOk = computed(() => /^[A-Za-z]:[\\/].+$/.test(external.value.trim()) && !external.value.includes('..'))

const importing = computed(() => props.mode === 'import')
const defaultDir = computed(() => `projects/${props.projectKey || 'anahtar'}`)
const shown = computed(() => props.modelValue || (importing.value ? 'klasör seçilmedi' : defaultDir.value))
const isDefault = computed(() => !props.modelValue)
/** Gezilen yer depo disi mi (suruculu yol): kirintilar surucuden baslar. */
const outside = computed(() => /^[A-Za-z]:/.test(path.value))
/** Gezilen yer bu surucude mi: kirintida surucu dugmesi vurgulanir, kirinti suruden sonrasini gosterir. */
function onDrive(d: string): boolean { return outside.value && path.value.slice(0, 2).toUpperCase() === d.slice(0, 2).toUpperCase() }
/** Surucu koku (`C:/`) gezilir ama secilemez: sunucu reddeder, butun diski proje yapmak anlamsiz. */
const isDriveRoot = computed(() => /^[A-Za-z]:\/?$/.test(path.value))
/** Gezilen klasorun icinde acilacak yeni klasorun yolu. */
const newHere = computed(() => (path.value ? `${path.value}/` : '') + (props.projectKey || 'anahtar'))

async function go(p: string) {
  busy.value = true
  error.value = null
  try {
    listing.value = await api.get<DirectoryListing>(`/api/v1/projects/dirs?path=${encodeURIComponent(p)}`)
    path.value = listing.value.path
  } catch (e) {
    error.value = errorText(e)
  } finally {
    busy.value = false
  }
}

function toggle() {
  open.value = !open.value
  if (open.value) {
    // Mevcut degerin ust klasorunden basla: kullanici nerede oldugunu gorur. Iceri almada deger yoksa depo kokunden
    // (surucu dugmeleri oradan da gorunur).
    const cur = props.modelValue || (importing.value ? '' : 'projects')
    void go(cur.includes('/') ? cur.slice(0, cur.lastIndexOf('/')) : cur)
  }
}

function pickExternal() {
  if (externalOk.value) pick(external.value.trim().replace(/\\/g, '/').replace(/\/+$/, ''))
}

function pick(v: string) {
  emit('update:modelValue', !importing.value && v === defaultDir.value ? '' : v)
  open.value = false
}

const crumbs = computed(() => {
  const parts = path.value ? path.value.split('/').filter(Boolean) : []
  return parts.map((name, i) => ({ name, path: parts.slice(0, i + 1).join('/') }))
})
</script>

<template>
  <div class="picker">
    <div class="current">
      <code :id="id" :class="{ dim: isDefault }">{{ shown }}</code>
      <span v-if="isDefault && !importing" class="sub">varsayılan</span>
      <button type="button" class="small" @click="toggle">{{ open ? 'Kapat' : 'Klasör seç…' }}</button>
      <button v-if="!isDefault && !importing" type="button" class="small ico-btn" title="Varsayılana dön" @click="pick(defaultDir)"><Ico name="refresh" :size="13" /></button>
    </div>

    <div v-if="open" class="browser" role="dialog" aria-label="Klasör seç">
      <nav class="crumbs" aria-label="Yol">
        <button type="button" class="crumb" :class="{ on: !path }" @click="go('')">depo</button>
        <!-- Depo disi (2026-09-26): surucu kokleri; secili surucu kirintinin basinda da gorunur. -->
        <button v-for="d in listing?.drives ?? []" :key="d" type="button" class="crumb drive" :class="{ on: isDriveRoot && onDrive(d), here: onDrive(d) }" :title="`${d} sürücüsünü gez`" @click="go(d)">{{ d.slice(0, 2) }}</button>
        <template v-for="c in (outside ? crumbs.slice(1) : crumbs)" :key="c.path">
          <span class="sep">/</span>
          <button type="button" class="crumb" :class="{ on: c.path === path }" @click="go(c.path)">{{ c.name }}</button>
        </template>
      </nav>

      <ul class="list" :aria-busy="busy">
        <li v-if="listing?.parent !== null && listing?.parent !== undefined">
          <button type="button" class="dir up" @click="go(listing!.parent!)">‹ üst klasör</button>
        </li>
        <li v-for="d in listing?.dirs ?? []" :key="d.path">
          <button type="button" class="dir" :title="d.path" @click="go(d.path)"><Ico name="folder" :size="13" /> {{ d.name }}</button>
        </li>
        <li v-if="listing && !listing.dirs.length" class="sub empty">alt klasör yok</li>
      </ul>

      <div class="choose">
        <button v-if="path && !isDriveRoot" type="button" class="small strong" @click="pick(path)">Burayı seç: <code>{{ path }}</code></button>
        <span v-else-if="isDriveRoot" class="sub">Sürücü kökü seçilemez; bir klasöre gir.</span>
        <button v-if="!importing" type="button" class="small" @click="pick(newHere)">Yeni klasör: <code>{{ newHere }}</code></button>
      </div>
      <div class="external">
        <label class="sub" :for="`${id ?? 'dir'}-ext`">Depo dışı tam yol</label>
        <input :id="`${id ?? 'dir'}-ext`" v-model="external" type="text" placeholder="D:\Work\Klasor" spellcheck="false" @keydown.enter.prevent="pickExternal">
        <button type="button" class="small" :disabled="!externalOk" @click="pickExternal">Bu yolu kullan</button>
      </div>
      <p v-if="error" class="err" role="alert">{{ error }}</p>
    </div>
  </div>
</template>

<style scoped>
.picker { display: flex; flex-direction: column; gap: 6px; min-width: 0; }
.current { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; min-height: 33px; }
.current code { font-size: 12px; background: rgba(0,0,0,0.06); padding: 4px 7px; border-radius: 4px; color: #23283a; }
.current code.dim { color: #6b7285; }
.sub { font-size: 11px; color: #6b7285; }
.small { font: inherit; font-size: 12px; padding: 4px 9px; border-radius: 4px; border: 1px solid #c9c3b3; background: #fff; color: #23283a; cursor: pointer; }
.small.strong { border-color: #23283a; font-weight: 700; }
.browser { border: 1px solid #c9c3b3; border-radius: 6px; background: #fbf9f3; padding: 8px; display: flex; flex-direction: column; gap: 6px; }
.crumbs { display: flex; align-items: center; flex-wrap: wrap; gap: 2px; font-size: 12px; }
.crumb { font: inherit; font-size: 12px; background: none; border: none; color: #4a5068; cursor: pointer; padding: 2px 4px; border-radius: 3px; }
.crumb.on { color: #23283a; font-weight: 700; }
.crumb.drive { border: 1px solid #d8d2c2; font-family: ui-monospace, monospace; font-size: 11px; padding: 1px 5px; }
.crumb.drive.here { border-color: #23283a; color: #23283a; font-weight: 700; }
.crumb:hover { background: rgba(0,0,0,0.06); }
.sep { color: #9aa0b3; }
.list { list-style: none; margin: 0; padding: 0; max-height: 180px; overflow: auto; display: flex; flex-direction: column; gap: 2px; }
.dir { font: inherit; font-size: 13px; width: 100%; display: flex; align-items: center; gap: 6px; text-align: left; background: #fff; border: 1px solid #e3ded0; border-radius: 4px; padding: 5px 8px; color: #23283a; cursor: pointer; }
.ico-btn { display: inline-flex; align-items: center; gap: 5px; }
.dir:hover { border-color: #4f8ef7; }
.dir.up { color: #6b7285; background: transparent; border-style: dashed; }
.empty { padding: 6px 8px; }
.choose { display: flex; flex-wrap: wrap; gap: 6px; }
.choose code { font-size: 11px; }
.external { display: flex; align-items: center; gap: 6px; flex-wrap: wrap; border-top: 1px dashed #e3ded0; padding-top: 6px; }
.external input { font: inherit; font-size: 12px; flex: 1 1 180px; min-width: 0; padding: 4px 7px; border: 1px solid #c9c3b3; border-radius: 4px; background: #fff; }
.err { margin: 0; color: #b3261e; font-size: 12px; }
</style>
