<template>
  <!-- Giris kapisi (docs/DOMAIN.md → Giris): belirtec yoksa yalniz giris ekrani; kabuk ve sahne Api'ye dokunmaz. -->
  <LoginPanel v-if="!loggedIn" @done="onLoggedIn" />
  <div v-else class="shell">
    <header class="bar">
      <span class="brand">MrHobist.AITeam</span>
      <span class="chip" :class="status">{{ STATUS_LABEL[status] }}</span>
      <!-- Kalan kullanim: aktif saglayicilarin kota pencereleri (5 saat, hafta, modele ozel). -->
      <LimitsBar ref="limitsBar" @open="toggleSettings" />
      <span class="run">
        <!-- Sahne HUD'u: yalniz bir adim akarken (bos "— · tur 0" cipleri kalabalik yapiyordu). -->
        <template v-if="overview?.running">
          <span class="chip">{{ hud.stage }}</span>
          <span class="chip ghost">{{ hud.task }} · tur {{ hud.round }}</span>
        </template>
        <!-- Isler: kac is var + senden bir sey bekleyen sayisi (GET /runs/overview, 5 s). -->
        <button type="button" class="chip action jobs" :class="{ on: jobs, alert: inboxCount > 0 }" :title="jobsTitle" @click="toggleJobs">
          İşler <b v-if="overview" class="n">{{ overview.total }}</b>
          <span v-if="inboxCount" class="badge" :aria-label="`${inboxCount} iş senden cevap bekliyor`">{{ inboxCount }}</span>
        </button>
        <button type="button" class="chip action" :class="{ on: teamPanel }" aria-label="Ekip yönetimi" title="Ekip yönetimi (E): ajanlar ve takımlar" @click="toggleTeam">Ekip</button>
        <span class="help-wrap">
          <button type="button" class="chip action help" aria-label="Kısayollar" title="Kısayollar" @click="help = !help">?</button>
          <div v-if="help" class="help-menu" role="dialog" aria-label="Kısayollar">
            <div class="bell-head">Kısayollar</div>
            <dl class="keys">
              <dt><kbd>N</kbd></dt><dd>Yeni proje</dd>
              <dt><kbd>I</kbd></dt><dd>İşler</dd>
              <dt><kbd>B</kbd></dt><dd>Sprint panosu (Kanban)</dd>
              <dt><kbd>E</kbd></dt><dd>Ekip yönetimi</dd>
              <dt><kbd>S</kbd></dt><dd>Ayarlar</dd>
              <dt><kbd>Esc</kbd></dt><dd>Açık paneli kapat</dd>
            </dl>
            <p class="bell-empty">Sahnede bir ajana tıkla: durur, işini yazar. Panoya tıkla: Kanban. Proje kartına tıkla: işler ve "Yeni iş".</p>
          </div>
        </span>
        <button type="button" class="chip action" :class="{ on: settings }" aria-label="Ayarlar" title="Ayarlar (S)" @click="toggleSettings">⚙ Ayarlar</button>
        <!-- Bildirimler: senden cevap bekleyenler islerin DISINDA ayri bir alanda (kullanici istegi 2026-09-19). -->
        <span class="bell-wrap">
          <button type="button" class="chip action bell-btn" :class="{ on: bell, alert: inboxCount > 0 }" :aria-label="`Bildirimler: ${inboxCount} bekleyen`" title="Senden bekleyenler" @click="toggleBell">
            <span aria-hidden="true">🔔</span>
            <span v-if="inboxCount" class="badge">{{ inboxCount }}</span>
          </button>
          <div v-if="bell" class="bell-menu" role="dialog" aria-label="Senden bekleniyor">
            <div class="bell-head">Senden bekleniyor <b>{{ inboxCount }}</b></div>
            <p v-if="!inboxCount" class="bell-empty">Bekleyen bir şey yok. Plan onayı ya da düşen bir iş olunca burada görünür.</p>
            <button v-for="i in overview?.inbox ?? []" :key="i.runId + i.kind + i.ts" type="button" class="bell-item" @click="bell = false; openRun(i.runId)">
              <span class="kind" :class="i.kind">{{ INBOX_KIND_LABEL[i.kind] }}</span>
              <span class="bell-text"><strong>{{ i.label }}</strong> · {{ i.title }}</span>
              <span class="go">Cevapla →</span>
            </button>
          </div>
        </span>
        <!-- Profil: bugun gomulu tek kullanici; ileride LDAP / kullanici mimarisi ayni cipe dolar. -->
        <span class="profile" :title="`${user?.name} · ${user?.role}`">
          <span class="avatar">{{ initials(user?.name ?? '?') }}</span>
          <span class="who">{{ user?.name }}</span>
          <button type="button" class="out" title="Çıkış" aria-label="Çıkış" @click="logout">⎋</button>
        </span>
      </span>
    </header>

    <!-- Anlik uyarilar: yeni soru/karar. Tiklaninca calisma acilir; 12 s sonra kendi kapanir. -->
    <div v-if="toasts.length" class="toasts" aria-live="polite">
      <div v-for="t in toasts" :key="t.id" class="toast">
        <span class="kind" :class="t.kind">{{ INBOX_KIND_LABEL[t.kind] }}</span>
        <button type="button" class="toast-body" @click="dismissToast(t.id); openRun(t.runId)"><strong>{{ t.label }}</strong> · {{ t.title }}</button>
        <button type="button" class="toast-x" aria-label="Kapat" @click="dismissToast(t.id)">×</button>
      </div>
    </div>

    <!-- Ilk yuklemede kimlik kontrolu (docs/DOMAIN.md → Model, efor ve kimlik). Giris yoksa is baslatilmaz, kullanici uyarilir. -->
    <div v-if="providerNotice" class="notice" :class="providerNotice.kind" role="status">
      <strong>{{ providerNotice.title }}</strong>
      <span>{{ providerNotice.text }}</span>
      <code v-if="providerNotice.command">{{ providerNotice.command }}</code>
      <button v-if="providerNotice.kind === 'warn'" type="button" @click="toggleSettings">Ayarlar'dan giriş yap</button>
      <button type="button" @click="loadProviders(true)">Yeniden kontrol et</button>
    </div>

    <div class="main">
      <!-- Sol ray: ahsap; projeler igneli kagit kart (docs/DOMAIN.md → Projeler). Kart acilinca proje paneli; is yalniz orada baslar. -->
      <aside class="rail" :class="{ narrow: projectPanel }" aria-label="Projeler">
        <div class="rail-head">
          <span class="rail-title">Projeler</span>
          <span class="rail-count">{{ projects.length }}</span>
          <button type="button" class="rail-add" aria-label="Yeni proje" title="Yeni proje (N)" @click="openProject('')">+</button>
        </div>
        <button
          v-for="(p, i) in projects"
          :key="p.key"
          type="button"
          class="card"
          :class="{ on: projectPanel && projectKey === p.key, quiet: !p.running && !p.awaitingApproval && !p.paused }"
          :style="{ transform: `rotate(${((i % 3) - 1) * 0.6}deg)` }"
          :title="p.title"
          @click="openProject(p.key)"
        >
          <span class="pin" :class="pinOf(p)" aria-hidden="true" />
          <span class="card-color" :style="{ background: p.color || '#3d5a80' }" aria-hidden="true" />
          <span class="card-avatar" :style="{ background: p.color || '#3d5a80' }">{{ initials(p.title) }}</span>
          <strong class="card-title">{{ p.title }}</strong>
          <span class="card-meta">{{ p.runs }} iş<template v-if="p.running"> · <b class="run-n">{{ p.running }} çalışıyor</b></template><template v-if="p.paused"> · {{ p.paused }} durakladı</template> · {{ fmtCost(p.totalCostUsd) }}</span>
          <span v-if="inboxOfProject(p.key)" class="card-ask"><span aria-hidden="true">🔔</span> {{ inboxOfProject(p.key) }} senden bekliyor</span>
          <span v-else-if="p.lastActivityAt" class="card-detail">son hareket {{ fmtAgo(p.lastActivityAt) }}</span>
        </button>
        <p v-if="!projects.length" class="rail-empty">Henüz proje yok. "+" ile ilk projeyi aç; işler onun içinde başlar.</p>
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


        <!-- Ekip pusulasi: sahnede kim ne yapiyor; tiklaninca ajan paneli. Yari saydam, sahneyi kapatmaz. -->
        <!-- Ekip pusulasi: sahnede kim ne yapiyor. Kucultulebilir (kullanici istegi 2026-09-20): kapaliyken yalniz renkli
             noktalar + mesgul sayisi; tiklaninca acilir. Secim localStorage'da kalir. -->
        <div v-if="!selected && !runPanel && !settings && !jobs && !board && !teamPanel" class="compass" :class="{ min: compassMin }" aria-label="Ekip">
          <button type="button" class="compass-head" :title="compassMin ? 'Ekibi göster' : 'Ekibi küçült'" @click="toggleCompass">
            <span class="compass-title">Ekip</span>
            <span v-if="compassMin" class="compass-dots"><span v-for="a in agents" :key="a.key" class="dot" :class="{ busy: a.state !== 'idle' && a.state !== 'done' }" :style="{ background: ROLE_HEX[a.key] }" :title="`${a.name}: ${a.note || STATE_LABEL[a.state as AgentState]}`" /></span>
            <span v-if="compassMin && busyAgents" class="compass-busy">{{ busyAgents }} çalışıyor</span>
            <span class="compass-chev" aria-hidden="true">{{ compassMin ? '▴' : '▾' }}</span>
          </button>
          <button v-if="!compassMin" type="button" class="compass-manage" title="Ajan ekle, takım kur" @click.stop="toggleTeam">Yönet →</button>
          <template v-if="!compassMin">
            <button v-for="a in agents" :key="a.key" type="button" class="compass-row" :title="teamSummary(a.key)" @click="selectAgent(a.key)">
              <span class="dot" :style="{ background: ROLE_HEX[a.key] }" />
              <span class="name">{{ a.name }}</span>
              <span class="state" :style="{ color: STATE_HEX[a.state as AgentState] }">{{ a.note || STATE_LABEL[a.state as AgentState] }}</span>
            </button>
            <p v-if="teamState === 'error'" class="compass-warn">Ekip listesi alınamadı: {{ teamError }}</p>
          </template>
        </div>

        <!-- Buyuk pano: sahnedeki Kanban'a tiklaninca ya da B. Ajan paneliyle ayni anda acilmaz. -->
        <KanbanPanel v-if="board" :board="board" :inbox="overview?.inbox ?? []" @close="board = null" @open="openRun" />

        <!-- Isler: sayaclar, gelen kutusu, tum calismalar. -->
        <JobsPanel v-if="jobs" :overview="overview" @close="jobs = false" @open="openRun" @new="openRun('')" />

        <!-- Proje paneli: kagit pano, Isler / Ayarlar; "Yeni is" yalniz burada. -->
        <ProjectPanel
          v-if="projectPanel"
          :project-key="projectKey"
          :inbox="overview?.inbox ?? []"
          @close="projectPanel = false"
          @open-run="openRun"
          @new-run="openNewRun"
          @created="onProjectCreated"
          @changed="loadProjects"
        />

        <!-- Calisma paneli: yeni brief (projeye bagli), plan onayi, devir notlari. Diger panellerle ayni anda acilmaz. -->
        <RunPanel v-if="runPanel" :run-id="runId" :project="runProject" :agents="team" @close="closeRun" @open="openRun" @jobs="toggleJobs" @new-run="openNewRun" />

        <!-- Ekip yonetimi: ajan havuzu (ekle/sil) ve takimlar = is akislari (kullanici karari 2026-09-20). -->
        <TeamPanel v-if="teamPanel" :agents="team" @close="teamPanel = false" @select="k => { teamPanel = false; selectAgent(k) }" @changed="loadTeam(); loadProjects()" />

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
import ProjectPanel from '~/components/ProjectPanel.vue'
import LoginPanel from '~/components/LoginPanel.vue'
import TeamPanel from '~/components/TeamPanel.vue'
import { useAuth } from '~/composables/useAuth'
import type { Hud } from '~/scene/world'
import { ROLE_HEX, STATE_HEX, STATE_LABEL, type AgentState, type FeedStatus } from '~/scene/contract'
import type { AgentDetail, AgentListItem, InboxKind, ProjectCard, ProviderStatus, RunSummary, RunsOverview } from '~/api/types'
import { isApiError, useApiClient } from '~/api/client'
import { errorText } from '~/api/errors'
import { INBOX_KIND_LABEL, RUN_STATUS_LABEL, providerLabel, fmtCost as fmtCostLabel } from '~/api/labels'

