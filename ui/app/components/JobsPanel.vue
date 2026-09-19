<script setup lang="ts">
import type { InboxItem, RunStatus, RunSummary, RunsOverview } from '~/api/types'
import { useApiClient } from '~/api/client'
import { errorText } from '~/api/errors'
import { INBOX_KIND_LABEL, RUN_STATUS_LABEL } from '~/api/labels'

/**
 * Isler paneli: kac is var, kaci ne durumda, kaci SENDEN bir sey bekliyor.
 *  - Ustte gelen kutusu (GET /runs/overview → inbox): soru (plan onayi) ve karar (durdu: yeniden dene / iptal).
 *  - Ortada durum sayaclari; tiklaninca liste filtrelenir.
 *  - Altta tum calismalar, yeni → eski. Satira tiklaninca calisma paneli acilir.
 * Gelen kutusunu kabuk yoklar (5 s); liste burada 5 s'de bir yenilenir.
 */
const props = defineProps<{ overview: RunsOverview | null }>()
const emit = defineEmits<{ close: []; open: [id: string]; new: [] }>()

const api = useApiClient()
const runs = ref<RunSummary[] | null>(null)
const loadError = ref<string | null>(null)
let timer: ReturnType<typeof setInterval> | undefined

async function load() {
  try {
    runs.value = await api.get<RunSummary[]>('/api/v1/runs?limit=200')
    loadError.value = null
  } catch (e) {
    loadError.value = errorText(e)
  }
}
onMounted(() => { void load(); timer = setInterval(() => { void load() }, 5000) })
onBeforeUnmount(() => clearInterval(timer))

/** Filtre gruplari: "basarisiz" tum dusme turlerini toplar (overview.failed ile ayni tanim). */
type Group = 'all' | 'inbox' | 'running' | 'awaitingApproval' | 'paused' | 'failed' | 'completed' | 'cancelled'
type StatusGroup = Exclude<Group, 'all' | 'inbox'>
const FAILED: ReadonlySet<RunStatus> = new Set<RunStatus>(['failed', 'interrupted', 'budgetExceeded', 'policyRejected'])
function groupOf(s: RunStatus): StatusGroup {
  return FAILED.has(s) ? 'failed' : (s as StatusGroup)
}
const GROUPS: ReadonlyArray<{ id: Group; label: string }> = [
  { id: 'all', label: 'tümü' },
  { id: 'inbox', label: 'senden bekliyor' },
  { id: 'running', label: 'çalışıyor' },
  { id: 'awaitingApproval', label: 'onay bekliyor' },
  { id: 'paused', label: 'durakladı' },
  { id: 'failed', label: 'başarısız' },
  { id: 'completed', label: 'tamamlandı' },
  { id: 'cancelled', label: 'iptal' },
]
const filter = ref<Group>('all')

const inbox = computed<InboxItem[]>(() => props.overview?.inbox ?? [])
const inboxRuns = computed(() => new Set(inbox.value.map(i => i.runId)))

const counts = computed<Record<Group, number>>(() => {
  const c: Record<Group, number> = { all: 0, inbox: inbox.value.length, running: 0, awaitingApproval: 0, paused: 0, failed: 0, completed: 0, cancelled: 0 }
  for (const r of runs.value ?? []) { c.all++; c[groupOf(r.status)]++ }
  return c
})

const shown = computed(() => {
  const list = runs.value ?? []
  if (filter.value === 'all') return list
  if (filter.value === 'inbox') return list.filter(r => inboxRuns.value.has(r.id))
  return list.filter(r => groupOf(r.status) === filter.value)
})

/** Bir calismanin gelen kutusundaki ilk satiri (varsa): listede "senden bekliyor" rozeti icin. */
function pending(id: string): InboxItem | undefined {
  return inbox.value.find(i => i.runId === id)
}

function fmtCost(v: number): string { return v ? `$${v.toFixed(3)}` : '$0' }
function fmtWhen(s: string): string {
  const d = new Date(s)
  const today = new Date().toDateString() === d.toDateString()
  const time = d.toLocaleTimeString('tr-TR', { hour: '2-digit', minute: '2-digit' })
  return today ? time : `${d.toLocaleDateString('tr-TR', { day: '2-digit', month: '2-digit' })} ${time}`
}
const totalCost = computed(() => (runs.value ?? []).reduce((a, r) => a + r.totalCostUsd, 0))
</script>

