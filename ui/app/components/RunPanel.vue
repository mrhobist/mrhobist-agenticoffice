<script setup lang="ts">
import type { AgentListItem, AttachmentKind, AttachmentRules, LaunchResult, LiveTurn, ProjectCard, RunAttachment, RunDetail, RunRequest, RunStatus, RunSummary, StagedAttachment, Turn, WorkflowListItem } from '~/api/types'
import { useApiClient } from '~/api/client'
import { errorText } from '~/api/errors'
import { COST_TITLE, RUN_CANCELLABLE, RUN_RETRYABLE, RUN_STATUS_LABEL, fmtCost, subjectLabel } from '~/api/labels'

/**
 * Calisma paneli (docs/DOMAIN.md → Plan onayi). Iki gorunum:
 *  - runId yok: "Yeni calisma" formu + son calismalar listesi
 *  - runId var: durum, plan (ozet, mimari, kurallar, gorevler + sira), onay / revize, devir notlari.
 * Canli akis SSE degil, 2 s'de bir GET /runs/{id} (docs/API.md → varsayimla ilerlenir).
 */
const props = defineProps<{
  runId: string | null
  /** GET /agents listesi (kabuk yukler); ajan anahtarini ada cevirmek icin. null = alinamadi. */
  agents: AgentListItem[] | null
  /** Yeni is formu icin proje anahtari: is yalniz bir projenin icinde baslar (docs/DOMAIN.md → Projeler). */
  project: string | null
  /** Canli arac akisi (SSE agent.tool → kabuk): suren turda ajanin yaptigi son cagrilar. */
  liveTools?: Array<{ ts: number; agent: string; tool: string; target: string | null; task: string | null }>
  /** Panodan acildiysa kartin gorevi: canli akis bu goreve odaklanir. */
  focusTask?: string | null
}>()
const emit = defineEmits<{ close: []; open: [id: string]; jobs: []; newRun: [project: string] }>()

const api = useApiClient()

// ------------------------------------------------------------------ yeni calisma

const brief = ref('')
const label = ref('')
const maxCost = ref('')
const workflowKey = ref('default')
const workflows = ref<WorkflowListItem[]>([])
const starting = ref(false)
const startError = ref<string | null>(null)
const recent = ref<RunSummary[]>([])

const projectCard = ref<ProjectCard | null>(null)
async function loadForm() {
  try { workflows.value = await api.get<WorkflowListItem[]>('/api/v1/workflows') } catch { workflows.value = [] }
  if (!attachRules.value) {
    try { attachRules.value = await api.get<AttachmentRules>('/api/v1/attachments/rules') } catch { attachRules.value = null }
  }
  if (props.project) {
    try {
      projectCard.value = await api.get<ProjectCard>(`/api/v1/projects/${encodeURIComponent(props.project)}`)
      workflowKey.value = projectCard.value.workflow
      recent.value = await api.get<RunSummary[]>(`/api/v1/projects/${encodeURIComponent(props.project)}/runs?limit=8`)
    } catch { recent.value = [] }
  } else {
    projectCard.value = null
    recent.value = []
  }
}

// ------------------------------------------------------------------ ekler (docs/DOMAIN.md → Ekler)
// Dosya secilince hemen yuklenir (gecici alan, kimlik doner); is gonderilirken kimlikler govdeye girer. Icerik isteme
// gomulmez: ajan yolu gorur, Read ile okur (PDF sayfa sayfa, resim goruntu olarak).

const attachRules = ref<AttachmentRules | null>(null)
const staged = ref<StagedAttachment[]>([])
const uploading = ref(0)
const uploadError = ref<string | null>(null)
const dragOver = ref(false)
const fileInput = useTemplateRef<HTMLInputElement>('fileInput')
const acceptList = computed(() => attachRules.value?.extensions.join(',') ?? '')

const KIND_LABEL: Record<AttachmentKind, string> = { document: 'PDF', image: 'Resim', text: 'Metin', word: 'Word' }

