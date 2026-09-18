<template>
  <aside class="panel">
    <nav class="tabs">
      <button :class="{ on: tab === 'agents' }" @click="tab = 'agents'">Ajanlar</button>
      <button :class="{ on: tab === 'flow' }" @click="tab = 'flow'">Is akisi</button>
      <button :class="{ on: tab === 'settings' }" @click="tab = 'settings'">Ayarlar</button>
    </nav>

    <!-- ─── Ajanlar ─────────────────────────────────────────────────── -->
    <div v-if="tab === 'agents'" class="body">
      <p class="note">
        Her ajanin md govdesi o rolun <strong>sistem promptudur</strong>.
        Kaydedince sonraki calismada davranis degisir.
        Sahnedeki bir ajana tiklayarak da buraya gelebilirsiniz.
      </p>

      <article
        v-for="a in run.agents"
        :key="a.key"
        :id="`agent-${a.key}`"
        :class="['agent', { on: open === a.key }]"
      >
        <button class="agent-head" @click="open = open === a.key ? null : a.key">
          <span class="dot" :style="{ background: a.hex }" />
          <strong>{{ a.name }}</strong>
          <span class="state" :style="{ color: STATE_TINT[a.state] }">
            {{ STATE_LABEL[a.state] }}
          </span>
          <span class="caret">{{ open === a.key ? '−' : '+' }}</span>
        </button>

        <div v-if="open === a.key" class="agent-body">
          <label>
            <span>Saglayici</span>
            <select v-model="cfg[a.key]!.provider">
              <option value="">(team.yaml varsayilani)</option>
              <option value="nvidia">nvidia</option>
              <option value="anthropic">anthropic</option>
              <option value="ollama">ollama</option>
            </select>
          </label>

          <label>
            <span>Model</span>
            <select v-model="cfg[a.key]!.model">
              <option v-for="m in MODELS[cfg[a.key]!.provider] ?? []" :key="m" :value="m">
                {{ m }}
              </option>
              <option value="">(varsayilan)</option>
            </select>
          </label>

          <label>
            <span>Alt md'ler</span>
            <div class="chips">
              <button
                v-for="k in KNOWLEDGE"
                :key="k"
                :class="['chip', { on: cfg[a.key]!.includes.includes(k) }]"
                @click="toggleInclude(a.key, k)"
              >{{ k }}</button>
            </div>
          </label>

          <label>
            <span>Sistem promptu (govde)</span>
            <textarea v-model="cfg[a.key]!.prompt" rows="7" spellcheck="false" />
          </label>

          <div class="row">
            <button class="primary" disabled>Kaydet</button>
            <span class="hint">Faz 2'de <code>PUT /api/v1/agents/{{ a.key }}</code></span>
          </div>
        </div>
      </article>
    </div>

    <!-- ─── Is akisi ────────────────────────────────────────────────── -->
    <div v-else-if="tab === 'flow'" class="body">
      <p class="note">
        Adimlari siralayin, silin, her adima rol atayin.
        <code>config/workflow.json</code> dosyasina yazilir.
      </p>

      <ol class="stages">
        <li v-for="(st, i) in stages" :key="st.id">
          <span class="ord">{{ i + 1 }}</span>
          <span class="dot" :style="{ background: hexOf(st.agent) }" />
          <div class="grow">
            <strong>{{ st.title }}</strong>
            <select v-model="st.agent" class="mini">
              <option v-for="a in run.agents" :key="a.key" :value="a.key">{{ a.name }}</option>
            </select>
          </div>
          <span class="kind">{{ st.kind }}</span>
          <span class="move">
            <button :disabled="i === 0" @click="move(i, -1)">↑</button>
            <button :disabled="i === stages.length - 1" @click="move(i, 1)">↓</button>
            <button class="del" @click="stages.splice(i, 1)">×</button>
          </span>
        </li>
      </ol>

      <div class="row">
        <button @click="addStage">+ Adim ekle</button>
        <button class="primary" disabled>Kaydet</button>
      </div>
      <p class="hint">Faz 2'de <code>PUT /api/v1/workflow</code></p>
    </div>

    <!-- ─── Ayarlar ─────────────────────────────────────────────────── -->
    <div v-else class="body">
      <label class="switch">
        <input v-model="prefs.board" type="checkbox">
        <span>Scrum board kisayolu (B)</span>
      </label>
      <label>
        <span>Tur limiti</span>
        <input v-model.number="prefs.rounds" type="number" min="1" max="9">
      </label>
      <label>
        <span>Butce ust siniri (USD)</span>
        <input v-model.number="prefs.budget" type="number" min="0" step="1">
      </label>
      <label>
        <span>Hassasiyet</span>
        <select v-model="prefs.sensitivity">
          <option value="open">open — NVIDIA'ya cikabilir</option>
          <option value="anthropic">anthropic — NVIDIA yasak</option>
          <option value="local">local — hicbir LLM'e cikmaz</option>
        </select>
      </label>
      <p class="note">
        Hassasiyet politikasi sunucuda denetlenir; aykiri bir rol varsa
        calisma <strong>hic baslamaz</strong>.
      </p>
    </div>
  </aside>
