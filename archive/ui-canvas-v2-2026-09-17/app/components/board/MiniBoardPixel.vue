<template>
  <button
    class="mini"
    :title="`${total} gorev · acmak icin tikla (B)`"
    @click="$emit('open')"
  >
    <canvas ref="cv" />
    <span class="cap">Is akisi · {{ total }} gorev</span>
  </button>
</template>

<script setup lang="ts">
import { type RunView, STAGES, TASK_TINT, type TaskState } from '~/composables/useTeamState'

/**
 * Sahnenin sag alt kosesinde duran MINI SCRUM BOARD.
 *
 * Dekoratif bir ikon degil: sutunlar gercek adimlar, yapiskan notlar gercek
 * gorevler, renkleri gercek durumlar. Tiklaninca tam board acilir.
 *
 * Piksel diliyle cizilir (dusuk cozunurluk + koyu kontur + tam kat buyutme)
 * ki ofis gorunumuyle ayni gorsel dili konussun.
 */

const props = defineProps<{ run: RunView }>()
defineEmits<{ open: [] }>()
const { run } = toRefs(props)

const total = computed(() => run.value.tasks.length)

const BW = 92
const BH = 54

const C = {
  line: '#1d1728', frame: '#6b5636', board: '#efe7d6', head: '#cfc3ab',
  leg: '#5a482e', col: '#d8cdb8',
} as const

/** Notlar board ile AYNI durum renklerini kullanir (useTeamState). */
const NOTE: Record<TaskState, string> = TASK_TINT

const cv = useTemplateRef<HTMLCanvasElement>('cv')
let stop: (() => void) | undefined

onMounted(() => {
  const canvas = cv.value
  if (!canvas) return

  const buf = document.createElement('canvas')
  buf.width = BW; buf.height = BH
  const b = buf.getContext('2d')!
  const ctx = canvas.getContext('2d')!

  const SCALE = 2
  canvas.width = BW * SCALE
  canvas.height = BH * SCALE
  ctx.imageSmoothingEnabled = false

  const px = (x: number, y: number, w: number, h: number, c: string) => {
    b.fillStyle = c
    b.fillRect(x | 0, y | 0, w, h)
  }
  const boxo = (x: number, y: number, w: number, h: number, fill: string) => {
    px(x - 1, y - 1, w + 2, h + 2, C.line)
    px(x, y, w, h, fill)
  }

  const draw = (t: number) => {
    b.clearRect(0, 0, BW, BH)

    // Ayaklar + cerceve
    px(14, 46, 3, 7, C.leg)
    px(75, 46, 3, 7, C.leg)
    boxo(4, 3, 84, 44, C.frame)
    px(6, 5, 80, 40, C.board)
    px(6, 5, 80, 5, C.head)          // ust serit

    // Sutunlar — gercek adimlar
    const cw = 12
    const gap = 1
    const x0 = 7
    STAGES.forEach((st, i) => {
      const cx = x0 + i * (cw + gap)
      px(cx, 11, cw, 33, C.col)
      px(cx, 11, cw, 1, C.line)

      const items = run.value.tasks.filter(x => x.stage === st.id)
      items.slice(0, 5).forEach((task, j) => {
        const ny = 13 + j * 6
        const on = task.state === 'active' || task.state === 'blocked'
        // Aktif not hafifce nabiz atar — board canli oldugunu soyler.
        const w = on ? cw - 2 + Math.round(Math.sin(t * 4 + i) * 0.5) : cw - 3
        px(cx + 1, ny, w, 4, NOTE[task.state])
        px(cx + 1, ny + 4, w, 1, C.line)
      })
      if (items.length > 5) px(cx + 1, 43, cw - 3, 1, C.line)
    })

    // Kalem rafinda kirmizi kalem — kucuk hayat detayi
    px(70, 45, 8, 2, '#b8433f')
    px(78, 45, 2, 2, C.line)
  }

  let frame = 0
  const tick = () => {
    frame = requestAnimationFrame(tick)
    draw(performance.now() / 1000)
    ctx.imageSmoothingEnabled = false
    ctx.clearRect(0, 0, canvas.width, canvas.height)
    ctx.drawImage(buf, 0, 0, canvas.width, canvas.height)
  }
  tick()

  stop = () => cancelAnimationFrame(frame)
})

onBeforeUnmount(() => stop?.())
</script>

<style scoped>
/* Sag alt kose: sol alt HUD rozetleriyle dolu, orta ajanlarin uzerine
   biniyordu. Kose hem sahneyi acik birakir hem her iki gorunumde de bos. */
.mini {
  position: absolute;
  right: 14px;
  top: 14px;
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  gap: 4px;
  background: none;
  border: none;
  padding: 4px;
  border-radius: 10px;
  cursor: pointer;
  z-index: 10;
  transition: transform 120ms ease, filter 120ms ease;
}

.mini canvas {
  image-rendering: pixelated;
  display: block;
  filter: drop-shadow(0 6px 14px rgb(0 0 0 / 55%));
}

.mini:hover { transform: translateY(2px); }
.mini:hover canvas { filter: drop-shadow(0 10px 18px rgb(0 0 0 / 65%)) brightness(1.08); }
.mini:focus-visible { outline: 2px solid #5b7fd6; outline-offset: 2px; }

.cap {
  font-size: 10.5px;
  color: var(--ink-3);
  background: #0c0f16cc;
  border: 1px solid var(--rule);
  border-radius: 999px;
  padding: 2px 8px;
  white-space: nowrap;
}

.mini:hover .cap { color: var(--ink); }
</style>
