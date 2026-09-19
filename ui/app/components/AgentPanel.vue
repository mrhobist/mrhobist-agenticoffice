<script setup lang="ts">
import type { AgentDetail, AgentListItem, AgentRunWork, AgentUpdate, Effort, KnowledgeItem, ModelInfo, Phase as RunPhase, Provider, RunMessage, Turn } from '~/api/types'
import { isApiError, useApiClient } from '~/api/client'
import { errorText } from '~/api/errors'
import { EFFORTS, EFFORT_LABEL, PROVIDERS, PROVIDER_LABEL, RUN_STATUS_LABEL } from '~/api/labels'

/**
 * Bir ajanin md dosyasini duzenler: ustveri (saglayici, model, bilgi dosyalari, sorabilir)
 * + sistem promptu. Kaynak yalniz Api'dir; panel yapilandirma kopyasi tutmaz.
 */
const props = defineProps<{
  agentKey: string
  /** Sahnedeki ad; detay gelene kadar ve is akisi disi karakterler icin baslikta kullanilir. */
  sceneName: string
  /** GET /agents listesi (kabuk yukler). null = liste alinamadi. */
  agents: AgentListItem[] | null
}>()
const emit = defineEmits<{ close: []; saved: [detail: AgentDetail] }>()

const api = useApiClient()

type Phase = 'loading' | 'ready' | 'outside' | 'missing' | 'error'
const phase = ref<Phase>('loading')
const loadError = ref('')
const form = ref<AgentDetail | null>(null)
const original = ref('')

const knowledge = ref<KnowledgeItem[] | null>(null)

type ModelsState = 'idle' | 'loading' | 'ready' | 'runtime-down' | 'missing' | 'error'
const modelsState = ref<ModelsState>('idle')
const models = ref<ModelInfo[]>([])
const modelsError = ref('')
const modelCache = new Map<Provider, ModelInfo[]>()

const saving = ref(false)
const saveError = ref<string | null>(null)
const savedAt = ref<number | null>(null)

// ------------------------------------------------------------------ yukleme

function toUpdate(d: AgentDetail): AgentUpdate {
  return {
    key: d.key,
    name: d.name.trim(),
    summary: d.summary.trim(),
    officeRoles: d.officeRoles,
    provider: d.provider,
    model: d.model?.trim() || null,
    effort: d.effort,
    includes: d.includes,
    canAsk: d.canAsk,
    prompt: d.prompt,
  }
}

function apply(d: AgentDetail) {
  form.value = { ...d, officeRoles: [...d.officeRoles], includes: [...d.includes] }
  original.value = JSON.stringify(toUpdate(d))
}

/** Hizli ajan degisiminde eski yanit yeniyi ezmesin. */
let loadSeq = 0

async function load() {
  const seq = ++loadSeq
  phase.value = 'loading'
  loadError.value = ''
  saveError.value = null
  savedAt.value = null
  form.value = null
  if (props.agents && !props.agents.some(a => a.key === props.agentKey)) { phase.value = 'outside'; return }
  let detail: AgentDetail
  try {
    detail = await api.get<AgentDetail>(`/api/v1/agents/${props.agentKey}`)
    if (seq !== loadSeq) return
  } catch (e) {
    if (seq !== loadSeq) return
    if (isApiError(e) && e.endpointMissing) phase.value = 'missing'
    else if (isApiError(e) && e.errorCode === 'agent.not_found') phase.value = 'outside'
    else { loadError.value = errorText(e); phase.value = 'error' }
    return
  }
  apply(detail)
  phase.value = 'ready'
  if (knowledge.value === null) void loadKnowledge()
  // Saglayici bos = varsayilan (anthropic): listeyi yine de getir ki model secilebilsin.
  void loadModels(detail.provider ?? 'anthropic')
}

async function loadKnowledge() {
  try { knowledge.value = await api.get<KnowledgeItem[]>('/api/v1/knowledge') }
  catch { knowledge.value = null }
}

