<script setup lang="ts">
import type { AgentListItem, Effort, KnowledgeItem, Provider, WorkflowDetail, WorkflowListItem } from '~/api/types'
import { useApiClient } from '~/api/client'
import { errorText } from '~/api/errors'
import { EFFORTS, EFFORT_LABEL, OFFICE_ROLES, OFFICE_ROLE_LABEL, PROVIDERS, PROVIDER_LABEL, STAGE_KINDS, STAGE_KIND_LABEL } from '~/api/labels'

/**
 * Ekip yonetimi (kullanici karari 2026-09-20). Iki sekme:
 *  - Ajanlar: havuz (config/agents/*.md). Yeni ajan eklenir (POST /agents; sahnede bos sprite + bos masa, yoksa ziyaretci),
 *    silinir (akista ya da can_ask'ta kullaniliyorsa 409), tiklaninca ayrinti paneli (Ekip · ad) acilir.
 *  - Takimlar = is akislari (config/workflows/*.json): "takim kurmak" bir akis kurmaktir — adimlar sirali, her adima bir ajan.
 *    Proje bir akis secer. Duzenleyici PUT /workflows/{key} ile tum akisi yazar; default silinemez.
 */
const props = defineProps<{ agents: AgentListItem[] | null }>()
const emit = defineEmits<{ close: []; select: [key: string]; changed: [] }>()
const api = useApiClient()

const tab = ref<'agents' | 'teams' | 'knowledge'>('agents')

/** Dosya secicisinden metin oku (UTF-8). Yalniz .md; icerik Api'ye metin olarak gider, dosya Api'de yazilir. */
function readText(ev: Event): Promise<{ name: string; text: string } | null> {
  const input = ev.target as HTMLInputElement
  const f = input.files?.[0]
  input.value = ''
  if (!f) return Promise.resolve(null)
  return f.text().then(text => ({ name: f.name.replace(/\.md$/i, ''), text }))
}

// ------------------------------------------------------------------ ajanlar

const knowledge = ref<KnowledgeItem[]>([])
async function loadKnowledge() { try { knowledge.value = await api.get<KnowledgeItem[]>('/api/v1/knowledge') } catch { knowledge.value = [] } }

const adding = ref(false)
const nKey = ref(''); const nName = ref(''); const nSummary = ref(''); const nRoles = ref<string[]>(['dev'])
const nProvider = ref<Provider | ''>(''); const nModel = ref(''); const nEffort = ref<Effort | ''>(''); const nCanAsk = ref(''); const nIncludes = ref<string[]>([])
const nPrompt = ref('')
/** Ofisteki karakter; null = otomatik (kullanilmayan ilk karakter). */
const nSprite = ref<string | null>(null)
const spriteUsers = computed<Record<string, string[]>>(() => {
  const map: Record<string, string[]> = {}
  for (const a of props.agents ?? []) if (a.sprite) (map[a.sprite] ??= []).push(a.name)
  return map
})
const addBusy = ref(false)
const addError = ref<string | null>(null)
function slug(s: string): string { return s.toLowerCase().replace(/ğ/g, 'g').replace(/ü/g, 'u').replace(/ş/g, 's').replace(/ı/g, 'i').replace(/ö/g, 'o').replace(/ç/g, 'c').replace(/[^a-z0-9_-]+/g, '-').replace(/^-+|-+$/g, '') }
watch(nName, (v) => { if (!nKey.value || nKey.value === slug(nNamePrev)) nKey.value = slug(v); nNamePrev = v })
let nNamePrev = ''
function toggleIn(list: string[], v: string): string[] { return list.includes(v) ? list.filter(x => x !== v) : [...list, v] }

async function addAgent() {
  if (addBusy.value) return
  addBusy.value = true
  addError.value = null
  try {
    await api.post('/api/v1/agents', {
      key: nKey.value.trim(), name: nName.value.trim(), summary: nSummary.value.trim(), officeRoles: nRoles.value,
      provider: nProvider.value || null, model: nModel.value.trim() || null, effort: nEffort.value || null,
      includes: nIncludes.value, canAsk: nCanAsk.value || null, prompt: nPrompt.value.trim(), sprite: nSprite.value,
    })
    adding.value = false
    nKey.value = ''; nName.value = ''; nSummary.value = ''; nRoles.value = ['dev']; nProvider.value = ''; nModel.value = ''; nEffort.value = ''; nCanAsk.value = ''; nIncludes.value = []; nPrompt.value = ''; nSprite.value = null
    emit('changed')
  } catch (e) {
    addError.value = errorText(e)
  } finally {
    addBusy.value = false
  }
}

