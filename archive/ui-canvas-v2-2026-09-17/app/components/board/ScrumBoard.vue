<template>
  <div class="overlay" @click.self="$emit('close')">
    <section class="board">
      <header>
        <h2>Is akisi</h2>
        <span class="sub">{{ STAGES.length }} adim · {{ draft.length }} gorev</span>

        <button class="ghost" title="Calismanin guncel halinden yeniden yukle" @click="sync">
          Yenile
        </button>
        <button class="x" aria-label="Kapat" @click="$emit('close')">×</button>
      </header>

      <div class="cols">
        <div
          v-for="st in STAGES"
          :key="st.id"
          :class="['col', { drop: dragOver === st.id }]"
          @dragover.prevent="dragOver = st.id"
          @dragleave="dragOver === st.id && (dragOver = null)"
          @drop.prevent="onDrop(st.id)"
        >
          <div class="col-head">
            <span class="dot" :style="{ background: agentHex(st.agent) }" />
            <strong>{{ st.title }}</strong>
            <em class="kind">{{ KIND_LABEL[st.kind] }}</em>
          </div>

          <div class="cards">
            <article
              v-for="t in tasksOf(st.id)"
              :key="t.id"
              :class="['card', t.state, 'grab']"
              draggable="true"
              @dragstart="drag = t.id"
              @dragend="drag = null; dragOver = null"
            >
              <div class="card-top">
                <span class="id">{{ t.id }}</span>
                <span v-if="t.round > 1" class="round">tur {{ t.round }}</span>
                <button class="del" title="Sil" @click="remove(t.id)">×</button>
              </div>

              <input v-model="t.title" class="title-edit" spellcheck="false">

              <select v-model="t.state" class="state-edit">
                <option v-for="(lbl, k) in TASK_LABEL" :key="k" :value="k">{{ lbl }}</option>
              </select>
            </article>

            <button class="add" @click="add(st.id)">+ gorev</button>
          </div>
        </div>
      </div>

      <footer>
        <span>Kartlari surukleyerek adimlar arasinda tasiyin.</span>
        <span class="spacer" />
        <button class="primary" disabled>Kaydet</button>
        <code>Faz 5 · PUT /api/v1/runs/{id}/tasks</code>
      </footer>
    </section>
  </div>
</template>

<script setup lang="ts">
import { agentHex, STAGES, TASK_LABEL, type RunView, type StageKind, type TaskView } from '~/composables/useTeamState'

const props = defineProps<{ run: RunView }>()
defineEmits<{ close: [] }>()
const { run } = toRefs(props)

const KIND_LABEL: Record<StageKind, string> = {
  analyze: 'analiz', design: 'tasarim', implement: 'kod',
  review: 'denetim', handoff: 'devir',
}


/**
 * Board her zaman duzenlenebilir.
 *
 * Acilista calismanin o anki hali bir TASLAGA kopyalanir ve bundan sonra
 * taslak uzerinde calisilir. Taslak sart: canli akis gorevleri surekli
 * yeniden uretiyor; dogrudan onun uzerinde duzenleme yapilsaydi her tik
 * kullanicinin tasidigi karti geri alirdi. `Yenile` bilincli olarak
 * calismanin guncel halini geri getirir.
 */
const draft = ref<TaskView[]>(run.value.tasks.map(t => ({ ...t })))
const sync = () => { draft.value = run.value.tasks.map(t => ({ ...t })) }

const tasksOf = (stageId: string) => draft.value.filter(t => t.stage === stageId)

const drag = ref<string | null>(null)
const dragOver = ref<string | null>(null)

function onDrop(stageId: string) {
  dragOver.value = null
  if (!drag.value) return
  const t = draft.value.find(x => x.id === drag.value)
  if (t) t.stage = stageId
  drag.value = null
}

function add(stageId: string) {
  const n = draft.value.length + 1
  draft.value.push({
    id: `T${n}`, title: 'Yeni gorev', stage: stageId, state: 'queued', round: 1,
  })
}

const remove = (id: string) => {
  const i = draft.value.findIndex(t => t.id === id)
  if (i >= 0) draft.value.splice(i, 1)
}
</script>