<template>
  <div class="wrap" @click.self="emit('close')">
    <section class="panel" role="dialog" aria-labelledby="jobs-title">
      <header>
        <h2 id="jobs-title">İşler <span v-if="runs" class="total">{{ counts.all }}</span></h2>
        <span v-if="runs" class="sub">toplam {{ fmtCost(totalCost) }}</span>
        <button type="button" class="small primary" @click="emit('new')">+ Yeni çalışma</button>
        <button class="x" type="button" aria-label="Kapat" @click="emit('close')">×</button>
      </header>

      <!-- Gelen kutusu: senden cevap ya da karar bekleyenler. Bos degilse sari, doluysa kirmizi sayac. -->
      <section class="inbox" :class="{ empty: !inbox.length }" aria-labelledby="inbox-title">
        <h3 id="inbox-title">
          <span aria-hidden="true">{{ inbox.length ? '🔔' : '✓' }}</span>
          Senden bekleniyor <b>{{ inbox.length }}</b>
        </h3>
        <p v-if="!overview" class="sub">Gelen kutusu alınıyor…</p>
        <p v-else-if="!inbox.length" class="sub">Şu an senden bir şey beklenmiyor. Onay bekleyen plan ya da duran bir çalışma olunca burada ve üst barda görünür.</p>
        <ul v-else>
          <li v-for="i in inbox" :key="i.runId + i.kind + i.ts">
            <button type="button" class="item" @click="emit('open', i.runId)">
              <span class="kind" :class="i.kind">{{ INBOX_KIND_LABEL[i.kind] }}</span>
              <strong>{{ i.label }}</strong>
              <span class="sub when">{{ fmtWhen(i.ts) }}</span>
              <span class="title">{{ i.title }}</span>
              <span class="go">Cevapla →</span>
              <span v-if="i.detail" class="sub detail">{{ i.detail }}</span>
            </button>
          </li>
        </ul>
      </section>

      <div class="filters" role="group" aria-label="Duruma göre filtre">
        <button
          v-for="g in GROUPS"
          :key="g.id"
          type="button"
          :class="[g.id, { on: filter === g.id }]"
          :aria-pressed="filter === g.id"
          @click="filter = g.id"
        >{{ g.label }} <b>{{ counts[g.id] }}</b></button>
      </div>

      <p v-if="loadError" class="err" role="alert">{{ loadError }}</p>
      <p v-else-if="runs === null" class="sub">Yükleniyor…</p>
      <p v-else-if="!shown.length" class="sub empty-list">{{ runs.length ? 'Bu filtreye uyan iş yok.' : 'Henüz iş yok. "Yeni çalışma" ile bir brief gönder; analist plana çevirir, plan onayına gelir.' }}</p>
      <ol v-else class="list">
        <li v-for="r in shown" :key="r.id">
          <button type="button" class="row" :class="{ pending: pending(r.id) }" @click="emit('open', r.id)">
            <span class="status" :class="r.status">{{ RUN_STATUS_LABEL[r.status] }}</span>
            <strong class="label">{{ r.label }}</strong>
            <span v-if="pending(r.id)" class="kind" :class="pending(r.id)!.kind">{{ INBOX_KIND_LABEL[pending(r.id)!.kind] }}</span>
            <span class="sub meta">{{ r.workflow }} · {{ fmtWhen(r.startedAt) }} · {{ fmtCost(r.totalCostUsd) }}<template v-if="r.retries"> · {{ r.retries }}× yeniden</template></span>
            <span v-if="r.detail" class="sub detail">{{ r.detail }}</span>
          </button>
        </li>
      </ol>

      <p class="hint">Kısayol: <kbd>I</kbd> bu paneli açar/kapatır. <b>Soru</b> = senden cevap bekleniyor (onayla / revize et). <b>Karar</b> = çalışma durdu (yeniden dene / iptal et).</p>
    </section>
  </div>
</template>