/** Sozlesmede 64 bit sayilar `number | string` uretilir (OpenAPI); ekranda sayiya cevrilir. */
function fmtSize(value: number | string): string {
  const bytes = Number(value)
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`
}

async function addFiles(files: File[]) {
  uploadError.value = null
  const rules = attachRules.value
  for (const f of files) {
    if (rules && staged.value.length + uploading.value >= Number(rules.maxPerRun)) {
      uploadError.value = `Bir işe en fazla ${rules.maxPerRun} dosya eklenebilir.`
      break
    }
    if (rules && f.size > Number(rules.maxBytes)) {
      uploadError.value = `${f.name}: ${fmtSize(f.size)}; üst sınır ${fmtSize(rules.maxBytes)}.`
      continue
    }
    uploading.value++
    try {
      const form = new FormData()
      form.append('files', f, f.name)
      staged.value.push(...await api.upload<StagedAttachment[]>('/api/v1/attachments', form))
    } catch (e) {
      uploadError.value = `${f.name}: ${errorText(e)}`
    } finally {
      uploading.value--
    }
  }
}

function onPick(e: Event) {
  const input = e.target as HTMLInputElement
  void addFiles(Array.from(input.files ?? []))
  input.value = ''
}

function onDrop(e: DragEvent) {
  dragOver.value = false
  void addFiles(Array.from(e.dataTransfer?.files ?? []))
}

/** Ekran goruntusu brief'e yapistirilabilir: panodaki dosya ek olur, metin yapistirma bozulmaz. */
function onPaste(e: ClipboardEvent) {
  const files = Array.from(e.clipboardData?.files ?? [])
  if (!files.length) return
  e.preventDefault()
  const stamp = new Date().toISOString().slice(11, 19).replaceAll(':', '')
  void addFiles(files.map((f, i) => f.name && f.name !== 'image.png' ? f : new File([f], `ekran-${stamp}${i ? `-${i}` : ''}.png`, { type: f.type })))
}

function removeStaged(id: string) {
  staged.value = staged.value.filter(a => a.id !== id)
}

/** Ek indirme: JWT basligi gerekir, duz baglanti olmaz. PDF/resim yeni sekmede acilir, digerleri iner. */
async function openAttachment(a: RunAttachment, file?: string) {
  if (!run.value) return
  const name = file ?? a.fileName
  try {
    const blob = await api.blob(`/api/v1/runs/${encodeURIComponent(run.value.id)}/attachments/${encodeURIComponent(name)}`)
    const url = URL.createObjectURL(blob)
    const link = document.createElement('a')
    link.href = url
    if (!file && (a.kind === 'document' || a.kind === 'image')) link.target = '_blank'
    else link.download = file ?? a.name
    link.click()
    setTimeout(() => URL.revokeObjectURL(url), 60_000)
  } catch (e) {
    actError.value = errorText(e)
  }
}

async function start() {
  if (starting.value || !brief.value.trim() || uploading.value) return
  starting.value = true
  startError.value = null
  if (!props.project) { startError.value = 'İş bir projenin içinde başlar; önce proje seç.'; return }
  try {
    const budget = Number.parseFloat(maxCost.value.replace(',', '.'))
    const body: RunRequest = {
      project: props.project,
      brief: brief.value.trim(),
      workflow: workflowKey.value || null,
      label: label.value.trim() || null,
      maxCostUsd: Number.isFinite(budget) && budget > 0 ? budget : null,
      attachments: staged.value.length ? staged.value.map(a => a.id) : null,
    }
    const run = await api.post<RunSummary>('/api/v1/runs', body)
    brief.value = ''
    label.value = ''
    staged.value = []
    uploadError.value = null
    emit('open', run.id)
  } catch (e) {
    startError.value = errorText(e)
  } finally {
    starting.value = false
  }
}

// ------------------------------------------------------------------ calisma gorunumu

const run = ref<RunDetail | null>(null)
const loadError = ref<string | null>(null)
const note = ref('')
const acting = ref(false)
const actError = ref<string | null>(null)
let timer: ReturnType<typeof setInterval> | undefined

/** Yalniz bu durumlarda kayit kendiliginden degisir; digerlerinde yoklama durur (approve/revise/answer yeniden baslatir). */
const LIVE: ReadonlySet<RunStatus> = new Set<RunStatus>(['running', 'paused'])
/** Yoklama araligi: calisirken 2 s; limit beklemesi saatler surebilir, 20 s yeter (kabuk zaten 5 s'de overview ceker). */
function pollInterval(status: RunStatus | undefined): number { return status === 'paused' ? 20_000 : 2000 }

async function poll() {
  if (!props.runId) return
  try {
    run.value = await api.get<RunDetail>(`/api/v1/runs/${encodeURIComponent(props.runId)}`)
    loadError.value = null
    if (!LIVE.has(run.value.status)) stopPolling()
    else if (timer && pollInterval(run.value.status) !== currentInterval) startPolling()
    if (showLog.value) void loadTurns()
    if (run.value.status === 'running') void loadLive()
    else live.value = []
  } catch (e) {
    loadError.value = errorText(e)
  }
}

// ------------------------------------------------------------------ canli tur (kullanici istekleri 2026-09-23)
// Ajanin dusuncesi/metni anlik, elindeki baglam ve gercekten calisip calismadigi (son hareket). Ek maliyet yok: runtime
// akista zaten uretilen icerigi bildirir; burasi 2 s'de bir GET /runs/{id}/live okur.

const live = ref<LiveTurn[]>([])
const openContext = ref<string | null>(null)
/** Akis kutulari (tur basina); yeni satir gelince kullanici en alttaysa asagi kayar. */
const streamBox = new Map<string, HTMLElement>()
function liveKey(t: LiveTurn): string { return t.agent + (t.task ?? '') + t.startedAt }
async function loadLive() {
  if (!props.runId) return
  try {
    const next = await api.get<LiveTurn[]>(`/api/v1/runs/${encodeURIComponent(props.runId)}/live`)
    const grew = next.some((t, i) => t.stream.length !== (live.value[i]?.stream.length ?? -1))
    live.value = next
    if (grew) {
      await nextTick()
      for (const el of streamBox.values()) if (el.scrollHeight - el.scrollTop - el.clientHeight < 80) el.scrollTop = el.scrollHeight
    }
  } catch { /* canli gorunum yardimcidir; hata calisma panelini bozmaz */ }
}
/** Odak: panodan gelen gorev once; yoksa hepsi. */
const liveShown = computed(() => {
  const f = props.focusTask
  return f && live.value.some(t => t.task === f) ? [...live.value].sort((a, b) => Number(b.task === f) - Number(a.task === f)) : live.value
})
function ago(ts: string): number { return Math.max(0, Math.floor((nowTick.value - new Date(ts).getTime()) / 1000)) }
function fmtAgo(s: number): string { return s < 60 ? `${s} sn` : `${Math.floor(s / 60)} dk ${s % 60} sn` }
/** Canlilik: yesil (yakin zamanda hareket), sari (esigin yarisini gecti), kirmizi (esige yaklasti — bekci keser). */
function pulse(t: LiveTurn): 'ok' | 'slow' | 'stale' {
  const s = ago(t.lastSeenAt)
  return s < 90 ? 'ok' : s < t.idleLimitS * 0.6 ? 'slow' : 'stale'
}
function fmtTokens(n: number): string { return n >= 1_000_000 ? `${(n / 1_000_000).toFixed(2)}M` : n >= 1000 ? `${Math.round(n / 1000)}K` : String(n) }
/** Kaba token tahmini (karakter / 3,5); kesin sayi tur sonunda turda. */
function estTokens(chars: number): string { return fmtTokens(Math.round(chars / 3.5)) }

// ------------------------------------------------------------------ gunluk (turlar + mesajlar + fazlar)

const showLog = ref(false)
const turns = ref<Turn[] | null>(null)
const turnsError = ref<string | null>(null)

async function loadTurns() {
  if (!props.runId) return
  try {
    turns.value = await api.get<Turn[]>(`/api/v1/runs/${encodeURIComponent(props.runId)}/turns`)
    turnsError.value = null
  } catch (e) {
    turnsError.value = errorText(e)
  }
}

watch(showLog, (v) => { if (v && turns.value === null) void loadTurns() })

type LogEntry =
  | { ts: string; kind: 'turn'; turn: Turn }
  | { ts: string; kind: 'message'; from: string; to: string; subject: string; body: string; task: string | null; stage: string | null; error: boolean }
  | { ts: string; kind: 'phase'; agent: string; task: string; stage: string; status: string; round: number; detail: string | null }

/** Tek zaman cizgisi: LLM turlari, ajanlar arasi mesajlar (devir, not, hata) ve faz gecisleri. */
const log = computed<LogEntry[]>(() => {
  const r = run.value
  if (!r) return []
  const out: LogEntry[] = []
  for (const t of turns.value ?? []) out.push({ ts: t.ts, kind: 'turn', turn: t })
  for (const m of r.messages) out.push({ ts: m.ts, kind: 'message', from: m.from, to: m.to, subject: m.subject, body: m.body, task: m.task, stage: m.stage, error: m.subject === 'error' })
  for (const t of r.tasks) for (const p of t.phases) out.push({ ts: p.ts, kind: 'phase', agent: p.agent, task: p.task, stage: p.stage, status: p.status, round: p.round, detail: p.detail })
  return out.sort((a, b) => a.ts.localeCompare(b.ts))
})

let currentInterval = 2000
function startPolling() {
  stopPolling()
  currentInterval = pollInterval(run.value?.status)
  timer = setInterval(() => { void poll() }, currentInterval)
  void poll()
}
function stopPolling() { clearInterval(timer); timer = undefined }

watch(() => props.runId, (id) => {
  run.value = null
  loadError.value = null
  actError.value = null
  note.value = ''
  if (id) startPolling()
  else { stopPolling(); void loadForm() }
}, { immediate: true })

onBeforeUnmount(stopPolling)

async function approve() {
  if (!props.runId || acting.value) return
  acting.value = true
  actError.value = null
  try {
    await api.post(`/api/v1/runs/${encodeURIComponent(props.runId)}/approve`)
    startPolling()
  } catch (e) {
    actError.value = errorText(e)
  } finally {
    acting.value = false
  }
}

async function revise() {
  if (!props.runId || acting.value || !note.value.trim()) return
  acting.value = true
  actError.value = null
  try {
    await api.post(`/api/v1/runs/${encodeURIComponent(props.runId)}/revise`, { note: note.value.trim() })
    note.value = ''
    startPolling()
  } catch (e) {
    actError.value = errorText(e)
  } finally {
    acting.value = false
  }
}

/** Yeniden dene: kaldigi adimdan surer (POST /runs/{id}/retry, 202). Onay beklerken iptal edilmisse yalniz onaya doner. */
async function retry() {
  if (!props.runId || acting.value) return
  acting.value = true
  actError.value = null
  try {
    await api.post(`/api/v1/runs/${encodeURIComponent(props.runId)}/retry`)
    startPolling()
  } catch (e) {
    actError.value = errorText(e)
  } finally {
    acting.value = false
  }
}

/** Iptal: hemen yazilir; suren LLM cagrisinin sonucu yazilmaz. Geri donus "Yeniden dene". */
async function cancel() {
  if (!props.runId || acting.value) return
  const q = canRetry.value
    ? 'Çalışma kapatılsın mı? Gelen kutusundan düşer; gerekirse "Yeniden dene" ile kaldığı yerden sürdürebilirsin.'
    : 'Çalışma iptal edilsin mi? Gerekirse "Yeniden dene" ile kaldığı yerden sürdürebilirsin.'
  if (!window.confirm(q)) return
  acting.value = true
  actError.value = null
  try {
    await api.post(`/api/v1/runs/${encodeURIComponent(props.runId)}/cancel`)
    await poll()
  } catch (e) {
    actError.value = errorText(e)
  } finally {
    acting.value = false
  }
}

/** Takilma cevabi (docs/DOMAIN.md → Takilma): secenek + (gerekirse) not → POST /runs/{id}/answer, 202. */
const answerChoice = ref<string | null>(null)
async function answer(choice: string) {
  if (!props.runId || acting.value || !run.value?.question) return
  const opt = run.value.question.options.find(o => o.id === choice)
  if (!opt) return
  if (opt.needsNote && !note.value.trim()) { actError.value = `"${opt.label}" için not gerekli.`; return }
  if (choice === 'cancel' && !window.confirm('Çalışma kapatılsın mı?')) return
  acting.value = true
  actError.value = null
  answerChoice.value = choice
  try {
    await api.post(`/api/v1/runs/${encodeURIComponent(props.runId)}/answer`, { choice, note: note.value.trim() || null })
    note.value = ''
    startPolling()
  } catch (e) {
    actError.value = errorText(e)
  } finally {
    acting.value = false
    answerChoice.value = null
  }
}

// ------------------------------------------------------------------ turetilenler

const awaiting = computed(() => run.value?.status === 'awaitingApproval')
const asking = computed(() => run.value?.status === 'awaitingInput' && !!run.value.question)
const busy = computed(() => run.value?.status === 'running')

// ------------------------------------------------------------------ gecen sure (kullanici istegi 2026-09-20: uzun adimda bos bekleme olmasin)
const nowTick = ref(Date.now())
let tickTimer: ReturnType<typeof setInterval> | undefined
onMounted(() => { tickTimer = setInterval(() => { nowTick.value = Date.now() }, 1000) })
onBeforeUnmount(() => clearInterval(tickTimer))
/** Suren adimin baslangici: son Started fazi; faz yoksa (analiz) son tekrar notu ya da calismanin baslangici. */
const stepStartedAt = computed<string | null>(() => {
  const r = run.value
  if (!r || r.status !== 'running') return null
  let last: string | null = null
  for (const t of r.tasks) for (const p of t.phases) if (p.status === 'started' && (!last || p.ts > last)) last = p.ts
  if (last) return last
  const retry = [...r.messages].reverse().find(m => m.subject === 'retry' || m.subject === 'plan-revision')
  return retry?.ts ?? r.startedAt
})
const elapsed = computed(() => {
  if (!stepStartedAt.value) return ''
  const s = Math.max(0, Math.floor((nowTick.value - new Date(stepStartedAt.value).getTime()) / 1000))
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`
})
/** Adim icin beklenen sure ipucu (ilk gercek kosudan): analiz ~1 dk, gelistirme 2–3 dk, inceleme ~1 dk. */
const stepHint = computed(() => {
  const r = run.value
  if (!r || r.status !== 'running') return ''
  if (!r.spec) return 'analist dizine bakıyor ve planı yazıyor; genelde 1 dk'
  const d = r.detail ?? ''
  if (d.startsWith('Geliştirme')) return 'developer dosyaları yazıyor, build/test koşuyor; genelde 2–3 dk'
  if (d.startsWith('Test') || d.startsWith('Karar')) return 'inceleme: kod okunuyor, testler koşuyor; genelde 1 dk'
  return 'devir notu yazılıyor'
})

