<script lang="ts">
import type { World } from '~/scene/world'

/** Sahne motorunun anlik goruntusu. Panel artik bunu cizmez (sekmeler proje bazli); tip, sahnedeki kucuk pano icin app.vue'da kullanilir. */
export type BoardSnapshot = ReturnType<World['board']['snapshot']>
</script>

<script setup lang="ts">
import type { TaskState } from '~/scene/contract'
import type { InboxItem, ProjectCard, RunDetail, RunStatus, RunSummary } from '~/api/types'
import { INBOX_KIND_LABEL, RUN_STATUS_LABEL } from '~/api/labels'
import { useApiClient } from '~/api/client'
import { deriveCards, HIDDEN_RUN_STATUS, type DerivedCard } from '~/api/board'

/**
 * Sprint panosu (kullanici karari 2026-09-20): sekmeler **Tümü | proje | proje …** (proje sirasi ve rengiyle; "Sahne" yok).
 * Sutunlar dort Kanban seridi (akislar projeye gore degisebilir). Kartlar calisma detayindan TURETILIR (GET /runs/{id}, 5 s):
 * "Tümü"de proje proje gruplu, proje sekmesinde calisma (is) basina gruplu. Sag tarafta sekmeden bagimsiz "Senden bekleniyor"
 * sutunu: zille ayni kaynak (`inbox`). Iptal / politika reddi kartlari gosterilmez.
 */
const props = defineProps<{ board: BoardSnapshot; inbox: InboxItem[] }>()
const emit = defineEmits<{ close: []; open: [runId: string] }>()

const api = useApiClient()

// ------------------------------------------------------------------ veri: projeler, calismalar, detaylar

/** Grup sirasi: senden cevap bekleyen → calisan → onay bekleyen → limit → dusen → bitmis (yeni → eski). */
const RANK: Record<RunStatus, number> = { awaitingInput: 0, running: 1, awaitingApproval: 2, paused: 3, failed: 4, interrupted: 4, budgetExceeded: 4, completed: 5, cancelled: 6, policyRejected: 7 }

const projects = ref<ProjectCard[]>([])
const runs = ref<RunSummary[]>([])
const details = ref<Record<string, RunDetail>>({})
/** null = Tümü; aksi halde proje anahtari. */
const tab = ref<string | null>(null)
let runsTimer: ReturnType<typeof setInterval> | undefined
let detailsTimer: ReturnType<typeof setInterval> | undefined

async function loadRuns() {
  try {
    const [all, ps] = await Promise.all([api.get<RunSummary[]>('/api/v1/runs?limit=60'), api.get<ProjectCard[]>('/api/v1/projects').catch(() => projects.value)])
    projects.value = ps
    runs.value = all.filter(r => !HIDDEN_RUN_STATUS.has(r.status)).sort((a, b) => (RANK[a.status] - RANK[b.status]) || b.startedAt.localeCompare(a.startedAt))
    if (tab.value && !projects.value.some(p => p.key === tab.value)) tab.value = null
    void loadDetails()
  } catch { /* eski liste kalir */ }
}

/** Gorunen calismalar: Tümü → hepsi (en yeni 30), proje sekmesi → o projenin calismalari. */
const visibleRuns = computed(() => (tab.value ? runs.value.filter(r => r.project === tab.value) : runs.value).slice(0, 30))

/** Yalniz gorunen calismalarin detayi cekilir; bitmis calismanin detayi degismez, bir kez yeter. */
async function loadDetails() {
  const need = visibleRuns.value.filter(r => !details.value[r.id] || !['completed', 'failed', 'interrupted', 'budgetExceeded'].includes(r.status))
  const results = await Promise.all(need.map(r => api.get<RunDetail>(`/api/v1/runs/${encodeURIComponent(r.id)}`).catch(() => null)))
  const next = { ...details.value }
  need.forEach((r, i) => { const d = results[i]; if (d) next[r.id] = d })
  details.value = next
}

function selectTab(key: string | null) {
  tab.value = key
  selectedId.value = null
  void loadDetails()
}