<style scoped>
.wrap { position: absolute; inset: 0; background: rgba(10, 12, 18, 0.55); display: flex; justify-content: flex-end; }
.panel {
  background: #ede9dc; color: #23283a; border-left: 6px solid #6b4a2b;
  width: min(640px, 100%); height: 100%; overflow: auto; padding: 16px 18px;
  box-shadow: -20px 0 60px rgba(0,0,0,0.5); display: flex; flex-direction: column; gap: 12px;
}
.panel > header { display: flex; align-items: center; gap: 10px; }
h2 { margin: 0; font-size: 16px; letter-spacing: 0.04em; text-transform: uppercase; display: flex; align-items: center; gap: 8px; }
.total { font-size: 12px; background: #23283a; color: #fff; border-radius: 999px; padding: 1px 8px; }
h3 { margin: 0 0 6px; font-size: 13px; text-transform: uppercase; letter-spacing: 0.06em; color: #4a5068; display: flex; align-items: center; gap: 6px; }
h3 b { background: #d23b3b; color: #fff; border-radius: 999px; padding: 0 7px; font-size: 11px; }
.inbox.empty h3 b { background: #7cc46b; color: #1f2430; }
.x { background: none; border: none; font-size: 22px; cursor: pointer; color: #23283a; line-height: 1; margin-left: auto; padding: 0 4px; }
.sub { font-size: 11px; color: #6b7285; line-height: 1.4; }
.err { color: #b3261e; font-size: 12px; margin: 0; }
button { font: inherit; cursor: pointer; border-radius: 4px; padding: 7px 14px; border: 1px solid #c9c3b3; background: #fff; color: #23283a; }
.small { padding: 3px 9px; font-size: 11px; }
.primary { background: #23283a; color: #fff; border-color: #23283a; }

.inbox { background: #fff8e1; border: 1px solid #f3c34a; border-radius: 6px; padding: 10px 12px; }
.inbox.empty { background: #f1f5ea; border-color: #b9d8a8; }
.inbox p { margin: 0; }
.inbox ul { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 6px; }
.item {
  width: 100%; text-align: left; display: grid; grid-template-columns: auto 1fr auto; gap: 2px 10px; align-items: center;
  background: #fff; border-left: 4px solid #d23b3b; padding: 8px 10px;
}
.item:hover { box-shadow: 0 2px 6px rgba(0,0,0,0.15); }
.item strong { font-size: 13px; }
.item .title { grid-column: 1 / span 2; font-size: 12px; color: #4a5068; }
.item .go { font-size: 12px; font-weight: 700; color: #1f5f93; }
.item .detail { grid-column: 1 / span 3; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.kind { font-size: 10px; font-weight: 700; text-transform: uppercase; letter-spacing: 0.04em; padding: 1px 7px; border-radius: 999px; background: #e5e7ee; color: #4a5068; white-space: nowrap; }
.kind.approval, .kind.question { background: #f3c34a; color: #3a2f12; }
.kind.decision { background: #fadada; color: #9c1f1f; }

.filters { display: flex; gap: 4px; flex-wrap: wrap; }
.filters button { font-size: 11px; color: #4a5068; background: rgba(0,0,0,0.05); border: 1px solid transparent; border-radius: 999px; padding: 3px 10px; }
.filters button b { font-weight: 700; margin-left: 3px; }
.filters button:hover { background: rgba(0,0,0,0.09); }
.filters button.on { background: #fff; border-color: #c9c3b3; color: #23283a; }
.filters button.inbox.on { border-color: #d23b3b; }

.list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 6px; }
.row {
  width: 100%; text-align: left; display: grid; grid-template-columns: auto 1fr auto; gap: 2px 10px; align-items: center;
  padding: 8px 10px; border-left: 4px solid #c9c3b3;
}
.row:hover { box-shadow: 0 2px 6px rgba(0,0,0,0.15); }
.row.pending { border-left-color: #d23b3b; }
.row .label { font-size: 13px; }
.row .meta { grid-column: 1 / span 3; }
.row .detail { grid-column: 1 / span 3; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.status { font-size: 10px; font-weight: 700; padding: 2px 8px; border-radius: 999px; background: rgba(0,0,0,0.08); text-transform: uppercase; letter-spacing: 0.04em; white-space: nowrap; }
.status.awaitingApproval { background: #f3c34a; }
.status.running { background: #4fa3e0; color: #fff; }
.status.paused { background: #a889e6; color: #fff; }
.status.completed { background: #7cc46b; }
.status.cancelled { background: #8a90a2; color: #fff; }
.status.failed, .status.policyRejected, .status.interrupted, .status.budgetExceeded { background: #d23b3b; color: #fff; }
.empty-list { margin: 8px 0; }
.hint { margin: auto 0 0; font-size: 11px; color: #6b7285; }
kbd { font-size: 10px; background: rgba(0,0,0,0.06); padding: 1px 4px; border-radius: 3px; }
</style>