</template>

<script setup lang="ts">
import { STAGES, STATE_LABEL, STATE_TINT, type RunView, type StageView } from '~/composables/useTeamState'

const props = defineProps<{ run: RunView; selected?: string | null }>()
const emit = defineEmits<{ clear: [] }>()
const { run, selected } = toRefs(props)

const tab = ref<'agents' | 'flow' | 'settings'>('agents')
const open = ref<string | null>(null)

// Sahnede bir ajana tiklaninca: sekmeyi Ajanlar'a al, o ajani ac ve gorunume
// kaydir. Ayni ajana tekrar tiklamak kapatmaz — kullanici sahneden secti,
// panelin kapanmasi sasirtici olurdu.
watch(selected, async (key) => {
  if (!key) return
  tab.value = 'agents'
  open.value = key
  await nextTick()
  document.getElementById(`agent-${key}`)?.scrollIntoView({ block: 'nearest', behavior: 'smooth' })
  emit('clear')
})

/** Faz 2'de `GET /api/v1/catalog/models` besleyecek. */
const MODELS: Record<string, string[]> = {
  '': [],
  nvidia: ['z-ai/glm-5.3', 'openai/gpt-oss-20b', 'moonshotai/kimi-k3'],
  anthropic: ['claude-opus-5', 'claude-fable-5-1', 'claude-sonnet-5'],
  ollama: ['qwen2.5-coder:7b', 'qwen2.5-coder:14b'],
}

const KNOWLEDGE = ['mimari-kurallar', 'kodlama-standartlari', 'tasarim-ilkeleri', 'karar-ilkeleri']

interface AgentCfg { provider: string; model: string; includes: string[]; prompt: string }

const cfg = reactive<Record<string, AgentCfg>>(
  Object.fromEntries(run.value.agents.map(a => [a.key, {
    provider: '', model: '', includes: [],
    prompt: `Sen bir yazilim uretim ofisinin ${a.name.toUpperCase()}'isin.\n\n...`,
  }])),
)

function toggleInclude(key: string, k: string) {
  const arr = cfg[key]!.includes
  const i = arr.indexOf(k)
  if (i < 0) arr.push(k)
  else arr.splice(i, 1)
}

const stages = ref<StageView[]>(STAGES.map(s => ({ ...s })))
const hexOf = (k: string) => run.value.agents.find(a => a.key === k)?.hex ?? '#5a6070'

function move(i: number, d: number) {
  const [s] = stages.value.splice(i, 1)
  stages.value.splice(i + d, 0, s!)
}

