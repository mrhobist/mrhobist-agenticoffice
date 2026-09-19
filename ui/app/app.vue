<template>
  <div class="shell">
    <header class="bar">
      <span class="brand">MrHobist.AITeam</span>
      <span class="chip" :class="status">{{ STATUS_LABEL[status] }}</span>
      <!-- Kalan kullanim: aktif saglayicilarin kota pencereleri (5 saat, hafta, modele ozel). -->
      <LimitsBar ref="limitsBar" @open="toggleSettings" />
      <span class="run">
        <span class="chip">{{ hud.stage }}</span>
        <span class="chip ghost">{{ hud.task }} · tur {{ hud.round }}</span>
        <!-- Isler: kac is var + senden bir sey bekleyen sayisi (GET /runs/overview, 5 s). -->
        <button type="button" class="chip action jobs" :class="{ on: jobs, alert: inboxCount > 0 }" :title="jobsTitle" @click="toggleJobs">
          İşler <b v-if="overview" class="n">{{ overview.total }}</b>
          <span v-if="inboxCount" class="badge" :aria-label="`${inboxCount} iş senden cevap bekliyor`">{{ inboxCount }}</span>
        </button>
        <button type="button" class="chip action" :class="{ on: runPanel }" @click="toggleRun">{{ runPanel ? 'Çalışmayı kapat' : 'Yeni çalışma' }}</button>
        <button type="button" class="chip action" :class="{ on: settings }" aria-label="Ayarlar" title="Ayarlar (S)" @click="toggleSettings">⚙ Ayarlar</button>
      </span>
    </header>

    <!-- Ilk yuklemede kimlik kontrolu (docs/DOMAIN.md → Model, efor ve kimlik). Giris yoksa is baslatilmaz, kullanici uyarilir. -->
    <div v-if="providerNotice" class="notice" :class="providerNotice.kind" role="status">
      <strong>{{ providerNotice.title }}</strong>
      <span>{{ providerNotice.text }}</span>
      <code v-if="providerNotice.command">{{ providerNotice.command }}</code>
      <button v-if="providerNotice.kind === 'warn'" type="button" @click="toggleSettings">Ayarlar'dan giriş yap</button>
      <button type="button" @click="loadProviders(true)">Yeniden kontrol et</button>
    </div>

    <div class="main">
      <!-- Sol ray: ahsap; calismalar kagit kart (proje varligi gelince kartlar proje olur, isler icine girer). -->
      <aside class="rail" aria-label="Çalışmalar">
        <div class="rail-head">
          <span class="rail-title">Çalışmalar</span>
          <span class="rail-count">{{ railRuns.length }}</span>
          <button type="button" class="rail-add" aria-label="Yeni çalışma" title="Yeni çalışma (N)" @click="openRun('')">+</button>
        </div>
        <button
          v-for="(r, i) in railRuns"
          :key="r.id"
          type="button"
          class="card"
          :class="[r.status, { on: runPanel && runId === r.id }]"
          :style="{ transform: `rotate(${(i % 3) - 1 * 0.6}deg)` }"
          @click="openRun(r.id)"
        >
          <span class="pin" :class="r.status" aria-hidden="true" />
          <strong class="card-title">{{ r.label }}</strong>
          <span class="card-meta">{{ RUN_STATUS_LABEL[r.status] }} · {{ fmtCost(r.totalCostUsd) }}</span>
          <span v-if="pendingOf(r.id)" class="card-ask"><span aria-hidden="true">🔔</span> {{ INBOX_KIND_LABEL[pendingOf(r.id)!.kind] }}: {{ pendingOf(r.id)!.title }}</span>
          <span v-else-if="r.detail" class="card-detail">{{ r.detail }}</span>
        </button>
        <p v-if="!railRuns.length" class="rail-empty">Aktif çalışma yok. Brief ver, analist planı çıkarsın.</p>
        <button type="button" class="rail-all" @click="toggleJobs">Tüm işler <b v-if="overview">{{ overview.total }}</b></button>
      </aside>

      <main class="stage">
        <OfficeScene
          ref="scene"
          @hud="hud = $event"
          @status="status = $event"
          @agents="agents = $event"
          @select="selectAgent"
          @board="openBoard"
        />

        <!-- Senden cevap/karar bekleyen is: sahnenin ustunde sari yapiskan not. "Cevapla" o calismayi acar. -->
        <div v-if="firstPending" class="sticky" role="alert">
          <span class="bell" aria-hidden="true">🔔</span>
          <span class="sticky-text"><strong>{{ firstPending.label }}</strong> · <span class="kind" :class="firstPending.kind">{{ INBOX_KIND_LABEL[firstPending.kind] }}</span> {{ firstPending.title }}<template v-if="inboxCount > 1"> · +{{ inboxCount - 1 }}</template></span>
          <button type="button" class="sticky-go" @click="openRun(firstPending.runId)">Cevapla</button>
        </div>

        <!-- Ekip pusulasi: sahnede kim ne yapiyor; tiklaninca ajan paneli. Yari saydam, sahneyi kapatmaz. -->
        <div v-if="!selected && !runPanel && !settings && !jobs && !board" class="compass" aria-label="Ekip">
          <div class="compass-title">Ekip</div>
          <button v-for="a in agents" :key="a.key" type="button" class="compass-row" :title="teamSummary(a.key)" @click="selectAgent(a.key)">
            <span class="dot" :style="{ background: ROLE_HEX[a.key] }" />
            <span class="name">{{ a.name }}</span>
            <span class="state" :style="{ color: STATE_HEX[a.state as AgentState] }">{{ a.note || STATE_LABEL[a.state as AgentState] }}</span>
          </button>
          <p v-if="teamState === 'error'" class="compass-warn">Ekip listesi alınamadı: {{ teamError }}</p>
        </div>

        <!-- Buyuk pano: sahnedeki Kanban'a tiklaninca ya da B. Ajan paneliyle ayni anda acilmaz. -->
        <KanbanPanel v-if="board" :board="board" :inbox="overview?.inbox ?? []" @close="board = null" @open="openRun" />

        <!-- Isler: sayaclar, gelen kutusu, tum calismalar. -->
        <JobsPanel v-if="jobs" :overview="overview" @close="jobs = false" @open="openRun" @new="openRun('')" />

        <!-- Calisma paneli: yeni brief, plan onayi, devir notlari. Diger panellerle ayni anda acilmaz. -->
        <RunPanel v-if="runPanel" :run-id="runId" :agents="team" @close="runPanel = false" @open="openRun" @jobs="toggleJobs" />

        <!-- Ayarlar: LLM baglantilari (tek tikla giris) ve kullanim. -->
        <SettingsPanel v-if="settings" @close="settings = false" @changed="loadProviders(); limitsBar?.reload()" />

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
    </div>
  </div>