// ------------------------------------------------------------------ bitmis calisma: degisen dosyalar, projeyi baslat, devam brief'i
const projectOfRun = ref<ProjectCard | null>(null)
watch(() => run.value?.project, async (p) => {
  projectOfRun.value = null
  if (p) { try { projectOfRun.value = await api.get<ProjectCard>(`/api/v1/projects/${encodeURIComponent(p)}`) } catch { /* dugme pasif kalir */ } }
})
/** Developer raporlarindan "Dosyalar: a, b" satirlari (implement-report). */
const changedFiles = computed(() => {
  const out = new Set<string>()
  for (const m of run.value?.messages ?? []) {
    if (m.subject !== 'implement-report') continue
    const line = m.body.split('\n').find(l => l.startsWith('Dosyalar:'))
    if (!line) continue
    for (const f of line.slice('Dosyalar:'.length).split(',')) { const t = f.trim(); if (t && t !== '—') out.add(t) }
  }
  return [...out]
})
const launching = ref(false)
const launchNote = ref<{ kind: 'ok' | 'err'; text: string } | null>(null)
async function launchProject() {
  if (!run.value || launching.value) return
  launching.value = true
  launchNote.value = null
  try {
    const r = await api.post<LaunchResult>(`/api/v1/projects/${encodeURIComponent(run.value.project)}/launch`)
    launchNote.value = { kind: 'ok', text: `Yeni pencerede açıldı (${r.launcher}, pid ${r.processId}).` }
  } catch (e) {
    launchNote.value = { kind: 'err', text: errorText(e) }
  } finally {
    launching.value = false
    setTimeout(() => { launchNote.value = null }, 6000)
  }
}
const doneStats = computed(() => {
  const r = run.value
  if (!r) return null
  const phases = r.tasks.flatMap(t => t.phases)
  return { tasks: r.spec?.tasks.length ?? 0, rejects: phases.filter(p => p.status === 'rejected').length, minutes: r.finishedAt ? Math.max(1, Math.round((new Date(r.finishedAt).getTime() - new Date(r.startedAt).getTime()) / 60_000)) : 0 }
})
const canRetry = computed(() => !!run.value && (RUN_RETRYABLE.has(run.value.status) || (run.value.status === 'paused' && !!run.value.resumeAt)))
const canCancel = computed(() => !!run.value && RUN_CANCELLABLE.has(run.value.status))

/** Gorevler yurutme sirasinda; her birinin son fazi (varsa). */
const orderedTasks = computed(() => {
  const r = run.value
  if (!r?.spec) return []
  const byId = new Map(r.spec.tasks.map(t => [t.id, t]))
  const ids = r.order.length ? r.order : r.spec.tasks.map(t => t.id)
  return ids.flatMap((id, i) => {
    const t = byId.get(id)
    if (!t) return []
    const phases = r.tasks.find(x => x.id === id)?.phases ?? []
    const last = phases[phases.length - 1]
    return [{ ...t, index: i + 1, last }]
  })
})

const handoffs = computed(() => run.value?.messages.filter(m => m.kind === 'handoff') ?? [])
const revisions = computed(() => run.value?.messages.filter(m => m.subject === 'plan-revision') ?? [])

function agentName(key: string): string {
  return props.agents?.find(a => a.key === key)?.name ?? key
}

function stageTitle(id: string): string {
  return run.value?.workflowDef?.stages.find(s => s.id === id)?.title ?? id
}

function fmtTime(s: string): string { return new Date(s).toLocaleTimeString('tr-TR', { hour: '2-digit', minute: '2-digit' }) }
function fmtClock(s: string): string { return new Date(s).toLocaleTimeString('tr-TR', { hour: '2-digit', minute: '2-digit', second: '2-digit' }) }
const TOOL_SHORT: Record<string, string> = { Write: 'yaz', Edit: 'düzenle', MultiEdit: 'düzenle', Read: 'oku', Glob: 'ara', Grep: 'ara', Bash: 'çalıştır' }
const PHASE_LABEL: Record<string, string> = { started: 'başladı', done: 'bitti', rejected: 'reddedildi', failed: 'başarısız', skipped: 'atlandı' }
const errorCount = computed(() => run.value?.messages.filter(m => m.subject === 'error').length ?? 0)
</script>