// ------------------------------------------------------------------ giris
const { user, loggedIn, logout } = useAuth()
/** Giris sonrasi kabuk ilk kez kurulur (v-else): veriler onMounted yerine burada yuklenir. */
function onLoggedIn() { void nextTick(() => bootData()) }

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

// ------------------------------------------------------------------ sol ray: projeler (GET /projects, 5 s)

const projects = ref<ProjectCard[]>([])
const runsList = ref<RunSummary[]>([])
async function loadProjects() {
  try { projects.value = await api.get<ProjectCard[]>('/api/v1/projects') } catch { /* ray eski kalir */ }
}
async function loadRuns() {
  try { runsList.value = await api.get<RunSummary[]>('/api/v1/runs?limit=100') } catch { /* eski kalir */ }
}
/** Projenin gelen kutusundaki is sayisi (run → project eslesmesi runs listesinden). */
function inboxOfProject(key: string): number {
  const ids = new Set(runsList.value.filter(r => r.project === key).map(r => r.id))
  return (overview.value?.inbox ?? []).filter(i => ids.has(i.runId)).length
}
function pinOf(p: ProjectCard): string {
  if (inboxOfProject(p.key)) return 'ask'
  if (p.running) return 'running'
  if (p.paused) return 'paused'
  return 'quiet'
}
function initials(t: string): string { return t.split(/\s+/).filter(Boolean).slice(0, 2).map(w => w[0]!.toUpperCase()).join('') || '?' }
function fmtCost(v: number): string { return fmtCostLabel(v, 2) }
function fmtAgo(s: string): string {
  const m = Math.max(0, Math.round((Date.now() - new Date(s).getTime()) / 60_000))
  if (m < 1) return 'az önce'
  if (m < 60) return `${m} dk önce`
  const h = Math.floor(m / 60)
  if (h < 24) return `${h} sa önce`
  return `${Math.floor(h / 24)} g önce`
}