</template>

<script setup lang="ts">
import OfficeScene from '~/components/OfficeScene.vue'
import KanbanPanel, { type BoardSnapshot } from '~/components/KanbanPanel.vue'
import AgentPanel from '~/components/AgentPanel.vue'
import RunPanel from '~/components/RunPanel.vue'
import SettingsPanel from '~/components/SettingsPanel.vue'
import LimitsBar from '~/components/LimitsBar.vue'
import JobsPanel from '~/components/JobsPanel.vue'
import type { Hud } from '~/scene/world'
import { ROLE_HEX, STATE_HEX, STATE_LABEL, type AgentState, type FeedStatus } from '~/scene/contract'
import type { AgentDetail, AgentListItem, InboxItem, ProviderStatus, RunStatus, RunSummary, RunsOverview } from '~/api/types'
import { isApiError, useApiClient } from '~/api/client'
import { errorText } from '~/api/errors'
import { INBOX_KIND_LABEL, RUN_STATUS_LABEL, providerLabel } from '~/api/labels'

const hud = ref<Hud>({ stage: '—', task: '—', round: 0 })
const status = ref<FeedStatus>('connecting')
const agents = ref<Array<{ key: string; name: string; state: string; note: string | null }>>([])
const selected = ref<string | null>(null)
const board = ref<BoardSnapshot | null>(null)
const scene = useTemplateRef<InstanceType<typeof OfficeScene>>('scene')
const agentPanel = useTemplateRef<InstanceType<typeof AgentPanel>>('agentPanel')
const limitsBar = useTemplateRef<InstanceType<typeof LimitsBar>>('limitsBar')

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

