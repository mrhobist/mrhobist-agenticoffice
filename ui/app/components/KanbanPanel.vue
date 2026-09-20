<script lang="ts">
import type { World } from '~/scene/world'

/** Sahne motorunun verdigi anlik goruntu; panel yalniz bunu okur, kendi kopyasini tutmaz. */
export type BoardSnapshot = ReturnType<World['board']['snapshot']>
</script>

<script setup lang="ts">
import type { TaskState } from '~/scene/contract'
import type { InboxItem, ProjectCard, RunDetail, RunStatus, RunSummary } from '~/api/types'
import { INBOX_KIND_LABEL, RUN_STATUS_LABEL } from '~/api/labels'
import { useApiClient } from '~/api/client'

/**
 * `inbox`: senden bir sey bekleyen isler (GET /runs/overview). Onaysiz plan panoya is acmaz; bekleyen plan burada gorunur.
 * Sekmeler (kullanici karari 2026-09-19): "Sahne" = canli sahne panosu (olay akisi); her aktif calisma icin bir sekme,
 * sutunlar o calismanin dondurulmus akisindan, kartlar plan + fazlardan TURETILIR (GET /runs/{id}, 3 s) — yenilemede kaybolmaz.
 */
const props = defineProps<{ board: BoardSnapshot; inbox: InboxItem[] }>()
const emit = defineEmits<{ close: []; open: [runId: string] }>()

const api = useApiClient()

// ------------------------------------------------------------------ calisma sekmeleri

const ACTIVE: ReadonlySet<RunStatus> = new Set<RunStatus>(['running', 'awaitingApproval', 'paused', 'awaitingInput'])
/** Sekme sirasi: senden cevap bekleyen → calisan → onay bekleyen → limit → bitmis (yeni → eski). */
const RANK: Record<RunStatus, number> = { awaitingInput: 0, running: 1, awaitingApproval: 2, paused: 3, failed: 4, interrupted: 4, budgetExceeded: 4, completed: 5, cancelled: 6, policyRejected: 7 }
const runs = ref<RunSummary[]>([])
const projects = ref<ProjectCard[]>([])
const tabRun = ref<string | null>(null) // null = sahne

/** Sekmeler proje bazinda gruplu (kullanici istegi 2026-09-19): baslik = proje adi, altinda o projenin aktif isleri. */
const groups = computed(() => {
  const byKey = new Map<string, { key: string; title: string; runs: RunSummary[] }>()
  for (const r of runs.value) {
    const k = r.project ?? ''
    if (!byKey.has(k)) byKey.set(k, { key: k, title: projects.value.find(p => p.key === k)?.title ?? (k || 'Projesiz'), runs: [] })
    byKey.get(k)!.runs.push(r)
  }
  const order = new Map(projects.value.map((p, i) => [p.key, i]))
  return [...byKey.values()].sort((a, b) => (order.get(a.key) ?? 99) - (order.get(b.key) ?? 99))
})
const runDetail = ref<RunDetail | null>(null)
let picked = false

// ------------------------------------------------------------------ "Tumu" (kullanici istegi 2026-09-20): tum projelerin gorevleri tek panoda,
// akislar farkli olabildigi icin sutunlar dort Kanban seridi; kart rengi = proje rengi.
const allMode = ref(false)
const allDetails = ref<Record<string, RunDetail>>({})
let allTimer: ReturnType<typeof setInterval> | undefined
async function loadAllDetails() {
  const ids = runs.value.map(r => r.id)
  const results = await Promise.all(ids.map(id => api.get<RunDetail>(`/api/v1/runs/${encodeURIComponent(id)}`).catch(() => null)))
  const next: Record<string, RunDetail> = {}
  ids.forEach((id, i) => { const d = results[i]; if (d) next[id] = d })
  allDetails.value = next
}
function selectAll() {
  tabRun.value = null
  runDetail.value = null
  selectedId.value = null
  clearInterval(detailTimer)
  allMode.value = true
  void loadAllDetails()
  clearInterval(allTimer)
  allTimer = setInterval(() => { void loadAllDetails() }, 5000)
}
function projectColor(key: string): string { return projects.value.find(p => p.key === key)?.color || '#7b87a0' }
function projectTitle(key: string): string { return projects.value.find(p => p.key === key)?.title ?? key }

type Lane = 'todo' | 'doing' | 'review' | 'done'
const LANES: ReadonlyArray<{ id: Lane; title: string; hex: string }> = [
  { id: 'todo', title: 'Yapılacak', hex: '#f3c34a' },
  { id: 'doing', title: 'Yapılıyor', hex: '#4fa3e0' },
  { id: 'review', title: 'İnceleme', hex: '#ef6f9a' },
  { id: 'done', title: 'Bitti', hex: '#7cc46b' },
]

