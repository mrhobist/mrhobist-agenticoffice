<script setup lang="ts">
import type { AgentListItem, McpServerRequest, McpServerView, McpTestResult, McpTransport } from '~/api/types'
import { useApiClient } from '~/api/client'
import { errorText } from '~/api/errors'
import { PROVIDER_LABEL } from '~/api/labels'

/**
 * MCP sunuculari (docs/DOMAIN.md → MCP sunuculari): ekle, duzenle, baglantiyi dene, sil; ekipteki ajanlara yetki ver.
 * Tanim veritabaninda (makineye ozgu, belirtec tasir), yetki ajan md'sinde (`mcp: [...]`). Ortam degiskeni / baslik
 * degerleri Api'den hic donmez: formda "kayitli" gorunur, bos birakilirsa korunur.
 */
const props = defineProps<{
  /** GET /agents listesi (kabuk yukler). null = alinamadi. */
  agents: AgentListItem[] | null
}>()
const emit = defineEmits<{ close: []; changed: [] }>()

const api = useApiClient()

const servers = ref<McpServerView[] | null>(null)
const loadError = ref<string | null>(null)
const busy = ref<string | null>(null)
const actError = ref<string | null>(null)
const tests = ref<Record<string, McpTestResult | 'running'>>({})

const TRANSPORT_LABEL: Record<McpTransport, string> = { stdio: 'stdio (yerel süreç)', http: 'HTTP', sse: 'SSE' }

async function load() {
  try {
    servers.value = await api.get<McpServerView[]>('/api/v1/mcp')
    loadError.value = null
  } catch (e) {
    loadError.value = errorText(e)
  }
}
onMounted(load)

// ------------------------------------------------------------------ form

interface SecretRow { name: string; value: string; hasValue: boolean }
interface Draft {
  key: string
  name: string
  description: string
  transport: McpTransport
  command: string
  args: string
  url: string
  env: SecretRow[]
  headers: SecretRow[]
  enabled: boolean
}

const editing = ref<string | null>(null) // '' = yeni, anahtar = duzenleme
const draft = ref<Draft | null>(null)
const formError = ref<string | null>(null)

function blank(): Draft {
  return { key: '', name: '', description: '', transport: 'stdio', command: '', args: '', url: '', env: [], headers: [], enabled: true }
}

function openNew() {
  editing.value = ''
  draft.value = blank()
  formError.value = null
}

function openEdit(s: McpServerView) {
  editing.value = s.key
  draft.value = {
    key: s.key,
    name: s.name,
    description: s.description,
    transport: s.transport,
    command: s.command ?? '',
    args: s.args.join('\n'),
    url: s.url ?? '',
    env: s.env.map(e => ({ name: e.name, value: '', hasValue: e.hasValue })),
    headers: s.headers.map(e => ({ name: e.name, value: '', hasValue: e.hasValue })),
    enabled: s.enabled,
  }
  formError.value = null
}

function closeForm() {
  editing.value = null
  draft.value = null
}

/** Satir: kayitli degeri olan ve bos birakilan satir null gider (sunucu korur); yeni satir yazilan degeri gonderir. */
function secrets(rows: SecretRow[]) {
  return rows.filter(r => r.name.trim()).map(r => ({ name: r.name.trim(), value: r.hasValue && !r.value ? null : r.value }))
}

async function save() {
  const d = draft.value
  if (!d || busy.value) return
  const body: McpServerRequest = {
    key: d.key.trim(),
    name: d.name.trim() || d.key.trim(),
    description: d.description.trim(),
    transport: d.transport,
    command: d.transport === 'stdio' ? d.command.trim() || null : null,
    args: d.transport === 'stdio' ? d.args.split('\n').map(a => a.trim()).filter(Boolean) : [],
    url: d.transport === 'stdio' ? null : d.url.trim() || null,
    env: d.transport === 'stdio' ? secrets(d.env) : [],
    headers: d.transport === 'stdio' ? [] : secrets(d.headers),
    enabled: d.enabled,
  }
  busy.value = 'save'
  formError.value = null
  try {
    if (editing.value) await api.put<McpServerView>(`/api/v1/mcp/${encodeURIComponent(editing.value)}`, body)
    else await api.post<McpServerView>('/api/v1/mcp', body)
    closeForm()
    await load()
  } catch (e) {
    formError.value = errorText(e)
  } finally {
    busy.value = null
  }
}

