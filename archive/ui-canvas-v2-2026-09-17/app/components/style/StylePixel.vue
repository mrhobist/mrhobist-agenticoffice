<template>
  <div class="wrap">
    <canvas ref="cv" class="fill" @click="pick" />
    <div class="hud">
      <span v-for="a in busy" :key="a.key" class="chip note" :style="{ '--c': a.hex }">
        {{ a.name }} · {{ a.task }}
      </span>
    </div>
  </div>
</template>

<script setup lang="ts">
import { type RunView, STATE_TINT } from '~/composables/useTeamState'

/**
 * Piksel ofis — referans gorseline yaklastirilmis modern acik ofis.
 *
 * Referanstan alinanlar:
 *   - Sicak bej karo zemin + koyu arduvaz duvar; kontrast buradan gelir.
 *   - Panoramik pencere: gun batimi gokyuzu, sehir silueti, su yansimasi.
 *   - Kahve bari, camli toplanti odasi, oturma kosesi (kanepe + hali + lamba).
 *   - Masa ADALARI: iki masa sirt sirta, aralarinda bolme; altta dosya dolabi,
 *     ustte monitor, yaninda bitki ve kupa.
 *   - Cok sayida bitki; bos zemin olu durur.
 *
 * Cozunurluk 280x200 (onceki 112x84). Referansin detay yogunlugu bu
 * olcekten asagida tasinamiyordu; ekranda her piksel ~2 px kalir, yani
 * piksel hissi korunur ama detaya yer acilir.
 *
 * Tampon TAM KAT buyutulur. Durum prop olarak gelir; akis grafigiyle
 * AYNI kaynak.
 */

const cv = useTemplateRef<HTMLCanvasElement>('cv')
const props = defineProps<{ run: RunView }>()
const emit = defineEmits<{ select: [key: string] }>()
const { run } = toRefs(props)
const busy = computed(() => run.value.agents.filter(a => a.task))

const W = 280
const H = 200

const P = {
  wallOut: '#2b323f', wallIn: '#3d4655', wallHi: '#4c5666',
  floor: '#d9b98c', floorAlt: '#d2b083', grout: '#c6a274',
  wood: '#d7a668', woodLo: '#b98b52', woodHi: '#e8bd82',
  panel: '#8d949f', panelLo: '#767d88',
  chair: '#3a4150', chairLo: '#2a303c', chairHi: '#4a5263',
  cab: '#b9bdc5', cabLo: '#9aa0aa',
  screen: '#2a303c', ui: '#4f90d6', uiLo: '#2f6aa8', uiHi: '#9fc8ef',
  skin: '#f0c49a', skinLo: '#d9a87e',
  leaf: '#4f9c5c', leafLo: '#3a7746', leafHi: '#6cbb78',
  potW: '#e9e9ec', potT: '#c06a42',
  sky1: '#f4a95f', sky2: '#ef82a2', sky3: '#8e63aa', sky4: '#5a4487',
  sun: '#ffe9a8', city: '#6a5a9e', cityLo: '#4b3f78', water: '#5d6fa6',
  glass: '#7fa8c4', glassHi: '#a9cde2', frame: '#4a5263',
  sofa: '#4f8a5c', sofaHi: '#63a271', rug: '#3f5a7a', rugHi: '#54739a',
  paper: '#f2ead9', book: '#8c4a38', book2: '#3f6b8c', book3: '#c9a23f',
  lamp: '#ffd98a', ink: '#242a35', hedge: '#3f7a4a',
} as const

/** Uc masa adasi, her adada iki kisi. */
const SEAT: Record<string, { x: number; y: number }> = {
  analyst: { x: 66, y: 96 },
  designer: { x: 108, y: 96 },
  developer: { x: 160, y: 96 },
  organizer: { x: 202, y: 96 },
  tester: { x: 66, y: 158 },
  manager: { x: 108, y: 158 },
}

let stop: (() => void) | undefined

function pick(ev: MouseEvent) {
  const canvas = cv.value
  if (!canvas) return
  const r = canvas.getBoundingClientRect()
  const scale = Math.max(1, Math.floor(Math.min(r.width / W, r.height / H)))
  const bx = (ev.clientX - r.left - (r.width - W * scale) / 2) / scale
  const by = (ev.clientY - r.top - (r.height - H * scale) / 2) / scale

  for (const [key, s] of Object.entries(SEAT)) {
    if (bx >= s.x - 20 && bx <= s.x + 20 && by >= s.y - 22 && by <= s.y + 26) {
      emit('select', key)
      return
    }
  }
}