onMounted(() => {
  void loadRuns()
  runsTimer = setInterval(() => { void loadRuns() }, 5000)
  detailsTimer = setInterval(() => { void loadDetails() }, 5000)
})
onBeforeUnmount(() => { clearInterval(runsTimer); clearInterval(detailsTimer) })

function projectOf(key: string): ProjectCard | undefined { return projects.value.find(p => p.key === key) }
function projectColor(key: string): string { return projectOf(key)?.color || '#7b87a0' }
function projectTitle(key: string): string { return projectOf(key)?.title ?? key }
/** Sekme rozeti: projede senden bekleyen var mi. */
function pendingOfProject(key: string): number {
  const ids = new Set(runs.value.filter(r => r.project === key).map(r => r.id))
  return props.inbox.filter(i => ids.has(i.runId)).length
}

// ------------------------------------------------------------------ turetim: kartlar ve seritler

type Lane = 'todo' | 'doing' | 'review' | 'done'
const LANES: ReadonlyArray<{ id: Lane; title: string; hex: string }> = [
  { id: 'todo', title: 'Yapılacak', hex: '#f3c34a' },
  { id: 'doing', title: 'Yapılıyor', hex: '#4fa3e0' },
  { id: 'review', title: 'İnceleme', hex: '#ef6f9a' },
  { id: 'done', title: 'Bitti', hex: '#7cc46b' },
]

type Card = DerivedCard & { color: string }
interface Group { key: string; title: string; color: string; status?: RunStatus; cards: Card[] }

const cards = computed<Card[]>(() => visibleRuns.value.flatMap((r) => {
  const d = details.value[r.id]
  return d ? deriveCards(r, d).map(c => ({ ...c, color: projectColor(r.project) })) : []
}))

type Filter = 'all' | 'active' | 'blocked' | 'done'
const FILTERS: ReadonlyArray<{ id: Filter; label: string }> = [
  { id: 'all', label: 'tümü' },
  { id: 'active', label: 'çalışılıyor' },
  { id: 'blocked', label: 'takıldı' },
  { id: 'done', label: 'bitti' },
]
const TASK_LABEL: Record<TaskState, string> = { queued: 'sırada', active: 'çalışılıyor', blocked: 'takıldı', done: 'bitti' }
const filter = ref<Filter>('all')
const selectedId = ref<string | null>(null)

/** Serit → gruplar: Tümü'de proje proje (proje sirasiyla), proje sekmesinde calisma basina (durum sirasiyla). */
const columns = computed(() => LANES.map((lane) => {
  const inLane = cards.value.filter(c => c.lane === lane.id)
  const shown = filter.value === 'all' ? inLane : inLane.filter(c => c.state === filter.value)
  const groups: Group[] = []
  if (tab.value === null) {
    for (const p of projects.value) {
      const mine = shown.filter(c => c.run.project === p.key)
      if (mine.length) groups.push({ key: p.key, title: p.title, color: p.color || '#7b87a0', cards: mine })
    }
    const orphan = shown.filter(c => !projects.value.some(p => p.key === c.run.project))
    if (orphan.length) groups.push({ key: '__none', title: 'Projesiz', color: '#7b87a0', cards: orphan })
  } else {
    for (const r of visibleRuns.value) {
      const mine = shown.filter(c => c.run.id === r.id)
      if (mine.length) groups.push({ key: r.id, title: r.label, color: projectColor(r.project), status: r.status, cards: mine })
    }
  }
  return { ...lane, total: inLane.length, shown: shown.length, groups }
}))

const filterCount = computed<Record<Filter, number>>(() => ({
  all: cards.value.length,
  active: cards.value.filter(c => c.state === 'active').length,
  blocked: cards.value.filter(c => c.state === 'blocked').length,
  done: cards.value.filter(c => c.state === 'done').length,
}))

/** Sekmeye gore gelen kutusu: Tümü → hepsi, proje → o projenin calismalari. */
const inboxShown = computed(() => {
  if (tab.value === null) return props.inbox
  const ids = new Set(runs.value.filter(r => r.project === tab.value).map(r => r.id))
  return props.inbox.filter(i => ids.has(i.runId))
})

