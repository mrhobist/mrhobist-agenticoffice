<template>
  <button class="mini" :title="`${total} gorev · acmak icin tikla (B)`" @click="$emit('open')">
    <svg :viewBox="`0 0 ${VW} ${VH}`" :width="VW" :height="VH" aria-hidden="true">
      <defs>
        <filter id="mbf-glow" x="-60%" y="-60%" width="220%" height="220%">
          <feGaussianBlur stdDeviation="2.2" result="blur" />
          <feMerge>
            <feMergeNode in="blur" />
            <feMergeNode in="SourceGraphic" />
          </feMerge>
        </filter>
      </defs>

      <!-- Ray: adimlar arasi hat -->
      <line
        :x1="X0" :y1="BASE" :x2="X0 + (STAGES.length - 1) * DX" :y2="BASE"
        stroke="#33405e" stroke-width="1.2"
      />

      <g v-for="(st, i) in STAGES" :key="st.id" filter="url(#mbf-glow)">
        <!-- Gorev noktalari: adimin uzerinde yigilir -->
        <circle
          v-for="(t, j) in tasksOf(st.id).slice(0, 4)"
          :key="t.id"
          :cx="X0 + i * DX"
          :cy="BASE - 9 - j * 6"
          :r="t.state === 'active' || t.state === 'blocked' ? 2.6 : 2"
          :fill="TASK_TINT[t.state]"
          :opacity="t.state === 'done' ? 0.45 : 0.95"
        />

        <!-- Adim isareti -->
        <circle
          :cx="X0 + i * DX" :cy="BASE" :r="hot(st.id) ? 4.6 : 3.2"
          :fill="hot(st.id) ? agentHex(st.agent) : '#1b2436'"
          :stroke="agentHex(st.agent)" stroke-width="1.3"
        />
      </g>
    </svg>

    <span class="cap">Is akisi · {{ total }} gorev</span>
  </button>
</template>

<script setup lang="ts">
import { agentHex, STAGES, TASK_TINT, type RunView } from '~/composables/useTeamState'

/**
 * Akis gorunumunun mini board'u.
 *
 * Piksel board bu sahneye uymuyordu: ahsap cerceve ve yapiskan notlar,
 * isiktan orulmus bir grafigin yaninda yabanci duruyor. Burada ayni bilgi
 * AYNI DILLE anlatilir — ince hat, parlayan dugum, isik noktasi.
 * Sutun yok; adimlar bir ray uzerinde, gorevler o adimin uzerinde yigili.
 */

const props = defineProps<{ run: RunView }>()
defineEmits<{ open: [] }>()
const { run } = toRefs(props)

const DX = 26
const X0 = 16
const BASE = 46
const VW = X0 * 2 + (STAGES.length - 1) * DX
const VH = 58


const total = computed(() => run.value.tasks.length)
const tasksOf = (id: string) => run.value.tasks.filter(t => t.stage === id)

/** Uzerinde calisilan veya takilan gorev barindiran adim vurgulanir. */
const hot = (id: string) =>
  run.value.tasks.some(t => t.stage === id && (t.state === 'active' || t.state === 'blocked'))
</script>

<style scoped>
.mini {
  position: absolute;
  right: 14px;
  top: 14px;
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  gap: 4px;
  background: #0a0d14aa;
  border: 1px solid #22304a;
  border-radius: 10px;
  padding: 6px 10px 5px;
  cursor: pointer;
  z-index: 10;
  backdrop-filter: blur(6px);
  transition: transform 120ms ease, border-color 120ms ease, background 120ms ease;
}

.mini:hover {
  transform: translateY(-3px);
  border-color: #3f5c8e;
  background: #0d1220cc;
}
.mini:focus-visible { outline: 2px solid #5b7fd6; outline-offset: 2px; }

svg { display: block; overflow: visible; }

.cap {
  font-size: 10.5px;
  color: var(--ink-3);
  white-space: nowrap;
}
.mini:hover .cap { color: var(--ink); }
</style>