/** Bir calisma detayindan kartlar (sutun + serit). Tek sekme ve "Tumu" ayni turetimi kullanir; sunucu BoardTarget ile ayni kural. */
function deriveTasks(d: RunDetail): Array<BoardSnapshot['tasks'][number]> {
  const stages = d.workflowDef?.stages.filter(s => s.kind !== 'analyze' && s.kind !== 'handoff') ?? []
  return (d.spec?.tasks ?? []).map((t) => {
    const phases = d.tasks.find(x => x.id === t.id)?.phases ?? []
    const last = phases[phases.length - 1]
    let state: TaskState = 'queued'
    let column = stages[0]?.id ?? '__done'
    if (last) {
      const i = stages.findIndex(s => s.id === last.stage)
      column = last.stage
      if (last.status === 'started') state = 'active'
      else if (last.status === 'done' || last.status === 'skipped') {
        if (i >= 0 && i + 1 < stages.length) { column = stages[i + 1]!.id; state = 'queued' }
        else { column = '__done'; state = 'done' }
      } else if (last.status === 'rejected') {
        state = 'blocked'
        for (let k = i - 1; k >= 0; k--) if (stages[k]!.kind === 'implement') { column = stages[k]!.id; break }
      } else state = 'blocked'
    }
    const kind = stages.find(s => s.id === column)?.kind
    const lane: Lane = column === '__done' ? 'done' : state === 'active' ? 'doing' : (kind === 'review' || state === 'blocked') ? 'review' : 'todo'
    return { id: t.id, title: t.title, stage: column, state, column, lane } as BoardSnapshot['tasks'][number]
  })
}

/** "Tumu" panosu: seritler sutun olur; kart kimligi calisma+gorev (farkli calismalarda t1 cakisir). */
const allBoard = computed<(BoardSnapshot & { meta: Record<string, { run: string; label: string; project: string; color: string }> }) | null>(() => {
  if (!allMode.value) return null
  const tasks: BoardSnapshot['tasks'] = []
  const meta: Record<string, { run: string; label: string; project: string; color: string }> = {}
  const order = new Map(projects.value.map((p, i) => [p.key, i]))
  // Iptal/politika reddi: gorevleri hic baslamamis, "Tumu"yu kalabaliklastirmasin.
  const sorted = [...runs.value].filter(r => r.status !== 'cancelled' && r.status !== 'policyRejected').sort((a, b) => (order.get(a.project) ?? 99) - (order.get(b.project) ?? 99))
  for (const r of sorted) {
    const d = allDetails.value[r.id]
    if (!d) continue
    for (const t of deriveTasks(d)) {
      const id = `${r.id}:${t.id}`
      tasks.push({ ...t, id, column: t.lane })
      meta[id] = { run: r.id, label: r.label, project: r.project, color: projectColor(r.project) }
    }
  }
  return { columns: LANES.map(l => ({ id: l.id, title: l.title, hex: l.hex })), tasks, meta }
})
let runsTimer: ReturnType<typeof setInterval> | undefined
let detailTimer: ReturnType<typeof setInterval> | undefined

async function loadRuns() {
  try {
    const [all, ps] = await Promise.all([api.get<RunSummary[]>('/api/v1/runs?limit=50'), api.get<ProjectCard[]>('/api/v1/projects').catch(() => projects.value)])
    projects.value = ps
    // Bitmis isler de sekmede kalir (kullanici: "tamamlanan isler yanlis" — listeden dusuyordu): aktifler once, sonra en yeni bitenler.
    runs.value = [...all].sort((a, b) => (RANK[a.status] - RANK[b.status]) || b.startedAt.localeCompare(a.startedAt)).slice(0, 12)
    // Ilk acilista en anlamli sekme kendi secilir: sahne panosu yalniz canli olaylari bilir, yenilemede bos kalir.
    if (!picked && runs.value.length) { picked = true; selectTab(runs.value[0]!.id) }
  } catch { /* sahne sekmesi her zaman var */ }
}

async function loadDetail() {
  if (!tabRun.value) return
  try { runDetail.value = await api.get<RunDetail>(`/api/v1/runs/${encodeURIComponent(tabRun.value)}`) } catch { /* eski detay kalir */ }
}

function selectTab(id: string | null) {
  allMode.value = false
  clearInterval(allTimer)
  tabRun.value = id
  runDetail.value = null
  selectedId.value = null
  clearInterval(detailTimer)
  if (id) { void loadDetail(); detailTimer = setInterval(() => { void loadDetail() }, 3000) }
}

