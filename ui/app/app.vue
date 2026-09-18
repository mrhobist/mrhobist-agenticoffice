<template>
  <div class="shell">
    <header class="bar">
      <span class="brand">MrHobist.AITeam</span>
      <span class="chip" :class="status">{{ STATUS_LABEL[status] }}</span>
      <span class="run">
        <span class="chip">{{ hud.stage }}</span>
        <span class="chip ghost">{{ hud.task }} · tur {{ hud.round }}</span>
      </span>
    </header>

    <div class="main">
      <main class="stage">
        <OfficeScene
          ref="scene"
          @hud="hud = $event"
          @status="status = $event"
          @agents="agents = $event"
          @select="selected = $event"
          @board="openBoard"
        />

        <!-- Buyuk pano: sahnedeki Kanban'a tiklaninca. Tum is akisi sutunlari, gorev adlari. -->
        <div v-if="board" class="board-wrap" @click.self="board = null">
          <section class="board">
            <header>
              <h2>Sprint panosu</h2>
              <button class="x" @click="board = null" aria-label="Kapat">×</button>
            </header>
            <div class="cols">
              <div v-for="c in board.columns" :key="c.id" class="col">
                <div class="col-h" :style="{ background: c.hex }">{{ c.title }}</div>
                <div
                  v-for="t in board.tasks.filter(t => t.column === c.id)"
                  :key="t.id"
                  class="card"
                  :class="t.state"
                  :style="{ borderColor: c.hex }"
                >
                  <span class="id">{{ t.id }}</span>
                  <span class="title">{{ t.title }}</span>
                  <span class="st">{{ TASK_LABEL[t.state] }}</span>
                </div>
              </div>
            </div>
            <p class="hint">Sütunlar <code>config/workflow.json</code> adımları; sahnedeki küçük pano bunları dört Kanban şeridine katlar. <kbd>Esc</kbd> kapatır.</p>
          </section>
        </div>
      </main>

      <aside class="side">
        <h2>Ekip</h2>
        <ul class="agents">
          <li v-for="a in agents" :key="a.key" :class="{ on: a.key === selected }" @click="selected = a.key">
            <span class="dot" :style="{ background: ROLE_HEX[a.key] }" />
            <span class="name">{{ a.name }}</span>
            <span class="state" :style="{ color: STATE_HEX[a.state as AgentState] }">{{ STATE_LABEL[a.state as AgentState] }}</span>
            <span v-if="a.note" class="note">{{ a.note }}</span>
          </li>
        </ul>
        <p class="hint">
          Sahne <code>config/scene.json</code>'dan gelir; olaylar <code>/api/v1/scene/events</code> ile akar.
          Api yoksa sahte yönetmen çalışır.
        </p>
      </aside>
    </div>
  </div>
</template>

<script setup lang="ts">
import OfficeScene from '~/components/OfficeScene.vue'
import type { Hud, World } from '~/scene/world'
import { ROLE_HEX, STATE_HEX, STATE_LABEL, type AgentState, type FeedStatus, type TaskState } from '~/scene/contract'

type BoardSnapshot = ReturnType<World['board']['snapshot']>

useHead({ title: 'MrHobist.AITeam — Üretim Ofisi', htmlAttrs: { lang: 'tr' } })

const hud = ref<Hud>({ stage: '—', task: '—', round: 0 })
const status = ref<FeedStatus>('connecting')
const agents = ref<Array<{ key: string; name: string; state: string; note: string | null }>>([])
const selected = ref<string | null>(null)
const board = ref<BoardSnapshot | null>(null)
const scene = useTemplateRef<InstanceType<typeof OfficeScene>>('scene')

const TASK_LABEL: Record<TaskState, string> = { queued: 'sırada', active: 'çalışılıyor', blocked: 'takıldı', done: 'bitti' }

function openBoard(s: BoardSnapshot) { board.value = s }

let boardTimer: ReturnType<typeof setInterval> | undefined
function onKey(e: KeyboardEvent) {
  const el = e.target as HTMLElement | null
  if (el && /^(INPUT|TEXTAREA|SELECT)$/.test(el.tagName)) return
  if (e.key === 'Escape') board.value = null
  if (e.key === 'b' || e.key === 'B') { if (board.value) board.value = null; else scene.value?.publishBoard() }
}
onMounted(() => {
  window.addEventListener('keydown', onKey)
  boardTimer = setInterval(() => { if (board.value) scene.value?.publishBoard() }, 1500)
})
onBeforeUnmount(() => { window.removeEventListener('keydown', onKey); clearInterval(boardTimer) })

