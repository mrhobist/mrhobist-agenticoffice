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
          @select="selectAgent"
          @board="openBoard"
        />

        <!-- Buyuk pano: sahnedeki Kanban'a tiklaninca ya da B. Ajan paneliyle ayni anda acilmaz. -->
        <KanbanPanel v-if="board" :board="board" @close="board = null" />

        <AgentPanel
          v-if="selected"
          ref="agentPanel"
          :agent-key="selected"
          :scene-name="sceneNameOf(selected)"
          :agents="team"
          @close="closeAgent"
          @saved="onSaved"
        />
      </main>

      <aside class="side">
        <h2>Ekip</h2>
        <ul class="agents">
          <li v-for="a in agents" :key="a.key" :class="{ on: a.key === selected }" @click="selectAgent(a.key)">
            <span class="dot" :style="{ background: ROLE_HEX[a.key] }" />
            <span class="name">{{ a.name }}</span>
            <span class="state" :style="{ color: STATE_HEX[a.state as AgentState] }">{{ STATE_LABEL[a.state as AgentState] }}</span>
            <span v-if="a.note" class="note">{{ a.note }}</span>
            <span v-if="teamMeta(a.key)" class="meta" :title="teamSummary(a.key)">{{ teamMeta(a.key) }}</span>
          </li>
        </ul>
        <p v-if="teamState === 'missing'" class="hint warn">Api'de ajan uçları henüz hazır değil; model ataması Api açılınca yapılır.</p>
        <p v-else-if="teamState === 'error'" class="hint warn">Ekip listesi alınamadı: {{ teamError }}</p>
        <p class="hint">
          Sahne <code>config/scene.json</code>'dan gelir; olaylar <code>/api/v1/scene/events</code> ile akar.
          Api yoksa sahte yönetmen çalışır. Bir ajana tıklayınca model ataması açılır.
        </p>
      </aside>
    </div>
  </div>
</template>

<script setup lang="ts">
import OfficeScene from '~/components/OfficeScene.vue'
import KanbanPanel, { type BoardSnapshot } from '~/components/KanbanPanel.vue'
import AgentPanel from '~/components/AgentPanel.vue'
import type { Hud } from '~/scene/world'
import { ROLE_HEX, STATE_HEX, STATE_LABEL, type AgentState, type FeedStatus } from '~/scene/contract'
import type { AgentDetail, AgentListItem } from '~/api/types'
import { isApiError, useApiClient } from '~/api/client'
import { errorText } from '~/api/errors'
import { providerLabel } from '~/api/labels'

useHead({ title: 'MrHobist.AITeam — Üretim Ofisi', htmlAttrs: { lang: 'tr' } })

const hud = ref<Hud>({ stage: '—', task: '—', round: 0 })
const status = ref<FeedStatus>('connecting')
const agents = ref<Array<{ key: string; name: string; state: string; note: string | null }>>([])
const selected = ref<string | null>(null)
const board = ref<BoardSnapshot | null>(null)
const scene = useTemplateRef<InstanceType<typeof OfficeScene>>('scene')
const agentPanel = useTemplateRef<InstanceType<typeof AgentPanel>>('agentPanel')

// ------------------------------------------------------------------ ekip (GET /agents)

const api = useApiClient()
const team = ref<AgentListItem[] | null>(null)
const teamState = ref<'loading' | 'ready' | 'missing' | 'error'>('loading')
const teamError = ref('')

async function loadTeam() {
  try {
    team.value = await api.get<AgentListItem[]>('/api/v1/agents')
    teamState.value = 'ready'
  } catch (e) {
    team.value = null
    teamState.value = isApiError(e) && e.endpointMissing ? 'missing' : 'error'
    teamError.value = errorText(e)
  }
}

/** Api acilista kapaliysa SSE canliya donunce liste yeniden denenir. */
watch(status, (s) => { if (s === 'live' && teamState.value !== 'ready') void loadTeam() })

function teamItem(key: string): AgentListItem | undefined {
  return team.value?.find(a => a.key === key)
}

/** Ekip satirinin alt bilgisi: saglayici · model; sahnede olup is akisinda olmayanlar icin not. */
function teamMeta(key: string): string | null {
  if (!team.value) return null
  const a = teamItem(key)
  if (!a) return 'iş akışında rolü yok'
  return `${providerLabel(a.provider)} · ${a.model ?? 'varsayılan model'}`
}

function teamSummary(key: string): string | undefined {
  return teamItem(key)?.summary
}

function sceneNameOf(key: string): string {
  return agents.value.find(a => a.key === key)?.name ?? key
}

function onSaved(d: AgentDetail) {
  if (team.value) team.value = team.value.map(a => (a.key === d.key ? d : a))
}

// ------------------------------------------------------------------ paneller

function leaveAgent(): boolean {
  return agentPanel.value?.canLeave() ?? true
}

function selectAgent(key: string | null) {
  if (key === selected.value || !leaveAgent()) return
  selected.value = key
  if (key) board.value = null
}

function closeAgent() {
  if (leaveAgent()) selected.value = null
}

function openBoard(s: BoardSnapshot) {
  if (selected.value && !leaveAgent()) return
  selected.value = null
  board.value = s
}

let boardTimer: ReturnType<typeof setInterval> | undefined
function onKey(e: KeyboardEvent) {
  const el = e.target as HTMLElement | null
  if (el && /^(INPUT|TEXTAREA|SELECT)$/.test(el.tagName)) return
  if (e.key === 'Escape') {
    if (board.value) board.value = null
    else if (selected.value) closeAgent()
  }
  if (e.key === 'b' || e.key === 'B') {
    if (board.value) board.value = null
    else scene.value?.publishBoard()
  }
}
onMounted(() => {
  window.addEventListener('keydown', onKey)
  boardTimer = setInterval(() => { if (board.value) scene.value?.publishBoard() }, 1500)
  void loadTeam()
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

.side {
  width: 260px; flex: none; border-left: 1px solid var(--rule); background: var(--surface);
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
.meta { grid-column: 2 / span 2; font-size: 10px; color: var(--ink-3); opacity: 0.85; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hint { margin: auto 0 0; font-size: 11px; color: var(--ink-3); line-height: 1.5; }
.hint + .hint { margin-top: 8px; }
.hint.warn { color: #f0c26a; }
.hint code { font-size: 10px; color: var(--ink-2); }
</style>
