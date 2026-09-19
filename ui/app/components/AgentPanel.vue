<script setup lang="ts">
import type { AgentDetail, AgentListItem, AgentUpdate, KnowledgeItem, ModelInfo, Provider } from '~/api/types'
import { isApiError, useApiClient } from '~/api/client'
import { errorText } from '~/api/errors'
import { PROVIDERS, PROVIDER_LABEL } from '~/api/labels'

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
  void loadModels(detail.provider)
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
    if (form.value?.provider !== p) return
    modelCache.set(p, list)
    models.value = list
    modelsState.value = 'ready'
  } catch (e) {
    if (form.value?.provider !== p) return
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
    void loadModels(form.value.provider)
  },
})

const modelText = computed<string>({
  get: () => form.value?.model ?? '',
  set: (v) => { if (form.value) form.value.model = v || null },
})

const canAskModel = computed<string>({
  get: () => form.value?.canAsk ?? '',
  set: (v) => { if (form.value) form.value.canAsk = v || null },
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
    case 'idle': return 'Sağlayıcı seçilmedi; öneri listesi için sağlayıcı seçin. Boş bırakılırsa sağlayıcı varsayılanı kullanılır.'
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
        <h2 id="agent-title">Ajan · {{ form?.name || sceneName }}</h2>
        <code class="key">{{ agentKey }}</code>
        <button class="x" type="button" aria-label="Kapat" @click="emit('close')">×</button>
      </header>

      <p v-if="phase === 'loading'" class="msg">Yükleniyor…</p>

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

        <div class="field">
          <label class="lbl" for="agent-model">Model</label>
          <input
            id="agent-model"
            v-model="modelText"
            type="text"
            list="agent-model-options"
            autocomplete="off"
            spellcheck="false"
            placeholder="sağlayıcı varsayılanı"
          >
          <datalist id="agent-model-options">
            <option v-for="m in models" :key="m.model" :value="m.model">{{ m.reachable ? 'erişilebilir' : 'katalogda, erişim doğrulanmadı' }}</option>
          </datalist>
          <span class="sub" :class="{ warn: modelsState === 'runtime-down' || modelsState === 'error' }">{{ modelsHint }}</span>
        </div>

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
  position: absolute; inset: 0; background: rgba(10, 12, 18, 0.55);
  display: flex; justify-content: flex-end;
}
.panel {
  background: #ede9dc; color: #23283a; border-left: 6px solid #6b4a2b;
  width: min(560px, 100%); height: 100%; overflow: auto; padding: 16px 18px;
  box-shadow: -20px 0 60px rgba(0,0,0,0.5);
  display: flex; flex-direction: column; gap: 12px;
}
.panel > header { display: flex; align-items: center; gap: 10px; }
h2 { margin: 0; font-size: 16px; letter-spacing: 0.04em; text-transform: uppercase; }
.key { font-size: 10px; background: rgba(0,0,0,0.06); padding: 1px 6px; border-radius: 3px; }
.x { background: none; border: none; font-size: 22px; cursor: pointer; color: #23283a; line-height: 1; margin-left: auto; padding: 0 4px; }

.msg { margin: 0; font-size: 13px; color: #4a5068; }
.empty { display: flex; flex-direction: column; gap: 8px; padding: 18px 14px; background: rgba(0,0,0,0.04); border-radius: 6px; }
.empty span { font-size: 12px; color: #6b7285; line-height: 1.5; }
.empty button { align-self: flex-start; }

.form { display: flex; flex-direction: column; gap: 12px; }
.row { display: grid; grid-template-columns: 1fr 1fr; gap: 10px; }
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
