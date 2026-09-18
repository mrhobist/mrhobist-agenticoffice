<template>
  <div ref="host" class="host">
    <canvas ref="cv" class="cv" @mousemove="onMove" @mouseleave="hoverKey = null" @click="onClick" />
    <div v-if="error" class="err">
      <strong>Sahne yüklenemedi.</strong>
      <span>{{ error }}</span>
      <button @click="boot">Yeniden dene</button>
    </div>
    <div v-else-if="!ready" class="loading">Sahne yükleniyor…</div>
  </div>
</template>

<script setup lang="ts">
import type { FeedStatus, SceneEvent } from '~/scene/contract'
import { World, type Hud } from '~/scene/world'
import { connectFeed } from '~/scene/feed'

const emit = defineEmits<{
  hud: [h: Hud]
  status: [s: FeedStatus]
  agents: [list: Array<{ key: string; name: string; state: string; note: string | null }>]
  select: [key: string | null]
}>()

const host = useTemplateRef<HTMLDivElement>('host')
const cv = useTemplateRef<HTMLCanvasElement>('cv')
const ready = ref(false)
const error = ref<string | null>(null)
const hoverKey = ref<string | null>(null)

const apiBase = useRuntimeConfig().public.apiBase as string

let world: World | null = null
let feed: { stop: () => void } | null = null
let raf = 0
let last = 0
let view = { scale: 1, ox: 0, oy: 0 }
let ro: ResizeObserver | null = null
let agentsTimer: ReturnType<typeof setInterval> | undefined

function toWorld(ev: MouseEvent) {
  const r = cv.value!.getBoundingClientRect()
  return { x: (ev.clientX - r.left - view.ox) / view.scale, y: (ev.clientY - r.top - view.oy) / view.scale }
}

function onMove(ev: MouseEvent) {
  if (!world) return
  const a = world.pick(toWorld(ev))
  hoverKey.value = a?.key ?? null
  world.hovered = hoverKey.value
  cv.value!.style.cursor = a ? 'pointer' : 'default'
}

function onClick(ev: MouseEvent) {
  if (!world) return
  emit('select', world.pick(toWorld(ev))?.key ?? null)
}

function fit() {
  const el = host.value
  const canvas = cv.value
  if (!el || !canvas || !world) return
  const dpr = Math.min(2, window.devicePixelRatio || 1)
  const cw = el.clientWidth
  const ch = el.clientHeight
  canvas.width = Math.floor(cw * dpr)
  canvas.height = Math.floor(ch * dpr)
  canvas.style.width = `${cw}px`
  canvas.style.height = `${ch}px`
  const { w, h } = world.cfg.world
  const scale = Math.min(cw / w, ch / h)
  view = { scale, ox: (cw - w * scale) / 2, oy: (ch - h * scale) / 2 }
}

function frame(now: number) {
  raf = requestAnimationFrame(frame)
  if (!world || !cv.value) return
  const dt = Math.min(0.05, (now - last) / 1000 || 0)
  last = now
  world.update(dt, now)

  const ctx = cv.value.getContext('2d')!
  const dpr = cv.value.width / cv.value.clientWidth
  ctx.setTransform(1, 0, 0, 1, 0, 0)
  ctx.fillStyle = '#151820'
  ctx.fillRect(0, 0, cv.value.width, cv.value.height)
  ctx.setTransform(view.scale * dpr, 0, 0, view.scale * dpr, view.ox * dpr, view.oy * dpr)
  ctx.imageSmoothingEnabled = true
  ctx.imageSmoothingQuality = 'high'
  world.draw(ctx, view.scale * dpr, now)
}

function publishAgents() {
  if (!world) return
  emit('agents', [...world.agents.values()].map(a => ({ key: a.key, name: a.def.name, state: a.state, note: a.note })))
}

async function boot() {
  error.value = null
  ready.value = false
  try {
    world = await World.create(apiBase)
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
    return
  }
  world.onHud = h => emit('hud', h)
  ready.value = true
  fit()
  feed = connectFeed(apiBase, (e: SceneEvent) => { world?.apply(e); publishAgents() }, s => emit('status', s))
  agentsTimer = setInterval(publishAgents, 1500)
  last = performance.now()
  cancelAnimationFrame(raf)
  raf = requestAnimationFrame(frame)
}

onMounted(() => {
  ro = new ResizeObserver(fit)
  if (host.value) ro.observe(host.value)
  void boot()
})

onBeforeUnmount(() => {
  cancelAnimationFrame(raf)
  feed?.stop()
  ro?.disconnect()
  clearInterval(agentsTimer)
})
</script>

<style scoped>
.host { position: absolute; inset: 0; overflow: hidden; background: #151820; }
.cv { display: block; }
.err, .loading {
  position: absolute; inset: 0; display: flex; flex-direction: column; gap: 8px;
  align-items: center; justify-content: center; color: var(--ink-2); font-size: 14px;
}
.err span { color: var(--ink-3); font-size: 12px; max-width: 480px; text-align: center; }
.err button {
  margin-top: 6px; background: var(--surface-2); color: var(--ink); border: 1px solid var(--rule);
  border-radius: var(--r-ctl); padding: 6px 12px; font: inherit; cursor: pointer;
}
</style>
