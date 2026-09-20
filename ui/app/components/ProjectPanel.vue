<script setup lang="ts">
import type { CreateProjectRequest, InboxItem, LaunchResult, ProjectCard, ProjectDeleteResult, ProjectModel, ReorderRequest, RunSummary, WorkflowListItem } from '~/api/types'
import DirPicker from '~/components/DirPicker.vue'
import { useApiClient } from '~/api/client'
import { errorText } from '~/api/errors'
import { INBOX_KIND_LABEL, RUN_STATUS_LABEL, fmtCost as fmtCostLabel } from '~/api/labels'

/**
 * Proje karti acildi (docs/DOMAIN.md → Projeler): kagit pano, sekmeler Isler / Ayarlar.
 *  - projectKey null → yeni proje formu (baslik, aciklama, akis, hedef dizin; butce yok).
 *  - Isler: bu projenin calismalari; satira tiklaninca calisma paneli. "Yeni is" YALNIZ burada.
 *  - Ayarlar: proje alanlari (hedef dizin klasor seciciyle, serbest metin degil); silme iki adimda onaylanir:
 *    suren is varsa kapali, gecmis projeyle gider, dosyalarin silinip silinmeyecegi ayrica isaretlenir.
 */
const props = defineProps<{ projectKey: string | null; inbox: InboxItem[] }>()
const emit = defineEmits<{ close: []; openRun: [id: string]; newRun: [projectKey: string]; created: [key: string]; changed: [] }>()

const api = useApiClient()

// ------------------------------------------------------------------ yeni proje

const nKey = ref('')
const nTitle = ref('')
const nDesc = ref('')
const nWorkflow = ref('default')
const nDir = ref('')
const workflows = ref<WorkflowListItem[]>([])
const creating = ref(false)
const createError = ref<string | null>(null)

/** Basliktan anahtar: kucuk harf, Turkce karakterler sadelesir, bosluk → tire. */
function slug(s: string): string {
  const map: Record<string, string> = { ç: 'c', ğ: 'g', ı: 'i', ö: 'o', ş: 's', ü: 'u', Ç: 'c', Ğ: 'g', İ: 'i', Ö: 'o', Ş: 's', Ü: 'u' }
  return s.replace(/[çğıöşüÇĞİÖŞÜ]/g, c => map[c] ?? c).toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 40)
}
const keyTouched = ref(false)
watch(nTitle, (t) => { if (!keyTouched.value) nKey.value = slug(t) })

async function loadWorkflows() {
  try { workflows.value = await api.get<WorkflowListItem[]>('/api/v1/workflows') } catch { workflows.value = [] }
}

async function create() {
  if (creating.value || !nTitle.value.trim() || !nKey.value.trim()) return
  creating.value = true
  createError.value = null
  try {
    const body: CreateProjectRequest = { key: nKey.value.trim(), title: nTitle.value.trim(), description: nDesc.value.trim() || null, workflow: nWorkflow.value || null, targetDir: nDir.value.trim() || null }
    const card = await api.post<ProjectCard>('/api/v1/projects', body)
    emit('created', card.key)
  } catch (e) {
    createError.value = errorText(e)
  } finally {
    creating.value = false
  }
}

// ------------------------------------------------------------------ acik proje: durum

const card = ref<ProjectCard | null>(null)
const runs = ref<RunSummary[] | null>(null)
const loadError = ref<string | null>(null)
const tab = ref<'runs' | 'settings'>('runs')
let timer: ReturnType<typeof setInterval> | undefined
// Silme durumu: projectKey watch'i bunlari sifirlar, o yuzden watch'tan once tanimli.
const confirmingDelete = ref(false)
const deleteFiles = ref(false)
const deleting = ref(false)
const deleted = ref<ProjectDeleteResult | null>(null)

// ------------------------------------------------------------------ ayarlar