// ------------------------------------------------------------------ eylemler

async function test(s: McpServerView) {
  tests.value = { ...tests.value, [s.key]: 'running' }
  try {
    tests.value = { ...tests.value, [s.key]: await api.post<McpTestResult>(`/api/v1/mcp/${encodeURIComponent(s.key)}/test`) }
  } catch (e) {
    tests.value = { ...tests.value, [s.key]: { ok: false, detail: errorText(e), tools: [], serverName: null, serverVersion: null } }
  }
}

async function remove(s: McpServerView) {
  if (busy.value || !confirm(`"${s.name}" MCP sunucusu silinsin mi? Tanım ve kayıtlı belirteçler gider.`)) return
  busy.value = `del:${s.key}`
  actError.value = null
  try {
    await api.del(`/api/v1/mcp/${encodeURIComponent(s.key)}`)
    await load()
  } catch (e) {
    actError.value = errorText(e)
  } finally {
    busy.value = null
  }
}

/** Yetki: kutu isaretlenince hemen yazilir (ajan md'si guncellenir); kabuk ekibi yeniden yukler. */
async function toggleAccess(s: McpServerView, agentKey: string, on: boolean) {
  const next = on ? [...new Set([...s.agents, agentKey])] : s.agents.filter(a => a !== agentKey)
  busy.value = `acc:${s.key}`
  actError.value = null
  try {
    const updated = await api.put<McpServerView>(`/api/v1/mcp/${encodeURIComponent(s.key)}/access`, { agents: next })
    servers.value = (servers.value ?? []).map(x => x.key === s.key ? updated : x)
    emit('changed')
  } catch (e) {
    actError.value = errorText(e)
  } finally {
    busy.value = null
  }
}

/** MCP yalniz Claude Agent SDK'da calisir: saglayicisi bos (varsayilan anthropic) ya da anthropic olan ajan. */
function canUseMcp(a: AgentListItem): boolean { return !a.provider || a.provider === 'anthropic' }

const sortedAgents = computed(() => [...(props.agents ?? [])].sort((a, b) => a.name.localeCompare(b.name, 'tr')))