/** Pusula satirinin ipucu: ozet + saglayici · model. */
function teamSummary(key: string): string | undefined {
  const a = teamItem(key)
  if (!a) return 'iş akışında rolü yok'
  return `${a.summary} — ${providerLabel(a.provider)} · ${a.model ?? 'varsayılan model'}`
}

// ------------------------------------------------------------------ sol ray: aktif calismalar (GET /runs, 5 s)

const RAIL_ACTIVE: ReadonlySet<RunStatus> = new Set<RunStatus>(['running', 'awaitingApproval', 'paused'])
const runsList = ref<RunSummary[]>([])
/** Aktif olanlar + senden bir sey bekleyen dusmus calismalar; en fazla 8 kart, kalani "Tum isler". */
const railRuns = computed(() => {
  const inboxIds = new Set((overview.value?.inbox ?? []).map(i => i.runId))
  return runsList.value.filter(r => RAIL_ACTIVE.has(r.status) || inboxIds.has(r.id)).slice(0, 8)
})
function pendingOf(id: string): InboxItem | undefined {
  return overview.value?.inbox.find(i => i.runId === id)
}
function fmtCost(v: number): string { return v ? `$${v.toFixed(2)}` : '$0' }
async function loadRuns() {
  try { runsList.value = await api.get<RunSummary[]>('/api/v1/runs?limit=50') } catch { /* ray eski kalir */ }
}

function sceneNameOf(key: string): string {
  return agents.value.find(a => a.key === key)?.name ?? key
}

function onSaved(d: AgentDetail) {
  if (team.value) team.value = team.value.map(a => (a.key === d.key ? d : a))
}

// ------------------------------------------------------------------ isler ve gelen kutusu (GET /runs/overview)

const overview = ref<RunsOverview | null>(null)
const inboxCount = computed(() => overview.value?.inbox.length ?? 0)
const firstPending = computed<InboxItem | null>(() => overview.value?.inbox[0] ?? null)
const jobsTitle = computed(() => {
  const o = overview.value
  if (!o) return 'İşler (I)'
  const parts = [`${o.total} iş`]
  if (o.running) parts.push(`${o.running} çalışıyor`)
  if (o.awaitingApproval) parts.push(`${o.awaitingApproval} onay bekliyor`)
  if (o.paused) parts.push(`${o.paused} durakladı`)
  if (o.failed) parts.push(`${o.failed} başarısız`)
  if (o.inbox.length) parts.push(`— ${o.inbox.length} tanesi senden cevap bekliyor`)
  return parts.join(' · ') + ' (I)'
})
let overviewTimer: ReturnType<typeof setInterval> | undefined

/** 5 s'de bir; hata olursa son iyi deger kalir (Api kapaliyken sayilar kaybolmaz, durum cipi zaten soyler). */
async function loadOverview() {
  try {
    overview.value = await api.get<RunsOverview>('/api/v1/runs/overview')
  } catch {
    // sessiz: ust bardaki durum cipi Api'nin halini gosterir
  }
}

/** Sahnedeki panoya rozet: kac is senden bir sey bekliyor. */
watch(inboxCount, n => scene.value?.setAttention(n), { immediate: true })

// Sekme basligi: bekleyen sayisi one gelir, "(2) MrHobist.AITeam". inboxCount tanimlandiktan SONRA (TDZ).
useHead({ title: computed(() => (inboxCount.value ? `(${inboxCount.value}) ` : '') + 'MrHobist.AITeam — Üretim Ofisi'), htmlAttrs: { lang: 'tr' } })

// ------------------------------------------------------------------ saglayici kimligi (GET /providers)

interface ProviderNotice { kind: 'warn' | 'down'; title: string; text: string; command?: string }
const providerNotice = ref<ProviderNotice | null>(null)

