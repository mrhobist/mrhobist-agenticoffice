<script lang="ts">
import type { World } from '~/scene/world'

/** Sahne motorunun verdigi anlik goruntu; panel yalniz bunu okur, kendi kopyasini tutmaz. */
export type BoardSnapshot = ReturnType<World['board']['snapshot']>
</script>

<script setup lang="ts">
import type { TaskState } from '~/scene/contract'

const props = defineProps<{ board: BoardSnapshot }>()
const emit = defineEmits<{ close: [] }>()

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

const columns = computed(() => props.board.columns.map((c) => {
  const all = props.board.tasks.filter(t => t.column === c.id)
  const shown = filter.value === 'all' ? all : all.filter(t => t.state === filter.value)
  return { ...c, total: all.length, tasks: shown }
}))

const filterCount = computed<Record<Filter, number>>(() => ({
  all: props.board.tasks.length,
  active: props.board.tasks.filter(t => t.state === 'active').length,
  blocked: props.board.tasks.filter(t => t.state === 'blocked').length,
  done: props.board.tasks.filter(t => t.state === 'done').length,
}))

/** Pano her 1.5 s yenilenir; secim id ile tutulur, gorev kaybolursa detay kapanir. */
const selected = computed(() => selectedId.value ? props.board.tasks.find(t => t.id === selectedId.value) ?? null : null)
const selectedColumn = computed(() => props.board.columns.find(c => c.id === selected.value?.column)?.title ?? '—')

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

      <p class="hint">Sütunlar seçili iş akışının adımları (<code>config/workflows/</code>); sahnedeki küçük pano bunları dört Kanban şeridine katlar. <kbd>Esc</kbd> ya da <kbd>B</kbd> kapatır.</p>
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

.filters { display: flex; gap: 4px; }
.filters button {
  font: inherit; font-size: 11px; cursor: pointer; color: #4a5068;
  background: rgba(0,0,0,0.05); border: 1px solid transparent; border-radius: 999px; padding: 3px 10px;
}
.filters button b { font-weight: 700; margin-left: 3px; }
.filters button:hover { background: rgba(0,0,0,0.09); }
.filters button.on { background: #fff; border-color: #c9c3b3; color: #23283a; }

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