const eTitle = ref(''); const eDesc = ref(''); const eWorkflow = ref('default'); const eDir = ref(''); const eColor = ref('#4fa3e0')
const editing = ref(false)
const saving = ref(false)
const saveError = ref<string | null>(null)
function fillForm(c: ProjectCard) { eTitle.value = c.title; eDesc.value = c.description; eWorkflow.value = c.workflow; eDir.value = c.targetDir; eColor.value = c.color || '#4fa3e0' }
const dirty = computed(() => !!card.value && (eTitle.value !== card.value.title || eDesc.value !== card.value.description || eWorkflow.value !== card.value.workflow || eDir.value !== card.value.targetDir || eColor.value.toLowerCase() !== (card.value.color || '#4fa3e0').toLowerCase()))

// ------------------------------------------------------------------ sira (POST /projects/reorder): ray ve Kanban bu sirayi okur
const allProjects = ref<ProjectCard[]>([])
async function loadAll() { try { allProjects.value = await api.get<ProjectCard[]>('/api/v1/projects') } catch { /* ok kalir */ } }
const position = computed(() => allProjects.value.findIndex(p => p.key === props.projectKey))
const reordering = ref(false)
async function move(delta: -1 | 1) {
  const i = position.value
  const j = i + delta
  if (i < 0 || j < 0 || j >= allProjects.value.length || reordering.value) return
  reordering.value = true
  try {
    const keys = allProjects.value.map(p => p.key)
    ;[keys[i], keys[j]] = [keys[j]!, keys[i]!]
    allProjects.value = await api.post<ProjectCard[]>('/api/v1/projects/reorder', { keys } satisfies ReorderRequest)
    emit('changed')
  } catch (e) {
    saveError.value = errorText(e)
  } finally {
    reordering.value = false
  }
}
watch(dirty, d => { editing.value = d })

// ------------------------------------------------------------------ acik proje

// ------------------------------------------------------------------ projeyi baslat (POST /projects/{key}/launch)
const launching = ref(false)
const launchNote = ref<{ kind: 'ok' | 'err'; text: string } | null>(null)
async function launch() {
  if (!props.projectKey || launching.value) return
  launching.value = true
  launchNote.value = null
  try {
    const r = await api.post<LaunchResult>(`/api/v1/projects/${encodeURIComponent(props.projectKey)}/launch`)
    launchNote.value = { kind: 'ok', text: `Yeni pencerede açıldı (${r.launcher}, pid ${r.processId}).` }
  } catch (e) {
    launchNote.value = { kind: 'err', text: errorText(e) }
  } finally {
    launching.value = false
    setTimeout(() => { launchNote.value = null }, 6000)
  }
}

async function load() {
  if (!props.projectKey) return
  try {
    const [c, r] = await Promise.all([
      api.get<ProjectCard>(`/api/v1/projects/${encodeURIComponent(props.projectKey)}`),
      api.get<RunSummary[]>(`/api/v1/projects/${encodeURIComponent(props.projectKey)}/runs?limit=100`),
    ])
    card.value = c
    runs.value = r
    if (!editing.value) fillForm(c)
    loadError.value = null
    void loadAll()
  } catch (e) {
    loadError.value = errorText(e)
  }
}

watch(() => props.projectKey, (k) => {
  clearInterval(timer)
  card.value = null; runs.value = null; loadError.value = null; tab.value = 'runs'; editing.value = false; confirmingDelete.value = false; deleted.value = null
  if (k) { void load(); timer = setInterval(() => { void load() }, 5000) }
  else { void loadWorkflows() }
}, { immediate: true })
onMounted(() => { if (props.projectKey) void loadWorkflows() })
onBeforeUnmount(() => clearInterval(timer))

async function save() {
  if (!props.projectKey || saving.value) return
  saving.value = true
  saveError.value = null
  try {
    const body: ProjectModel = { title: eTitle.value.trim(), description: eDesc.value.trim() || null, workflow: eWorkflow.value || null, targetDir: eDir.value.trim() || null, color: eColor.value || null }
    card.value = await api.put<ProjectCard>(`/api/v1/projects/${encodeURIComponent(props.projectKey)}`, body)
    fillForm(card.value)
    editing.value = false
    emit('changed')
  } catch (e) {
    saveError.value = errorText(e)
  } finally {
    saving.value = false
  }
}