<template>
  <div class="wrap" @click.self="emit('close')">
    <section class="panel" role="dialog" aria-labelledby="run-title">
      <header>
        <h2 id="run-title">{{ runId ? `Çalışma · ${run?.label ?? '…'}` : `Yeni iş · ${projectCard?.title ?? project ?? 'proje seç'}` }}</h2>
        <code v-if="runId" class="key">{{ runId }}</code>
        <button v-if="runId" type="button" class="ghost small" @click="emit('open', '')">← yeni</button>
        <button class="x" type="button" aria-label="Kapat" @click="emit('close')">×</button>
      </header>

      <!-- ---------------------------------------------------------------- yeni calisma -->
      <div v-if="!runId && !project" class="msg empty">
        <strong>İş bir projenin içinde başlar.</strong>
        <span>Soldaki raydan bir proje kartı aç ve "Yeni iş" de; ya da "+" ile proje oluştur.</span>
      </div>
      <form v-else-if="!runId" class="form" @submit.prevent="start">
        <div class="field">
          <label class="lbl" for="run-brief">Brief</label>
          <textarea id="run-brief" v-model="brief" class="brief" placeholder="Ne yapılacak? Analist bunu plana çevirir; plan senin onayına gelir." required @paste="onPaste" />
        </div>
        <div class="field">
          <span class="lbl">Ekler <span class="sub">(PDF, resim, Word, metin · en fazla {{ attachRules?.maxPerRun ?? 10 }} dosya, dosya başına {{ fmtSize(attachRules?.maxBytes ?? 20 * 1024 * 1024) }})</span></span>
          <div
            class="drop" :class="{ over: dragOver }"
            @dragenter.prevent="dragOver = true" @dragover.prevent="dragOver = true" @dragleave="dragOver = false" @drop.prevent="onDrop"
          >
            <button type="button" class="small" @click="fileInput?.click()">Dosya seç</button>
            <span class="sub">ya da buraya sürükle · ekran görüntüsünü brief'e yapıştırabilirsin</span>
            <input ref="fileInput" type="file" multiple :accept="acceptList" hidden @change="onPick">
          </div>
          <ul v-if="staged.length || uploading" class="att-list">
            <li v-for="a in staged" :key="a.id">
              <span class="att-kind" :class="a.kind">{{ KIND_LABEL[a.kind] }}</span>
              <span class="att-name" :title="a.name">{{ a.name }}</span>
              <span class="sub">{{ fmtSize(a.size) }}</span>
              <button type="button" class="att-x" :aria-label="`${a.name} ekini kaldır`" @click="removeStaged(a.id)">×</button>
            </li>
            <li v-if="uploading" class="sub">{{ uploading }} dosya yükleniyor…</li>
          </ul>
          <span v-if="uploadError" class="err" role="alert">{{ uploadError }}</span>
          <span v-if="staged.length" class="sub">İçerik isteme gömülmez: ajan dosyaların yolunu görür ve gerektiğinde okur (PDF ve resmi Claude okuyabilir; Word'ün metni çıkarılır).</span>
        </div>
        <div class="row">
          <div class="field">
            <label class="lbl" for="run-workflow">İş akışı</label>
            <select id="run-workflow" v-model="workflowKey">
              <option v-for="w in workflows" :key="w.key" :value="w.key">{{ w.title }} · {{ w.roles.join(' → ') }}</option>
              <option v-if="!workflows.length" value="default">Varsayılan</option>
            </select>
          </div>
          <div class="field">
            <label class="lbl" for="run-label">Etiket <span class="sub">(isteğe bağlı)</span></label>
            <input id="run-label" v-model="label" type="text" placeholder="brief'in ilk satırı">
          </div>
        </div>
        <div class="row">
          <div class="field">
            <label class="lbl" for="run-budget">Bütçe üst sınırı <span class="sub">($, isteğe bağlı)</span></label>
            <input id="run-budget" v-model="maxCost" type="text" inputmode="decimal" placeholder="örn. 2.00 — aşılınca çalışma durur">
          </div>
        </div>
        <p class="sub">Proje: <strong>{{ projectCard?.title ?? project }}</strong><template v-if="projectCard"> · hedef <code>{{ projectCard.targetDir }}</code></template>. Hassasiyet: <strong>anthropic</strong>. Plan onaylanmadan hiçbir ajan iş almaz.</p>
        <div class="actions">
          <button class="primary" type="submit" :disabled="starting || !brief.trim() || uploading > 0">{{ starting ? 'Başlatılıyor…' : uploading ? 'Ekler yükleniyor…' : 'Analize gönder' }}</button>
          <span v-if="startError" class="err" role="alert">{{ startError }}</span>
        </div>

        <div v-if="recent.length" class="recent">
          <span class="lbl">Son çalışmalar <button type="button" class="link" @click="emit('jobs')">tüm işler →</button></span>
          <ul>
            <li v-for="r in recent" :key="r.id">
              <button type="button" class="link" @click="emit('open', r.id)">{{ r.label }}</button>
              <span class="status" :class="r.status">{{ RUN_STATUS_LABEL[r.status] }}</span>
              <span class="sub">{{ fmtTime(r.startedAt) }} · {{ fmtCost(r.totalCostUsd, 3) }}</span>
            </li>
          </ul>
        </div>
      </form>

      <!-- ---------------------------------------------------------------- calisma -->
      <template v-else>
        <p v-if="loadError" class="err" role="alert">{{ loadError }}</p>
        <p v-else-if="!run" class="msg">Yükleniyor…</p>

        <div v-else class="run">
          <div class="statusline">
            <span class="status" :class="run.status">{{ RUN_STATUS_LABEL[run.status] }}</span>
            <span v-if="busy" class="spin" aria-hidden="true" />
            <span class="sub">{{ run.detail }}</span>
            <span v-if="busy && elapsed" class="elapsed" :title="stepHint"><Ico name="clock" :size="12" /> {{ elapsed }}</span>
          </div>
          <!-- Canli arac akisi: developer/testci calisirken hangi dosya, hangi komut (SSE agent.tool). Tur bitince tam liste gunlukte. -->
          <div v-if="busy && props.liveTools?.length" class="livetools" aria-live="polite">
            <span class="lbl">Şu an</span>
            <span v-for="t in props.liveTools.slice(-6)" :key="t.ts" class="tool" :class="t.tool.toLowerCase()" :title="`${agentName(t.agent)} · ${t.tool} · ${t.target ?? ''}`"><b>{{ TOOL_SHORT[t.tool] ?? t.tool }}</b> {{ t.target ?? '' }}</span>
            <span class="sub">{{ props.liveTools.length }} çağrı</span>
          </div>
          <!-- Canli tur: dusunce/metin akisi, canlilik, anlik kullanim ve ajanin baglami (GET /runs/{id}/live). -->
          <section v-for="t in liveShown" :key="liveKey(t)" class="live" :class="{ focus: t.task && t.task === props.focusTask }" aria-live="polite">
            <div class="live-head">
              <span class="pulse" :class="pulse(t)" :title="`Hareketsizlik eşiği ${Math.round(t.idleLimitS / 60)} dk: aşılırsa tur kesilir, Yeniden dene kaldığı yerden sürdürür.`" />
              <strong>{{ agentName(t.agent) }}</strong>
              <span v-if="t.task"><code>{{ t.task }}</code> · {{ stageTitle(t.stage ?? '') }}</span>
              <span class="sub">son hareket <b>{{ fmtAgo(ago(t.lastSeenAt)) }}</b> önce · tur {{ fmtAgo(ago(t.startedAt)) }} · {{ t.toolCount }} araç</span>
              <span class="sub right" title="Şu ana kadar: toplam girdi (önbellekten okunan) / çıktı">{{ fmtTokens(t.usage.inputTokens) }} ({{ fmtTokens(t.usage.cacheReadTokens) }} önb.) / {{ fmtTokens(t.usage.outputTokens) }} tk</span>
            </div>
            <p v-if="pulse(t) === 'stale'" class="warn">Ajan {{ fmtAgo(ago(t.lastSeenAt)) }} boyunca hareket etmedi. Uzun bir build/test sürüyor olabilir; {{ Math.round(t.idleLimitS / 60) }} dk dolunca tur kesilir ve "Yeniden dene" kaldığı yerden devam eder.</p>
            <div :ref="(el) => { if (el) streamBox.set(liveKey(t), el as HTMLElement); else streamBox.delete(liveKey(t)) }" class="stream">
              <p v-if="!t.stream.length" class="sub">Henüz akış yok — ajan bağlamı okuyor.</p>
              <div v-for="(e, i) in t.stream.slice(-80)" :key="i" class="entry" :class="e.kind">
                <span class="ts">{{ fmtClock(e.ts) }}</span>
                <template v-if="e.kind === 'tool'"><b class="tool" :class="(e.tool ?? '').toLowerCase()">{{ TOOL_SHORT[e.tool ?? ''] ?? e.tool }}</b> <code>{{ e.target }}</code></template>
                <span v-else class="txt">{{ e.kind === 'thinking' ? '💭 ' : '' }}{{ e.text }}</span>
              </div>
            </div>
            <details class="ctx" :open="openContext === t.agent + t.startedAt" @toggle="(ev) => { if ((ev.target as HTMLDetailsElement).open) openContext = t.agent + t.startedAt }">
              <summary>Ajanın bağlamı · {{ t.context.length }} parça · ~{{ estTokens(t.context.reduce((s, c) => s + c.chars, 0)) }} tk</summary>
              <details v-for="(c, i) in t.context" :key="i" class="part">
                <summary><span class="role" :class="c.role">{{ c.role }}</span> {{ c.name }} <span class="sub">{{ c.chars.toLocaleString('tr-TR') }} kr · ~{{ estTokens(c.chars) }} tk</span></summary>
                <pre>{{ c.text }}</pre>
              </details>
              <p class="sub">Claude Code'un kendi kılavuzu ve araç tanımları bu listeye dahil değil; ajan turda okuduğu dosyaları da bağlamına ekler (kullanım satırında görünür).</p>
            </details>
          </section>
          <!-- Akis/maliyet + eylemler her durumda gorunur: once canli araç şeridinin icinde kaliyordu, dusmus iste
               "Yeniden dene" hic cikmiyordu (2026-09-23). -->
          <div class="runactions">
            <span class="sub right">{{ run.workflow }} · <span :title="COST_TITLE">{{ fmtCost(run.totalCostUsd, 4) }}</span><template v-if="run.maxCostUsd"> / {{ fmtCost(run.maxCostUsd) }}</template><template v-if="run.retries"> · {{ run.retries }}× yeniden</template></span>
            <button v-if="canRetry" type="button" class="small" :disabled="acting" @click="retry">Yeniden dene</button>
            <button v-if="canCancel" type="button" class="small danger" :disabled="acting" @click="cancel">{{ canRetry ? 'Kapat (iptal)' : 'İptal et' }}</button>
          </div>
          <p v-if="actError && !awaiting && !asking" class="err" role="alert">{{ actError }}</p>

          <!-- Takilma: ajan ya da akis kullanicidan secim bekliyor. Secenekler sunucudan gelir; kimlikleri sabittir. -->
          <section v-if="asking && run.question" class="ask" aria-live="polite">
            <h3><span class="qmark" aria-hidden="true">?</span> {{ agentName(run.question.agent) }} soruyor<template v-if="run.question.task"> · <code>{{ run.question.task }}</code> · {{ stageTitle(run.question.stage ?? '') }}</template></h3>
            <p class="qtext">{{ run.question.text }}</p>
            <details v-if="run.question.context"><summary>Bağlam</summary><pre>{{ run.question.context }}</pre></details>
            <div class="field">
              <label class="lbl" for="run-answer-note">Not <span class="sub">(seçeneğe göre isteğe bağlı ya da zorunlu; ajana iletilir)</span></label>
              <textarea id="run-answer-note" v-model="note" class="note" placeholder="Örn. Türkçe karakter için küçük i kullan; testleri pytest ile koş." />
            </div>
            <div class="options">
              <button v-for="o in run.question.options" :key="o.id" type="button" :class="{ primary: o.id === 'retry', danger: o.id === 'cancel' }" :disabled="acting" :title="o.detail" @click="answer(o.id)">
                {{ acting && answerChoice === o.id ? '…' : o.label }}<span v-if="o.needsNote" class="req" aria-label="not gerekli">*</span>
              </button>
            </div>
            <p class="sub">{{ run.question.options.map(o => `${o.label}: ${o.detail}`).join(' · ') }}</p>
            <p v-if="actError" class="err" role="alert">{{ actError }}</p>
          </section>

          <!-- Limit beklemesi: kendisi surer; kullanici isterse hemen dener. -->
          <p v-if="run.status === 'paused' && run.resumeAt" class="limit"><Ico name="clock" :size="13" /> Limit doldu; <strong>{{ new Date(run.resumeAt).toLocaleTimeString('tr-TR', { hour: '2-digit', minute: '2-digit' }) }}</strong>'de kendisi sürer. Eşik Ayarlar'da; "Yeniden dene" hemen dener.</p>

          <details class="brief-box">
            <summary>Brief</summary>
            <pre>{{ run.brief }}</pre>
          </details>

          <details v-if="run.attachments?.length" class="brief-box" open>
            <summary>Ekler ({{ run.attachments.length }})</summary>
            <ul class="att-list">
              <li v-for="a in run.attachments" :key="a.id">
                <span class="att-kind" :class="a.kind">{{ KIND_LABEL[a.kind] }}</span>
                <button type="button" class="link att-name" :title="`${a.name} — aç`" @click="openAttachment(a)">{{ a.name }}</button>
                <span class="sub">{{ fmtSize(a.size) }}</span>
                <button v-if="a.textFile" type="button" class="link small-link" title="Word'den çıkarılan metin (ajan bunu okur)" @click="openAttachment(a, a.textFile)">metni</button>
              </li>
            </ul>
          </details>

          <!-- Hata varsa en uste: "takildi" tek basina bilgi degildir. -->
          <section v-if="errorCount" class="errors">
            <h3>Hata</h3>
            <article v-for="(m, i) in run.messages.filter(x => x.subject === 'error')" :key="i">
              <div class="sub">{{ fmtClock(m.ts) }} · {{ agentName(m.from) }} · {{ stageTitle(m.stage ?? '') }}<template v-if="m.task"> · <code>{{ m.task }}</code></template></div>
              <pre class="errtext">{{ m.body }}</pre>
            </article>
          </section>

          <template v-if="run.spec">
            <section class="plan">
              <h3>Plan <span class="sub">({{ run.spec.tasks.length }} görev)</span></h3>
              <p class="summary">{{ run.spec.summary }}</p>
              <details>
                <summary>Mimari</summary>
                <pre>{{ run.spec.architecture }}</pre>
              </details>
              <details v-if="run.spec.rules.length">
                <summary>Kurallar ({{ run.spec.rules.length }})</summary>
                <ul class="rules"><li v-for="(r, i) in run.spec.rules" :key="i">{{ r }}</li></ul>
              </details>

              <ol class="tasks">
                <li v-for="t in orderedTasks" :key="t.id">
                  <div class="task-head">
                    <code class="tid">{{ t.id }}</code>
                    <strong>{{ t.title }}</strong>
                    <span v-if="t.last" class="phase" :class="t.last.status">{{ stageTitle(t.last.stage) }} · {{ agentName(t.last.agent) }}</span>
                    <span v-else class="phase queued">sırada</span>
                  </div>
                  <p>{{ t.description }}</p>
                  <dl>
                    <div v-if="t.files.length"><dt>Dosyalar</dt><dd><code v-for="f in t.files" :key="f">{{ f }}</code></dd></div>
                    <div v-if="t.acceptance.length"><dt>Kabul</dt><dd><ul><li v-for="(a, i) in t.acceptance" :key="i">{{ a }}</li></ul></dd></div>
                    <div v-if="t.dependsOn.length"><dt>Bağlı</dt><dd><code v-for="d in t.dependsOn" :key="d">{{ d }}</code></dd></div>
                  </dl>
                </li>
              </ol>
            </section>

            <section v-if="awaiting" class="approve" aria-live="polite">
              <h3><span class="qmark" aria-hidden="true">?</span> Soru: bu planı onaylıyor musun?</h3>
              <p class="sub">Senden cevap bekleniyor; onaysız hiçbir ajan iş almaz. Onaylanınca görevler panoya açılır ve organizatör ilk işi dağıtır. Değişiklik istiyorsan not yaz; analist planı yeniden üretir.</p>
              <div class="actions">
                <button class="primary" type="button" :disabled="acting" @click="approve">{{ acting ? '…' : 'Planı onayla' }}</button>
              </div>
              <div class="field">
                <label class="lbl" for="run-note">Revize notu</label>
                <textarea id="run-note" v-model="note" class="note" placeholder="Örn. t2'yi ikiye böl; testler pytest ile olsun." />
              </div>
              <div class="actions">
                <button type="button" :disabled="acting || !note.trim()" @click="revise">Revize et</button>
                <span v-if="actError" class="err" role="alert">{{ actError }}</span>
              </div>
            </section>
          </template>
          <p v-else-if="busy" class="msg">Analist planı hazırlıyor… <span class="sub">{{ stepHint }}</span></p>

          <!-- Bitti: sonuc ozeti, degisen dosyalar, projeyi baslat, ayni projede devam (kullanici istegi 2026-09-20). -->
          <section v-if="run.status === 'completed'" class="done-box">
            <h3><span aria-hidden="true">✓</span> Tamamlandı<span v-if="doneStats" class="sub"> · {{ doneStats.tasks }} görev · {{ doneStats.rejects }} red · {{ doneStats.minutes }} dk · {{ fmtCost(run.totalCostUsd) }}</span></h3>
            <div v-if="changedFiles.length" class="files">
              <span class="lbl">Değişen dosyalar</span>
              <code v-for="f in changedFiles" :key="f">{{ f }}</code>
            </div>
            <div class="actions">
              <button type="button" class="launch" :disabled="launching || !projectOfRun?.launchable" :title="projectOfRun?.launchable ? 'Proje kökündeki run.cmd yeni pencerede çalışır' : 'run.cmd yok: developer uygulamayı çalıştırılabilir yapınca açılır'" @click="launchProject"><span aria-hidden="true">▶</span> {{ launching ? 'Açılıyor…' : 'Projeyi başlat' }}</button>
              <button type="button" @click="emit('newRun', run.project)">Devam brief'i (aynı proje)</button>
              <span class="sub">Kod <code>{{ projectOfRun?.targetDir ?? run.project }}</code> altında.</span>
            </div>
            <p v-if="launchNote" class="launch-note" :class="launchNote.kind" role="status">{{ launchNote.text }}</p>
          </section>

          <section v-if="revisions.length" class="notes">
            <h3>Revize notları</h3>
            <ul><li v-for="(m, i) in revisions" :key="i"><span class="sub">{{ fmtTime(m.ts) }}</span> {{ m.body }}</li></ul>
          </section>

          <section v-if="handoffs.length" class="notes">
            <h3>Devir notları</h3>
            <article v-for="(m, i) in handoffs" :key="i">
              <div class="sub">{{ fmtTime(m.ts) }} · {{ agentName(m.from) }} → {{ agentName(m.to) }} · <code>{{ m.task }}</code> · {{ stageTitle(m.stage ?? '') }}</div>
              <pre>{{ m.body }}</pre>
            </article>
          </section>

          <!-- Gunluk: ajanlarin yaptigi her sey, zaman sirasiyla. Prompt ve cikti tam metin, acilir. -->
          <section class="log">
            <div class="log-head">
              <h3>Günlük</h3>
              <button type="button" class="small" @click="showLog = !showLog">{{ showLog ? 'Gizle' : 'Göster' }}</button>
              <button v-if="showLog" type="button" class="small" @click="loadTurns">Yenile</button>
              <span class="sub">LLM turları (tam prompt ve çıktı), devir ve hata notları, faz geçişleri. Kaynak: bu çalışmanın tur, mesaj ve faz kayıtları.</span>
            </div>
            <template v-if="showLog">
              <p v-if="turnsError" class="err">{{ turnsError }}</p>
              <p v-else-if="turns === null" class="sub">Yükleniyor…</p>
              <p v-else-if="!log.length" class="sub">Henüz kayıt yok.</p>
              <ol v-else class="entries">
                <li v-for="(e, i) in log" :key="i" :class="e.kind">
                  <template v-if="e.kind === 'turn'">
                    <div class="entry-head">
                      <span class="when">{{ fmtClock(e.ts) }}</span>
                      <span class="tag turn">LLM turu</span>
                      <strong>{{ agentName(e.turn.agent) }}</strong>
                      <span class="sub">{{ e.turn.provider }} · <code>{{ e.turn.model }}</code> · {{ e.turn.durationS.toFixed(1) }} s · {{ e.turn.inputTokens ?? '?' }}→{{ e.turn.outputTokens ?? '?' }} tk · {{ fmtCost(e.turn.costUsd ?? 0, 4) }}<template v-if="e.turn.turns && e.turn.turns > 1"> · {{ e.turn.turns }} iç tur</template><template v-if="e.turn.stage"> · {{ stageTitle(e.turn.stage) }}</template><template v-if="e.turn.task"> · <code>{{ e.turn.task }}</code></template></span>
                    </div>
                    <details v-if="e.turn.toolUses?.length"><summary>Araçlar ({{ e.turn.toolUses.length }})</summary><ul class="tools"><li v-for="(t, k) in e.turn.toolUses" :key="k"><b>{{ t.tool }}</b> <code>{{ t.target }}</code></li></ul></details>
                    <details><summary>Gönderilen (son mesaj, {{ e.turn.promptChars }} kr)</summary><pre>{{ e.turn.prompt }}</pre></details>
                    <details open><summary>Çıktı ({{ e.turn.outputChars }} kr)</summary><pre>{{ e.turn.output }}</pre></details>
                  </template>
                  <template v-else-if="e.kind === 'message'">
                    <div class="entry-head">
                      <span class="when">{{ fmtClock(e.ts) }}</span>
                      <span class="tag" :class="e.error ? 'error' : e.subject === 'review-feedback' ? 'reject' : 'msg'">{{ subjectLabel(e.subject) }}</span>
                      <strong>{{ agentName(e.from) }} → {{ agentName(e.to) }}</strong>
                      <span v-if="e.task" class="sub"><code>{{ e.task }}</code></span>
                    </div>
                    <pre :class="{ errtext: e.error }">{{ e.body }}</pre>
                  </template>
                  <template v-else>
                    <div class="entry-head">
                      <span class="when">{{ fmtClock(e.ts) }}</span>
                      <span class="tag phase">faz</span>
                      <strong>{{ agentName(e.agent) }}</strong>
                      <span class="sub"><code>{{ e.task }}</code> · {{ stageTitle(e.stage) }} · {{ PHASE_LABEL[e.status] ?? e.status }} · tur {{ e.round }}<template v-if="e.detail"> · {{ e.detail }}</template></span>
                    </div>
                  </template>
                </li>
              </ol>
            </template>
          </section>
        </div>
      </template>
    </section>
  </div>
</template>

<style scoped>
.wrap { position: absolute; inset: 0; background: rgba(10, 12, 18, 0.55); display: flex; justify-content: flex-end; }
.panel {
  background: #ede9dc; color: #23283a; border-left: 6px solid #6b4a2b;
  width: min(620px, 100%); height: 100%; overflow: auto; padding: 16px 18px;
  box-shadow: -20px 0 60px rgba(0,0,0,0.5); display: flex; flex-direction: column; gap: 12px;
}
.panel > header { display: flex; align-items: center; gap: 10px; }
h2 { margin: 0; font-size: 16px; letter-spacing: 0.04em; text-transform: uppercase; }
h3 { margin: 0 0 6px; font-size: 13px; text-transform: uppercase; letter-spacing: 0.06em; color: #4a5068; }
.key { font-size: 10px; background: rgba(0,0,0,0.06); padding: 1px 6px; border-radius: 3px; }
/* Kapat: 28x28 tiklama alani, isaret tam ortada, ustune gelince hafif zemin (tum panellerde ayni). */
.x {
  margin-left: auto; width: 28px; height: 28px; flex: none; display: inline-flex; align-items: center; justify-content: center;
  background: none; border: none; border-radius: 6px; padding: 0; font-size: 20px; line-height: 1; color: #23283a; cursor: pointer;
}
.x:hover { background: rgba(35,40,58,0.10); }
.msg { margin: 0; font-size: 13px; color: #4a5068; }

.form, .run { display: flex; flex-direction: column; gap: 12px; }
.row { display: grid; grid-template-columns: 1fr 1fr; gap: 10px; }
.field { display: flex; flex-direction: column; gap: 4px; min-width: 0; }
.lbl { font-size: 11px; font-weight: 700; text-transform: uppercase; letter-spacing: 0.04em; color: #4a5068; }
input[type="text"], select, textarea {
  font: inherit; font-size: 13px; background: #fff; color: #23283a;
  border: 1px solid #c9c3b3; border-radius: 4px; padding: 6px 8px; width: 100%;
}
input:focus, select:focus, textarea:focus { outline: 2px solid #4f8ef7; outline-offset: 0; }
.brief { min-height: 160px; resize: vertical; line-height: 1.45; }
.drop { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; padding: 10px; border: 1px dashed #b9b2a0; border-radius: 6px; background: #faf8f2; }
.drop.over { border-color: #4f8ef7; background: #eef4ff; }
.att-list { list-style: none; margin: 4px 0 0; padding: 0; display: flex; flex-direction: column; gap: 4px; }
.att-list li { display: flex; align-items: center; gap: 8px; min-width: 0; font-size: 13px; }
.att-name { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.att-kind { flex: none; font-size: 10px; font-weight: 700; text-transform: uppercase; letter-spacing: 0.04em; padding: 1px 6px; border-radius: 3px; background: #e5e7ee; color: #4a5068; }
.att-kind.document { background: #f7dcdc; color: #7a2323; }
.att-kind.image { background: #dcf1d3; color: #2d5a22; }
.att-kind.word { background: #d8ebfa; color: #1d4f7a; }
.att-x { margin-left: auto; flex: none; width: 22px; height: 22px; padding: 0; border: none; background: none; font-size: 16px; line-height: 1; color: #6b7285; }
.att-x:hover { background: rgba(35,40,58,0.10); color: #23283a; }
.small-link { font-size: 11px; }
.note { min-height: 70px; resize: vertical; }
.sub { font-size: 11px; color: #6b7285; line-height: 1.4; }
.sub.right { margin-left: auto; }

.actions { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
button { font: inherit; cursor: pointer; border-radius: 4px; padding: 7px 14px; border: 1px solid #c9c3b3; background: #fff; color: #23283a; }
button:disabled { opacity: 0.5; cursor: default; }
.primary { background: #23283a; color: #fff; border-color: #23283a; }
.ghost { background: transparent; }
.small { padding: 3px 8px; font-size: 11px; }
.link { border: none; background: none; padding: 0; text-decoration: underline; cursor: pointer; color: #23283a; font-size: 13px; text-align: left; }
.err { color: #b3261e; font-size: 12px; }

.recent ul { list-style: none; margin: 6px 0 0; padding: 0; display: flex; flex-direction: column; gap: 4px; }
.recent li { display: flex; gap: 8px; align-items: baseline; }

.statusline { display: flex; align-items: center; gap: 8px; }
.status { font-size: 11px; font-weight: 700; padding: 2px 8px; border-radius: 999px; background: rgba(0,0,0,0.08); text-transform: uppercase; letter-spacing: 0.04em; }
.status.awaitingApproval { background: #f3c34a; }
.status.running { background: #4fa3e0; color: #fff; }
.status.paused { background: #a889e6; color: #fff; }
.status.awaitingInput { background: #d23b3b; color: #fff; }
.runactions { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; margin-top: 6px; }
.runactions .right { margin-right: auto; }
.livetools { display: flex; flex-wrap: wrap; gap: 4px 6px; align-items: center; margin-top: 6px; padding: 6px 8px; background: #fff; border: 1px solid #c9c3b3; border-radius: 6px; }
.livetools .lbl { font-size: 10px; font-weight: 700; text-transform: uppercase; color: #4a5068; }
.livetools .tool { font-size: 11px; padding: 1px 7px; border-radius: 999px; background: #e5e7ee; color: #23283a; max-width: 240px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.livetools .tool b { color: #4a5068; margin-right: 3px; }
.livetools .tool.write, .livetools .tool.edit, .livetools .tool.multiedit { background: #dcf1d3; }
.livetools .tool.bash { background: #d8ebfa; }
.live { margin-top: 6px; padding: 8px 10px; background: #fff; border: 1px solid #c9c3b3; border-radius: 6px; display: flex; flex-direction: column; gap: 6px; }
.live.focus { border-color: #4fa3e0; box-shadow: 0 0 0 2px rgba(79, 163, 224, 0.25); }
.live-head { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 10px; font-size: 12px; }
.live-head .right { margin-left: auto; font-variant-numeric: tabular-nums; }
.pulse { width: 10px; height: 10px; border-radius: 50%; background: #7cc46b; box-shadow: 0 0 0 0 rgba(124, 196, 107, 0.6); animation: pulse 1.6s infinite; }
.pulse.slow { background: #f3c34a; animation-duration: 3s; }
.pulse.stale { background: #e0605e; animation: none; }
@keyframes pulse { 70% { box-shadow: 0 0 0 7px rgba(124, 196, 107, 0); } 100% { box-shadow: 0 0 0 0 rgba(124, 196, 107, 0); } }
.live .warn { margin: 0; font-size: 12px; color: #8a4b12; background: #fdf1dc; border: 1px solid #efcf97; border-radius: 4px; padding: 4px 8px; }
.stream { max-height: 260px; overflow: auto; font-size: 12px; line-height: 1.45; display: flex; flex-direction: column; gap: 2px; background: #faf8f2; border: 1px solid #e3ddcc; border-radius: 4px; padding: 6px 8px; }
.stream .entry { display: flex; gap: 6px; align-items: baseline; }
.stream .ts { flex: none; font-size: 10px; color: #8a90a2; font-variant-numeric: tabular-nums; }
.stream .txt { white-space: pre-wrap; word-break: break-word; }
.stream .thinking .txt { color: #6b7285; font-style: italic; }
.stream .tool { font-size: 11px; padding: 0 6px; border-radius: 999px; background: #e5e7ee; color: #4a5068; }
.stream .tool.write, .stream .tool.edit, .stream .tool.multiedit { background: #dcf1d3; }
.stream .tool.bash { background: #d8ebfa; }
.stream code { font-size: 11px; word-break: break-all; }
.ctx > summary, .part > summary { cursor: pointer; font-size: 12px; }
.ctx .part { margin: 3px 0 0 10px; }
.ctx pre { max-height: 240px; overflow: auto; white-space: pre-wrap; font-size: 11px; background: #faf8f2; border: 1px solid #e3ddcc; border-radius: 4px; padding: 6px; }
.role { font-size: 10px; text-transform: uppercase; padding: 0 5px; border-radius: 3px; background: #e5e7ee; color: #4a5068; }
.role.system { background: #efe3f7; }
.role.user { background: #d8ebfa; }
.role.assistant { background: #dcf1d3; }
.elapsed { display: inline-flex; align-items: center; gap: 4px; font-size: 11px; font-variant-numeric: tabular-nums; color: #4a5068; background: #fff; border: 1px solid #c9c3b3; border-radius: 999px; padding: 1px 8px; }
.done-box { background: #e3f4dc; border: 2px solid #7cc46b; border-radius: 6px; padding: 10px 12px; display: flex; flex-direction: column; gap: 8px; }
.done-box h3 { display: flex; align-items: center; gap: 8px; color: #2d5a22; font-size: 14px; text-transform: none; letter-spacing: 0; margin: 0; }
.done-box .files { display: flex; flex-wrap: wrap; gap: 4px 6px; align-items: center; }
.done-box .files .lbl { font-size: 10px; font-weight: 700; text-transform: uppercase; color: #4a5068; margin-right: 4px; }
.done-box .launch { font: inherit; font-size: 12px; font-weight: 700; padding: 6px 12px; border-radius: 4px; background: #7cc46b; color: #14301a; border: 2px solid #3d6b2f; cursor: pointer; }
.done-box .launch:disabled { background: #c9c3b3; color: #6b7285; border-color: #b9ad92; cursor: default; }
.launch-note { margin: 0; font-size: 12px; padding: 6px 10px; border-radius: 4px; }
.launch-note.ok { background: #fff; color: #2d5a22; }
.launch-note.err { background: #fadada; color: #9c1f1f; }
.ask { background: #fff8e1; border: 2px solid #d23b3b; border-radius: 6px; padding: 10px 12px; display: flex; flex-direction: column; gap: 8px; }
.ask h3 { display: flex; align-items: center; gap: 8px; color: #23283a; font-size: 14px; text-transform: none; letter-spacing: 0; }
.ask .qtext { margin: 0; font-size: 13px; line-height: 1.5; }
.ask .options { display: flex; gap: 8px; flex-wrap: wrap; }
.ask .req { color: #d23b3b; margin-left: 2px; }
.ask .danger { border-color: #d23b3b; color: #9c1f1f; }
.limit { margin: 0; padding: 8px 10px; background: #efe6ff; border: 1px solid #a889e6; border-radius: 6px; font-size: 12px; }
.tools { margin: 4px 0 0; padding-left: 18px; font-size: 11px; }
.tools code { font-size: 11px; }
.tag.reject { background: #fadada; color: #9c1f1f; }
.status.completed { background: #7cc46b; }
.status.failed, .status.policyRejected, .status.interrupted, .status.budgetExceeded { background: #d23b3b; color: #fff; }
.spin { width: 10px; height: 10px; border: 2px solid #4fa3e0; border-top-color: transparent; border-radius: 50%; animation: spin 0.9s linear infinite; }
@keyframes spin { to { transform: rotate(360deg); } }

details summary { cursor: pointer; font-size: 12px; font-weight: 600; color: #4a5068; }
pre { margin: 6px 0 0; white-space: pre-wrap; font: 12px/1.45 Consolas, "Cascadia Mono", monospace; background: #fff; border: 1px solid #c9c3b3; padding: 8px; border-radius: 4px; }
.summary { margin: 0; font-size: 13px; line-height: 1.5; }
.rules { margin: 6px 0 0; padding-left: 18px; font-size: 12px; }

.tasks { margin: 8px 0 0; padding: 0; list-style: none; display: flex; flex-direction: column; gap: 8px; counter-reset: t; }
.tasks > li { background: #fff; border: 1px solid #c9c3b3; border-radius: 6px; padding: 8px 10px; counter-increment: t; }
.task-head { display: flex; align-items: center; gap: 8px; font-size: 13px; }
.task-head::before { content: counter(t); font-size: 11px; font-weight: 700; color: #6b7285; min-width: 14px; }
.tid { font-size: 10px; background: rgba(0,0,0,0.06); padding: 1px 5px; border-radius: 3px; }
.tasks p { margin: 4px 0 6px; font-size: 12px; line-height: 1.45; color: #4a5068; }
.tasks dl { margin: 0; display: grid; grid-template-columns: auto 1fr; gap: 2px 10px; font-size: 11px; }
.tasks dl > div { display: contents; }
.tasks dt { color: #6b7285; font-weight: 600; }
.tasks dd { margin: 0; display: flex; flex-wrap: wrap; gap: 4px; }
.tasks dd ul { margin: 0; padding-left: 16px; }
.tasks dd code { font-size: 10px; background: rgba(0,0,0,0.06); padding: 1px 5px; border-radius: 3px; }
.phase { margin-left: auto; font-size: 10px; padding: 1px 7px; border-radius: 999px; background: rgba(0,0,0,0.08); }
.phase.started { background: #4fa3e0; color: #fff; }
.phase.done { background: #7cc46b; }
.phase.rejected, .phase.failed { background: #d23b3b; color: #fff; }

.approve { background: #fff8e1; border: 2px solid #f3c34a; border-radius: 6px; padding: 10px 12px; display: flex; flex-direction: column; gap: 8px; }
.approve h3 { display: flex; align-items: center; gap: 8px; color: #23283a; font-size: 14px; text-transform: none; letter-spacing: 0; }
.qmark { display: inline-flex; align-items: center; justify-content: center; width: 20px; height: 20px; border-radius: 50%; background: #d23b3b; color: #fff; font-weight: 700; font-size: 13px; }
.danger { border-color: #d23b3b; color: #9c1f1f; }
.danger:hover:not(:disabled) { background: #fdecec; }
.errors { background: #fdecec; border: 1px solid #e0a0a0; border-radius: 6px; padding: 10px 12px; }
.errtext { border-color: #e0a0a0; color: #7a1f1f; }
.log-head { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
.log-head h3 { margin: 0; }
.entries { list-style: none; margin: 8px 0 0; padding: 0; display: flex; flex-direction: column; gap: 6px; }
.entries li { background: #fff; border: 1px solid #c9c3b3; border-left-width: 4px; border-radius: 6px; padding: 6px 10px; }
.entries li.turn { border-left-color: #4f8ef7; }
.entries li.message { border-left-color: #d9a13a; }
.entries li.phase { border-left-color: #7cc46b; }
.entry-head { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; font-size: 12px; }
.when { font-variant-numeric: tabular-nums; color: #6b7285; font-size: 11px; }
.tag { font-size: 10px; font-weight: 700; text-transform: uppercase; letter-spacing: 0.04em; padding: 1px 6px; border-radius: 999px; background: rgba(0,0,0,0.08); }
.tag.turn { background: #dbe8fb; }
.tag.msg { background: #f6e6c2; }
.tag.error { background: #e05252; color: #fff; }
.tag.phase { background: #dff2d8; }
.entries pre { max-height: 360px; overflow: auto; }
.entries details summary { font-size: 11px; }
.notes ul { margin: 0; padding-left: 16px; font-size: 12px; }
.notes article + article { margin-top: 8px; }
code { font-size: 10px; background: rgba(0,0,0,0.06); padding: 1px 4px; border-radius: 3px; }
</style>