// Hazir ajan md'si yukle: frontmatter (name, summary, office_roles, provider, model, effort, includes, can_ask) + prompt govdesi.
const importing = ref<{ key: string; markdown: string; name: string } | null>(null)
const importBusy = ref(false)
const importError = ref<string | null>(null)
async function pickAgentMd(ev: Event) {
  const f = await readText(ev)
  if (!f) return
  importError.value = null
  importing.value = { key: slug(f.name), markdown: f.text, name: f.name }
}
async function importAgent() {
  if (!importing.value || importBusy.value) return
  importBusy.value = true
  importError.value = null
  try {
    await api.post('/api/v1/agents/import', { key: importing.value.key.trim(), markdown: importing.value.markdown })
    importing.value = null
    emit('changed')
  } catch (e) {
    importError.value = errorText(e)
  } finally {
    importBusy.value = false
  }
}

const delError = ref<string | null>(null)
async function removeAgent(a: AgentListItem) {
  if (!window.confirm(`"${a.name}" ekipten silinsin mi? md dosyası silinir; bir akışta kullanılıyorsa reddedilir.`)) return
  delError.value = null
  try {
    await api.del(`/api/v1/agents/${encodeURIComponent(a.key)}`)
    emit('changed')
  } catch (e) {
    delError.value = errorText(e)
  }
}

// ------------------------------------------------------------------ bilgi dosyalari (config/knowledge/*.md)

const kEdit = ref<{ key: string; title: string; body: string; isNew: boolean } | null>(null)
const kBusy = ref(false)
const kError = ref<string | null>(null)
const kSaved = ref(false)
const usedBy = computed(() => {
  const m: Record<string, string[]> = {}
  for (const a of props.agents ?? []) for (const k of a.includes) (m[k] ??= []).push(a.name)
  return m
})
function openKnowledge(k: KnowledgeItem) { kError.value = null; kEdit.value = { key: k.key, title: k.title, body: k.body, isNew: false } }
function newKnowledge() { kError.value = null; kEdit.value = { key: '', title: '', body: '', isNew: true } }
async function pickKnowledgeMd(ev: Event) {
  const f = await readText(ev)
  if (!f) return
  kError.value = null
  kBusy.value = true
  try {
    await api.post('/api/v1/knowledge/import', { key: slug(f.name), markdown: f.text })
    await loadKnowledge()
    const item = knowledge.value.find(k => k.key === slug(f.name))
    if (item) openKnowledge(item)
    emit('changed')
  } catch (e) {
    kError.value = errorText(e)
  } finally {
    kBusy.value = false
  }
}
async function saveKnowledge() {
  const k = kEdit.value
  if (!k || kBusy.value) return
  kBusy.value = true
  kError.value = null
  kSaved.value = false
  try {
    const key = k.isNew ? slug(k.key || k.title) : k.key
    await api.put(`/api/v1/knowledge/${encodeURIComponent(key)}`, { title: k.title.trim() || null, body: k.body })
    await loadKnowledge()
    kEdit.value = { ...k, key, isNew: false }
    kSaved.value = true
    setTimeout(() => { kSaved.value = false }, 2500)
    emit('changed')
  } catch (e) {
    kError.value = errorText(e)
  } finally {
    kBusy.value = false
  }
}
async function deleteKnowledge() {
  const k = kEdit.value
  if (!k || k.isNew || !window.confirm(`"${k.title}" bilgi dosyası silinsin mi? Bir ajanın listesindeyse reddedilir.`)) return
  kError.value = null
  try {
    await api.del(`/api/v1/knowledge/${encodeURIComponent(k.key)}`)
    kEdit.value = null
    await loadKnowledge()
    emit('changed')
  } catch (e) {
    kError.value = errorText(e)
  }
}

// ------------------------------------------------------------------ takimlar = is akislari

const workflows = ref<WorkflowListItem[] | null>(null)
const wfError = ref<string | null>(null)
async function loadWorkflows() {
  try { workflows.value = await api.get<WorkflowListItem[]>('/api/v1/workflows'); wfError.value = null } catch (e) { wfError.value = errorText(e) }
}

type StageRow = { id: string; title: string; kind: string; role: string; officeRole: string; description: string }
/** Ajan degil kullanici: sunucudaki Workflow.UserRole ile ayni deger. */
const USER_ROLE = 'user'
/** Onay kapisi yok: sunucudaki Workflow.AutoApprove. */
const AUTO_APPROVE = 'auto'
const editKey = ref<string | null>(null)
const editing = ref<{ key: string; title: string; maxReviewRounds: number; handoffRole: string; askRole: string; planApprover: string; stages: StageRow[]; isNew: boolean } | null>(null)
const wfSaving = ref(false)
const wfSaveError = ref<string | null>(null)
const wfSaved = ref(false)