function addStage() {
  stages.value.push({
    id: `adim-${stages.value.length + 1}`,
    title: 'Yeni adim',
    kind: 'review',
    agent: 'tester',
  })
}

const prefs = reactive({ board: true, rounds: 3, budget: 20, sensitivity: 'open' })
</script>

<style scoped>
.panel {
  width: 340px;
  flex: none;
  background: var(--surface);
  border-left: 1px solid var(--rule);
  display: flex;
  flex-direction: column;
  min-height: 0;
}

.tabs { display: flex; border-bottom: 1px solid var(--rule); flex: none; }
.tabs button {
  flex: 1; background: none; border: none; border-bottom: 2px solid transparent;
  color: var(--ink-3); font: inherit; font-size: 13px; padding: 10px 4px; cursor: pointer;
}
.tabs button.on { color: var(--ink); border-bottom-color: #5b7fd6; }

.body { padding: 12px; overflow-y: auto; display: flex; flex-direction: column; gap: 10px; }

.note, .hint { color: var(--ink-3); font-size: 11.5px; line-height: 1.5; margin: 0; }
code { background: var(--surface-2); padding: 1px 4px; border-radius: 4px; font-size: 11px; }

.dot { width: 9px; height: 9px; border-radius: 50%; flex: none; }

.agent {
  border: 1px solid var(--rule); border-radius: var(--r-ctl); overflow: hidden;
  transition: border-color 160ms ease;
}
.agent.on { border-color: #47597f; }
.agent-head {
  width: 100%; display: flex; align-items: center; gap: 8px;
  background: var(--surface-2); border: none; color: var(--ink);
  font: inherit; font-size: 13px; padding: 9px 10px; cursor: pointer; text-align: left;
}
.agent-head strong { flex: 1; font-weight: 600; }
.state { font-size: 11px; }
.caret { color: var(--ink-3); width: 10px; }

.agent-body { padding: 10px; display: flex; flex-direction: column; gap: 9px; }

label { display: flex; flex-direction: column; gap: 4px; font-size: 12px; color: var(--ink-2); }
label.switch { flex-direction: row; align-items: center; gap: 8px; }

select, input, textarea {
  background: var(--bg); color: var(--ink);
  border: 1px solid var(--rule); border-radius: 6px;
  padding: 6px 8px; font: inherit; font-size: 12px;
}
textarea { resize: vertical; font-family: ui-monospace, monospace; font-size: 11.5px; line-height: 1.45; }

.chips { display: flex; flex-wrap: wrap; gap: 4px; }
.chip {
  background: var(--bg); color: var(--ink-3);
  border: 1px solid var(--rule); border-radius: 999px;
  padding: 3px 8px; font-size: 11px; cursor: pointer;
}
.chip.on { background: #2b3550; border-color: #47597f; color: var(--ink); }

.row { display: flex; align-items: center; gap: 8px; }
.row button, .move button {
  background: var(--surface-2); color: var(--ink-2);
  border: 1px solid var(--rule); border-radius: 6px;
  padding: 5px 10px; font: inherit; font-size: 12px; cursor: pointer;
}
.row button.primary { background: #35507f; border-color: #4a6aa8; color: #fff; }
.row button:disabled, .move button:disabled { opacity: 0.45; cursor: default; }

.stages { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 6px; }
.stages li {
  display: flex; align-items: center; gap: 7px;
  background: var(--surface-2); border: 1px solid var(--rule);
  border-radius: var(--r-ctl); padding: 7px 8px;
}
.ord { color: var(--ink-3); font-size: 11px; width: 12px; }
.grow { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 3px; }
.grow strong { font-size: 12px; font-weight: 600; }
.mini { padding: 2px 4px; font-size: 11px; }
.kind { color: var(--ink-3); font-size: 10.5px; }
.move { display: flex; gap: 2px; }
.move button { padding: 2px 6px; font-size: 11px; }
.move .del { color: var(--danger); }
</style>