function fmtWhen(iso: string | null | undefined): string {
  return iso ? new Date(iso).toLocaleString('tr-TR', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' }) : ''
}
</script>

<template>
  <div class="wrap" @click.self="emit('close')">
    <section class="panel" role="dialog" aria-labelledby="mcp-title">
      <header>
        <h2 id="mcp-title">MCP sunucuları</h2>
        <button v-if="editing === null" type="button" class="primary small" @click="openNew">+ Sunucu ekle</button>
        <button class="x" type="button" aria-label="Kapat" @click="emit('close')">×</button>
      </header>

      <p class="sub">
        Ajanlara dış araç açan Model Context Protocol sunucuları (GitHub, veritabanı, tarayıcı, belge…). Tanım bu makinedeki
        veritabanında durur, git'e girmez; ortam değişkeni ve başlık değerleri bir daha gösterilmez. Yetkiyi aşağıdaki kutulardan
        ajan bazında verirsin; yetkili ajan araçlı adımlarda (analiz, geliştirme, test) sunucunun araçlarını <code>mcp__anahtar__araç</code>
        adıyla görür. Yalnız Claude (Anthropic) ajanlarında çalışır. Her aracın şeması her çağrıda bağlama girer: yalnız gereken sunucuyu ver.
      </p>

      <!-- ---------------------------------------------------------- form -->
      <form v-if="draft" class="card form" @submit.prevent="save">
        <h3>{{ editing ? `Düzenle · ${editing}` : 'Yeni MCP sunucusu' }}</h3>
        <div class="row">
          <label class="field">
            <span class="lbl">Anahtar</span>
            <input v-model="draft.key" type="text" :disabled="!!editing" placeholder="ör. github" required pattern="[a-z0-9][a-z0-9_\-]*" title="küçük harf, rakam, - ve _">
          </label>
          <label class="field">
            <span class="lbl">Ad</span>
            <input v-model="draft.name" type="text" placeholder="ör. GitHub">
          </label>
        </div>
        <label class="field">
          <span class="lbl">Açıklama <span class="sub">(isteğe bağlı)</span></span>
          <input v-model="draft.description" type="text" placeholder="Ne için? Ajanlara hangi işte gerekli?">
        </label>
        <div class="row">
          <label class="field">
            <span class="lbl">Taşıma</span>
            <select v-model="draft.transport">
              <option v-for="(label, t) in TRANSPORT_LABEL" :key="t" :value="t">{{ label }}</option>
            </select>
          </label>
          <label class="field check">
            <input v-model="draft.enabled" type="checkbox">
            <span>Açık <span class="sub">(kapalıyken yetkili ajanlara da verilmez)</span></span>
          </label>
        </div>

        <template v-if="draft.transport === 'stdio'">
          <div class="row">
            <label class="field">
              <span class="lbl">Komut</span>
              <input v-model="draft.command" type="text" placeholder="ör. npx, uvx, node, cmd" required>
            </label>
            <label class="field">
              <span class="lbl">Argümanlar <span class="sub">(satır başına bir)</span></span>
              <textarea v-model="draft.args" rows="3" placeholder="-y&#10;@modelcontextprotocol/server-github" />
            </label>
          </div>
          <p class="sub">Windows'ta <code>npx</code> çalışmazsa komutu <code>cmd</code>, argümanları <code>/c</code>, <code>npx</code>, <code>-y</code>, <code>paket</code> olarak yaz. Belirteçleri argümana değil ortam değişkenine koy.</p>
          <div class="field">
            <span class="lbl">Ortam değişkenleri</span>
            <div v-for="(r, i) in draft.env" :key="i" class="kv">
              <input v-model="r.name" type="text" placeholder="AD (ör. GITHUB_TOKEN)">
              <input v-model="r.value" type="password" autocomplete="off" :placeholder="r.hasValue ? 'kayıtlı — değiştirmek için yaz' : 'değer'">
              <button type="button" class="small ghost" aria-label="Satırı sil" @click="draft.env.splice(i, 1)">×</button>
            </div>
            <button type="button" class="small ghost add" @click="draft.env.push({ name: '', value: '', hasValue: false })">+ değişken</button>
          </div>
        </template>
        <template v-else>
          <label class="field">
            <span class="lbl">Adres</span>
            <input v-model="draft.url" type="url" :placeholder="draft.transport === 'sse' ? 'https://ornek/sse' : 'https://ornek/mcp'" required>
          </label>
          <div class="field">
            <span class="lbl">Başlıklar</span>
            <div v-for="(r, i) in draft.headers" :key="i" class="kv">
              <input v-model="r.name" type="text" placeholder="Ad (ör. Authorization)">
              <input v-model="r.value" type="password" autocomplete="off" :placeholder="r.hasValue ? 'kayıtlı — değiştirmek için yaz' : 'değer (ör. Bearer …)'">
              <button type="button" class="small ghost" aria-label="Satırı sil" @click="draft.headers.splice(i, 1)">×</button>
            </div>
            <button type="button" class="small ghost add" @click="draft.headers.push({ name: '', value: '', hasValue: false })">+ başlık</button>
          </div>
        </template>

        <div class="actions">
          <button type="submit" class="primary" :disabled="busy === 'save'">{{ busy === 'save' ? 'Kaydediliyor…' : 'Kaydet' }}</button>
          <button type="button" class="ghost" @click="closeForm">Vazgeç</button>
          <span v-if="formError" class="err" role="alert">{{ formError }}</span>
        </div>
      </form>

      <!-- ---------------------------------------------------------- liste -->
      <p v-if="loadError" class="err" role="alert">{{ loadError }}</p>
      <p v-else-if="!servers" class="sub">Yükleniyor…</p>
      <p v-else-if="!servers.length && !draft" class="empty">Henüz MCP sunucusu yok. "+ Sunucu ekle" ile başla.</p>
      <p v-if="actError" class="err" role="alert">{{ actError }}</p>

      <article v-for="s in servers ?? []" :key="s.key" class="card" :class="{ off: !s.enabled }">
        <div class="card-head">
          <strong>{{ s.name }}</strong>
          <code>{{ s.key }}</code>
          <span class="badge">{{ s.transport }}</span>
          <span class="badge" :class="s.enabled ? 'ok' : 'offb'">{{ s.enabled ? 'açık' : 'kapalı' }}</span>
          <span class="sub right" :title="s.updatedAt ?? ''">{{ fmtWhen(s.updatedAt) }}</span>
        </div>
        <p v-if="s.description" class="sub">{{ s.description }}</p>
        <p class="target"><code>{{ s.transport === 'stdio' ? [s.command, ...s.args].join(' ') : s.url }}</code></p>
        <p v-if="s.env.length || s.headers.length" class="sub">
          {{ s.transport === 'stdio' ? 'Ortam' : 'Başlıklar' }}:
          <span v-for="e in (s.transport === 'stdio' ? s.env : s.headers)" :key="e.name" class="secret">{{ e.name }}<template v-if="e.hasValue"> ••••</template></span>
        </p>

        <div class="access">
          <span class="lbl">Yetkili ajanlar</span>
          <p v-if="!agents" class="sub">Ekip alınamadı.</p>
          <label
            v-for="a in sortedAgents" :key="a.key" class="agent"
            :class="{ disabled: !canUseMcp(a) && !s.agents.includes(a.key) }"
            :title="canUseMcp(a) ? '' : `${PROVIDER_LABEL[a.provider!] ?? a.provider}: MCP yalnız Claude (Anthropic) ajanlarında çalışır`"
          >
            <input
              type="checkbox" :checked="s.agents.includes(a.key)"
              :disabled="busy === `acc:${s.key}` || (!canUseMcp(a) && !s.agents.includes(a.key))"
              @change="toggleAccess(s, a.key, ($event.target as HTMLInputElement).checked)"
            >
            <span>{{ a.name }}</span>
          </label>
        </div>

        <div class="actions">
          <button type="button" class="small" :disabled="tests[s.key] === 'running'" @click="test(s)">{{ tests[s.key] === 'running' ? 'Deneniyor…' : 'Bağlantıyı dene' }}</button>
          <button type="button" class="small ghost" :disabled="editing !== null" @click="openEdit(s)">Düzenle</button>
          <button type="button" class="small ghost danger" :disabled="!!busy" @click="remove(s)">Sil</button>
        </div>

        <div v-if="tests[s.key] && tests[s.key] !== 'running'" class="test" :class="(tests[s.key] as McpTestResult).ok ? 'good' : 'bad'">
          <template v-if="(tests[s.key] as McpTestResult).ok">
            <strong>Bağlandı</strong>
            <span v-if="(tests[s.key] as McpTestResult).serverName" class="sub"> · {{ (tests[s.key] as McpTestResult).serverName }} {{ (tests[s.key] as McpTestResult).serverVersion }}</span>
            <span class="sub"> · {{ (tests[s.key] as McpTestResult).detail }}</span>
            <ul class="tools">
              <li v-for="t in (tests[s.key] as McpTestResult).tools" :key="t.name"><code>{{ t.name }}</code><span v-if="t.description" class="sub"> — {{ t.description }}</span></li>
            </ul>
          </template>
          <template v-else>
            <strong>Bağlanamadı</strong>
            <pre>{{ (tests[s.key] as McpTestResult).detail }}</pre>
          </template>
        </div>
      </article>
    </section>
  </div>
</template>

<style scoped>
.wrap { position: absolute; inset: 0; background: rgba(10, 12, 18, 0.55); display: flex; justify-content: flex-end; }
.panel {
  background: #ede9dc; color: #23283a; border-left: 6px solid #6b4a2b;
  width: min(680px, 100%); height: 100%; overflow: auto; padding: 16px 18px;
  box-shadow: -20px 0 60px rgba(0,0,0,0.5); display: flex; flex-direction: column; gap: 12px;
}
.panel > header { display: flex; align-items: center; gap: 10px; }
h2 { margin: 0; font-size: 16px; letter-spacing: 0.04em; text-transform: uppercase; }
h3 { margin: 0; font-size: 13px; text-transform: uppercase; letter-spacing: 0.06em; color: #4a5068; }
/* Kapat: 28x28 tiklama alani, isaret tam ortada, ustune gelince hafif zemin (tum panellerde ayni). */
.x {
  margin-left: auto; width: 28px; height: 28px; flex: none; display: inline-flex; align-items: center; justify-content: center;
  background: none; border: none; border-radius: 6px; padding: 0; font-size: 20px; line-height: 1; color: #23283a; cursor: pointer;
}
.x:hover { background: rgba(35,40,58,0.10); }
.sub { font-size: 11px; color: #6b7285; line-height: 1.45; margin: 0; }
.sub.right { margin-left: auto; }
.empty { margin: 0; font-size: 13px; color: #4a5068; }
.card { background: #fff; border: 1px solid #c9c3b3; border-radius: 6px; padding: 10px 12px; display: flex; flex-direction: column; gap: 6px; }
.card.off { opacity: 0.75; }
.card-head { display: flex; align-items: center; gap: 8px; font-size: 14px; flex-wrap: wrap; }
.badge { font-size: 10px; font-weight: 700; padding: 2px 8px; border-radius: 999px; text-transform: uppercase; letter-spacing: 0.04em; background: #e5e7ee; }
.badge.ok { background: #7cc46b; }
.badge.offb { background: #f3c34a; }
.target { margin: 0; }
.target code { font-size: 11px; word-break: break-all; }
code { font-size: 10px; background: rgba(0,0,0,0.06); padding: 1px 4px; border-radius: 3px; }
.secret { display: inline-block; margin-right: 8px; font-family: ui-monospace, monospace; }
.access { display: flex; flex-wrap: wrap; gap: 4px 12px; align-items: center; padding-top: 4px; border-top: 1px solid rgba(0,0,0,0.06); }
.access .lbl { width: 100%; }
.agent { display: inline-flex; align-items: center; gap: 5px; font-size: 13px; cursor: pointer; }
.agent.disabled { color: #9aa0b2; cursor: not-allowed; }
.lbl { font-size: 11px; font-weight: 700; text-transform: uppercase; letter-spacing: 0.04em; color: #4a5068; }
.form { gap: 10px; border-color: #6b4a2b; }
.row { display: grid; grid-template-columns: 1fr 1fr; gap: 10px; }
.field { display: flex; flex-direction: column; gap: 4px; min-width: 0; }
.field.check { flex-direction: row; align-items: center; gap: 6px; font-size: 13px; padding-top: 18px; }
input[type="text"], input[type="url"], input[type="password"], select, textarea {
  font: inherit; font-size: 13px; background: #fff; color: #23283a; border: 1px solid #c9c3b3; border-radius: 4px; padding: 6px 8px; width: 100%;
}
input:disabled { background: #f1eee5; color: #6b7285; }
textarea { resize: vertical; font-family: ui-monospace, monospace; font-size: 12px; }
input:focus, select:focus, textarea:focus { outline: 2px solid #4f8ef7; outline-offset: 0; }
.kv { display: grid; grid-template-columns: 1fr 1.4fr auto; gap: 6px; }
.add { align-self: flex-start; }
.actions { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
button { font: inherit; cursor: pointer; border-radius: 4px; padding: 7px 14px; border: 1px solid #c9c3b3; background: #fff; color: #23283a; }
button:disabled { opacity: 0.5; cursor: default; }
.primary { background: #23283a; color: #fff; border-color: #23283a; }
.ghost { background: transparent; }
.small { padding: 3px 8px; font-size: 11px; }
.danger { color: #b3261e; }
.err { color: #b3261e; font-size: 12px; margin: 0; }
.test { border-radius: 4px; padding: 6px 8px; font-size: 12px; }
.test.good { background: #e3f4dc; border: 1px solid #7cc46b; }
.test.bad { background: #fbe4e2; border: 1px solid #e0605e; }
.test pre { margin: 4px 0 0; white-space: pre-wrap; word-break: break-word; font-size: 11px; }
.tools { margin: 4px 0 0; padding-left: 16px; max-height: 220px; overflow: auto; }
.tools li { font-size: 12px; }
@media (max-width: 560px) { .row { grid-template-columns: 1fr; } .kv { grid-template-columns: 1fr; } }
</style>