async function loadModels(p: Provider | null) {
  models.value = []
  modelsError.value = ''
  if (!p) { modelsState.value = 'idle'; return }
  const cached = modelCache.get(p)
  if (cached) { models.value = cached; modelsState.value = 'ready'; return }
  modelsState.value = 'loading'
  try {
    const list = await api.get<ModelInfo[]>(`/api/v1/models?provider=${encodeURIComponent(p)}`)
    if ((form.value?.provider ?? 'anthropic') !== p) return
    modelCache.set(p, list)
    models.value = list
    modelsState.value = 'ready'
  } catch (e) {
    if ((form.value?.provider ?? 'anthropic') !== p) return
    if (isApiError(e) && e.errorCode === 'runtime.unavailable') modelsState.value = 'runtime-down'
    else if (isApiError(e) && e.endpointMissing) modelsState.value = 'missing'
    else { modelsError.value = errorText(e); modelsState.value = 'error' }
  }
}

watch(() => props.agentKey, load, { immediate: true })

// ------------------------------------------------------------------ form baglari

const providerModel = computed<Provider | ''>({
  get: () => form.value?.provider ?? '',
  set: (v) => {
    if (!form.value) return
    form.value.provider = v || null
    void loadModels(form.value.provider ?? 'anthropic')
  },
})

const modelText = computed<string>({
  get: () => form.value?.model ?? '',
  set: (v) => { if (form.value) form.value.model = v || null },
})

/** Acilir menu secenekleri: liste + (listede olmayan) mevcut deger, secim kaybolmasin. */
const modelOptions = computed<ModelInfo[]>(() => {
  const cur = form.value?.model
  if (cur && !models.value.some(m => m.model === cur)) {
    return [...models.value, { provider: (form.value?.provider ?? 'anthropic') as Provider, model: cur, reachable: false, detail: 'listede yok' }]
  }
  return models.value
})

const canAskModel = computed<string>({
  get: () => form.value?.canAsk ?? '',
  set: (v) => { if (form.value) form.value.canAsk = v || null },
})

const effortModel = computed<Effort | ''>({
  get: () => form.value?.effort ?? '',
  set: (v) => { if (form.value) form.value.effort = v || null },
})

/** Kendisi haric ajanlar; liste yoksa yalniz mevcut deger secilebilir kalir. */
const askOptions = computed<Array<{ key: string; name: string }>>(() => {
  if (props.agents) return props.agents.filter(a => a.key !== props.agentKey).map(a => ({ key: a.key, name: a.name }))
  const cur = form.value?.canAsk
  return cur ? [{ key: cur, name: cur }] : []
})

const dirty = computed(() => form.value !== null && JSON.stringify(toUpdate(form.value)) !== original.value)

const modelsHint = computed(() => {
  switch (modelsState.value) {
    case 'idle': return 'Boş bırakılırsa sağlayıcı varsayılanı (claude-opus-5) kullanılır.'
    case 'loading': return 'Model listesi alınıyor…'
    case 'runtime-down': return 'Runtime kapalı, model adı elle yazılır.'
    case 'missing': return 'Model ucu hazır değil, model adı elle yazılır.'
    case 'error': return modelsError.value
  }
  const reachable = models.value.filter(m => m.reachable).length
  const cur = modelText.value.trim()
  const hit = cur ? models.value.find(m => m.model === cur) : undefined
  let s = `${models.value.length} model listelendi, ${reachable} tanesi erişilebilir.`
  if (cur && !hit) s += ' Yazılan model listede yok.'
  else if (hit && !hit.reachable) s += ' Bu model katalogda var ama bu hesaptan erişilemiyor.'
  return s
})

// ------------------------------------------------------------------ kaydet / kapat