/** `refresh`: runtime'in kimlik onbellegini atlar (kullanici `claude login` sonrasi yeniden bakti). */
async function loadProviders(refresh = false) {
  try {
    const list = await api.get<ProviderStatus[]>(`/api/v1/providers${refresh ? '?refresh=true' : ''}`)
    const missing = list.filter(p => !p.loggedIn)
    providerNotice.value = missing.length
      ? {
          kind: 'warn',
          title: `${missing.map(p => providerLabel(p.provider)).join(', ')}: giriş yok.`,
          text: 'Modeller Claude Code oturumunu kullanır; oturum Windows kullanıcısına bağlıdır. Runtime\'ı çalıştıran kullanıcıyla bir terminalde giriş yapın, sonra yeniden kontrol edin.',
          command: 'claude login',
        }
      : null
  } catch (e) {
    providerNotice.value = isApiError(e) && (e.errorCode === 'runtime.unavailable' || e.endpointMissing)
      ? { kind: 'down', title: 'Runtime kapalı.', text: 'Model çağrısı yapılamaz; çalışma başlatılırsa analiz başarısız olur.', command: 'runtime/.venv/Scripts/python.exe -m uvicorn app.main:app --host 127.0.0.1 --port 5090 --app-dir runtime' }
      : { kind: 'down', title: 'Sağlayıcı durumu alınamadı.', text: errorText(e) }
  }
}

// ------------------------------------------------------------------ paneller

const runPanel = ref(false)
const runId = ref<string | null>(null)
const settings = ref(false)
const jobs = ref(false)

function toggleSettings() {
  if (settings.value) { settings.value = false; return }
  if (selected.value && !leaveAgent()) return
  selected.value = null
  board.value = null
  runPanel.value = false
  jobs.value = false
  settings.value = true
}

function toggleJobs() {
  if (jobs.value) { jobs.value = false; return }
  if (selected.value && !leaveAgent()) return
  selected.value = null
  board.value = null
  runPanel.value = false
  settings.value = false
  jobs.value = true
  void loadOverview()
}

function leaveAgent(): boolean {
  return agentPanel.value?.canLeave() ?? true
}

function selectAgent(key: string | null) {
  if (key === selected.value || !leaveAgent()) return
  selected.value = key
  if (key) { board.value = null; runPanel.value = false; settings.value = false; jobs.value = false }
}

function closeAgent() {
  if (leaveAgent()) selected.value = null
}

function openBoard(s: BoardSnapshot) {
  if (selected.value && !leaveAgent()) return
  selected.value = null
  runPanel.value = false
  settings.value = false
  jobs.value = false
  board.value = s
}

function toggleRun() {
  if (runPanel.value) { runPanel.value = false; return }
  if (selected.value && !leaveAgent()) return
  selected.value = null
  board.value = null
  settings.value = false
  jobs.value = false
  runPanel.value = true
}

/** Panel, gelen kutusu ya da pano icinden: '' → yeni calisma formu, id → o calisma. Diger paneller kapanir. */
function openRun(id: string) {
  if (selected.value && !leaveAgent()) return
  selected.value = null
  board.value = null
  settings.value = false
  jobs.value = false
  runId.value = id || null
  runPanel.value = true
}

