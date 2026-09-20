<script setup lang="ts">
/**
 * Kucuk tek renkli ikon seti (kullanici istegi 2026-09-20: "ikon ve ekran yerlesimlerini gozden gecirelim").
 *
 * Neden SVG: arayuzde emoji (🔔 📁 🗑) ile sembol glifleri (⚙ ↺ ⏱ ⎋) karisikti; emoji isletim
 * sistemine gore renkli ve farkli boyda dusuyor, bazi glifler (ornegin "⎋") Windows yazi tiplerinde
 * hic yok — daire-slash olarak cikiyordu. Buradaki ikonlar `currentColor` ile cizilir, metinle ayni
 * renge ve satira oturur.
 *
 * Anlam METINDE; ikon yalniz isarettir, bu yuzden `aria-hidden`. Yanindaki metin ya da
 * dugmenin `aria-label`i anlami tasir.
 */
export type IconName =
  | 'bell' | 'gear' | 'folder' | 'trash' | 'refresh' | 'clock' | 'check' | 'exit'
  | 'chevron-down' | 'chevron-up'

const PATHS: Record<IconName, string[]> = {
  bell: ['M8 2.2a3.6 3.6 0 0 0-3.6 3.6c0 2.6-.9 3.6-1.4 4.2-.2.2 0 .5.2.5h9.6c.3 0 .4-.3.2-.5-.5-.6-1.4-1.6-1.4-4.2A3.6 3.6 0 0 0 8 2.2Z', 'M6.6 12.9a1.6 1.6 0 0 0 2.8 0'],
  gear: ['M9.5 1.6h-3l-.3 1.9c-.4.2-.8.4-1.1.7l-1.8-.7-1.5 2.5 1.5 1.2a4.7 4.7 0 0 0 0 1.4l-1.5 1.2 1.5 2.5 1.8-.7c.3.3.7.5 1.1.7l.3 1.9h3l.3-1.9c.4-.2.8-.4 1.1-.7l1.8.7 1.5-2.5-1.5-1.2a4.7 4.7 0 0 0 0-1.4l1.5-1.2-1.5-2.5-1.8.7a4.7 4.7 0 0 0-1.1-.7Z', 'M8 6a2 2 0 1 0 0 4 2 2 0 0 0 0-4Z'],
  folder: ['M1.8 4.4c0-.6.5-1 1-1h3.1l1.4 1.6h5.9c.5 0 1 .4 1 1v5.6c0 .6-.5 1-1 1H2.8c-.5 0-1-.4-1-1V4.4Z'],
  trash: ['M2.8 4.4h10.4', 'M6.2 4.4V3.2c0-.4.4-.8.8-.8h2c.4 0 .8.4.8.8v1.2', 'M4.3 4.4l.6 8.1c0 .5.4.9.9.9h4.4c.5 0 .9-.4.9-.9l.6-8.1', 'M6.8 7v4', 'M9.2 7v4'],
  refresh: ['M13.2 8a5.2 5.2 0 1 1-1.7-3.8', 'M13.4 2.4v2.8h-2.8'],
  clock: ['M8 2.6a5.4 5.4 0 1 0 0 10.8 5.4 5.4 0 0 0 0-10.8Z', 'M8 5.2V8.2l2 1.4'],
  check: ['M3.2 8.4l3 3 6.6-7'],
  exit: ['M9.5 2.5h-6v11h6', 'M11 5.5 13.5 8 11 10.5', 'M6.5 8h7'],
  'chevron-down': ['M4 6.4 8 10l4-3.6'],
  'chevron-up': ['M4 9.6 8 6l4 3.6'],
}

withDefaults(defineProps<{ name: IconName; size?: number }>(), { size: 14 })
</script>

<template>
  <svg
    class="ico" :width="size" :height="size" viewBox="0 0 16 16" aria-hidden="true"
    fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"
  >
    <path v-for="d in PATHS[name]" :key="d" :d="d" />
  </svg>
</template>

<style scoped>
/* Metinle ayni satirda durur, kucultulmez; renk metinden gelir. */
.ico { flex: none; vertical-align: -0.16em; }
</style>