async function save() {
  if (!form.value || saving.value) return
  saving.value = true
  saveError.value = null
  savedAt.value = null
  try {
    const d = await api.put<AgentDetail>(`/api/v1/agents/${props.agentKey}`, toUpdate(form.value))
    apply(d)
    savedAt.value = Date.now()
    emit('saved', d)
  } catch (e) {
    saveError.value = errorText(e)
  } finally {
    saving.value = false
  }
}

// ------------------------------------------------------------------ Isler sekmesi (GET /agents/{key}/work)

/** Isler sekmesi onde (kullanici istegi): once ne yaptigi, istenirse ozellikleri. */
const tab = ref<'props' | 'work'>('work')
const work = ref<AgentRunWork[] | null>(null)
const workError = ref<string | null>(null)

async function loadWork() {
  try {
    work.value = await api.get<AgentRunWork[]>(`/api/v1/agents/${encodeURIComponent(props.agentKey)}/work?runs=30`)
    workError.value = null
  } catch (e) {
    workError.value = errorText(e)
  }
}
watch([tab, () => props.agentKey], ([t]) => { if (t === 'work') void loadWork() }, { immediate: true }) // Isler onde: acilista yuklenir
watch(() => props.agentKey, () => { work.value = null })

type WorkEntry =
  | { ts: string; kind: 'turn'; turn: Turn }
  | { ts: string; kind: 'message'; msg: RunMessage }
  | { ts: string; kind: 'phase'; phase: RunPhase }

/** Bir calismadaki isi zaman sirasina dizer: tur, not, faz. */
function timelineOf(w: AgentRunWork): WorkEntry[] {
  const out: WorkEntry[] = []
  for (const t of w.turns) out.push({ ts: t.ts, kind: 'turn', turn: t })
  for (const m of w.messages) out.push({ ts: m.ts, kind: 'message', msg: m })
  for (const p of w.phases) out.push({ ts: p.ts, kind: 'phase', phase: p })
  return out.sort((a, b) => a.ts.localeCompare(b.ts))
}

function fmtCost(v: number): string { return v ? `$${v.toFixed(4)}` : '$0' }
function fmtClock(s: string): string { return new Date(s).toLocaleTimeString('tr-TR', { hour: '2-digit', minute: '2-digit', second: '2-digit' }) }
function fmtWhen(s: string): string { return new Date(s).toLocaleString('tr-TR', { dateStyle: 'short', timeStyle: 'short' }) }

/** Kabuk kapatmadan / baska ajana gecmeden once sorar. Edit kaybi geri alinamaz. */
function canLeave(): boolean {
  return !dirty.value || window.confirm('Kaydedilmemiş değişiklikler var. Vazgeçilsin mi?')
}
defineExpose({ canLeave })
</script>

