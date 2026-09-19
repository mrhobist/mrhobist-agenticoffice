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

    <!-- Senden cevap/karar bekleyen is varsa: gozden kacmasin diye ayri serit. Ilk madde + kalan sayi; "Cevapla" o calismayi acar. -->
    <div v-if="firstPending" class="notice ask" role="alert">
      <span class="bell" aria-hidden="true">🔔</span>
      <strong>Senden cevap bekleniyor ({{ inboxCount }}):</strong>
      <span><span class="kind" :class="firstPending.kind">{{ INBOX_KIND_LABEL[firstPending.kind] }}</span> «{{ firstPending.label }}» — {{ firstPending.title }}<template v-if="inboxCount > 1"> · ve {{ inboxCount - 1 }} iş daha</template></span>
      <button type="button" class="primary" @click="openRun(firstPending.runId)">Cevapla</button>
      <button type="button" @click="toggleJobs">Tümünü gör</button>
    </div>

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
import RunPanel from '~/components/RunPanel.vue'
import SettingsPanel from '~/components/SettingsPanel.vue'
import LimitsBar from '~/components/LimitsBar.vue'
import JobsPanel from '~/components/JobsPanel.vue'
import type { Hud } from '~/scene/world'
import { ROLE_HEX, STATE_HEX, STATE_LABEL, type AgentState, type FeedStatus } from '~/scene/contract'
import type { AgentDetail, AgentListItem, InboxItem, ProviderStatus, RunsOverview } from '~/api/types'
import { isApiError, useApiClient } from '~/api/client'
import { errorText } from '~/api/errors'
import { INBOX_KIND_LABEL, providerLabel } from '~/api/labels'

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
  overviewTimer = setInterval(() => { void loadOverview() }, 5000)
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