onMounted(() => {
  const canvas = cv.value
  if (!canvas) return

  const buf = document.createElement('canvas')
  buf.width = W; buf.height = H
  const b = buf.getContext('2d')!
  const ctx = canvas.getContext('2d')!

  const px = (x: number, y: number, w: number, h: number, c: string) => {
    b.fillStyle = c
    b.fillRect(x | 0, y | 0, w, h)
  }

  // ─── Ortak parcalar ────────────────────────────────────────────────────
  const plant = (x: number, y: number, big = false, pot = P.potW) => {
    const s = big ? 1 : 0
    px(x - 4 - s, y, 9 + s * 2, 7 + s, pot)
    px(x - 4 - s, y, 9 + s * 2, 2, pot === P.potW ? '#d6d6da' : '#a85a38')
    px(x - 5 - s, y - 7 - s * 2, 11 + s * 2, 8 + s * 2, P.leaf)
    px(x - 3 - s, y - 10 - s * 3, 7 + s * 2, 4 + s, P.leafHi)
    px(x - 5 - s, y - 2, 11 + s * 2, 2, P.leafLo)
  }

  const chairTop = (x: number, y: number) => {
    px(x - 8, y, 16, 10, P.chair)
    px(x - 8, y, 16, 2, P.chairHi)
    px(x - 6, y + 10, 12, 4, P.chairLo)
    px(x - 2, y + 14, 4, 3, P.chairLo)
  }

  /** Masa: tabla + monitor + klavye + dolap + bitki + kupa. */
  const desk = (x: number, y: number, color: string) => {
    px(x - 20, y - 14, 40, 26, P.wood)          // tabla
    px(x - 20, y - 14, 40, 2, P.woodHi)
    px(x - 20, y + 12, 40, 2, P.woodLo)

    px(x - 18, y + 14, 16, 16, P.cab)           // dosya dolabi
    px(x - 18, y + 14, 16, 2, '#cdd1d8')
    px(x - 16, y + 19, 12, 1, P.cabLo)
    px(x - 16, y + 25, 12, 1, P.cabLo)

    px(x - 13, y - 12, 26, 15, P.screen)        // monitor
    px(x - 11, y - 10, 22, 11, P.ui)
    px(x - 10, y - 9, 9, 4, P.uiHi)
    px(x - 10, y - 4, 14, 1, P.uiLo)
    px(x - 10, y - 2, 11, 1, P.uiLo)
    px(x + 1, y - 9, 9, 6, color)               // rol rengi: ekranin bir panosu
    px(x - 3, y + 3, 6, 2, P.screen)            // ayak

    px(x - 9, y + 6, 18, 4, P.panel)            // klavye
    px(x + 11, y + 6, 4, 4, P.panelLo)          // fare
    px(x - 18, y + 4, 5, 5, color)              // kupa
    plant(x + 16, y + 2)                        // masa bitkisi
  }

  /** Oturan ajan — arkadan gorunum, referanstaki gibi. */
  const agent = (x: number, y: number, color: string, hair: string, bob: number) => {
    const yy = y + bob
    chairTop(x, yy + 2)
    px(x - 7, yy - 4, 14, 14, color)            // govde
    px(x - 7, yy - 4, 14, 2, '#ffffff22')
    px(x - 9, yy + 0, 2, 8, color)              // kollar
    px(x + 7, yy + 0, 2, 8, color)
    px(x - 6, yy - 15, 12, 12, P.skin)          // kafa (arkadan)
    px(x - 6, yy - 16, 12, 8, hair)             // sac
    px(x - 6, yy - 8, 12, 2, P.skinLo)
  }

  /** Panoramik pencere: gokyuzu + sehir + su. */
  const window = (x: number, y: number, w: number, h: number) => {
    px(x - 2, y - 2, w + 4, h + 4, P.frame)
    const bands = [P.sky1, P.sky1, P.sky2, P.sky2, P.sky3, P.sky4]
    const bh = Math.floor(h * 0.52 / bands.length)
    bands.forEach((c, i) => px(x, y + i * bh, w, bh, c))
    px(x, y + bands.length * bh, w, h - bands.length * bh, P.water)

    // Gunes
    px(x + Math.floor(w * 0.56), y + 14, 10, 10, P.sun)

    // Sehir silueti
    const sky = y + bands.length * bh
    let bx2 = x + 3
    for (const [bw, bhh] of [[8, 22], [6, 32], [9, 16], [7, 27], [10, 20],
                             [6, 34], [8, 18], [7, 25], [9, 30], [6, 15]] as const) {
      px(bx2, sky - bhh, bw, bhh, bx2 % 3 ? P.city : P.cityLo)
      for (let wy = sky - bhh + 3; wy < sky - 3; wy += 5)
        for (let wx = bx2 + 1; wx < bx2 + bw - 1; wx += 3)
          if ((wx + wy) % 7 < 3) px(wx, wy, 1, 2, P.sun)
      bx2 += bw + 2
      if (bx2 > x + w - 8) break
    }
    // Su yansimasi
    for (let ry = sky + 2; ry < y + h; ry += 3)
      px(x + 4 + ((ry * 7) % 9), ry, w - 20, 1, '#ffffff1e')

    // Dogramalar
    for (let fx = x + Math.floor(w / 4); fx < x + w; fx += Math.floor(w / 4))
      px(fx, y, 2, h, P.frame)
  }

  const bookshelf = (x: number, y: number, w: number, h: number) => {
    px(x, y, w, h, P.woodLo)
    px(x + 1, y + 1, w - 2, h - 2, '#6b4a2e')
    for (let sy = y + 4; sy < y + h - 3; sy += 10) {
      px(x + 1, sy + 8, w - 2, 2, P.woodLo)
      let bx2 = x + 3
      const cols = [P.book, P.book2, P.book3, P.paper]
      while (bx2 < x + w - 4) {
        const bw = 2 + ((bx2 + sy) % 3)
        px(bx2, sy, bw, 8, cols[(bx2 + sy) % 4]!)
        bx2 += bw + 1
      }
    }
  }

  const draw = (t: number) => {
    // ─── Zemin: sicak bej karo ────────────────────────────────────────────
    px(0, 0, W, H, P.floor)
    for (let y = 8; y < H; y += 12)
      for (let x = 4; x < W; x += 12) {
        px(x, y, 12, 12, ((x + y) / 4) % 2 ? P.floor : P.floorAlt)
        px(x, y, 12, 1, P.grout)
        px(x, y, 1, 12, P.grout)
      }

    // ─── Duvarlar ─────────────────────────────────────────────────────────
    px(0, 0, W, 8, P.wallOut); px(0, 6, W, 2, P.wallHi)
    px(0, 0, 4, H, P.wallOut); px(4, 0, 2, H, P.wallHi)
    px(W - 4, 0, 4, H, P.wallOut); px(W - 6, 0, 2, H, P.wallHi)
    px(0, H - 6, W, 6, P.wallOut)

    // ─── Ust serit: kahve bari · toplanti odasi · manzara · kitaplik ──────
    // Kahve bari
    px(6, 10, 62, 34, '#8a6a4a')
    px(6, 10, 62, 3, '#a2825f')
    px(10, 14, 30, 16, P.ink)                   // menu tahtasi
    px(13, 17, 8, 8, P.paper)
    for (let i = 0; i < 4; i++) px(24, 18 + i * 3, 12, 1, '#6e7683')
    px(44, 16, 20, 14, P.wood)                  // pasta rafi
    for (let i = 0; i < 3; i++) { px(46, 18 + i * 5, 16, 3, '#d99a5c'); px(46, 21 + i * 5, 16, 1, '#b8783f') }
    px(10, 32, 10, 12, P.panel)                 // espresso makinesi
    px(12, 34, 6, 5, P.ink)
    px(22, 34, 6, 10, P.panelLo)
    px(6, 44, 62, 6, P.woodLo)                  // tezgah
    for (const sx of [16, 32, 48]) { px(sx, 52, 10, 4, P.woodLo); px(sx + 3, 56, 4, 6, P.chairLo) }

    // Camli toplanti odasi
    px(76, 10, 62, 46, P.glass + '')
    px(78, 12, 58, 42, '#c9b995')
    px(76, 10, 62, 2, P.frame); px(76, 54, 62, 2, P.frame)
    px(76, 10, 2, 46, P.frame); px(136, 10, 2, 46, P.frame)
    for (const gx of [96, 116]) px(gx, 10, 2, 46, P.frame)
    px(92, 26, 30, 14, P.wood)                  // masa
    px(92, 26, 30, 2, P.woodHi)
    for (const cx of [88, 126]) chairTop(cx, 26)
    for (const cx of [100, 114]) { chairTop(cx, 14); chairTop(cx, 42) }
    px(105, 12, 4, 6, P.frame)                  // sarkit lamba
    px(101, 18, 12, 4, P.lamp)
    plant(107, 34)

    // Panoramik manzara
    window(146, 10, 104, 44)
    px(146, 56, 104, 6, P.wood)                 // pencere onu banko
    px(146, 56, 104, 2, P.woodHi)
    plant(156, 56, true)
    px(172, 48, 10, 8, P.book2); px(174, 48, 2, 8, P.book3)

    // Kitaplik (sag ust)
    bookshelf(256, 12, 18, 46)

    // ─── Sag: oturma kosesi ───────────────────────────────────────────────
    px(228, 80, 48, 42, P.rug)
    px(231, 83, 42, 36, P.rugHi)
    px(232, 66, 40, 16, P.sofa)                 // kanepe
    px(232, 66, 40, 3, P.sofaHi)
    px(228, 68, 5, 14, P.sofa); px(271, 68, 5, 14, P.sofa)
    px(252, 70, 12, 6, '#e8a05a')               // uyuyan kedi
    px(250, 72, 4, 4, '#e8a05a')
    px(240, 96, 26, 12, P.wood)                 // sehpa
    px(240, 96, 26, 2, P.woodHi)
    px(246, 99, 12, 6, P.ui)
    px(268, 56, 4, 14, P.panelLo)               // lambader
    px(263, 48, 14, 9, P.lamp)
    px(236, 30, 24, 18, P.wood)                 // cerceveli tablo
    px(238, 32, 20, 14, P.sky2)
    px(238, 40, 20, 6, P.city)

    // ─── Sol: pano, su sebili, cop ────────────────────────────────────────
    px(6, 96, 16, 30, P.woodLo)
    px(8, 98, 12, 26, '#8c7256')
    for (const [ny, nc] of [[100, '#e8d06a'], [108, '#e88ba8'], [116, '#8ad0e8']] as const)
      px(10, ny, 8, 6, nc)
    px(8, 140, 18, 10, '#9fd4e8')               // su sebili
    px(10, 150, 14, 18, P.panel)
    px(30, 152, 12, 14, P.chairLo)              // cop kovasi

    // ─── Sag alt: yazici istasyonu ────────────────────────────────────────
    px(238, 150, 38, 26, P.wood)
    px(238, 150, 38, 2, P.woodHi)
    px(248, 130, 26, 20, P.panel)               // yazici
    px(250, 134, 22, 8, P.ink)
    px(250, 144, 22, 4, P.paper)
    px(238, 122, 34, 4, P.woodLo)

    // ─── Alt: giris ve calilar ────────────────────────────────────────────
    px(116, H - 6, 48, 6, P.panelLo)
    px(122, H - 5, 36, 4, P.glassHi)
    for (let hx = 8; hx < W - 8; hx += 10) {
      if (hx > 110 && hx < 168) continue
      px(hx, H - 12, 8, 6, P.hedge)
      px(hx + 1, H - 13, 6, 2, P.leafHi)
    }

    // ─── Zemin bitkileri ──────────────────────────────────────────────────
    plant(22, 186, true, P.potT)
    plant(120, 178, true)
    plant(196, 178, true)
    plant(224, 40, true)

    // ─── Ekip ─────────────────────────────────────────────────────────────
    const HAIR = ['#4a3226', '#7a3f2c', '#2e2c3c', '#3b2b28', '#2b2b2b', '#5e4c3a']
    run.value.agents.forEach((a, i) => {
      const s = SEAT[a.key]
      if (!s) return
      desk(s.x, s.y, a.hex)
      const bob = Math.round(Math.sin(t * 2 + s.x) * 0.8)
      agent(s.x, s.y + 30, a.hex, HAIR[i % HAIR.length]!, bob)
      const on = (Math.sin(t * 3 + s.x) + 1) / 2 > 0.4
      px(s.x - 2, s.y - 20, 4, 4, on ? STATE_TINT[a.state] : P.panelLo)
    })
  }

  const resize = () => {
    const r = canvas.getBoundingClientRect()
    canvas.width = Math.max(1, Math.floor(r.width))
    canvas.height = Math.max(1, Math.floor(r.height))
    ctx.imageSmoothingEnabled = false
  }
  resize()
  const ro = new ResizeObserver(resize); ro.observe(canvas)

  let frame = 0
  const tick = () => {
    frame = requestAnimationFrame(tick)
    b.clearRect(0, 0, W, H)
    draw(performance.now() / 1000)

    const s = Math.max(1, Math.floor(Math.min(canvas.width / W, canvas.height / H)))
    const dw = W * s, dh = H * s
    ctx.imageSmoothingEnabled = false
    ctx.fillStyle = '#12101c'
    ctx.fillRect(0, 0, canvas.width, canvas.height)
    ctx.drawImage(buf, ((canvas.width - dw) / 2) | 0, ((canvas.height - dh) / 2) | 0, dw, dh)
  }
  tick()

  stop = () => { cancelAnimationFrame(frame); ro.disconnect() }
})

onBeforeUnmount(() => stop?.())
</script>

<style scoped>
.wrap { position: relative; width: 100%; height: 100%; }
.fill {
  width: 100%; height: 100%; display: block;
  image-rendering: pixelated; cursor: pointer;
}

.hud {
  position: absolute; left: 14px; bottom: 12px;
  display: flex; flex-wrap: wrap; gap: 6px; pointer-events: none;
}
.chip {
  background: #151a26cc; border: 1px solid #2b3550; border-radius: 999px;
  padding: 4px 11px; font-size: 12px; color: var(--ink); backdrop-filter: blur(6px);
}
.chip.note {
  border-color: color-mix(in srgb, var(--c) 55%, transparent);
  color: var(--c);
}
</style>