<template>
  <div class="wrap" @click.self="emit('close')">
    <section class="panel" role="dialog" aria-labelledby="agent-title">
      <header>
        <h2 id="agent-title">Ekip · {{ form?.name || sceneName }}</h2>
        <code class="key">{{ agentKey }}</code>
        <button class="x" type="button" aria-label="Kapat" @click="emit('close')">×</button>
      </header>

      <!-- Iki sekme (kullanici istegi): Ozellikler = md/ayarlar; Isler = calisma basina bu ajanin turlari, mesajlari, fazlari. -->
      <nav class="tabs" role="tablist">
        <button type="button" role="tab" :aria-selected="tab === 'props'" :class="{ on: tab === 'props' }" @click="tab = 'props'">Özellikler</button>
        <button type="button" role="tab" :aria-selected="tab === 'work'" :class="{ on: tab === 'work' }" @click="tab = 'work'">İşler <b v-if="work">{{ work.length }}</b></button>
      </nav>

      <section v-if="tab === 'work'" class="work">
        <div class="work-head">
          <span class="sub">Bu ajanın çalışma başına yaptıkları: her LLM turunun gönderilen metni ve çıktısı, o anda kullanılan sağlayıcı/model/efor, aldığı ve verdiği notlar, faz geçişleri. Kaynak <code>runs/*/conversations/{{ agentKey }}.jsonl</code>.</span>
          <button type="button" class="small" @click="loadWork">Yenile</button>
        </div>
        <p v-if="workError" class="err">{{ workError }}</p>
        <p v-else-if="work === null" class="msg">Yükleniyor…</p>
        <p v-else-if="!work.length" class="msg">Bu ajan henüz bir çalışmada yer almadı.</p>
        <details v-for="(w, i) in work" :key="w.run.id" class="job" :open="i === 0">
          <summary>
            <span class="status" :class="w.run.status">{{ RUN_STATUS_LABEL[w.run.status] }}</span>
            <strong>{{ w.run.label }}</strong>
            <span class="sub">{{ fmtWhen(w.run.startedAt) }} · {{ w.turns.length }} tur · {{ w.messages.length }} not · {{ fmtCost(w.turns.reduce((s, t) => s + (t.costUsd ?? 0), 0)) }}</span>
          </summary>
          <ol class="timeline">
            <li v-for="(e, i) in timelineOf(w)" :key="i" :class="e.kind">
              <template v-if="e.kind === 'turn'">
                <div class="entry-head">
                  <span class="when">{{ fmtClock(e.turn.ts) }}</span>
                  <span class="tag turn">LLM turu</span>
                  <span class="sub">{{ e.turn.provider }} · <code>{{ e.turn.model }}</code> · {{ e.turn.durationS.toFixed(1) }} s · {{ e.turn.inputTokens ?? '?' }}→{{ e.turn.outputTokens ?? '?' }} tk · {{ fmtCost(e.turn.costUsd ?? 0) }}<template v-if="e.turn.stage"> · {{ e.turn.stage }}</template><template v-if="e.turn.task"> · <code>{{ e.turn.task }}</code></template></span>
                </div>
                <details><summary>Gönderilen ({{ e.turn.promptChars }} kr)</summary><pre>{{ e.turn.prompt }}</pre></details>
                <details><summary>Çıktı ({{ e.turn.outputChars }} kr)</summary><pre>{{ e.turn.output }}</pre></details>
              </template>
              <template v-else-if="e.kind === 'message'">
                <div class="entry-head">
                  <span class="when">{{ fmtClock(e.msg.ts) }}</span>
                  <span class="tag" :class="e.msg.subject === 'error' ? 'error' : 'msg'">{{ e.msg.subject === 'error' ? 'hata' : e.msg.subject === 'handoff' ? 'devir' : e.msg.subject === 'retry' ? 'tekrar' : e.msg.subject === 'plan-revision' ? 'revize notu' : 'not' }}</span>
                  <span class="sub">{{ e.msg.from }} → {{ e.msg.to }}<template v-if="e.msg.task"> · <code>{{ e.msg.task }}</code></template></span>
                </div>
                <pre :class="{ errtext: e.msg.subject === 'error' }">{{ e.msg.body }}</pre>
              </template>
              <template v-else>
                <div class="entry-head">
                  <span class="when">{{ fmtClock(e.phase.ts) }}</span>
                  <span class="tag phase">faz</span>
                  <span class="sub"><code>{{ e.phase.task }}</code> · {{ e.phase.stageTitle }} · {{ e.phase.status }} · tur {{ e.phase.round }}</span>
                </div>
              </template>
            </li>
          </ol>
        </details>
      </section>

      <p v-else-if="phase === 'loading'" class="msg">Yükleniyor…</p>

      <div v-else-if="phase === 'missing'" class="msg empty">
        <strong>Api'de ajan uçları henüz hazır değil.</strong>
        <span><code>GET /api/v1/agents/{{ agentKey }}</code> yanıt vermedi. Api açılınca yeniden deneyin.</span>
        <button type="button" @click="load">Yeniden dene</button>
      </div>

      <div v-else-if="phase === 'outside'" class="msg empty">
        <strong>{{ sceneName }} iş akışında rol almaz.</strong>
        <span>Bu karakter yalnız sahnede yaşar (<code>config/scene.json</code>); <code>config/agents/</code> altında md dosyası yok, model ataması yapılmaz.</span>
      </div>

      <div v-else-if="phase === 'error'" class="msg empty">
        <strong>{{ loadError }}</strong>
        <button type="button" @click="load">Yeniden dene</button>
      </div>

      <form v-else-if="form" class="form" @submit.prevent="save">
        <div class="row">
          <div class="field">
            <label class="lbl" for="agent-name">Ad</label>
            <input id="agent-name" v-model="form.name" type="text" required>
          </div>
          <div class="field">
            <span class="lbl">Ofis rolleri</span>
            <div class="chips">
              <span v-for="r in form.officeRoles" :key="r" class="tag">{{ r }}</span>
              <span v-if="!form.officeRoles.length" class="sub">—</span>
            </div>
          </div>
        </div>

        <div class="field">
          <label class="lbl" for="agent-summary">Özet</label>
          <input id="agent-summary" v-model="form.summary" type="text">
        </div>

        <div class="row">
          <div class="field">
            <label class="lbl" for="agent-provider">Sağlayıcı</label>
            <select id="agent-provider" v-model="providerModel">
              <option value="">varsayılan</option>
              <option v-for="p in PROVIDERS" :key="p" :value="p">{{ PROVIDER_LABEL[p] }}</option>
            </select>
          </div>
          <div class="field">
            <label class="lbl" for="agent-ask">Sorabilir</label>
            <select id="agent-ask" v-model="canAskModel">
              <option value="">yok</option>
              <option v-for="a in askOptions" :key="a.key" :value="a.key">{{ a.name }}</option>
            </select>
          </div>
        </div>

        <div class="row model-row">
          <div class="field">
            <label class="lbl" for="agent-model">Model</label>
            <!-- Liste geldiyse acilir menu (kullanici karari: metin degil secim); runtime kapaliysa metin alani kalir. -->
            <select v-if="modelsState === 'ready' && models.length" id="agent-model" v-model="modelText">
              <option value="">varsayılan (claude-opus-5)</option>
              <option v-for="m in modelOptions" :key="m.model" :value="m.model">{{ m.model }}{{ m.reachable ? '' : ' · giriş gerekli' }}</option>
            </select>
            <input
              v-else
              id="agent-model"
              v-model="modelText"
              type="text"
              autocomplete="off"
              spellcheck="false"
              placeholder="varsayılan: claude-opus-5"
            >
          </div>
          <div class="field">
            <label class="lbl" for="agent-effort">Efor</label>
            <select id="agent-effort" v-model="effortModel">
              <option value="">varsayılan (yüksek)</option>
              <option v-for="e in EFFORTS" :key="e" :value="e">{{ EFFORT_LABEL[e] }}</option>
            </select>
          </div>
        </div>
        <span class="sub" :class="{ warn: modelsState === 'runtime-down' || modelsState === 'error' }">{{ modelsHint }}</span>

        <div class="field">
          <span class="lbl">Bilgi dosyaları</span>
          <div v-if="knowledge" class="checks">
            <label v-for="k in knowledge" :key="k.key" class="chk">
              <input v-model="form.includes" type="checkbox" :value="k.key">
              <span>{{ k.title }} <code>{{ k.key }}</code></span>
            </label>
            <span v-if="!knowledge.length" class="sub">config/knowledge boş.</span>
          </div>
          <div v-else class="chips">
            <span v-for="k in form.includes" :key="k" class="tag">{{ k }}</span>
            <span class="sub">Bilgi listesi alınamadı; seçim değiştirilemez.</span>
          </div>
        </div>

        <div class="field">
          <label class="lbl" for="agent-prompt">Sistem promptu</label>
          <textarea id="agent-prompt" v-model="form.prompt" class="prompt" spellcheck="false" />
        </div>

        <details class="composed">
          <summary>Modele giden metin <span class="sub">(gövde + bilgi dosyaları, salt okunur; kaydedince yenilenir)</span></summary>
          <pre>{{ form.composedPrompt }}</pre>
        </details>

        <div class="actions">
          <button class="primary" type="submit" :disabled="saving || !dirty">{{ saving ? 'Kaydediliyor…' : 'Kaydet' }}</button>
          <button type="button" class="ghost" :disabled="!dirty || saving" @click="load">Geri al</button>
          <span v-if="saveError" class="err" role="alert">{{ saveError }}</span>
          <span v-else-if="savedAt" class="ok">Kaydedildi.</span>
          <span v-else-if="dirty" class="sub">Kaydedilmemiş değişiklik var.</span>
        </div>
      </form>
    </section>
  </div>
</template>

<style scoped>
.wrap {
  position: absolute; inset: 0; background: rgba(10, 12, 18, 0.25);
  display: flex; justify-content: flex-end; align-items: flex-start; padding: 12px;
}
.panel {
  background: #ede9dc; color: #23283a; border: 6px solid #6b4a2b; border-radius: 6px;
  width: min(560px, 100%); max-height: 100%; overflow: auto; padding: 14px 16px;
  box-shadow: 0 20px 60px rgba(0,0,0,0.5);
  display: flex; flex-direction: column; gap: 12px;
}
.panel > header { display: flex; align-items: center; gap: 10px; }
h2 { margin: 0; font-size: 16px; letter-spacing: 0.04em; text-transform: uppercase; }

.tabs { display: flex; gap: 4px; border-bottom: 1px solid rgba(0,0,0,0.12); }
.tabs button {
  font: inherit; font-size: 12px; font-weight: 600; cursor: pointer; background: transparent; color: #4a5068;
  border: none; border-bottom: 2px solid transparent; border-radius: 0; padding: 6px 10px; margin-bottom: -1px;
}
.tabs button.on { color: #23283a; border-bottom-color: #23283a; }
.tabs button b { background: rgba(0,0,0,0.08); border-radius: 999px; padding: 0 6px; font-size: 10px; margin-left: 4px; }
.work { display: flex; flex-direction: column; gap: 8px; }
.work-head { display: flex; align-items: flex-start; gap: 10px; }
.work-head .sub { flex: 1; }
.small { padding: 3px 8px; font-size: 11px; }
.job { background: #fff; border: 1px solid #c9c3b3; border-radius: 6px; padding: 6px 10px; }
.job > summary { display: flex; align-items: center; gap: 8px; cursor: pointer; font-size: 13px; list-style: none; }
.job > summary strong { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.status { font-size: 10px; font-weight: 700; padding: 1px 7px; border-radius: 999px; background: rgba(0,0,0,0.08); text-transform: uppercase; letter-spacing: 0.04em; }
.status.awaitingApproval { background: #f3c34a; }
.status.running { background: #4fa3e0; color: #fff; }
.status.paused { background: #a889e6; color: #fff; }
.status.completed { background: #7cc46b; }
.status.failed, .status.policyRejected, .status.interrupted, .status.budgetExceeded, .status.cancelled { background: #d23b3b; color: #fff; }
.timeline { list-style: none; margin: 8px 0 0; padding: 0; display: flex; flex-direction: column; gap: 6px; }
.timeline li { border-left: 3px solid #c9c3b3; padding: 4px 8px; }
.timeline li.turn { border-left-color: #4f8ef7; }
.timeline li.message { border-left-color: #d9a13a; }
.timeline li.phase { border-left-color: #7cc46b; }
.entry-head { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; font-size: 12px; }
.when { font-variant-numeric: tabular-nums; color: #6b7285; font-size: 11px; }
.tag { font-size: 10px; font-weight: 700; text-transform: uppercase; letter-spacing: 0.04em; padding: 1px 6px; border-radius: 999px; background: rgba(0,0,0,0.08); }
.tag.turn { background: #dbe8fb; }
.tag.msg { background: #f6e6c2; }
.tag.error { background: #e05252; color: #fff; }
.tag.phase { background: #dff2d8; }
.timeline pre { margin: 4px 0 0; white-space: pre-wrap; font: 11px/1.45 Consolas, "Cascadia Mono", monospace; background: #f7f5ef; border: 1px solid #e0dbcc; padding: 6px; border-radius: 4px; max-height: 300px; overflow: auto; }
.timeline details summary { font-size: 11px; cursor: pointer; color: #4a5068; }
.errtext { border-color: #e0a0a0 !important; color: #7a1f1f; }
.key { font-size: 10px; background: rgba(0,0,0,0.06); padding: 1px 6px; border-radius: 3px; }
.x { background: none; border: none; font-size: 22px; cursor: pointer; color: #23283a; line-height: 1; margin-left: auto; padding: 0 4px; }

.msg { margin: 0; font-size: 13px; color: #4a5068; }
.empty { display: flex; flex-direction: column; gap: 8px; padding: 18px 14px; background: rgba(0,0,0,0.04); border-radius: 6px; }
.empty span { font-size: 12px; color: #6b7285; line-height: 1.5; }
.empty button { align-self: flex-start; }

.form { display: flex; flex-direction: column; gap: 12px; }
.row { display: grid; grid-template-columns: 1fr 1fr; gap: 10px; }
.model-row { grid-template-columns: 2fr 1fr; }
.field { display: flex; flex-direction: column; gap: 4px; min-width: 0; }
.lbl { font-size: 11px; font-weight: 700; text-transform: uppercase; letter-spacing: 0.04em; color: #4a5068; }
input[type="text"], select, textarea {
  font: inherit; font-size: 13px; background: #fff; color: #23283a;
  border: 1px solid #c9c3b3; border-radius: 4px; padding: 6px 8px; width: 100%;
}
input:focus, select:focus, textarea:focus { outline: 2px solid #4f8ef7; outline-offset: 0; }
.prompt { min-height: 280px; resize: vertical; font-family: Consolas, "Cascadia Mono", monospace; font-size: 12px; line-height: 1.45; }

.chips { display: flex; flex-wrap: wrap; gap: 4px; align-items: center; min-height: 30px; }
.tag { background: rgba(0,0,0,0.06); border-radius: 999px; padding: 2px 8px; font-size: 11px; }
.checks { display: grid; gap: 4px; }
.chk { display: flex; gap: 6px; align-items: baseline; font-size: 13px; cursor: pointer; }
.chk code { font-size: 10px; color: #6b7285; }
.sub { font-size: 11px; color: #6b7285; line-height: 1.4; }
.warn { color: #9a4b1c; }

.composed summary { cursor: pointer; font-size: 12px; font-weight: 600; color: #4a5068; }
.composed pre {
  margin: 6px 0 0; white-space: pre-wrap; font-size: 11px; line-height: 1.45;
  background: #fff; border: 1px solid #c9c3b3; padding: 8px; border-radius: 4px; max-height: 320px; overflow: auto;
}

.actions {
  display: flex; align-items: center; gap: 10px; flex-wrap: wrap;
  position: sticky; bottom: -16px; background: #ede9dc; padding: 10px 0 12px; border-top: 1px solid rgba(0,0,0,0.08);
}
button { font: inherit; cursor: pointer; border-radius: 4px; padding: 7px 14px; border: 1px solid #c9c3b3; background: #fff; color: #23283a; }
button:disabled { opacity: 0.5; cursor: default; }
.primary { background: #23283a; color: #fff; border-color: #23283a; }
.ghost { background: transparent; }
.err { color: #b3261e; font-size: 12px; }
.ok { color: #2f7a4a; font-size: 12px; }
code { font-size: 10px; background: rgba(0,0,0,0.06); padding: 1px 4px; border-radius: 3px; }
</style>
