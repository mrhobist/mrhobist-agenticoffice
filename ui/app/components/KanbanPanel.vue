<script lang="ts">
import type { World } from '~/scene/world'

/** Sahne motorunun verdigi anlik goruntu; panel yalniz bunu okur, kendi kopyasini tutmaz. */
export type BoardSnapshot = ReturnType<World['board']['snapshot']>
</script>

<script setup lang="ts">
import type { TaskState } from '~/scene/contract'
import type { InboxItem, RunDetail, RunStatus, RunSummary } from '~/api/types'
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

const ACTIVE: ReadonlySet<RunStatus> = new Set<RunStatus>(['running', 'awaitingApproval', 'paused'])
const runs = ref<RunSummary[]>([])
const tabRun = ref<string | null>(null) // null = sahne
const runDetail = ref<RunDetail | null>(null)
let runsTimer: ReturnType<typeof setInterval> | undefined
let detailTimer: ReturnType<typeof setInterval> | undefined

async function loadRuns() {
  try {
    const all = await api.get<RunSummary[]>('/api/v1/runs?limit=50')
    runs.value = all.filter(r => ACTIVE.has(r.status))
    if (tabRun.value && !runs.value.some(r => r.id === tabRun.value)) {
      // Sekmesi acik calisma bitti: sekme kalsin, detay son halini gostersin; listeye 'bitti' diye eklenir.
      const gone = all.find(r => r.id === tabRun.value)
      if (gone) runs.value = [...runs.value, gone]
    }
  } catch { /* sahne sekmesi her zaman var */ }
}

async function loadDetail() {
  if (!tabRun.value) return
  try { runDetail.value = await api.get<RunDetail>(`/api/v1/runs/${encodeURIComponent(tabRun.value)}`) } catch { /* eski detay kalir */ }
}

function selectTab(id: string | null) {
  tabRun.value = id
  runDetail.value = null
  selectedId.value = null
  clearInterval(detailTimer)
  if (id) { void loadDetail(); detailTimer = setInterval(() => { void loadDetail() }, 3000) }
}

onMounted(() => { void loadRuns(); runsTimer = setInterval(() => { void loadRuns() }, 5000) })
onBeforeUnmount(() => { clearInterval(runsTimer); clearInterval(detailTimer) })

const COLUMN_HEX = ['#f3c34a', '#4fa3e0', '#ef6f9a', '#a889e6', '#7cc46b', '#e0995c']

/** Calisma detayindan pano: sutun = devir disi adimlar + Bitti; kart durumu son fazdan. */
const runBoard = computed<BoardSnapshot | null>(() => {
  const d = runDetail.value
  if (!tabRun.value || !d?.workflowDef) return null
  const stages = d.workflowDef.stages.filter(s => s.kind !== 'analyze' && s.kind !== 'handoff')
  const columns = stages.map((s, i) => ({ id: s.id, title: s.title, hex: COLUMN_HEX[i % COLUMN_HEX.length]! }))
  columns.push({ id: '__done', title: 'Bitti', hex: '#7cc46b' })
  const lastStage = stages[stages.length - 1]?.id
  const tasks = (d.spec?.tasks ?? []).map((t) => {
    const phases = d.tasks.find(x => x.id === t.id)?.phases ?? []
    const last = phases[phases.length - 1]
    let state: TaskState = 'queued'
    let column = stages[0]?.id ?? '__done'
    if (last) {
      column = last.stage
      if (last.status === 'started') state = 'active'
      else if (last.status === 'done') { state = last.stage === lastStage ? 'done' : 'queued'; if (state === 'done') column = '__done' }
      else if (last.status === 'rejected' || last.status === 'failed') state = 'blocked'
      else state = 'queued'
    }
    const lane = column === '__done' ? 'done' : state === 'active' ? 'doing' : state === 'blocked' ? 'review' : 'todo'
    return { id: t.id, title: t.title, stage: column, state, column, lane } as BoardSnapshot['tasks'][number]
  })
  return { columns, tasks }
})

/** Gosterilen pano: secili calisma varsa turetilmis, yoksa sahne. */
const view = computed<BoardSnapshot>(() => runBoard.value ?? props.board)

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
        <button type="button" role="tab" :aria-selected="tabRun === null" :class="{ on: tabRun === null }" @click="selectTab(null)">Sahne</button>
        <button v-for="r in runs" :key="r.id" type="button" role="tab" :aria-selected="tabRun === r.id" :class="{ on: tabRun === r.id }" :title="`${r.label} · ${RUN_STATUS_LABEL[r.status]}`" @click="selectTab(r.id)">
          <span class="dot" :class="r.status" /> {{ r.label }}
        </button>
        <span v-if="!runs.length" class="sub">aktif çalışma yok</span>
        <button v-if="tabRun" type="button" class="open" @click="emit('open', tabRun)">Çalışmayı aç →</button>
      </nav>
      <p v-if="tabRun && runDetail && !runDetail.spec" class="empty tabnote">Bu çalışmanın planı henüz yok ({{ RUN_STATUS_LABEL[runDetail.status] }}); onaylanınca görevler burada açılır.</p>

      <!-- Senden bekleyenler: soru (plan onayi) ya da karar (duran calisma). Tiklaninca calisma paneli acilir. -->
      <section v-if="inbox.length" class="inbox" aria-label="Senden bekleniyor">
        <h3><span aria-hidden="true">🔔</span> Senden bekleniyor <b>{{ inbox.length }}</b></h3>
        <button v-for="i in inbox" :key="i.runId + i.kind + i.ts" type="button" class="item" @click="emit('open', i.runId)">
          <span class="kind" :class="i.kind">{{ INBOX_KIND_LABEL[i.kind] }}</span>
          <strong>{{ i.label }}</strong>
          <span class="what">{{ i.title }}</span>
          <span class="go">Cevapla →</span>
        </button>
      </section>

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
            :style="{ borderColor: c.hex }"
            @click="toggle(t.id)"
          >
            <span class="id">{{ t.id }}</span>
            <span class="title">{{ t.title }}</span>
            <span class="badge" :class="t.state">{{ TASK_LABEL[t.state] }}</span>
          </button>
        </div>
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

.inbox { background: #fff8e1; border: 1px solid #f3c34a; border-radius: 6px; padding: 8px 12px 10px; margin-bottom: 12px; display: flex; flex-direction: column; gap: 6px; }
.inbox h3 { margin: 0 0 2px; font-size: 12px; text-transform: uppercase; letter-spacing: 0.06em; color: #4a5068; display: flex; align-items: center; gap: 6px; }
.inbox h3 b { background: #d23b3b; color: #fff; border-radius: 999px; padding: 0 7px; font-size: 11px; }
.item {
  width: 100%; text-align: left; font: inherit; cursor: pointer; display: grid; grid-template-columns: auto auto 1fr auto; gap: 10px; align-items: center;
  background: #fff; color: #23283a; border: none; border-left: 4px solid #d23b3b; border-radius: 4px; padding: 7px 10px; font-size: 12px; box-shadow: 0 1px 2px rgba(0,0,0,0.12);
}
.item:hover { box-shadow: 0 2px 6px rgba(0,0,0,0.2); }
.item strong { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; max-width: 280px; }
.item .what { color: #4a5068; }
.item .go { font-weight: 700; color: #1f5f93; }
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
.tabs .open { margin-left: auto; font-weight: 700; color: #1f5f93; }
.tabs .sub { font-size: 11px; color: #6b7285; padding: 6px 4px; }
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
</style>