const selected = computed(() => selectedId.value ? cards.value.find(c => c.id === selectedId.value) ?? null : null)
function stageTitle(c: Card): string {
  if (c.stage === '__done') return 'Bitti'
  return details.value[c.run.id]?.workflowDef?.stages.find(s => s.id === c.stage)?.title ?? c.stage
}
function toggle(id: string) { selectedId.value = selectedId.value === id ? null : id }

function fmtWhen(ts: string): string {
  const d = new Date(ts)
  const today = new Date().toDateString() === d.toDateString()
  const time = d.toLocaleTimeString('tr-TR', { hour: '2-digit', minute: '2-digit' })
  return today ? time : `${d.toLocaleDateString('tr-TR', { day: '2-digit', month: '2-digit' })} ${time}`
}
</script>

<template>
  <div class="wrap" @click.self="emit('close')">
    <section class="board" role="dialog" aria-labelledby="board-title">
      <header>
        <h2 id="board-title">Sprint panosu</h2>
        <div class="filters" role="group" aria-label="Duruma göre filtre">
          <button
            v-for="f in FILTERS"
            :key="f.id"
            type="button"
            :class="{ on: filter === f.id }"
            :aria-pressed="filter === f.id"
            @click="filter = f.id"
          >{{ f.label }} <b>{{ filterCount[f.id] }}</b></button>
        </div>
        <button class="x" type="button" aria-label="Kapat" @click="emit('close')">×</button>
      </header>

      <!-- Sekmeler: Tümü | proje | proje … (proje sirasi ve rengi; kullanici karari 2026-09-20). -->
      <nav class="tabs" role="tablist" aria-label="Pano">
        <button type="button" role="tab" class="all" :aria-selected="tab === null" :class="{ on: tab === null }" @click="selectTab(null)">Tümü <b class="n">{{ runs.length }}</b></button>
        <button
          v-for="p in projects"
          :key="p.key"
          type="button"
          role="tab"
          :aria-selected="tab === p.key"
          :class="{ on: tab === p.key }"
          :title="`${p.title} · ${p.runs} iş`"
          @click="selectTab(p.key)"
        >
          <span class="dot" :style="{ background: p.color || '#7b87a0' }" /> {{ p.title }}
          <b v-if="pendingOfProject(p.key)" class="ask" :aria-label="`${pendingOfProject(p.key)} senden bekliyor`">{{ pendingOfProject(p.key) }}</b>
        </button>
        <span v-if="!projects.length" class="sub">henüz proje yok</span>
        <button v-if="selected" type="button" class="open" @click="emit('open', selected.run.id)">Çalışmayı aç →</button>
      </nav>
      <p v-if="!visibleRuns.length" class="empty tabnote">{{ tab ? 'Bu projede gösterilecek iş yok.' : 'Henüz iş yok. Bir projeden "Yeni iş" ile brief gönder; onaylanan plan burada görünür.' }}</p>

      <div class="layout">
        <div class="cols">
          <div v-for="c in columns" :key="c.id" class="col">
            <div class="col-h" :style="{ background: c.hex }">
              <span>{{ c.title }}</span>
              <span class="count">{{ filter === 'all' ? c.total : `${c.shown}/${c.total}` }}</span>
            </div>
            <p v-if="!c.groups.length" class="empty">{{ c.total ? 'filtreye uyan görev yok' : 'boş' }}</p>
            <!-- Grup: Tümü'de proje, proje sekmesinde çalışma. Başlık rengi projenin. -->
            <section v-for="g in c.groups" :key="g.key" class="group" :style="{ borderColor: g.color }">
              <header class="group-h" :title="g.title">
                <span class="gdot" :style="{ background: g.color }" aria-hidden="true" />
                <span class="gtitle">{{ g.title }}</span>
                <span v-if="g.status" class="gstatus" :class="g.status">{{ RUN_STATUS_LABEL[g.status] }}</span>
                <span class="count">{{ g.cards.length }}</span>
              </header>
              <button
                v-for="t in g.cards"
                :key="t.id"
                type="button"
                class="card"
                :class="[t.state, { on: t.id === selectedId }]"
                :style="{ borderColor: g.color }"
                :title="tab === null ? t.run.label : undefined"
                @click="toggle(t.id)"
              >
                <span class="id">{{ t.task }}</span>
                <span class="title">{{ t.title }}</span>
                <span v-if="tab === null" class="runlabel">{{ t.run.label }}</span>
                <span class="badge" :class="t.state">{{ TASK_LABEL[t.state] }}</span>
              </button>
            </section>
          </div>
        </div>

        <!-- Bagimsiz alan: senden bekleyenler = zildeki bildirimlerle AYNI kaynak (GET /runs/overview → inbox).
             Tümü'de hepsi, proje sekmesinde o projenin; bos olsa da durur; tiklaninca ilgili calisma acilir. -->
        <aside class="inbox-col" aria-label="Senden bekleniyor">
          <div class="col-h inbox-h">
            <span><span aria-hidden="true">🔔</span> Senden bekleniyor</span>
            <span class="count" :class="{ alert: inboxShown.length }">{{ inboxShown.length }}</span>
          </div>
          <p v-if="!inboxShown.length" class="empty">Bekleyen yok. Plan onayı, takılma sorusu ya da düşen iş buraya ve zile düşer.</p>
          <button v-for="i in inboxShown" :key="i.runId + i.kind + i.ts" type="button" class="item" :class="i.kind" :title="i.detail ?? ''" @click="emit('open', i.runId)">
            <span class="item-head"><span class="kind" :class="i.kind">{{ INBOX_KIND_LABEL[i.kind] }}</span><span class="when">{{ fmtWhen(i.ts) }}</span></span>
            <strong>{{ i.label }}</strong>
            <span class="what">{{ i.title }}</span>
            <span v-if="i.task" class="sub">görev <code>{{ i.task }}</code></span>
            <span class="go">Cevapla →</span>
          </button>
        </aside>
      </div>

      <dl v-if="selected" class="detail">
        <div><dt>Görev</dt><dd>{{ selected.task }}</dd></div>
        <div class="wide"><dt>Başlık</dt><dd>{{ selected.title }}</dd></div>
        <div><dt>Çalışma</dt><dd>{{ selected.run.label }} · <span class="gstatus" :class="selected.run.status">{{ RUN_STATUS_LABEL[selected.run.status] }}</span></dd></div>
        <div><dt>Adım</dt><dd>{{ stageTitle(selected) }}</dd></div>
        <div><dt>Durum</dt><dd><span class="badge" :class="selected.state">{{ TASK_LABEL[selected.state] }}</span></dd></div>
        <button class="x" type="button" aria-label="Detayı kapat" @click="selectedId = null">×</button>
      </dl>

      <p class="hint">Şeritler dört Kanban adımı; her projenin akış adımları bunlara katlanır (Bitti = akışın son adımı tamamlandı). Onaylanmamış plan panoya iş açmaz; bekleyen onay sağdaki sütunda ve zilde. <kbd>Esc</kbd> ya da <kbd>B</kbd> kapatır.</p>
    </section>
  </div>