// Silme (kullanici karari 2026-09-20): durum yukarida (watch ondan once kosar); once onay kutusu, dosyalar orada isaretlenir.
function askRemove() { deleteFiles.value = false; saveError.value = null; confirmingDelete.value = true }
async function remove() {
  if (!props.projectKey || !card.value || deleting.value) return
  deleting.value = true
  saveError.value = null
  try {
    deleted.value = await api.del<ProjectDeleteResult>(`/api/v1/projects/${encodeURIComponent(props.projectKey)}?deleteFiles=${deleteFiles.value}`)
    confirmingDelete.value = false
    clearInterval(timer)
    emit('changed')
  } catch (e) {
    saveError.value = errorText(e)
  } finally {
    deleting.value = false
  }
}

// ------------------------------------------------------------------ turetilenler

/** Silmeyi kapatan durumlar: is suruyor ya da kullanicidan bir sey bekliyor (sunucu da 409 project.in_use ile reddeder). */
const ACTIVE = new Set(['running', 'awaitingApproval', 'paused', 'awaitingInput'])
const activeRuns = computed(() => (runs.value ?? []).filter(r => ACTIVE.has(r.status)))
const otherRuns = computed(() => (runs.value ?? []).filter(r => !ACTIVE.has(r.status)))
function pendingOf(id: string): InboxItem | undefined { return props.inbox.find(i => i.runId === id) }
function fmtCost(v: number): string { return fmtCostLabel(v, 2) }
function fmtWhen(s: string | null): string {
  if (!s) return '—'
  const d = new Date(s)
  const today = new Date().toDateString() === d.toDateString()
  const time = d.toLocaleTimeString('tr-TR', { hour: '2-digit', minute: '2-digit' })
  return today ? time : `${d.toLocaleDateString('tr-TR', { day: '2-digit', month: '2-digit' })} ${time}`
}
function initials(t: string): string { return t.split(/\s+/).filter(Boolean).slice(0, 2).map(w => w[0]!.toUpperCase()).join('') || '?' }
</script>