onMounted(() => { void loadRuns(); runsTimer = setInterval(() => { void loadRuns() }, 5000) })
onBeforeUnmount(() => { clearInterval(runsTimer); clearInterval(detailTimer); clearInterval(allTimer) })

const COLUMN_HEX = ['#f3c34a', '#4fa3e0', '#ef6f9a', '#a889e6', '#7cc46b', '#e0995c']

/**
 * Calisma detayindan pano: sutun = devir disi adimlar + Bitti; kart yeri son fazdan (sunucu BoardTarget ile ayni kural):
 * Started → o sutunda calisiliyor · Done/Skipped → SONRAKI sutunda sirada, son adimsa Bitti · Rejected → onceki implement
 * sutununda takildi · Failed → ayni sutunda takildi · faz yok → ilk sutunda sirada.
 */
const runBoard = computed<BoardSnapshot | null>(() => {
  const d = runDetail.value
  if (!tabRun.value || !d?.workflowDef) return null
  const stages = d.workflowDef.stages.filter(s => s.kind !== 'analyze' && s.kind !== 'handoff')
  const columns = stages.map((s, i) => ({ id: s.id, title: s.title, hex: COLUMN_HEX[i % COLUMN_HEX.length]! }))
  columns.push({ id: '__done', title: 'Bitti', hex: '#7cc46b' })
  return { columns, tasks: deriveTasks(d) }
})

/** Gosterilen pano: "Tumu" → projeler arasi seritler; secili calisma → turetilmis; yoksa sahne. */
const view = computed<BoardSnapshot>(() => allBoard.value ?? runBoard.value ?? props.board)
function cardMeta(id: string) { return allBoard.value?.meta[id] }

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

const columns = computed(() => view.value.columns.map((c) => {
  const all = view.value.tasks.filter(t => t.column === c.id)
  const shown = filter.value === 'all' ? all : all.filter(t => t.state === filter.value)
  return { ...c, total: all.length, tasks: shown }
}))

const filterCount = computed<Record<Filter, number>>(() => ({
  all: view.value.tasks.length,
  active: view.value.tasks.filter(t => t.state === 'active').length,
  blocked: view.value.tasks.filter(t => t.state === 'blocked').length,
  done: view.value.tasks.filter(t => t.state === 'done').length,
}))

/** Pano her 1.5 s yenilenir; secim id ile tutulur, gorev kaybolursa detay kapanir. */
const selected = computed(() => selectedId.value ? view.value.tasks.find(t => t.id === selectedId.value) ?? null : null)
const selectedColumn = computed(() => view.value.columns.find(c => c.id === selected.value?.column)?.title ?? '—')

function fmtWhen(ts: string): string {
  const d = new Date(ts)
  const today = new Date().toDateString() === d.toDateString()
  const time = d.toLocaleTimeString('tr-TR', { hour: '2-digit', minute: '2-digit' })
  return today ? time : `${d.toLocaleDateString('tr-TR', { day: '2-digit', month: '2-digit' })} ${time}`
}

