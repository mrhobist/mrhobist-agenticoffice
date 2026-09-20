<script setup lang="ts">
import type { DirectoryListing } from '~/api/types'
import { useApiClient } from '~/api/client'
import { errorText } from '~/api/errors'

/**
 * Hedef dizin secici (kullanici karari 2026-09-20: serbest metin yalniz ad ve aciklamada). Depo icindeki klasorler
 * `GET /projects/dirs?path=` ile bir seviye bir seviye gezilir; deger her zaman depo kokune gore ileri bolulu yoldur.
 * Iki secim yolu: var olan bir klasoru "burayi sec" ya da gezilen klasorun icinde `<key>` adli yeni klasor.
 * Bos deger = varsayilan (`projects/<key>`); sunucu bosu oyle yorumlar.
 */
const props = defineProps<{ modelValue: string; projectKey: string; id?: string }>()
const emit = defineEmits<{ 'update:modelValue': [v: string] }>()
const api = useApiClient()

const open = ref(false)
const path = ref('')
const listing = ref<DirectoryListing | null>(null)
const error = ref<string | null>(null)
const busy = ref(false)

const defaultDir = computed(() => `projects/${props.projectKey || 'anahtar'}`)
const shown = computed(() => props.modelValue || defaultDir.value)
const isDefault = computed(() => !props.modelValue)
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
    // Mevcut degerin ust klasorunden basla: kullanici nerede oldugunu gorur.
    const cur = props.modelValue || 'projects'
    void go(cur.includes('/') ? cur.slice(0, cur.lastIndexOf('/')) : cur)
  }
}

function pick(v: string) {
  emit('update:modelValue', v === defaultDir.value ? '' : v)
  open.value = false
}

const crumbs = computed(() => {
  const parts = path.value ? path.value.split('/') : []
  return parts.map((name, i) => ({ name, path: parts.slice(0, i + 1).join('/') }))
})
</script>

<template>
  <div class="picker">
    <div class="current">
      <code :id="id" :class="{ dim: isDefault }">{{ shown }}</code>
      <span v-if="isDefault" class="sub">varsayılan</span>
      <button type="button" class="small" @click="toggle">{{ open ? 'Kapat' : 'Klasör seç…' }}</button>
      <button v-if="!isDefault" type="button" class="small" title="Varsayılana dön" @click="pick(defaultDir)">↺</button>
    </div>

    <div v-if="open" class="browser" role="dialog" aria-label="Klasör seç">
      <nav class="crumbs" aria-label="Yol">
        <button type="button" class="crumb" :class="{ on: !path }" @click="go('')">depo</button>
        <template v-for="c in crumbs" :key="c.path">
          <span class="sep">/</span>
          <button type="button" class="crumb" :class="{ on: c.path === path }" @click="go(c.path)">{{ c.name }}</button>
        </template>
      </nav>

      <ul class="list" :aria-busy="busy">
        <li v-if="listing?.parent !== null && listing?.parent !== undefined">
          <button type="button" class="dir up" @click="go(listing!.parent!)">‹ üst klasör</button>
        </li>
        <li v-for="d in listing?.dirs ?? []" :key="d.path">
          <button type="button" class="dir" :title="d.path" @click="go(d.path)"><span class="ico" aria-hidden="true">📁</span> {{ d.name }}</button>
        </li>
        <li v-if="listing && !listing.dirs.length" class="sub empty">alt klasör yok</li>
      </ul>

      <div class="choose">
        <button v-if="path" type="button" class="small strong" @click="pick(path)">Burayı seç: <code>{{ path }}</code></button>
        <button type="button" class="small" @click="pick(newHere)">Yeni klasör: <code>{{ newHere }}</code></button>
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
.crumb:hover { background: rgba(0,0,0,0.06); }
.sep { color: #9aa0b3; }
.list { list-style: none; margin: 0; padding: 0; max-height: 180px; overflow: auto; display: flex; flex-direction: column; gap: 2px; }
.dir { font: inherit; font-size: 13px; width: 100%; display: flex; align-items: center; gap: 6px; text-align: left; background: #fff; border: 1px solid #e3ded0; border-radius: 4px; padding: 5px 8px; color: #23283a; cursor: pointer; }
/* Klasor isareti: adla ayni eksende, biraz sonuk. */
.ico { font-size: 0.95em; line-height: 1; opacity: 0.8; flex: none; }
.dir:hover { border-color: #4f8ef7; }
.dir.up { color: #6b7285; background: transparent; border-style: dashed; }
.empty { padding: 6px 8px; }
.choose { display: flex; flex-wrap: wrap; gap: 6px; }
.choose code { font-size: 11px; }
.err { margin: 0; color: #b3261e; font-size: 12px; }
</style>