<template>
  <div class="wrap" @click.self="emit('close')">
    <section class="panel" role="dialog" aria-labelledby="project-title">

      <!-- ------------------------------------------------ yeni proje -->
      <template v-if="!projectKey">
        <header>
          <h2 id="project-title">Yeni proje</h2>
          <button class="x" type="button" aria-label="Kapat" @click="emit('close')">×</button>
        </header>
        <form class="form" @submit.prevent="create">
          <div class="field">
            <label class="lbl" for="p-title">Başlık</label>
            <input id="p-title" v-model="nTitle" type="text" required placeholder="Hello World Console">
          </div>
          <div class="field">
            <label class="lbl" for="p-key">Anahtar <span class="sub">(klasör ve adres; küçük harf, tire)</span></label>
            <input id="p-key" v-model="nKey" type="text" required pattern="[a-z0-9][a-z0-9_-]*" spellcheck="false" @input="keyTouched = true">
          </div>
          <div class="field">
            <label class="lbl" for="p-desc">Açıklama</label>
            <textarea id="p-desc" v-model="nDesc" rows="3" placeholder="Ne üretiyoruz, kime, hangi kısıtlarla?" />
          </div>
          <div class="row">
            <div class="field">
              <label class="lbl" for="p-wf">Varsayılan iş akışı</label>
              <select id="p-wf" v-model="nWorkflow">
                <option v-for="w in workflows" :key="w.key" :value="w.key">{{ w.title }}</option>
                <option v-if="!workflows.length" value="default">Varsayılan</option>
              </select>
            </div>
            <div class="field">
              <label class="lbl" for="p-dir">Hedef dizin <span class="sub">(depo içinden seçilir)</span></label>
              <DirPicker id="p-dir" v-model="nDir" :project-key="nKey" />
            </div>
          </div>
          <p class="sub">İşler bu projenin içinde açılır, akışı devralır. Bütçe iş başına verilir.</p>
          <div class="actions">
            <button class="primary" type="submit" :disabled="creating || !nTitle.trim() || !nKey.trim()">{{ creating ? 'Oluşturuluyor…' : 'Projeyi oluştur' }}</button>
            <span v-if="createError" class="err" role="alert">{{ createError }}</span>
          </div>
        </form>
      </template>

      <!-- ------------------------------------------------ silindi -->
      <div v-else-if="deleted" class="form done" role="status">
        <p><b>Proje silindi.</b> {{ deleted.runsDeleted }} çalışmanın geçmişi kaldırıldı.
          {{ deleted.filesDeleted ? 'Dosyalar da silindi' : 'Dosyalar yerinde' }}: <code>{{ deleted.targetDir }}</code>.</p>
        <div class="actions"><button type="button" class="primary" @click="emit('close')">Kapat</button></div>
      </div>

      <!-- ------------------------------------------------ acik proje -->
      <template v-else>
        <header>
          <span class="avatar">{{ card ? initials(card.title) : '…' }}</span>
          <div class="head-text">
            <h2 id="project-title">{{ card?.title ?? projectKey }}</h2>
            <span class="sub" v-if="card">{{ card.workflow }} akışı · <code>{{ card.targetDir }}</code> · {{ fmtCost(card.totalCostUsd) }}</span>
          </div>
          <!-- Projeyi baslat: kokteki run.cmd yeni konsolda; developer bunu her teslimde yazar/gunceller. -->
          <button
            type="button"
            class="launch"
            :disabled="launching || !card?.launchable"
            :title="card?.launchable ? 'Proje kökündeki run.cmd yeni pencerede çalışır' : 'run.cmd yok: developer uygulamayı çalıştırılabilir yapınca açılır'"
            @click="launch"
          ><span aria-hidden="true">▶</span> {{ launching ? 'Açılıyor…' : 'Projeyi başlat' }}</button>
          <button class="x" type="button" aria-label="Kapat" @click="emit('close')">×</button>
        </header>
        <p v-if="launchNote" class="launch-note" :class="launchNote.kind" role="status">{{ launchNote.text }}</p>
        <nav class="tabs" role="tablist">
          <button type="button" role="tab" :class="{ on: tab === 'runs' }" :aria-selected="tab === 'runs'" @click="tab = 'runs'">İşler <b v-if="runs">{{ runs.length }}</b></button>
          <button type="button" role="tab" :class="{ on: tab === 'settings' }" :aria-selected="tab === 'settings'" @click="tab = 'settings'">Ayarlar</button>
        </nav>

        <p v-if="loadError" class="err" role="alert">{{ loadError }}</p>

        <div v-else-if="tab === 'runs'" class="runs">
          <p v-if="card?.description" class="desc">{{ card.description }}</p>
          <p v-if="runs && !runs.length" class="empty">Bu projede henüz iş yok. Aşağıdan ilk brief'i ver.</p>
          <template v-for="group in [['aktif', activeRuns], ['geçmiş', otherRuns]] as const" :key="group[0]">
            <h3 v-if="group[1].length" class="group">{{ group[0] }} <span class="sub">{{ group[1].length }}</span></h3>
            <button v-for="r in group[1]" :key="r.id" type="button" class="run" :class="{ ask: pendingOf(r.id) }" @click="emit('openRun', r.id)">
              <span class="status" :class="r.status">{{ RUN_STATUS_LABEL[r.status] }}</span>
              <strong>{{ r.label }}</strong>
              <span class="meta">{{ fmtWhen(r.startedAt) }} · {{ fmtCost(r.totalCostUsd) }}</span>
              <span v-if="pendingOf(r.id)" class="pending"><span aria-hidden="true">🔔</span> {{ INBOX_KIND_LABEL[pendingOf(r.id)!.kind] }}: {{ pendingOf(r.id)!.title }}</span>
              <span v-else-if="r.detail" class="detail">{{ r.detail }}</span>
            </button>
          </template>
        </div>

        <form v-else class="form" @submit.prevent="save">
          <div class="field">
            <label class="lbl" for="e-title">Başlık</label>
            <input id="e-title" v-model="eTitle" type="text" required>
          </div>
          <div class="field">
            <label class="lbl" for="e-desc">Açıklama</label>
            <textarea id="e-desc" v-model="eDesc" rows="3" />
          </div>
          <div class="row">
            <div class="field">
              <label class="lbl" for="e-wf">Varsayılan iş akışı</label>
              <select id="e-wf" v-model="eWorkflow">
                <option v-for="w in workflows" :key="w.key" :value="w.key">{{ w.title }}</option>
                <option v-if="!workflows.some(w => w.key === eWorkflow)" :value="eWorkflow">{{ eWorkflow }}</option>
              </select>
            </div>
            <div class="field">
              <label class="lbl" for="e-dir">Hedef dizin <span class="sub">(depo içinden seçilir)</span></label>
              <DirPicker id="e-dir" v-model="eDir" :project-key="card?.key ?? ''" />
            </div>
          </div>
          <div class="row">
            <div class="field">
              <label class="lbl" for="e-color">Renk <span class="sub">(ray kartı ve Kanban "Tümü")</span></label>
              <span class="color-row"><input id="e-color" v-model="eColor" type="color"><code>{{ eColor }}</code></span>
            </div>
            <div class="field">
              <span class="lbl">Sıra <span class="sub">(ray ve Kanban)</span></span>
              <span class="order-row">
                <button type="button" class="small" :disabled="reordering || position <= 0" title="Yukarı taşı" @click="move(-1)">▲</button>
                <b>{{ position >= 0 ? position + 1 : '—' }}</b><span class="sub">/ {{ allProjects.length }}</span>
                <button type="button" class="small" :disabled="reordering || position < 0 || position >= allProjects.length - 1" title="Aşağı taşı" @click="move(1)">▼</button>
              </span>
            </div>
          </div>
          <p class="sub" v-if="card">Sahip: <code>{{ card.ownerId }}</code> · oluşturma {{ fmtWhen(card.createdAt) }}. Giriş gelince sahip kullanıcıya bağlanır.</p>
          <div class="actions">
            <button class="primary" type="submit" :disabled="saving || !dirty">{{ saving ? 'Kaydediliyor…' : 'Kaydet' }}</button>
            <button type="button" class="ghost" :disabled="!dirty || saving" @click="card && fillForm(card)">Geri al</button>
            <button type="button" class="danger" :disabled="confirmingDelete || activeRuns.length > 0" :title="activeRuns.length ? 'Süren iş var; önce bitirin ya da iptal edin' : undefined" @click="askRemove"><span class="ico" aria-hidden="true">🗑</span> Projeyi sil…</button>
            <span v-if="saveError" class="err" role="alert">{{ saveError }}</span>
          </div>

          <div v-if="confirmingDelete && card" class="confirm" role="alertdialog" aria-labelledby="del-title">
            <p id="del-title"><b>«{{ card.title }}» silinsin mi?</b> Proje tanımı ve içindeki <b>{{ card.runs }}</b> çalışmanın geçmişi silinir. Geri alınamaz.</p>
            <label class="check">
              <input v-model="deleteFiles" type="checkbox">
              <span>Proje dosyaları da silinsin: <code>{{ card.targetDir }}</code></span>
            </label>
            <p class="sub">{{ deleteFiles ? 'Klasör ve içindeki her şey diskten silinir.' : 'Klasör diskte kalır; yalnız proje kaydı ve çalışma geçmişi gider.' }}</p>
            <div class="actions">
              <button type="button" class="danger fill" :disabled="deleting" @click="remove"><span class="ico" aria-hidden="true">🗑</span> {{ deleting ? 'Siliniyor…' : (deleteFiles ? 'Evet, projeyi ve dosyaları sil' : 'Evet, projeyi sil (dosyalar kalsın)') }}</button>
              <button type="button" class="ghost" :disabled="deleting" @click="confirmingDelete = false">Vazgeç</button>
            </div>
          </div>
        </form>

        <footer>
          <button type="button" class="new" @click="emit('newRun', projectKey)">
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round"><path d="M12 5v14"/><path d="M5 12h14"/></svg>
            Yeni iş
          </button>
          <span class="sub">bu projenin akışıyla; plan onayına düşer</span>
        </footer>
      </template>
    </section>
  </div>
