<script setup lang="ts">
import type { LanguageShare } from '~/api/types'

/**
 * Dil seridi (kullanici istegi 2026-09-26: "projede kullanilan diller, GitHub'daki gibi"). Pay sunucuda baytla olculur
 * (Domain/Projects/Codebase, linguist ozu); renkler linguist'in. Ilk `max` dil ayri, kalani GitHub'daki gibi "Diger".
 * `compact`: yalniz serit (ray karti); lejant yok, ad/yuzde title'da.
 */
const props = withDefaults(defineProps<{ languages: LanguageShare[]; compact?: boolean; max?: number }>(), { compact: false, max: 6 })

/** GitHub'in "Other" rengi. */
const OTHER = '#ededed'

const parts = computed(() => {
  const head = props.languages.slice(0, props.max)
  const rest = props.languages.slice(props.max)
  const other = Math.round(rest.reduce((s, l) => s + l.percent, 0) * 10) / 10
  return other > 0 ? [...head, { name: 'Diğer', color: OTHER, bytes: rest.reduce((s, l) => s + l.bytes, 0), percent: other }] : head
})

function pct(v: number): string { return '%' + v.toLocaleString('tr-TR', { maximumFractionDigits: 1 }) }
const summary = computed(() => parts.value.map(p => `${p.name} ${pct(p.percent)}`).join(' · '))
</script>

<template>
  <!-- Kok span: ray kartinda <button> icinde durur (div orada gecersiz). -->
  <span v-if="languages.length" class="lang-bar" :class="{ compact }">
    <span class="lang-strip" role="img" :aria-label="`Diller: ${summary}`" :title="compact ? summary : undefined">
      <span v-for="p in parts" :key="p.name" class="lang-seg" :style="{ width: Math.max(p.percent, 0.6) + '%', background: p.color }" :title="`${p.name} ${pct(p.percent)}`" />
    </span>
    <ul v-if="!compact" class="lang-legend">
      <li v-for="p in parts" :key="p.name">
        <span class="lang-dot" :style="{ background: p.color }" aria-hidden="true" />
        <b>{{ p.name }}</b> <span class="lang-pct">{{ pct(p.percent) }}</span>
      </li>
    </ul>
  </span>
</template>

<style scoped>
.lang-bar { display: flex; flex-direction: column; gap: 6px; min-width: 0; }
/* GitHub'daki gibi: yuvarlatilmis serit, bolumler arasinda ince bosluk. */
.lang-strip { display: flex; height: 8px; border-radius: 6px; overflow: hidden; background: #ded6c4; gap: 2px; }
.compact .lang-strip { height: 4px; gap: 1px; border-radius: 2px; }
.lang-seg { display: block; height: 100%; flex: none; }
.lang-seg:last-child { flex: 1 1 auto; } /* yuvarlama artigi son bolume */
.lang-legend { list-style: none; margin: 0; padding: 0; display: flex; flex-wrap: wrap; gap: 3px 14px; font-size: 12px; color: #23283a; }
.lang-legend li { display: inline-flex; align-items: center; gap: 5px; white-space: nowrap; }
.lang-legend b { font-weight: 600; }
.lang-dot { width: 9px; height: 9px; border-radius: 50%; flex: none; box-shadow: inset 0 0 0 1px rgba(0,0,0,0.12); }
.lang-pct { color: #6b7285; }
</style>