let boardTimer: ReturnType<typeof setInterval> | undefined
function onKey(e: KeyboardEvent) {
  const el = e.target as HTMLElement | null
  if (el && /^(INPUT|TEXTAREA|SELECT)$/.test(el.tagName)) return
  if (e.key === 'Escape') {
    if (settings.value) settings.value = false
    else if (jobs.value) jobs.value = false
    else if (runPanel.value) runPanel.value = false
    else if (board.value) board.value = null
    else if (selected.value) closeAgent()
  }
  if (e.key === 'b' || e.key === 'B') {
    if (board.value) board.value = null
    else scene.value?.publishBoard()
  }
  if (e.key === 'n' || e.key === 'N') toggleRun()
  if (e.key === 's' || e.key === 'S') toggleSettings()
  if (e.key === 'i' || e.key === 'I') toggleJobs()
}
onMounted(() => {
  window.addEventListener('keydown', onKey)
  boardTimer = setInterval(() => { if (board.value) scene.value?.publishBoard() }, 1500)
  void loadTeam()
  void loadProviders()
  void loadOverview()
  void loadRuns()
  overviewTimer = setInterval(() => { void loadOverview(); void loadRuns() }, 5000)
})
onBeforeUnmount(() => { window.removeEventListener('keydown', onKey); clearInterval(boardTimer); clearInterval(overviewTimer) })

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
.run { margin-left: auto; display: flex; gap: 6px; flex: none; }
.chip {
  background: var(--surface-2); border: 1px solid var(--rule);
  border-radius: 999px; padding: 4px 11px; font-size: 12px; color: var(--ink);
}
.chip.ghost { color: var(--ink-3); }
.chip.live { border-color: #2f8f6a; color: #7fe0b3; }
.chip.mock { border-color: #8a6d2a; color: #f0c26a; }
.chip.reconnecting, .chip.connecting { color: var(--ink-3); }
.chip.action { cursor: pointer; font: inherit; font-size: 12px; border-color: #3d5a80; color: #9cc3ef; }
.chip.action:hover, .chip.action.on { background: #3d5a80; color: #fff; }
.chip.jobs { position: relative; display: inline-flex; align-items: center; gap: 6px; }
.chip.jobs .n { font-weight: 700; }
.chip.jobs.alert { border-color: #d23b3b; }
.badge {
  display: inline-flex; align-items: center; justify-content: center; min-width: 18px; height: 18px; padding: 0 5px;
  border-radius: 999px; background: #d23b3b; color: #fff; font-size: 11px; font-weight: 700;
  animation: pulse 1.6s ease-in-out infinite;
}
@keyframes pulse { 0%, 100% { box-shadow: 0 0 0 0 rgba(210,59,59,0.55); } 50% { box-shadow: 0 0 0 6px rgba(210,59,59,0); } }

.notice {
  display: flex; align-items: center; gap: 10px; flex-wrap: wrap;
  padding: 7px 14px; font-size: 12px; border-bottom: 1px solid var(--rule);
}
.notice.warn { background: #3a2f12; color: #f0c26a; }
.notice.down { background: #3a1a1a; color: #f0a0a0; }
.notice.ask { background: #4a3608; color: #ffd98a; border-bottom-color: #8a6d2a; }
.notice.ask { flex-wrap: nowrap; }
.notice.ask > span:not(.bell) { flex: 1 1 auto; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.notice.ask button { flex: none; }
.notice.ask .bell { font-size: 14px; }
.notice.ask .kind { font-size: 10px; font-weight: 700; text-transform: uppercase; letter-spacing: 0.04em; padding: 1px 7px; border-radius: 999px; background: #f3c34a; color: #3a2f12; margin-right: 4px; }
.notice.ask .kind.decision { background: #fadada; color: #9c1f1f; }
.notice.ask button.primary { background: #f3c34a; color: #3a2f12; border-color: #f3c34a; font-weight: 700; margin-left: auto; }
.notice.ask button.primary + button { margin-left: 0; }
.notice span { color: var(--ink-2); }
.notice code { font-size: 11px; background: rgba(0,0,0,0.35); padding: 2px 8px; border-radius: 4px; color: var(--ink); }
.notice button {
  margin-left: auto; font: inherit; font-size: 11px; cursor: pointer;
  background: transparent; color: inherit; border: 1px solid currentColor; border-radius: var(--r-ctl); padding: 3px 9px;
}

.main { flex: 1; min-height: 0; display: flex; }
.stage { flex: 1; min-width: 0; position: relative; }

/* ---- Sol ray: sahnenin ahsabi; calismalar igneli kagit kart ---- */
.rail {
  width: 232px; flex: none; padding: 14px 12px 14px 14px; display: flex; flex-direction: column; gap: 12px; overflow: auto;
  background: linear-gradient(90deg, #4e3620, #6b4a2b 60%, #5a3f24); border-right: 6px solid #3d2a17;
  box-shadow: 10px 0 30px rgba(0,0,0,0.35);
}
.rail-head { display: flex; align-items: center; gap: 8px; padding: 0 2px; }
.rail-title { font-size: 11px; font-weight: 700; letter-spacing: 0.1em; text-transform: uppercase; color: #f0dcc0; }
.rail-count { font-size: 11px; color: #d9b98f; }
.rail-add {
  margin-left: auto; width: 24px; height: 24px; border-radius: 6px; background: #d9a13a; border: 2px solid #3d2a17; color: #141413;
  font: inherit; font-size: 16px; font-weight: 700; line-height: 1; cursor: pointer;
}
.card {
  position: relative; display: flex; flex-direction: column; gap: 4px; text-align: left; font: inherit; cursor: pointer;
  padding: 12px 12px 10px; background: #ede9dc; color: #23283a; border: none; border-radius: 3px;
  box-shadow: 0 3px 0 #b9ad92, 0 8px 14px rgba(0,0,0,0.35);
}
.card:hover { background: #f3efe3; }
.card.on { outline: 3px solid #f6e2a0; }
.card.completed, .card.cancelled { opacity: 0.8; }
.pin { position: absolute; left: 50%; top: -6px; width: 12px; height: 12px; border-radius: 50%; transform: translateX(-50%); background: #7b87a0; border: 2px solid #4a5068; }
.pin.running { background: #4fa3e0; border-color: #1f5f93; }
.pin.awaitingApproval { background: #f3c34a; border-color: #8a6d2a; }
.pin.paused { background: #a889e6; border-color: #5b3fa0; }
.pin.failed, .pin.interrupted, .pin.budgetExceeded { background: #d23b3b; border-color: #7a1f1f; }
.card-title { font-size: 13px; line-height: 1.25; overflow: hidden; display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; }
.card-meta { font-size: 11px; color: #4a5068; }
.card-ask { font-size: 11px; font-weight: 700; color: #7a5a00; background: #f6e2a0; padding: 3px 8px; border-radius: 3px; margin-top: 2px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.card-detail { font-size: 10px; color: #6b7285; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.rail-empty { margin: 0; font-size: 11px; color: #d9b98f; line-height: 1.5; padding: 0 2px; }
.rail-all {
  margin-top: auto; font: inherit; font-size: 12px; font-weight: 700; cursor: pointer; padding: 8px 10px; border-radius: 4px;
  background: rgba(0,0,0,0.25); color: #f0dcc0; border: 1px solid #3d2a17;
}
.rail-all b { margin-left: 6px; color: #d9b98f; }

/* ---- Sahne ustu: sari yapiskan not ---- */
.sticky {
  position: absolute; left: 50%; top: 14px; transform: translateX(-50%); max-width: min(720px, calc(100% - 40px));
  display: flex; align-items: center; gap: 10px; padding: 8px 12px; background: #f6e2a0; color: #23283a; border-radius: 3px;
  box-shadow: 0 3px 0 #b8964a, 0 8px 16px rgba(0,0,0,0.4); font-size: 12px; z-index: 2;
}
.sticky .bell { font-size: 14px; }
.sticky-text { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.sticky .kind { font-size: 10px; font-weight: 700; text-transform: uppercase; letter-spacing: 0.04em; padding: 1px 6px; border-radius: 3px; background: #23283a; color: #f6e2a0; }
.sticky .kind.decision { background: #9c1f1f; color: #fff; }
.sticky-go { flex: none; font: inherit; font-size: 12px; font-weight: 700; padding: 4px 10px; border-radius: 4px; background: #23283a; color: #fff; border: none; cursor: pointer; }

/* ---- Sag alt: ekip pusulasi, yari saydam ---- */
.compass {
  position: absolute; right: 14px; bottom: 14px; width: 232px; padding: 10px 10px 8px; border-radius: 10px;
  background: rgba(21,24,32,0.88); border: 1px solid var(--rule); display: flex; flex-direction: column; gap: 2px; z-index: 2;
}
.compass-title { font-size: 10px; font-weight: 700; letter-spacing: 0.1em; text-transform: uppercase; color: var(--ink-3); padding: 0 4px 4px; }
.compass-row {
  display: grid; grid-template-columns: 9px 1fr auto; column-gap: 8px; align-items: center; text-align: left;
  font: inherit; font-size: 11px; color: var(--ink); background: transparent; border: none; border-radius: 6px; padding: 4px 6px; cursor: pointer;
}
.compass-row:hover { background: var(--surface-2); }
.compass .dot { width: 9px; height: 9px; border-radius: 2px; }
.compass .name { font-size: 12px; }
.compass .state { font-size: 11px; max-width: 120px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.compass-warn { margin: 4px 4px 0; font-size: 10px; color: #f0c26a; }
</style>