</template>

<style scoped>
.wrap {
  position: absolute; inset: 0; background: rgba(10, 12, 18, 0.55);
  display: flex; align-items: center; justify-content: center; padding: 24px;
}
.board {
  background: #ede9dc; color: #23283a; border-radius: 10px; border: 6px solid #6b4a2b;
  width: min(1100px, 100%); max-height: 100%; overflow: auto; padding: 16px 18px;
  box-shadow: 0 20px 60px rgba(0,0,0,0.5);
}
.board > header { display: flex; align-items: center; gap: 14px; margin-bottom: 12px; }
h2 { margin: 0; font-size: 16px; letter-spacing: 0.04em; text-transform: uppercase; }
.x { background: none; border: none; font-size: 22px; cursor: pointer; color: #23283a; line-height: 1; margin-left: auto; padding: 0 4px; }

.layout { display: grid; grid-template-columns: minmax(0, 1fr) 240px; gap: 12px; align-items: start; }
.inbox-col { background: #fff8e1; border: 1px solid #f3c34a; border-radius: 6px; padding: 6px; min-height: 160px; display: flex; flex-direction: column; gap: 6px; }
.inbox-h { background: #f3c34a; }
.inbox-h .count.alert { background: #d23b3b; color: #fff; }
.item {
  width: 100%; text-align: left; font: inherit; cursor: pointer; display: flex; flex-direction: column; gap: 3px;
  background: #fff; color: #23283a; border: none; border-left: 4px solid #f3c34a; border-radius: 4px; padding: 7px 9px; font-size: 12px; box-shadow: 0 1px 2px rgba(0,0,0,0.12);
}
.item.decision { border-left-color: #d23b3b; }
.item.question { border-left-color: #d23b3b; }
.item:hover { box-shadow: 0 2px 6px rgba(0,0,0,0.2); }
.item-head { display: flex; justify-content: space-between; align-items: center; gap: 6px; }
.item .when { font-size: 10px; color: #6b7285; }
.item strong { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.item .what { color: #4a5068; font-size: 11px; line-height: 1.35; display: -webkit-box; -webkit-line-clamp: 3; -webkit-box-orient: vertical; overflow: hidden; }
.item .sub { font-size: 10px; color: #6b7285; }
.item .go { font-weight: 700; color: #1f5f93; font-size: 11px; align-self: flex-end; }
.kind { font-size: 10px; font-weight: 700; text-transform: uppercase; letter-spacing: 0.04em; padding: 1px 7px; border-radius: 999px; background: #e5e7ee; color: #4a5068; white-space: nowrap; }
.kind.approval, .kind.question { background: #f3c34a; color: #3a2f12; }
.kind.decision { background: #fadada; color: #9c1f1f; }

.filters { display: flex; gap: 4px; }
.filters button {
  font: inherit; font-size: 11px; cursor: pointer; color: #4a5068;
  background: rgba(0,0,0,0.05); border: 1px solid transparent; border-radius: 999px; padding: 3px 10px;
}
.filters button b { font-weight: 700; margin-left: 3px; }
.filters button:hover { background: rgba(0,0,0,0.09); }
.filters button.on { background: #fff; border-color: #c9c3b3; color: #23283a; }

.tabs { display: flex; align-items: center; gap: 4px; flex-wrap: wrap; border-bottom: 1px solid rgba(0,0,0,0.12); margin-bottom: 10px; }
.tabs button {
  font: inherit; font-size: 12px; font-weight: 600; cursor: pointer; background: transparent; color: #4a5068;
  border: none; border-bottom: 2px solid transparent; border-radius: 0; padding: 6px 10px; margin-bottom: -1px;
  display: inline-flex; align-items: center; gap: 6px; max-width: 220px;
}
.tabs button.on { color: #23283a; border-bottom-color: #23283a; }
.tabs .dot { width: 8px; height: 8px; border-radius: 50%; background: #7b87a0; flex: none; }
.tabs .dot.running { background: #4fa3e0; }
.tabs .dot.awaitingApproval { background: #f3c34a; }
.tabs .dot.paused { background: #a889e6; }
.tabs .dot.awaitingInput { background: #d23b3b; }
.tabs .dot.completed { background: #7cc46b; }
.tabs .dot.failed, .tabs .dot.interrupted, .tabs .dot.budgetExceeded { background: #d23b3b; }
.tabs .dot.cancelled, .tabs .dot.policyRejected { background: #9aa1b3; }
.tabs .open { margin-left: auto; font-weight: 700; color: #1f5f93; }
.tabs .sub { font-size: 11px; color: #6b7285; padding: 6px 4px; }
.tabnote { margin: 0 0 10px; }
.cols { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 10px; min-width: 0; }
/* Dar pencere: bekleyenler sutunu seritlerin altina iner, seritler ikiser. */
@media (max-width: 900px) {
  .layout { grid-template-columns: 1fr; }
  .cols { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  .inbox-col { min-height: 0; }
}
.col { background: rgba(0,0,0,0.04); border-radius: 6px; padding: 6px; min-height: 160px; }
.col-h {
  display: flex; justify-content: space-between; align-items: center;
  font-size: 11px; font-weight: 700; text-transform: uppercase; padding: 5px 8px; border-radius: 4px; color: #1f2430; margin-bottom: 8px;
}
.count { background: rgba(255,255,255,0.55); border-radius: 999px; padding: 0 6px; font-size: 10px; }
.empty { margin: 14px 0; text-align: center; font-size: 11px; color: #8a90a2; font-style: italic; }

.card {
  width: 100%; text-align: left; font: inherit; cursor: pointer;
  background: #fff; border: none; border-left: 4px solid; border-radius: 4px; padding: 6px 8px; margin-bottom: 6px;
  display: grid; grid-template-columns: auto 1fr; gap: 3px 8px; font-size: 12px; box-shadow: 0 1px 2px rgba(0,0,0,0.12);
}
.card:hover { box-shadow: 0 2px 6px rgba(0,0,0,0.2); }
.card.on { outline: 2px solid #23283a; outline-offset: 1px; }
.card .id { font-weight: 700; color: #4a5068; }
.card .title { color: #23283a; }
.card .badge { grid-column: 1 / span 2; justify-self: start; }
.card.queued { opacity: 0.6; }
.card.blocked { box-shadow: 0 0 0 2px #d23b3b; }
.card.done .title { text-decoration: line-through; color: #6b7285; }

.badge { font-size: 10px; border-radius: 999px; padding: 1px 7px; font-weight: 600; background: #e5e7ee; color: #4a5068; }
.badge.active { background: #d8ebfa; color: #1f5f93; }
.badge.blocked { background: #fadada; color: #9c1f1f; }
.badge.done { background: #dcf1d3; color: #2f6b23; }

.detail {
  position: relative; margin: 12px 0 0; padding: 10px 40px 10px 12px;
  background: #fff; border-radius: 6px; border: 1px solid #c9c3b3;
  display: grid; grid-template-columns: repeat(4, auto); gap: 6px 22px; font-size: 12px;
}
.detail > div { display: flex; flex-direction: column; gap: 2px; }
.detail .wide { grid-column: span 1; min-width: 200px; }
.detail dt { font-size: 10px; text-transform: uppercase; letter-spacing: 0.04em; color: #6b7285; }
.detail dd { margin: 0; color: #23283a; }
.detail .x { position: absolute; top: 6px; right: 8px; margin: 0; font-size: 18px; }

.hint { margin: 12px 0 0; font-size: 11px; color: #6b7285; }
code, kbd { font-size: 10px; background: rgba(0,0,0,0.06); padding: 1px 4px; border-radius: 3px; }
.tabs .all { font-weight: 700; color: #6b4a2b; }
.tabs .n { font-weight: 700; margin-left: 2px; color: #6b7285; }
.tabs .ask { background: #d23b3b; color: #fff; border-radius: 999px; padding: 0 6px; font-size: 10px; margin-left: 2px; }
.group { border-left: 3px solid; border-radius: 4px; padding: 4px 4px 2px 6px; margin-bottom: 8px; background: rgba(255,255,255,0.35); }
.group-h { display: flex; align-items: center; gap: 5px; font-size: 10px; font-weight: 700; color: #4a5068; margin-bottom: 5px; min-width: 0; }
.group-h .gdot { width: 8px; height: 8px; border-radius: 50%; flex: none; }
.group-h .gtitle { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; min-width: 0; flex: 1; }
.group-h .count { background: rgba(0,0,0,0.06); }
.gstatus { font-size: 9px; font-weight: 700; padding: 0 6px; border-radius: 999px; background: #e5e7ee; color: #4a5068; white-space: nowrap; }
.gstatus.running { background: #d8ebfa; color: #1f5f93; }
.gstatus.awaitingApproval { background: #f3c34a; color: #3a2f12; }
.gstatus.awaitingInput, .gstatus.failed, .gstatus.interrupted, .gstatus.budgetExceeded { background: #fadada; color: #9c1f1f; }
.gstatus.paused { background: #efe6ff; color: #5b3fa0; }
.gstatus.completed { background: #dcf1d3; color: #2f6b23; }
.card .runlabel { grid-column: 1 / span 2; font-size: 10px; color: #6b7285; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
</style>
