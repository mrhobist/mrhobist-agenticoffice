<script setup lang="ts">
import type { AtlasJson } from '~/scene/atlas'
import { useApiClient } from '~/api/client'

/**
 * Ajanin ofisteki karakteri (kullanici istegi 2026-09-23). Secenekler sahnenin `sprites[]` listesi (GET /scene); onizleme
 * yuruyus sayfasinin "asagi" satirinin ilk karesi (atlas.json). Secim `sprite` alaniyla ajan kaydina gider; sahne yeniden kurulur.
 * `modelValue` null = otomatik (yeni ajanda bos ilk karakter, var olanda degismez).
 */
const props = defineProps<{
  modelValue: string | null
  /** karakter → onu kullanan ajan adlari (bu ajan haric). Ayni karakter secilebilir; yalniz bilgi. */
  usedBy?: Record<string, string[]>
  /** Yeni ajan formunda "otomatik" secenegi gosterilir. */
  allowAuto?: boolean
}>()
const emit = defineEmits<{ 'update:modelValue': [value: string | null] }>()

const api = useApiClient()
const sprites = ref<string[]>([])
const atlas = ref<AtlasJson | null>(null)
const error = ref<string | null>(null)

/** Onizleme yuksekligi (px); kare orani atlas'tan. */
const PREVIEW_H = 64

onMounted(async () => {
  try {
    const [scene, res] = await Promise.all([
      api.get<{ sprites?: string[] }>('/api/v1/scene'),
      fetch('/sprites/atlas.json', { cache: 'no-cache' }),
    ])
    atlas.value = (await res.json()) as AtlasJson
    sprites.value = (scene.sprites ?? Object.keys(atlas.value.characters)).filter(s => atlas.value!.characters[s])
  } catch {
    error.value = 'Karakter listesi alınamadı.'
  }
})

function previewStyle(name: string): Record<string, string> {
  const walk = atlas.value?.characters[name]?.walk
  if (!walk) return {}
  const scale = PREVIEW_H / walk.frameH
  const row = walk.dirRows?.down ?? 0
  return {
    width: `${Math.round(walk.frameW * scale)}px`,
    height: `${PREVIEW_H}px`,
    backgroundImage: `url('/sprites/${walk.image}')`,
    backgroundSize: `${walk.cols * walk.frameW * scale}px ${walk.rows * walk.frameH * scale}px`,
    backgroundPosition: `0 ${-row * walk.frameH * scale}px`,
  }
}

function label(name: string): string {
  const users = props.usedBy?.[name]
  return users?.length ? `${name} — kullanan: ${users.join(', ')}` : name
}
</script>

<template>
  <div class="picker" role="radiogroup" aria-label="Karakter">
    <span v-if="error" class="sub">{{ error }}</span>
    <button
      v-if="allowAuto" type="button" class="opt auto" role="radio" :aria-checked="modelValue === null" :class="{ on: modelValue === null }"
      title="Otomatik: kullanılmayan ilk karakter" @click="emit('update:modelValue', null)"
    >
      <span class="auto-mark">?</span>
      <span class="name">otomatik</span>
    </button>
    <button
      v-for="s in sprites" :key="s" type="button" class="opt" role="radio" :aria-checked="modelValue === s"
      :class="{ on: modelValue === s, used: usedBy?.[s]?.length }" :title="label(s)" @click="emit('update:modelValue', s)"
    >
      <span class="img" :style="previewStyle(s)" />
      <span class="name">{{ s }}</span>
    </button>
  </div>
</template>

<style scoped>
.picker { display: flex; flex-wrap: wrap; gap: 6px; }
.opt {
  display: flex; flex-direction: column; align-items: center; gap: 2px; padding: 4px 6px 3px; min-width: 64px;
  background: #faf8f2; border: 2px solid #e3ddcc; border-radius: 6px; cursor: pointer; font: inherit; color: #23283a;
}
.opt:hover { border-color: #b9b2a0; }
.opt.on { border-color: #4f8ef7; background: #eef4ff; box-shadow: 0 0 0 2px rgba(79, 142, 247, 0.2); }
.opt.used:not(.on) .img { opacity: 0.55; }
.img { display: block; background-repeat: no-repeat; image-rendering: pixelated; }
.name { font-size: 10px; color: #4a5068; max-width: 80px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.auto-mark { width: 36px; height: 64px; display: flex; align-items: center; justify-content: center; font-size: 22px; font-weight: 700; color: #9aa0b2; }
.sub { font-size: 11px; color: #6b7285; }
</style>