</template>

<style scoped>
.wrap { position: absolute; inset: 0; background: rgba(10, 12, 18, 0.35); display: flex; justify-content: flex-start; }
.panel {
  background: #ede9dc; color: #23283a; border: 6px solid #6b4a2b; border-left: none; border-radius: 0 8px 8px 0;
  width: min(420px, 100%); height: 100%; overflow: auto; display: flex; flex-direction: column;
  box-shadow: 20px 0 60px rgba(0,0,0,0.5);
}
.panel > header { display: flex; align-items: center; gap: 10px; padding: 14px 14px 10px; background: #f3efe3; border-bottom: 1px solid #cfcabb; }
.head-text { display: flex; flex-direction: column; gap: 2px; min-width: 0; }
h2 { margin: 0; font-size: 16px; font-weight: 700; letter-spacing: 0.02em; }
.avatar { width: 34px; height: 34px; border-radius: 8px; background: #3d5a80; color: #fff; font-weight: 700; font-size: 13px; display: inline-flex; align-items: center; justify-content: center; flex: none; }
.x { background: none; border: none; font-size: 22px; cursor: pointer; color: #23283a; line-height: 1; margin-left: auto; padding: 0 4px; }
.tabs { display: flex; gap: 2px; padding: 6px 10px 0; background: #f3efe3; border-bottom: 1px solid #cfcabb; }
.tabs button { font: inherit; font-size: 12px; font-weight: 600; color: #4a5068; background: transparent; border: none; border-bottom: 2px solid transparent; margin-bottom: -1px; padding: 7px 10px; cursor: pointer; border-radius: 0; }
.tabs button.on { color: #23283a; border-bottom-color: #23283a; font-weight: 700; }
.tabs b { background: rgba(0,0,0,0.08); border-radius: 999px; padding: 0 6px; font-size: 10px; margin-left: 4px; }

.form, .runs { display: flex; flex-direction: column; gap: 10px; padding: 12px 14px; }
.runs { flex-grow: 1; }
.row { display: grid; grid-template-columns: 1fr 1fr; gap: 10px; }
.field { display: flex; flex-direction: column; gap: 4px; min-width: 0; }
.lbl { font-size: 11px; font-weight: 700; text-transform: uppercase; letter-spacing: 0.04em; color: #4a5068; }
input[type="text"], select, textarea { font: inherit; font-size: 13px; background: #fff; color: #23283a; border: 1px solid #c9c3b3; border-radius: 4px; padding: 7px 9px; width: 100%; }
textarea { resize: vertical; line-height: 1.45; }
input:focus, select:focus, textarea:focus { outline: 2px solid #4f8ef7; outline-offset: 0; }
.sub { font-size: 11px; color: #6b7285; line-height: 1.4; }
.desc { margin: 0; font-size: 12px; color: #4a5068; line-height: 1.5; }
.empty { margin: 0; font-size: 12px; color: #6b7285; padding: 14px; background: rgba(0,0,0,0.04); border-radius: 6px; }
.group { margin: 4px 0 0; font-size: 11px; text-transform: uppercase; letter-spacing: 0.08em; color: #6b7285; display: flex; gap: 6px; }
.run {
  display: grid; grid-template-columns: auto 1fr auto; gap: 4px 8px; align-items: center; text-align: left; font: inherit; cursor: pointer;
  background: #fff; color: #23283a; border: 1px solid #c9c3b3; border-radius: 4px; padding: 8px 10px; box-shadow: 0 1px 2px rgba(0,0,0,0.1);
}
.run:hover { box-shadow: 0 2px 6px rgba(0,0,0,0.18); }
.run.ask { border-color: #f3c34a; background: #fff8e1; }
.run strong { font-size: 13px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.run .meta { font-size: 11px; color: #6b7285; }
.run .pending, .run .detail { grid-column: 1 / span 3; font-size: 11px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.run .pending { font-weight: 700; color: #7a5a00; }
.run .detail { color: #6b7285; }
.status { font-size: 10px; font-weight: 700; padding: 1px 7px; border-radius: 999px; background: rgba(0,0,0,0.08); text-transform: uppercase; letter-spacing: 0.04em; }
.status.awaitingApproval { background: #f3c34a; }
.status.running { background: #4fa3e0; color: #fff; }
.status.paused { background: #a889e6; color: #fff; }
.status.awaitingInput { background: #d23b3b; color: #fff; }
.status.completed { background: #7cc46b; }
.status.failed, .status.policyRejected, .status.interrupted, .status.budgetExceeded, .status.cancelled { background: #d23b3b; color: #fff; }

.actions { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
button { font: inherit; cursor: pointer; border-radius: 4px; padding: 7px 14px; border: 1px solid #c9c3b3; background: #fff; color: #23283a; }
button:disabled { opacity: 0.5; cursor: default; }
.primary { background: #23283a; color: #fff; border-color: #23283a; }
.ghost { background: transparent; }
.danger { margin-left: auto; display: inline-flex; align-items: center; gap: 6px; color: #7a1f1f; border-color: #e0a0a0; }
/* Dugme ikonu: metinle ayni satirda, hafif kucuk ve biraz sonuk; yalniz isaret, anlam metinde. */
.ico { font-size: 0.95em; line-height: 1; opacity: 0.85; }
.danger.fill { margin-left: 0; background: #b3261e; color: #fff; border-color: #b3261e; font-weight: 700; }
.confirm { border: 1px solid #e0a0a0; background: #fbecec; border-radius: 6px; padding: 10px 12px; display: flex; flex-direction: column; gap: 8px; }
.confirm p { margin: 0; font-size: 13px; line-height: 1.45; }
.check { display: flex; align-items: flex-start; gap: 8px; font-size: 13px; cursor: pointer; }
.check input { margin-top: 3px; }
.done p { font-size: 13px; line-height: 1.5; margin: 0; }
.err { color: #b3261e; font-size: 12px; }
code { font-size: 10px; background: rgba(0,0,0,0.06); padding: 1px 4px; border-radius: 3px; }

footer { margin-top: auto; padding: 10px 14px; border-top: 1px solid #cfcabb; background: #f3efe3; display: flex; align-items: center; gap: 10px; }
.new { display: inline-flex; align-items: center; gap: 8px; font-size: 13px; font-weight: 700; padding: 9px 14px; background: #d9a13a; color: #141413; border-color: #d9a13a; }
.launch { font: inherit; font-size: 12px; font-weight: 700; padding: 6px 12px; border-radius: 4px; background: #7cc46b; color: #14301a; border: 2px solid #3d6b2f; cursor: pointer; white-space: nowrap; }
.launch:hover:not(:disabled) { background: #8fd47d; }
.launch:disabled { background: #c9c3b3; color: #6b7285; border-color: #b9ad92; cursor: default; }
.launch-note { margin: 0; font-size: 12px; padding: 6px 10px; border-radius: 4px; }
.launch-note.ok { background: #e3f4dc; color: #2d5a22; }
.launch-note.err { background: #fadada; color: #9c1f1f; }
.color-row { display: inline-flex; align-items: center; gap: 8px; }
.color-row input[type="color"] { width: 40px; height: 28px; padding: 0; border: 1px solid #c9c3b3; border-radius: 4px; background: #fff; cursor: pointer; }
.order-row { display: inline-flex; align-items: center; gap: 6px; }
.order-row .small { font: inherit; font-size: 11px; padding: 3px 8px; border: 1px solid #c9c3b3; border-radius: 4px; background: #fff; cursor: pointer; }
.order-row .small:disabled { opacity: 0.4; cursor: default; }
</style>
