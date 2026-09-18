<template>
  <div class="shell">
    <header class="bar">
      <span class="brand">MrHobist.AITeam</span>

      <nav class="views">
        <button
          v-for="s in STYLES"
          :key="s.id"
          :class="['tab', { on: view === s.id }]"
          @click="view = s.id"
        >{{ s.label }}</button>
      </nav>

      <span class="run">
        <span class="chip">{{ run.stage }}</span>
        <span class="chip ghost">{{ run.task }} · tur {{ run.round }}</span>
      </span>
    </header>

    <div class="main">
      <main class="stage">
        <StyleFlow v-if="view === 'flow'" :run="run" @select="focus" />
        <StylePixel v-else :run="run" @select="focus" />

        <!-- Mini board her gorunumun KENDI diliyle cizilir: piksel ofiste
             ahsap pano, akis grafiginde isiktan bir ray. -->
        <template v-if="!board">
          <MiniBoardFlow v-if="view === 'flow'" :run="run" @open="board = true" />
          <MiniBoardPixel v-else :run="run" @open="board = true" />
        </template>
        <ScrumBoard v-if="board" :run="run" @close="board = false" />
      </main>

      <SidePanel :run="run" :selected="selected" @clear="selected = null" />
    </div>
  </div>
</template>

<script setup lang="ts">
import StyleFlow from '~/components/style/StyleFlow.vue'
import StylePixel from '~/components/style/StylePixel.vue'
import ScrumBoard from '~/components/board/ScrumBoard.vue'
import MiniBoardPixel from '~/components/board/MiniBoardPixel.vue'
import MiniBoardFlow from '~/components/board/MiniBoardFlow.vue'
import SidePanel from '~/components/panel/SidePanel.vue'

useHead({ title: 'MrHobist.AITeam — Uretim Ofisi', htmlAttrs: { lang: 'tr' } })

// TEK kaynak: burada kurulur, asagiya prop olarak gecer. Her bilesen kendi
// useTeamState()'ini cagirsaydi ayri zamanlayicilar kurulup gorunumler
// birbirinden kayardi.
const { run } = useTeamState()

const STYLES = [
  { id: 'flow', label: 'Akis' },
  { id: 'pixel', label: 'Ofis' },
] as const

const view = ref<(typeof STYLES)[number]['id']>('pixel')
const board = ref(false)

/** Sahnede bir ajana tiklaninca panel o ajanda acilir. */
const selected = ref<string | null>(null)
function focus(key: string) {
  selected.value = key
}

function onKey(e: KeyboardEvent) {
  const el = e.target as HTMLElement | null
  if (el && /^(INPUT|TEXTAREA|SELECT)$/.test(el.tagName)) return
  if (e.key === 'b' || e.key === 'B') board.value = !board.value
  if (e.key === 'Escape') board.value = false
}
onMounted(() => window.addEventListener('keydown', onKey))
onBeforeUnmount(() => window.removeEventListener('keydown', onKey))
</script>

<style>
.shell { display: flex; flex-direction: column; height: 100%; }

.bar {
  display: flex; align-items: center; gap: 12px;
  padding: 9px 14px;
  background: var(--surface);
  border-bottom: 1px solid var(--rule);
  flex: none;
}

.brand { font-weight: 600; letter-spacing: 0.02em; color: var(--ink-2); flex: none; }
.views { display: flex; gap: 5px; }

.tab {
  background: var(--surface-2); color: var(--ink-2);
  border: 1px solid var(--rule); border-radius: var(--r-ctl);
  padding: 6px 12px; font: inherit; font-size: 13px; cursor: pointer;
}
.tab:hover { color: var(--ink); }
.tab.on { background: #2b3550; border-color: #47597f; color: var(--ink); }

.run { margin-left: auto; display: flex; gap: 6px; }
.chip {
  background: var(--surface-2); border: 1px solid var(--rule);
  border-radius: 999px; padding: 4px 11px; font-size: 12px; color: var(--ink);
}
.chip.ghost { color: var(--ink-3); }

.main { flex: 1; min-height: 0; display: flex; }
.stage { flex: 1; min-width: 0; position: relative; }
</style>