function toggle(id: string) {
  selectedId.value = selectedId.value === id ? null : id
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

      <!-- Sekmeler: Sahne (canli olay akisi) + aktif calismalar (durumdan turetilir). -->
      <nav class="tabs" role="tablist" aria-label="Pano">
        <button type="button" role="tab" :aria-selected="tabRun === null && !allMode" :class="{ on: tabRun === null && !allMode }" @click="selectTab(null)">Sahne</button>
        <button type="button" role="tab" class="all" :aria-selected="allMode" :class="{ on: allMode }" title="Tüm projelerin görevleri; renk = proje" @click="selectAll">Tümü</button>
        <template v-for="g in groups" :key="g.key">
          <span class="group"><span class="pdot" :style="{ background: projectColor(g.key) }" aria-hidden="true" />{{ g.title }}</span>
          <button v-for="r in g.runs" :key="r.id" type="button" role="tab" :aria-selected="tabRun === r.id" :class="{ on: tabRun === r.id }" :title="`${g.title} › ${r.label} · ${RUN_STATUS_LABEL[r.status]}`" @click="selectTab(r.id)">
            <span class="dot" :class="r.status" /> {{ r.label }}
          </button>
        </template>
        <span v-if="!runs.length" class="sub">henüz çalışma yok</span>
        <button v-if="tabRun" type="button" class="open" @click="emit('open', tabRun)">Çalışmayı aç →</button>
        <button v-else-if="allMode && selected && cardMeta(selected.id)" type="button" class="open" @click="emit('open', cardMeta(selected.id)!.run)">Çalışmayı aç →</button>
      </nav>
      <p v-if="tabRun && runDetail && !runDetail.spec" class="empty tabnote">Bu çalışmanın planı henüz yok ({{ RUN_STATUS_LABEL[runDetail.status] }}); onaylanınca görevler burada açılır.</p>

      <div class="layout">
        <div class="cols">
          <div v-for="c in columns" :key="c.id" class="col">
            <div class="col-h" :style="{ background: c.hex }">
              <span>{{ c.title }}</span>
              <span class="count">{{ filter === 'all' ? c.total : `${c.tasks.length}/${c.total}` }}</span>
            </div>
            <p v-if="!c.tasks.length" class="empty">{{ c.total ? 'filtreye uyan görev yok' : 'boş' }}</p>
            <button
              v-for="t in c.tasks"
              :key="t.id"
              type="button"
              class="card"
              :class="[t.state, { on: t.id === selectedId }]"
              :style="{ borderColor: cardMeta(t.id)?.color ?? c.hex }"
              @click="toggle(t.id)"
            >
              <span v-if="cardMeta(t.id)" class="proj" :style="{ background: cardMeta(t.id)!.color }">{{ projectTitle(cardMeta(t.id)!.project) }}</span>
              <span class="id">{{ cardMeta(t.id) ? `${cardMeta(t.id)!.label} · ${t.id.split(':').pop()}` : t.id }}</span>
              <span class="title">{{ t.title }}</span>
              <span class="badge" :class="t.state">{{ TASK_LABEL[t.state] }}</span>
            </button>
          </div>
        </div>

        <!-- Bagimsiz alan (kullanici istegi 2026-09-20): senden bekleyenler = zildeki bildirimlerle AYNI kaynak (GET /runs/overview → inbox).
             Sekmeden bagimsizdir, bos olsa da durur; tiklaninca ilgili calisma acilir. -->
        <aside class="inbox-col" aria-label="Senden bekleniyor">
          <div class="col-h inbox-h">
            <span><span aria-hidden="true">🔔</span> Senden bekleniyor</span>
            <span class="count" :class="{ alert: inbox.length }">{{ inbox.length }}</span>
          </div>
          <p v-if="!inbox.length" class="empty">Bekleyen yok. Plan onayı, takılma sorusu ya da düşen iş buraya ve zile düşer.</p>
          <button v-for="i in inbox" :key="i.runId + i.kind + i.ts" type="button" class="item" :class="i.kind" :title="i.detail ?? ''" @click="emit('open', i.runId)">
            <span class="item-head"><span class="kind" :class="i.kind">{{ INBOX_KIND_LABEL[i.kind] }}</span><span class="when">{{ fmtWhen(i.ts) }}</span></span>
            <strong>{{ i.label }}</strong>
            <span class="what">{{ i.title }}</span>
            <span v-if="i.task" class="sub">görev <code>{{ i.task }}</code></span>
            <span class="go">Cevapla →</span>
          </button>
        </aside>
      </div>

      <dl v-if="selected" class="detail">
        <div><dt>Görev</dt><dd>{{ selected.id }}</dd></div>
        <div class="wide"><dt>Başlık</dt><dd>{{ selected.title }}</dd></div>
        <div><dt>Sütun</dt><dd>{{ selectedColumn }}</dd></div>
        <div><dt>Durum</dt><dd><span class="badge" :class="selected.state">{{ TASK_LABEL[selected.state] }}</span></dd></div>
        <button class="x" type="button" aria-label="Detayı kapat" @click="selectedId = null">×</button>
      </dl>

      <p class="hint">Sütunlar seçili iş akışının adımları (<code>config/workflows/</code>); sahnedeki küçük pano bunları dört Kanban şeridine katlar. Onaylanmamış plan panoya iş açmaz; onay bekleyenler üstteki şeritte durur. <kbd>Esc</kbd> ya da <kbd>B</kbd> kapatır.</p>
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
.tabs .group { font-size: 10px; font-weight: 700; letter-spacing: 0.08em; text-transform: uppercase; color: #6b4a2b; padding: 0 4px 0 10px; align-self: center; border-left: 2px solid #b9ad92; }
.tabnote { margin: 0 0 10px; }
.cols { display: grid; grid-auto-flow: column; grid-auto-columns: minmax(130px, 1fr); gap: 10px; }
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
.tabs .group .pdot { display: inline-block; width: 8px; height: 8px; border-radius: 50%; margin-right: 5px; vertical-align: middle; }
.card .proj { grid-column: 1 / span 2; justify-self: start; display: inline-block; font-size: 9px; font-weight: 700; color: #fff; padding: 1px 6px; border-radius: 999px; margin-bottom: 3px; text-shadow: 0 1px 1px rgba(0,0,0,0.35); max-width: 100%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
</style>