const STATUS_LABEL: Record<FeedStatus, string> = {
  connecting: 'bağlanıyor',
  live: 'canlı',
  reconnecting: 'yeniden bağlanıyor',
  mock: 'sahte yönetmen',
}
</script>

<style>
.shell { display: flex; flex-direction: column; height: 100%; }

.bar {
  display: flex; align-items: center; gap: 12px;
  padding: 9px 14px;
  background: var(--surface);
  border-bottom: 1px solid var(--rule);
  flex: none;
}
.brand { font-weight: 600; letter-spacing: 0.02em; color: var(--ink-2); }
.run { margin-left: auto; display: flex; gap: 6px; }
.chip {
  background: var(--surface-2); border: 1px solid var(--rule);
  border-radius: 999px; padding: 4px 11px; font-size: 12px; color: var(--ink);
}
.chip.ghost { color: var(--ink-3); }
.chip.live { border-color: #2f8f6a; color: #7fe0b3; }
.chip.mock { border-color: #8a6d2a; color: #f0c26a; }
.chip.reconnecting, .chip.connecting { color: var(--ink-3); }

.main { flex: 1; min-height: 0; display: flex; }
.stage { flex: 1; min-width: 0; position: relative; }

.board-wrap {
  position: absolute; inset: 0; background: rgba(10, 12, 18, 0.55);
  display: flex; align-items: center; justify-content: center; padding: 24px;
}
.board {
  background: #ede9dc; color: #23283a; border-radius: 10px; border: 6px solid #6b4a2b;
  width: min(1100px, 100%); max-height: 100%; overflow: auto; padding: 16px 18px;
  box-shadow: 0 20px 60px rgba(0,0,0,0.5);
}
.board header { display: flex; align-items: center; justify-content: space-between; margin-bottom: 12px; }
.board h2 { margin: 0; font-size: 16px; letter-spacing: 0.04em; text-transform: uppercase; }
.board .x { background: none; border: none; font-size: 22px; cursor: pointer; color: #23283a; line-height: 1; }
.cols { display: grid; grid-auto-flow: column; grid-auto-columns: minmax(130px, 1fr); gap: 10px; }
.col { background: rgba(0,0,0,0.04); border-radius: 6px; padding: 6px; min-height: 160px; }
.col-h { font-size: 11px; font-weight: 700; text-transform: uppercase; padding: 5px 8px; border-radius: 4px; color: #1f2430; margin-bottom: 8px; }
.card {
  background: #fff; border-left: 4px solid; border-radius: 4px; padding: 6px 8px; margin-bottom: 6px;
  display: grid; grid-template-columns: auto 1fr; gap: 2px 8px; font-size: 12px; box-shadow: 0 1px 2px rgba(0,0,0,0.12);
}
.card .id { font-weight: 700; color: #4a5068; }
.card .title { color: #23283a; }
.card .st { grid-column: 1 / span 2; font-size: 10px; color: #6b7285; }
.card.queued { opacity: 0.6; }
.card.blocked { outline: 2px solid #d23b3b; }
.card.done .title { text-decoration: line-through; color: #6b7285; }
.board .hint { margin: 12px 0 0; font-size: 11px; color: #6b7285; }
.board code, .board kbd { font-size: 10px; background: rgba(0,0,0,0.06); padding: 1px 4px; border-radius: 3px; }

.side {
  width: 240px; flex: none; border-left: 1px solid var(--rule); background: var(--surface);
  padding: 12px; display: flex; flex-direction: column; gap: 10px; overflow: auto;
}
.side h2 { margin: 0; font-size: 12px; text-transform: uppercase; letter-spacing: 0.08em; color: var(--ink-3); }
.agents { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 4px; }
.agents li {
  display: grid; grid-template-columns: 10px 1fr auto; column-gap: 8px; align-items: center;
  padding: 6px 8px; border-radius: var(--r-ctl); cursor: pointer; border: 1px solid transparent;
}
.agents li:hover { background: var(--surface-2); }
.agents li.on { border-color: var(--rule); background: var(--surface-2); }
.dot { width: 10px; height: 10px; border-radius: 2px; }
.name { font-size: 13px; color: var(--ink); }
.state { font-size: 11px; }
.note { grid-column: 2 / span 2; font-size: 11px; color: var(--ink-3); }
.hint { margin: auto 0 0; font-size: 11px; color: var(--ink-3); line-height: 1.5; }
.hint code { font-size: 10px; color: var(--ink-2); }
</style>