const agentKeys = computed(() => (props.agents ?? []).map(a => a.key))
function agentName(key: string): string { return props.agents?.find(a => a.key === key)?.name ?? key }
/** Adim turune gore mantikli ofis rolu; kullanici degistirebilir. */
const KIND_ROLE: Record<string, string> = { analyze: 'pm', design: 'designer', implement: 'dev', review: 'qa', handoff: 'ops' }

async function openWorkflow(key: string) {
  wfSaveError.value = null
  try {
    const d = await api.get<WorkflowDetail>(`/api/v1/workflows/${encodeURIComponent(key)}`)
    editKey.value = key
    editing.value = {
      key: d.key, title: d.title, maxReviewRounds: d.maxReviewRounds, handoffRole: d.handoffRole ?? '',
      // askRole bos = "ajanin kendi can_ask'i"; planApprover bos = kullanici (varsayilan).
      askRole: d.askRole ?? '', planApprover: d.planApprover ?? USER_ROLE,
      stages: d.stages.map(s => ({ ...s })), isNew: false,
    }
  } catch (e) {
    wfError.value = errorText(e)
  }
}
function newWorkflow() {
  const analyst = agentKeys.value.includes('analyst') ? 'analyst' : (agentKeys.value[0] ?? '')
  const dev = agentKeys.value.includes('developer') ? 'developer' : (agentKeys.value[0] ?? '')
  editKey.value = ''
  editing.value = {
    key: '', title: '', maxReviewRounds: 3, handoffRole: agentKeys.value.includes('organizer') ? 'organizer' : '',
    askRole: '', planApprover: USER_ROLE, isNew: true,
    stages: [
      { id: 'analiz', title: 'Analiz', kind: 'analyze', role: analyst, officeRole: 'pm', description: 'Brief çözümlenir, kurallar ve görev grafiği çıkarılır.' },
      { id: 'gelistirme', title: 'Geliştirme', kind: 'implement', role: dev, officeRole: 'dev', description: 'Görev kodlanır, dosyalar çalışma dizinine yazılır.' },
    ],
  }
}
function addStage() {
  if (!editing.value) return
  const n = editing.value.stages.length + 1
  editing.value.stages.push({ id: `adim-${n}`, title: `Adım ${n}`, kind: 'review', role: agentKeys.value.includes('tester') ? 'tester' : (agentKeys.value[0] ?? ''), officeRole: 'qa', description: '' })
}
function removeStage(i: number) { editing.value?.stages.splice(i, 1) }
function moveStage(i: number, d: -1 | 1) {
  const s = editing.value?.stages
  if (!s) return
  const j = i + d
  if (j < 0 || j >= s.length) return
  ;[s[i], s[j]] = [s[j]!, s[i]!]
}
function onKindChange(row: StageRow) { row.officeRole = KIND_ROLE[row.kind] ?? row.officeRole }
/** Ilk adim degistirilemez: analiz bir kez ve basta (Domain kurali). */
function stageLocked(i: number): boolean { return i === 0 }

async function saveWorkflow() {
  const w = editing.value
  if (!w || wfSaving.value) return
  wfSaving.value = true
  wfSaveError.value = null
  wfSaved.value = false
  try {
    const key = w.isNew ? slug(w.key || w.title) : w.key
    await api.put(`/api/v1/workflows/${encodeURIComponent(key)}`, {
      title: w.title.trim(), maxReviewRounds: Number(w.maxReviewRounds) || 1, handoffRole: w.handoffRole || null,
      askRole: w.askRole || null, planApprover: w.planApprover || null,
      stages: w.stages.map(s => ({ id: s.id.trim(), title: s.title.trim(), kind: s.kind, role: s.role, officeRole: s.officeRole, description: s.description })),
    })
    wfSaved.value = true
    setTimeout(() => { wfSaved.value = false }, 2500)
    await loadWorkflows()
    if (w.isNew) await openWorkflow(key)
    emit('changed')
  } catch (e) {
    wfSaveError.value = errorText(e)
  } finally {
    wfSaving.value = false
  }
}
async function deleteWorkflow() {
  const w = editing.value
  if (!w || w.isNew || !window.confirm(`"${w.title}" takımı (akışı) silinsin mi? Bu akışı kullanan projeler default'a düşer.`)) return
  wfSaveError.value = null
  try {
    await api.del(`/api/v1/workflows/${encodeURIComponent(w.key)}`)
    editing.value = null; editKey.value = null
    await loadWorkflows()
    emit('changed')
  } catch (e) {
    wfSaveError.value = errorText(e)
  }
}

