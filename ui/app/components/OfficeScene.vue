<template>
  <div ref="host" class="host">
    <canvas ref="cv" class="cv" @mousemove="onMove" @mouseleave="onLeave" @click="onClick" />
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
  board: [snapshot: ReturnType<World['board']['snapshot']>]
}>()

const host = useTemplateRef<HTMLDivElement>('host')
const cv = useTemplateRef<HTMLCanvasElement>('cv')
const ready = ref(false)
const error = ref<string | null>(null)

const apiBase = useRuntimeConfig().public.apiBase as string

let world: World | null = null
let feed: { stop: () => void } | null = null
let raf = 0
let view = { scale: 1, ox: 0, oy: 0 }
let ro: ResizeObserver | null = null
let agentsTimer: ReturnType<typeof setInterval> | undefined
let simTimer: ReturnType<typeof setInterval> | undefined
let simLast = 0

/**
 * Simulasyon adimi, cizimden BAGIMSIZ. Sekme gizliyken requestAnimationFrame durur;
 * setInterval (kisitli da olsa) calisir ve gecen sureyi sabit adimlarla telafi eder.
 * Boylece arka planda kalan sahne donmaz, donusunde ajanlar yerlerine varmis olur.
 */
function step(now: number) {
  if (!world) return
  let elapsed = Math.min(5, (now - simLast) / 1000)
  simLast = now
  while (elapsed > 0) {
    const dt = Math.min(0.05, elapsed)
    world.update(dt, now)
    elapsed -= dt
  }
}

function toWorld(ev: MouseEvent) {
  const r = cv.value!.getBoundingClientRect()
  return { x: (ev.clientX - r.left - view.ox) / view.scale, y: (ev.clientY - r.top - view.oy) / view.scale }
}

function onMove(ev: MouseEvent) {
  if (!world) return
  const p = toWorld(ev)
  const a = world.pick(p)
  world.hovered = a?.key ?? null
  cv.value!.style.cursor = a || world.hitBoard(p) ? 'pointer' : 'default'
}

function onLeave() {
  if (world) world.hovered = null
}

function onClick(ev: MouseEvent) {
  if (!world) return
  const p = toWorld(ev)
  if (world.hitBoard(p)) { emit('board', world.board.snapshot()); return }
  emit('select', world.pick(p)?.key ?? null)
}

/** Panel acikken canli kalsin: dis dunya her 1.5 s'de yeni anlik goruntu alir. */
function publishBoard() {
  if (world) emit('board', world.board.snapshot())
}
defineExpose({ publishBoard })

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
  step(now)

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
  // Gelistirme: konsoldan sahneyi sorgulamak icin (uretimde yok).
  if (import.meta.dev) (window as unknown as { __world?: World }).__world = world
  ready.value = true
  fit()
  feed = connectFeed(apiBase, (e: SceneEvent) => { world?.apply(e); publishAgents() }, s => emit('status', s))
  agentsTimer = setInterval(publishAgents, 1500)
  simLast = performance.now()
  clearInterval(simTimer)
  simTimer = setInterval(() => step(performance.now()), 200)
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
  clearInterval(simTimer)
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