/** Pusula kucuk mu: tercih tarayicida kalir (kisisel gorunum ayari, sunucuya gitmez). */
const compassMin = ref(false)
try { compassMin.value = localStorage.getItem('aiteam.compass') === 'min' } catch { /* varsayilan acik */ }
function toggleCompass() {
  compassMin.value = !compassMin.value
  try { localStorage.setItem('aiteam.compass', compassMin.value ? 'min' : 'open') } catch { /* yalniz bellek */ }
}
const busyAgents = computed(() => agents.value.filter(a => a.state !== 'idle' && a.state !== 'done').length)

function sceneNameOf(key: string): string {
  return agents.value.find(a => a.key === key)?.name ?? key
}

function onSaved(d: AgentDetail) {
  if (team.value) team.value = team.value.map(a => (a.key === d.key ? d : a))
}

// ------------------------------------------------------------------ isler ve gelen kutusu (GET /runs/overview)

const overview = ref<RunsOverview | null>(null)
const inboxCount = computed(() => overview.value?.inbox.length ?? 0)
const jobsTitle = computed(() => {
  const o = overview.value
  if (!o) return 'İşler (I)'
  const parts = [`${o.total} iş`]
  if (o.running) parts.push(`${o.running} çalışıyor`)
  if (o.awaitingApproval) parts.push(`${o.awaitingApproval} onay bekliyor`)
  if (o.awaitingInput) parts.push(`${o.awaitingInput} senden cevap bekliyor`)
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

// Yeni bekleyen (soru/karar) gelince kisa uyari (toast): baska panelde calisan kullanici kacirmasin (kullanici istegi 2026-09-20).
const help = ref(false)
const toasts = ref<Array<{ id: string; runId: string; label: string; kind: InboxKind; title: string }>>([])
const seenInbox = new Set<string>()
let inboxPrimed = false
watch(() => overview.value?.inbox, (list) => {
  if (!list) return
  const keys = list.map(i => `${i.runId}:${i.kind}:${i.ts}`)
  if (!inboxPrimed) { keys.forEach(k => seenInbox.add(k)); inboxPrimed = true; return }
  list.forEach((i, idx) => {
    const k = keys[idx]!
    if (seenInbox.has(k)) return
    seenInbox.add(k)
    toasts.value = [...toasts.value, { id: k, runId: i.runId, label: i.label, kind: i.kind, title: i.title }].slice(-3)
    setTimeout(() => { toasts.value = toasts.value.filter(t => t.id !== k) }, 12_000)
  })
})
function dismissToast(id: string) { toasts.value = toasts.value.filter(t => t.id !== id) }

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
          text: 'Modeller CLI oturumunu kullanır (Anthropic: Claude Code, OpenAI: Codex); oturum Windows kullanıcısına bağlıdır. Ayarlar\'dan giriş yapın ya da runtime\'ı çalıştıran kullanıcıyla bir terminalde giriş yapıp yeniden kontrol edin. Kullanmadığınız sağlayıcıda giriş gerekmez.',
          command: missing.map(p => p.provider === 'openai' ? 'codex login' : p.provider === 'anthropic' ? 'claude login' : `${p.provider}: giriş`).join('  ·  '),
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
/** Yeni is formunun projesi; is yalniz bir projenin icinde baslar. */
const runProject = ref<string | null>(null)
const settings = ref(false)
const jobs = ref(false)
const bell = ref(false)
function toggleBell() { bell.value = !bell.value; if (bell.value) void loadOverview() }
const teamPanel = ref(false)
function toggleTeam() {
  if (teamPanel.value) { teamPanel.value = false; return }
  if (selected.value && !leaveAgent()) return
  closeOthers()
  runPanel.value = false
  teamPanel.value = true
  void loadTeam()
}
const projectPanel = ref(false)
const projectKey = ref<string | null>(null) // null = yeni proje formu

function closeOthers() {
  selected.value = null
  board.value = null
  settings.value = false
  jobs.value = false
  teamPanel.value = false
}

/** Proje kartini ac ('' → yeni proje formu). Calisma paneli kapanir; ray daralir. */
function openProject(key: string) {
  if (selected.value && !leaveAgent()) return
  closeOthers()
  runPanel.value = false
  projectKey.value = key || null
  projectPanel.value = true
}

/** Proje panelinden "Yeni is": form o projeye bagli acilir, proje paneli acik kalir. */
function openNewRun(project: string) {
  runProject.value = project
  runId.value = null
  runPanel.value = true
}

function onProjectCreated(key: string) {
  void loadProjects()
  openProject(key)
}

function closeRun() {
  runPanel.value = false
}

function toggleSettings() {
  if (settings.value) { settings.value = false; return }
  if (selected.value && !leaveAgent()) return
  selected.value = null
  board.value = null
  runPanel.value = false
  jobs.value = false
  teamPanel.value = false
  settings.value = true
}

function toggleJobs() {
  if (jobs.value) { jobs.value = false; return }
  if (selected.value && !leaveAgent()) return
  selected.value = null
  board.value = null
  runPanel.value = false
  settings.value = false
  teamPanel.value = false
  jobs.value = true
  void loadOverview()
}

function leaveAgent(): boolean {
  return agentPanel.value?.canLeave() ?? true
}

function selectAgent(key: string | null) {
  if (key === selected.value || !leaveAgent()) return
  selected.value = key
  if (key) { board.value = null; runPanel.value = false; settings.value = false; jobs.value = false; teamPanel.value = false }
}

function closeAgent() {
  if (leaveAgent()) selected.value = null
}

/** Secili ajan sahnede durur, izleyiciye bakar, isini balonda yazar; panel kapaninca akisina doner. */
watch(selected, key => scene.value?.focusAgent(key))

function openBoard(s: BoardSnapshot) {
  if (selected.value && !leaveAgent()) return
  selected.value = null
  runPanel.value = false
  settings.value = false
  jobs.value = false
  teamPanel.value = false
  board.value = s
}


/** Gelen kutusu, pano ya da proje panelinden: id → o calisma; '' → yeni is (acik projenin icinde, yoksa uyari). Proje paneli acik kalir. */
function openRun(id: string) {
  if (selected.value && !leaveAgent()) return
  closeOthers()
  if (id) {
    runId.value = id
    const r = runsList.value.find(x => x.id === id)
    if (r?.project) runProject.value = r.project
  } else {
    runId.value = null
    runProject.value = projectPanel.value ? projectKey.value : null
  }
  runPanel.value = true
}

let boardTimer: ReturnType<typeof setInterval> | undefined
function onKey(e: KeyboardEvent) {
  const el = e.target as HTMLElement | null
  if (el && /^(INPUT|TEXTAREA|SELECT)$/.test(el.tagName)) return
  if (e.key === 'Escape') {
    if (help.value) help.value = false
    else if (bell.value) bell.value = false
    else if (teamPanel.value) teamPanel.value = false
    else if (settings.value) settings.value = false
    else if (jobs.value) jobs.value = false
    else if (runPanel.value) runPanel.value = false
    else if (projectPanel.value) projectPanel.value = false
    else if (board.value) board.value = null
    else if (selected.value) closeAgent()
  }
  if (e.key === 'b' || e.key === 'B') {
    if (board.value) board.value = null
    else scene.value?.publishBoard()
  }
  if (e.key === 'n' || e.key === 'N') openProject('')
  if (e.key === 's' || e.key === 'S') toggleSettings()
  if (e.key === 'i' || e.key === 'I') toggleJobs()
  if (e.key === 'e' || e.key === 'E') toggleTeam()
}
function bootData() {
  void loadTeam()
  void loadProviders()
  void loadOverview()
  void loadRuns()
  void loadProjects()
  clearInterval(overviewTimer)
  overviewTimer = setInterval(() => { void loadOverview(); void loadRuns(); void loadProjects() }, 5000)
}
onMounted(() => {
  window.addEventListener('keydown', onKey)
  boardTimer = setInterval(() => { if (board.value) scene.value?.publishBoard() }, 1500)
  if (loggedIn.value) bootData()
})
/** Oturum dustu (401 ya da cikis): sayaclar durur, giris ekrani gelir. */
watch(loggedIn, v => { if (!v) { clearInterval(overviewTimer); bell.value = false; closeOthers(); runPanel.value = false; projectPanel.value = false } })
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
.card.quiet { opacity: 0.85; }
.card-avatar { position: absolute; right: 10px; top: 10px; width: 24px; height: 24px; border-radius: 6px; background: #3d5a80; color: #fff; font-size: 10px; font-weight: 700; display: inline-flex; align-items: center; justify-content: center; text-shadow: 0 1px 1px rgba(0,0,0,0.35); }
/* Proje rengi: kartin sol kenari (kullanici istegi 2026-09-20: her projeye bir renk; ray ve Kanban ayni rengi okur). */
.card-color { position: absolute; left: 0; top: 0; bottom: 0; width: 5px; border-radius: 3px 0 0 3px; }
.card .card-title { padding-right: 30px; }
.run-n { color: #1f5f93; font-weight: 700; }
.pin.ask { background: #d23b3b; border-color: #7a1f1f; }
.pin.quiet { background: #9aa1b3; border-color: #5c6373; }
.rail.narrow { width: 232px; }
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


/* ---- Ust bar: bildirim zili ve profil ---- */
.bell-wrap { position: relative; }
.bell-btn { display: inline-flex; align-items: center; gap: 4px; padding: 4px 9px; }
.bell-btn.alert { border-color: #d23b3b; }
.bell-menu {
  position: absolute; right: 0; top: calc(100% + 8px); width: 380px; max-height: 60vh; overflow: auto; z-index: 20;
  background: #ede9dc; color: #23283a; border: 4px solid #6b4a2b; border-radius: 6px; box-shadow: 0 16px 40px rgba(0,0,0,0.5);
  display: flex; flex-direction: column; padding: 8px;
}
.bell-head { font-size: 11px; font-weight: 700; letter-spacing: 0.08em; text-transform: uppercase; color: #4a5068; padding: 4px 6px 8px; }
.bell-head b { margin-left: 6px; color: #9c1f1f; }
.bell-empty { margin: 0; padding: 8px 6px 10px; font-size: 12px; color: #6b7285; line-height: 1.5; }
.bell-item {
  display: grid; grid-template-columns: auto 1fr auto; gap: 8px; align-items: center; text-align: left; font: inherit; font-size: 12px; cursor: pointer;
  padding: 8px 8px; margin-bottom: 4px; background: #f6e2a0; color: #23283a; border: none; border-radius: 3px; box-shadow: 0 2px 0 #b8964a;
}
.bell-item:hover { background: #fbeab0; }
.bell-item .kind { font-size: 10px; font-weight: 700; text-transform: uppercase; letter-spacing: 0.04em; padding: 1px 6px; border-radius: 3px; background: #23283a; color: #f6e2a0; }
.bell-item .kind.decision { background: #9c1f1f; color: #fff; }
.bell-text { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.bell-item .go { font-weight: 700; white-space: nowrap; }
.profile { display: inline-flex; align-items: center; gap: 6px; padding: 2px 4px 2px 2px; border: 1px solid var(--rule); border-radius: 999px; background: var(--surface-2); }
.profile .avatar { width: 22px; height: 22px; border-radius: 50%; background: #d9a13a; color: #141413; font-size: 10px; font-weight: 700; display: inline-flex; align-items: center; justify-content: center; }
.profile .who { font-size: 12px; color: var(--ink); }
.profile .out { font: inherit; font-size: 12px; background: transparent; color: var(--ink-3); border: none; cursor: pointer; padding: 0 4px; }
.profile .out:hover { color: #f0a0a0; }
.help-wrap { position: relative; }
.chip.help { padding: 4px 9px; font-weight: 700; }
.help-menu { position: absolute; right: 0; top: calc(100% + 8px); width: 300px; z-index: 20; background: #ede9dc; color: #23283a; border: 4px solid #6b4a2b; border-radius: 6px; box-shadow: 0 16px 40px rgba(0,0,0,0.5); padding: 8px; }
.keys { display: grid; grid-template-columns: auto 1fr; gap: 4px 10px; margin: 0 6px 6px; font-size: 12px; align-items: center; }
.keys dt kbd { font-size: 11px; background: #fff; border: 1px solid #c9c3b3; border-radius: 4px; padding: 1px 6px; }
.keys dd { margin: 0; }
.toasts { position: fixed; right: 16px; top: 56px; z-index: 40; display: flex; flex-direction: column; gap: 8px; width: min(420px, calc(100% - 32px)); }
.toast { display: grid; grid-template-columns: auto 1fr auto; align-items: center; gap: 8px; background: #f6e2a0; color: #23283a; border-radius: 4px; padding: 8px 10px; box-shadow: 0 3px 0 #b8964a, 0 10px 24px rgba(0,0,0,0.45); font-size: 12px; animation: slidein 0.25s ease-out; }
.toast .kind { font-size: 10px; font-weight: 700; text-transform: uppercase; letter-spacing: 0.04em; padding: 1px 6px; border-radius: 3px; background: #23283a; color: #f6e2a0; }
.toast .kind.decision { background: #9c1f1f; color: #fff; }
.toast-body { font: inherit; text-align: left; background: transparent; border: none; color: inherit; cursor: pointer; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; padding: 0; }
.toast-x { font: inherit; font-size: 16px; background: transparent; border: none; cursor: pointer; color: #4a5068; padding: 0 2px; }
@keyframes slidein { from { transform: translateY(-8px); opacity: 0; } to { transform: none; opacity: 1; } }

/* ---- Sag alt: ekip pusulasi, yari saydam ---- */
.compass {
  position: absolute; right: 14px; bottom: 14px; width: 232px; padding: 10px 10px 8px; border-radius: 10px;
  background: rgba(21,24,32,0.88); border: 1px solid var(--rule); display: flex; flex-direction: column; gap: 2px; z-index: 2;
}
.compass-title { font-size: 10px; font-weight: 700; letter-spacing: 0.1em; text-transform: uppercase; color: var(--ink-3); }
.compass-head { display: flex; align-items: center; gap: 8px; width: 100%; font: inherit; background: transparent; border: none; color: var(--ink-3); cursor: pointer; padding: 2px 4px 6px; text-align: left; }
.compass-head:hover .compass-title { color: var(--ink-2); }
.compass-chev { margin-left: auto; font-size: 11px; }
.compass.min { width: auto; padding: 6px 10px; }
.compass.min .compass-head { padding: 0; }
.compass-dots { display: inline-flex; gap: 4px; }
.compass-dots .dot { width: 9px; height: 9px; border-radius: 2px; opacity: 0.45; }
.compass-dots .dot.busy { opacity: 1; box-shadow: 0 0 0 2px rgba(255,255,255,0.15); }
.compass-busy { font-size: 11px; color: var(--ink-2); }
.compass-manage { font: inherit; font-size: 11px; font-weight: 700; color: #9cc3ef; background: transparent; border: none; cursor: pointer; text-align: left; padding: 4px 6px 2px; }
.compass-manage:hover { text-decoration: underline; }
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