onMounted(() => { void loadKnowledge(); void loadWorkflows() })
</script>

<template>
  <div class="wrap" @click.self="emit('close')">
    <section class="panel" role="dialog" aria-labelledby="team-title">
      <header>
        <h2 id="team-title">Ekip yönetimi</h2>
        <nav class="tabs" role="tablist">
          <button type="button" role="tab" :class="{ on: tab === 'agents' }" :aria-selected="tab === 'agents'" @click="tab = 'agents'">Ajanlar <b v-if="agents">{{ agents.length }}</b></button>
          <button type="button" role="tab" :class="{ on: tab === 'teams' }" :aria-selected="tab === 'teams'" @click="tab = 'teams'">Takımlar <b v-if="workflows">{{ workflows.length }}</b></button>
          <button type="button" role="tab" :class="{ on: tab === 'knowledge' }" :aria-selected="tab === 'knowledge'" @click="tab = 'knowledge'">Bilgi dosyaları <b>{{ knowledge.length }}</b></button>
        </nav>
        <button class="x" type="button" aria-label="Kapat" @click="emit('close')">×</button>
      </header>

      <!-- ------------------------------------------------------------ ajanlar -->
      <template v-if="tab === 'agents'">
        <p class="sub">Ajan havuzu (<code>config/agents/</code>). Yeni ajan sahnede boş bir karakter ve boş masa alır; masa kalmadıysa ziyaretçi olur, arada panoya bakmaya gelir. Ajana tıklayınca özellikleri ve işleri açılır.</p>
        <p v-if="delError" class="err" role="alert">{{ delError }}</p>
        <ul class="agents">
          <li v-for="a in agents ?? []" :key="a.key">
            <button type="button" class="agent" @click="emit('select', a.key)">
              <strong>{{ a.name }}</strong> <code>{{ a.key }}</code>
              <span class="sub">{{ a.summary }}</span>
              <span class="meta">{{ a.officeRoles.map(r => OFFICE_ROLE_LABEL[r] ?? r).join(' · ') }} · {{ a.provider ? PROVIDER_LABEL[a.provider] : 'varsayılan sağlayıcı' }} · {{ a.model ?? 'varsayılan model' }} · {{ a.effort ? EFFORT_LABEL[a.effort] : 'varsayılan efor' }}</span>
            </button>
            <button type="button" class="del" title="Ekipten sil" aria-label="Sil" @click="removeAgent(a)">×</button>
          </li>
        </ul>

        <div v-if="!adding && !importing" class="actions">
          <button type="button" class="primary" @click="adding = true">+ Yeni ajan</button>
          <label class="upload"><input type="file" accept=".md,text/markdown" @change="pickAgentMd"> md yükle</label>
          <span class="sub">Hazır ajan md'si: <code>---</code> frontmatter (name, summary, office_roles, provider, model, effort, includes, can_ask) + sistem promptu gövdesi. Dosya adı anahtar olur.</span>
        </div>
        <form v-if="importing" class="form" @submit.prevent="importAgent">
          <h3>md'den ajan ekle · <code>{{ importing.name }}.md</code></h3>
          <div class="field"><label class="lbl" for="i-key">Anahtar</label><input id="i-key" v-model="importing.key" type="text" required pattern="[a-z0-9][a-z0-9_\-]*"></div>
          <div class="field"><label class="lbl" for="i-md">İçerik <span class="sub">(düzenleyebilirsin)</span></label><textarea id="i-md" v-model="importing.markdown" rows="12" spellcheck="false" /></div>
          <div class="actions">
            <button class="primary" type="submit" :disabled="importBusy">{{ importBusy ? 'Ekleniyor…' : 'Ekibe ekle' }}</button>
            <button type="button" class="ghost" :disabled="importBusy" @click="importing = null">Vazgeç</button>
            <span v-if="importError" class="err" role="alert">{{ importError }}</span>
          </div>
        </form>
        <form v-if="adding" class="form" @submit.prevent="addAgent">
          <h3>Yeni ajan</h3>
          <div class="row">
            <div class="field"><label class="lbl" for="n-name">Ad</label><input id="n-name" v-model="nName" type="text" required placeholder="Örn. Veri Mühendisi"></div>
            <div class="field"><label class="lbl" for="n-key">Anahtar</label><input id="n-key" v-model="nKey" type="text" required pattern="[a-z0-9][a-z0-9_\-]*" placeholder="veri-muhendisi"></div>
          </div>
          <div class="field">
            <span class="lbl">Karakter <span class="sub">(ofisteki görünümü)</span></span>
            <SpritePicker v-model="nSprite" :used-by="spriteUsers" allow-auto />
          </div>
          <div class="field"><label class="lbl" for="n-sum">Özet</label><input id="n-sum" v-model="nSummary" type="text" required placeholder="Ne yapar, tek cümle"></div>
          <div class="field">
            <span class="lbl">Ofis rolleri</span>
            <div class="chips"><label v-for="r in OFFICE_ROLES" :key="r" class="chk"><input type="checkbox" :checked="nRoles.includes(r)" @change="nRoles = toggleIn(nRoles, r)"> {{ OFFICE_ROLE_LABEL[r] }}</label></div>
          </div>
          <div class="row three">
            <div class="field"><label class="lbl" for="n-prov">Sağlayıcı</label><select id="n-prov" v-model="nProvider"><option value="">varsayılan</option><option v-for="p in PROVIDERS" :key="p" :value="p">{{ PROVIDER_LABEL[p] }}</option></select></div>
            <div class="field"><label class="lbl" for="n-model">Model</label><input id="n-model" v-model="nModel" type="text" placeholder="varsayılan (claude-opus-5)"></div>
            <div class="field"><label class="lbl" for="n-effort">Efor</label><select id="n-effort" v-model="nEffort"><option value="">varsayılan</option><option v-for="e in EFFORTS" :key="e" :value="e">{{ EFFORT_LABEL[e] }}</option></select></div>
          </div>
          <div class="row">
            <div class="field"><label class="lbl" for="n-ask">Sorabilir</label><select id="n-ask" v-model="nCanAsk"><option value="">kimseye</option><option v-for="k in agentKeys" :key="k" :value="k">{{ agentName(k) }}</option></select></div>
            <div class="field">
              <span class="lbl">Bilgi dosyaları</span>
              <div class="chips"><label v-for="k in knowledge" :key="k.key" class="chk"><input type="checkbox" :checked="nIncludes.includes(k.key)" @change="nIncludes = toggleIn(nIncludes, k.key)"> {{ k.title }}</label></div>
            </div>
          </div>
          <div class="field"><label class="lbl" for="n-prompt">Sistem promptu</label><textarea id="n-prompt" v-model="nPrompt" rows="6" required placeholder="Sen bir yazılım üretim ofisinin … rolüsün. …" /></div>
          <div class="actions">
            <button class="primary" type="submit" :disabled="addBusy">{{ addBusy ? 'Ekleniyor…' : 'Ekle' }}</button>
            <button type="button" class="ghost" :disabled="addBusy" @click="adding = false">Vazgeç</button>
            <span v-if="addError" class="err" role="alert">{{ addError }}</span>
          </div>
        </form>
      </template>

      <!-- ------------------------------------------------------------ bilgi dosyalari -->
      <template v-else-if="tab === 'knowledge'">
        <p class="sub">Alt md'ler (<code>config/knowledge/</code>): ajanın <em>bilgi dosyaları</em> listesine eklenince sistem promptunun sonuna gider. Burada oluştur, düzenle, md yükle ya da sil; bir ajanın listesindeyken silinemez.</p>
        <p v-if="kError" class="err" role="alert">{{ kError }}</p>
        <div class="teams">
          <ul class="teamlist">
            <li v-for="k in knowledge" :key="k.key">
              <button type="button" class="team" :class="{ on: kEdit?.key === k.key }" @click="openKnowledge(k)">
                <strong>{{ k.title }}</strong> <code>{{ k.key }}</code>
                <span class="sub">{{ k.body.length }} kr<template v-if="usedBy[k.key]?.length"> · {{ usedBy[k.key]!.join(', ') }}</template><template v-else> · kullanan yok</template></span>
              </button>
            </li>
            <li class="actions">
              <button type="button" class="primary small" @click="newKnowledge">+ Yeni</button>
              <label class="upload small"><input type="file" accept=".md,text/markdown" :disabled="kBusy" @change="pickKnowledgeMd"> md yükle</label>
            </li>
          </ul>

          <form v-if="kEdit" class="form editor" @submit.prevent="saveKnowledge">
            <div class="row">
              <div class="field"><label class="lbl" for="k-title">Başlık</label><input id="k-title" v-model="kEdit.title" type="text" placeholder="boşsa anahtar"></div>
              <div v-if="kEdit.isNew" class="field"><label class="lbl" for="k-key">Anahtar</label><input id="k-key" v-model="kEdit.key" type="text" pattern="[a-z0-9][a-z0-9_\-]*" placeholder="boşsa başlıktan üretilir"></div>
              <div v-else class="field"><span class="lbl">Anahtar</span><code class="keyval">{{ kEdit.key }}</code></div>
            </div>
            <div class="field"><label class="lbl" for="k-body">Gövde (Markdown)</label><textarea id="k-body" v-model="kEdit.body" rows="16" required spellcheck="false" /></div>
            <div class="actions">
              <button class="primary" type="submit" :disabled="kBusy">{{ kBusy ? 'Kaydediliyor…' : kSaved ? 'Kaydedildi ✓' : (kEdit.isNew ? 'Oluştur' : 'Kaydet') }}</button>
              <button v-if="!kEdit.isNew" type="button" class="danger" :disabled="kBusy" @click="deleteKnowledge">Sil</button>
              <button type="button" class="ghost" @click="kEdit = null">Kapat</button>
            </div>
          </form>
          <p v-else class="sub empty">Soldan bir dosya seç, yeni oluştur ya da md yükle.</p>
        </div>
      </template>

      <!-- ------------------------------------------------------------ takimlar -->
      <template v-else>
        <p class="sub">Takım = iş akışı: analist → tasarımcı → developer → testçi… gibi sıralı adımlar, her adımda bir ajan. Proje ayarlarından takım seçilir; iş o takımla koşar. İlk adım her zaman analizdir (plan onayı).</p>
        <p v-if="wfError" class="err" role="alert">{{ wfError }}</p>
        <div class="teams">
          <ul class="teamlist">
            <li v-for="w in workflows ?? []" :key="w.key">
              <button type="button" class="team" :class="{ on: editKey === w.key }" @click="openWorkflow(w.key)">
                <strong>{{ w.title }}</strong> <code>{{ w.key }}</code><span v-if="w.isDefault" class="tag">varsayılan</span>
                <span class="sub">{{ w.roles.map(agentName).join(' → ') }}</span>
              </button>
            </li>
            <li><button type="button" class="primary small" @click="newWorkflow">+ Yeni takım</button></li>
          </ul>

          <form v-if="editing" class="form editor" @submit.prevent="saveWorkflow">
            <div class="row">
              <div class="field"><label class="lbl" for="w-title">Takım adı</label><input id="w-title" v-model="editing.title" type="text" required placeholder="Örn. Tasarımlı"></div>
              <div v-if="editing.isNew" class="field"><label class="lbl" for="w-key">Anahtar</label><input id="w-key" v-model="editing.key" type="text" pattern="[a-z0-9][a-z0-9_\-]*" placeholder="boşsa addan üretilir"></div>
              <div v-else class="field"><span class="lbl">Anahtar</span><code class="keyval">{{ editing.key }}</code></div>
            </div>
            <div class="row">
              <div class="field"><label class="lbl" for="w-rounds">En fazla inceleme turu</label><input id="w-rounds" v-model.number="editing.maxReviewRounds" type="number" min="1" max="10"></div>
              <div class="field"><label class="lbl" for="w-handoff">Devir notu yazan</label><select id="w-handoff" v-model="editing.handoffRole"><option value="">yok</option><option v-for="k in agentKeys" :key="k" :value="k">{{ agentName(k) }}</option></select></div>
            </div>
            <div class="row">
              <div class="field">
                <label class="lbl" for="w-approver">Planı onaylayan</label>
                <select id="w-approver" v-model="editing.planApprover">
                  <option :value="AUTO_APPROVE">Onay yok — doğrudan dağıtım</option>
                  <option :value="USER_ROLE">Kullanıcı (sen)</option>
                  <option v-for="k in agentKeys" :key="k" :value="k">{{ agentName(k) }}</option>
                </select>
                <span class="hint">Onay yok: plan üretilir üretilmez işe başlanır. Ajan seçersen o onaylar; reddederse analist yeniden çalışır.</span>
              </div>
              <div class="field">
                <label class="lbl" for="w-ask">Soruları cevaplayan</label>
                <select id="w-ask" v-model="editing.askRole">
                  <option value="">ajanın kendi hedefi (can_ask)</option>
                  <option :value="USER_ROLE">Kullanıcı (sen)</option>
                  <option v-for="k in agentKeys" :key="k" :value="k">{{ agentName(k) }}</option>
                </select>
                <span class="hint">Takılan ajanın sorusu buraya gider. Bu akışta olmayan bir ajanı seçme.</span>
              </div>
            </div>

            <div class="stages">
              <div class="stage-h"><span>#</span><span>Adım</span><span>Tür</span><span>Ajan</span><span>Ofis rolü</span><span /></div>
              <div v-for="(s, i) in editing.stages" :key="i" class="stage" :class="{ locked: stageLocked(i) }">
                <span class="num">{{ i + 1 }}</span>
                <span class="cell"><input v-model="s.title" type="text" required placeholder="Başlık" :title="`kimlik: ${s.id}`"><input v-model="s.id" type="text" required pattern="[a-z0-9][a-z0-9_\-]*" class="idin" placeholder="kimlik"></span>
                <select v-model="s.kind" :disabled="stageLocked(i)" @change="onKindChange(s)"><option v-for="k in STAGE_KINDS" :key="k" :value="k">{{ STAGE_KIND_LABEL[k] }}</option></select>
                <select v-model="s.role"><option v-for="k in agentKeys" :key="k" :value="k">{{ agentName(k) }}</option></select>
                <select v-model="s.officeRole"><option v-for="r in OFFICE_ROLES" :key="r" :value="r">{{ OFFICE_ROLE_LABEL[r] }}</option></select>
                <span class="ops">
                  <button type="button" class="small" :disabled="i <= 1" title="Yukarı" @click="moveStage(i, -1)">▲</button>
                  <button type="button" class="small" :disabled="stageLocked(i) || i >= editing.stages.length - 1" title="Aşağı" @click="moveStage(i, 1)">▼</button>
                  <button type="button" class="small del" :disabled="stageLocked(i)" title="Adımı sil" @click="removeStage(i)">×</button>
                </span>
                <input v-model="s.description" type="text" class="desc" placeholder="Açıklama (ajana gider)">
              </div>
              <button type="button" class="small" @click="addStage">+ Adım ekle</button>
            </div>

            <div class="actions">
              <button class="primary" type="submit" :disabled="wfSaving">{{ wfSaving ? 'Kaydediliyor…' : wfSaved ? 'Kaydedildi ✓' : (editing.isNew ? 'Takımı kur' : 'Kaydet') }}</button>
              <button v-if="!editing.isNew && editing.key !== 'default'" type="button" class="danger" @click="deleteWorkflow">Takımı sil</button>
              <button type="button" class="ghost" @click="editing = null; editKey = null">Kapat</button>
              <span v-if="wfSaveError" class="err" role="alert">{{ wfSaveError }}</span>
            </div>
            <p class="sub">Kurallar: tam bir analiz adımı ve o ilk sırada; en az bir geliştirme adımı; inceleme adımı geliştirmeden sonra gelir. Ajan silinemez, bir takımda kullanılıyorsa önce buradan çıkarılır.</p>
          </form>
          <p v-else class="sub empty">Soldan bir takım seç ya da yeni kur.</p>
        </div>
      </template>
    </section>
  </div>
