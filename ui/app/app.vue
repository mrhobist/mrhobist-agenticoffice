<template>
  <div class="shell">
    <header class="bar">
      <span class="brand">MrHobist.AITeam</span>
      <span class="chip" :class="status">{{ STATUS_LABEL[status] }}</span>
      <span class="run">
        <span class="chip">{{ hud.stage }}</span>
        <span class="chip ghost">{{ hud.task }} · tur {{ hud.round }}</span>
      </span>
    </header>

    <div class="main">
      <main class="stage">
        <OfficeScene
          @hud="hud = $event"
          @status="status = $event"
          @agents="agents = $event"
          @select="selected = $event"
        />
      </main>

      <aside class="side">
        <h2>Ekip</h2>
        <ul class="agents">
          <li v-for="a in agents" :key="a.key" :class="{ on: a.key === selected }" @click="selected = a.key">
            <span class="dot" :style="{ background: ROLE_HEX[a.key] }" />
            <span class="name">{{ a.name }}</span>
            <span class="state" :style="{ color: STATE_HEX[a.state as AgentState] }">{{ STATE_LABEL[a.state as AgentState] }}</span>
            <span v-if="a.note" class="note">{{ a.note }}</span>
          </li>
        </ul>
        <p class="hint">
          Sahne <code>config/scene.json</code>'dan gelir; olaylar <code>/api/v1/scene/events</code> ile akar.
          Api yoksa sahte yönetmen çalışır.
        </p>
      </aside>
    </div>
  </div>
</template>

<script setup lang="ts">
import OfficeScene from '~/components/OfficeScene.vue'
import type { Hud } from '~/scene/world'
import { ROLE_HEX, STATE_HEX, STATE_LABEL, type AgentState, type FeedStatus } from '~/scene/contract'

useHead({ title: 'MrHobist.AITeam — Üretim Ofisi', htmlAttrs: { lang: 'tr' } })

const hud = ref<Hud>({ stage: '—', task: '—', round: 0 })
const status = ref<FeedStatus>('connecting')
const agents = ref<Array<{ key: string; name: string; state: string; note: string | null }>>([])
const selected = ref<string | null>(null)

const STATUS_LABEL: Record<FeedStatus, string> = {
  connecting: 'bağlanıyor',
  live: 'canlı',
  reconnecting: 'yeniden bağlanıyor',
  mock: 'sahte yönetmen',
}
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
.brand { font-weight: 600; letter-spacing: 0.02em; color: var(--ink-2); }
.run { margin-left: auto; display: flex; gap: 6px; }
.chip {
  background: var(--surface-2); border: 1px solid var(--rule);
  border-radius: 999px; padding: 4px 11px; font-size: 12px; color: var(--ink);
}
.chip.ghost { color: var(--ink-3); }
.chip.live { border-color: #2f8f6a; color: #7fe0b3; }
.chip.mock { border-color: #8a6d2a; color: #f0c26a; }
.chip.reconnecting, .chip.connecting { color: var(--ink-3); }

.main { flex: 1; min-height: 0; display: flex; }
.stage { flex: 1; min-width: 0; position: relative; }

.side {
  width: 240px; flex: none; border-left: 1px solid var(--rule); background: var(--surface);
  padding: 12px; display: flex; flex-direction: column; gap: 10px; overflow: auto;
}
.side h2 { margin: 0; font-size: 12px; text-transform: uppercase; letter-spacing: 0.08em; color: var(--ink-3); }
.agents { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 4px; }
.agents li {
  display: grid; grid-template-columns: 10px 1fr auto; column-gap: 8px; align-items: center;
  padding: 6px 8px; border-radius: var(--r-ctl); cursor: pointer; border: 1px solid transparent;
}
.agents li:hover { background: var(--surface-2); }
.agents li.on { border-color: var(--rule); background: var(--surface-2); }
.dot { width: 10px; height: 10px; border-radius: 2px; }
.name { font-size: 13px; color: var(--ink); }
.state { font-size: 11px; }
.note { grid-column: 2 / span 2; font-size: 11px; color: var(--ink-3); }
.hint { margin: auto 0 0; font-size: 11px; color: var(--ink-3); line-height: 1.5; }
.hint code { font-size: 10px; color: var(--ink-2); }
</style>