<style scoped>
.overlay {
  position: absolute; inset: 0;
  background: #06080dcc; backdrop-filter: blur(3px);
  display: grid; place-items: center; padding: 24px; z-index: 30;
}

.board {
  width: min(1120px, 100%); max-height: 100%;
  display: flex; flex-direction: column;
  background: var(--surface); border: 1px solid var(--rule);
  border-radius: var(--r-panel); box-shadow: var(--shadow-panel); overflow: hidden;
}

header {
  display: flex; align-items: center; gap: 12px;
  padding: 12px 16px; border-bottom: 1px solid var(--rule);
}
h2 { margin: 0; font-size: 15px; }
.sub { color: var(--ink-3); font-size: 12px; flex: 1; }

.ghost {
  background: var(--surface-2); color: var(--ink-2);
  border: 1px solid var(--rule); border-radius: 6px;
  padding: 4px 10px; font: inherit; font-size: 12px; cursor: pointer;
}
.ghost:hover { color: var(--ink); }

.x {
  background: none; border: none; color: var(--ink-3);
  font-size: 22px; line-height: 1; cursor: pointer; padding: 0 4px;
}
.x:hover { color: var(--ink); }

.cols {
  display: grid; grid-auto-flow: column;
  grid-auto-columns: minmax(158px, 1fr);
  gap: 8px; padding: 14px; overflow: auto; flex: 1;
}

.col { min-width: 0; border-radius: var(--r-ctl); padding: 4px; }
.col.drop { background: #2b355080; outline: 1px dashed #5b7fd6; }

.col-head {
  display: flex; align-items: center; gap: 6px;
  padding-bottom: 8px; margin-bottom: 8px;
  border-bottom: 1px solid var(--rule); font-size: 12px;
}
.col-head strong { flex: 1; font-weight: 600; }
.kind { color: var(--ink-3); font-style: normal; font-size: 11px; }
.dot { width: 8px; height: 8px; border-radius: 50%; flex: none; }

.cards { display: flex; flex-direction: column; gap: 6px; }

.card {
  background: var(--surface-2); border: 1px solid var(--rule);
  border-left-width: 3px; border-radius: var(--r-ctl); padding: 7px 8px;
}
.card.grab { cursor: grab; }
.card.grab:active { cursor: grabbing; }

.card-top { display: flex; align-items: center; gap: 6px; }
.id { font-size: 11px; font-weight: 700; color: var(--ink-2); flex: 1; }
.round {
  font-size: 10px; color: var(--warn);
  border: 1px solid color-mix(in srgb, var(--warn) 45%, transparent);
  border-radius: 999px; padding: 0 5px;
}
.del {
  background: none; border: none; color: var(--danger);
  font-size: 15px; line-height: 1; cursor: pointer; padding: 0 2px;
}

.title-edit, .state-edit {
  width: 100%; background: var(--bg); color: var(--ink);
  border: 1px solid var(--rule); border-radius: 5px;
  padding: 3px 5px; font: inherit; font-size: 12px; margin: 3px 0;
}
.state-edit { font-size: 11px; color: var(--ink-2); }

.card.queued  { border-left-color: #3a4152; }
.card.active  { border-left-color: var(--ok); }
.card.blocked { border-left-color: var(--danger); }
.card.done    { border-left-color: #55607a; opacity: 0.62; }

.add {
  background: none; border: 1px dashed var(--rule); border-radius: var(--r-ctl);
  color: var(--ink-3); font: inherit; font-size: 11.5px;
  padding: 6px; cursor: pointer;
}
.add:hover { color: var(--ink); border-color: #47597f; }

footer {
  display: flex; align-items: center; gap: 8px;
  padding: 10px 16px; border-top: 1px solid var(--rule);
  color: var(--ink-3); font-size: 11.5px;
}
.spacer { flex: 1; }
footer button {
  background: var(--surface-2); color: var(--ink-2);
  border: 1px solid var(--rule); border-radius: 6px;
  padding: 5px 11px; font: inherit; font-size: 12px; cursor: pointer;
}
footer button.primary { background: #35507f; border-color: #4a6aa8; color: #fff; }
footer button:disabled { opacity: 0.45; cursor: default; }
code { background: var(--surface-2); padding: 2px 6px; border-radius: 4px; font-size: 10.5px; }
</style>