</template>

<style scoped>
.wrap { position: absolute; inset: 0; background: rgba(10, 12, 18, 0.25); display: flex; justify-content: flex-end; align-items: flex-start; padding: 12px; }
.panel {
  background: #ede9dc; color: #23283a; border: 6px solid #6b4a2b; border-radius: 6px;
  width: min(820px, 100%); max-height: 100%; overflow: auto; padding: 14px 16px; display: flex; flex-direction: column; gap: 12px;
  box-shadow: 0 20px 60px rgba(0,0,0,0.5);
}
.panel > header { display: flex; align-items: center; gap: 14px; }
h2 { margin: 0; font-size: 16px; letter-spacing: 0.04em; text-transform: uppercase; }
h3 { margin: 0 0 4px; font-size: 13px; text-transform: uppercase; letter-spacing: 0.06em; color: #4a5068; }
/* Kapat: 28x28 tiklama alani, isaret tam ortada, ustune gelince hafif zemin (tum panellerde ayni). */
.x {
  margin-left: auto; width: 28px; height: 28px; flex: none; display: inline-flex; align-items: center; justify-content: center;
  background: none; border: none; border-radius: 6px; padding: 0; font-size: 20px; line-height: 1; color: #23283a; cursor: pointer;
}
.x:hover { background: rgba(35,40,58,0.10); }
.tabs { display: flex; gap: 2px; }
.tabs button { font: inherit; font-size: 12px; font-weight: 600; cursor: pointer; background: transparent; color: #4a5068; border: none; border-bottom: 2px solid transparent; padding: 6px 10px; }
.tabs button.on { color: #23283a; border-bottom-color: #23283a; }
.tabs b { margin-left: 4px; color: #6b7285; }
.sub { font-size: 11px; color: #6b7285; line-height: 1.45; margin: 0; }
.err { color: #9c1f1f; font-size: 12px; font-weight: 600; margin: 0; }
code { font-size: 10px; background: rgba(0,0,0,0.06); padding: 1px 4px; border-radius: 3px; }

.agents { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 6px; }
.agents li { display: flex; gap: 6px; align-items: stretch; }
.agent { flex: 1; text-align: left; font: inherit; cursor: pointer; background: #fff; border: 1px solid #c9c3b3; border-radius: 6px; padding: 8px 10px; display: flex; flex-direction: column; gap: 2px; font-size: 12px; }
.agent:hover { border-color: #6b4a2b; }
.agent .meta { font-size: 10px; color: #6b7285; }
.agent strong, .team strong { color: #23283a; }
.agent code, .team code { color: #4a5068; }
.del { font: inherit; font-size: 16px; width: 28px; background: #fff; border: 1px solid #c9c3b3; border-radius: 6px; cursor: pointer; color: #9c1f1f; }
.del:hover { background: #fadada; }

.form { display: flex; flex-direction: column; gap: 8px; background: #f6f2e6; border: 1px solid #c9c3b3; border-radius: 6px; padding: 10px 12px; }
.row { display: grid; grid-template-columns: 1fr 1fr; gap: 10px; }
.row.three { grid-template-columns: 1fr 1fr 1fr; }
.field { display: flex; flex-direction: column; gap: 3px; min-width: 0; }
.lbl { font-size: 10px; font-weight: 700; text-transform: uppercase; letter-spacing: 0.04em; color: #4a5068; }
input, select, textarea { font: inherit; font-size: 12px; background: #fff; color: #23283a; border: 1px solid #c9c3b3; border-radius: 4px; padding: 5px 8px; min-width: 0; }
textarea { resize: vertical; }
.chips { display: flex; flex-wrap: wrap; gap: 4px 10px; }
.chk { font-size: 11px; display: inline-flex; align-items: center; gap: 4px; }
.actions { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
button.primary, button.ghost, button.danger, button.small { font: inherit; cursor: pointer; border-radius: 4px; padding: 6px 12px; border: 1px solid #c9c3b3; background: #fff; color: #23283a; font-size: 12px; }
button.primary { background: #23283a; color: #fff; border-color: #23283a; align-self: flex-start; }
button.ghost { background: transparent; }
button.danger { color: #9c1f1f; border-color: #d23b3b; }
button.small { padding: 3px 8px; font-size: 11px; }
button:disabled { opacity: 0.5; cursor: default; }

.teams { display: grid; grid-template-columns: 240px minmax(0, 1fr); gap: 12px; align-items: start; }
.teamlist { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 6px; }
.team { width: 100%; text-align: left; font: inherit; cursor: pointer; background: #fff; border: 1px solid #c9c3b3; border-radius: 6px; padding: 8px 10px; display: flex; flex-direction: column; gap: 2px; font-size: 12px; }
.team.on { border-color: #23283a; outline: 2px solid #23283a; outline-offset: -1px; }
.team .tag { font-size: 9px; font-weight: 700; text-transform: uppercase; background: #f3c34a; color: #3a2f12; border-radius: 999px; padding: 0 6px; margin-left: 4px; }
.editor .keyval { font-size: 12px; padding: 5px 8px; }
.hint { font-size: 11px; color: var(--ink-3, var(--ink-2)); line-height: 1.35; }
.stages { display: flex; flex-direction: column; gap: 6px; }
.stage-h, .stage { display: grid; grid-template-columns: 22px 1.3fr 1.1fr 1fr 1fr 78px; gap: 6px; align-items: center; }
.stage-h { font-size: 10px; font-weight: 700; text-transform: uppercase; color: #6b7285; padding: 0 2px; }
.stage { background: #fff; border: 1px solid #c9c3b3; border-radius: 6px; padding: 6px; }
.stage.locked { background: #f3efe3; }
.stage .num { font-weight: 700; color: #6b7285; text-align: center; }
.stage .cell { display: flex; flex-direction: column; gap: 3px; }
.stage .idin { font-size: 10px; padding: 2px 6px; }
.stage .ops { display: flex; gap: 3px; }
.stage .desc { grid-column: 2 / -1; font-size: 11px; }
.empty { padding: 20px 0; text-align: center; font-style: italic; }
@media (max-width: 720px) { .teams { grid-template-columns: 1fr; } .row, .row.three { grid-template-columns: 1fr; } }
.upload { font: inherit; font-size: 12px; cursor: pointer; border-radius: 4px; padding: 6px 12px; border: 1px dashed #6b4a2b; background: #fff; color: #23283a; display: inline-flex; align-items: center; }
.upload.small { padding: 3px 8px; font-size: 11px; }
.upload input { display: none; }
.upload:hover { background: #f3efe3; }
</style>
