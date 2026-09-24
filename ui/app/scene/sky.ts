/**
 * Pencere manzarasi: saate gore gokyuzu, gunes/ay, sehir silueti ve su.
 * Arka plan gorselinde cam seffaftir (build-sprites key_window_glass); bu katman
 * arka planin ALTINA cizilir, dolayisiyla cam cerceveleri ve onundeki bitkiler
 * dokunulmadan kalir. Tamamen prosedurel; asset yok.
 */

interface Rgb { r: number; g: number; b: number }

interface SkyKey { hour: number; top: Rgb; bottom: Rgb; water: Rgb; light: number }

const rgb = (r: number, g: number, b: number): Rgb => ({ r, g, b })

/** Gunun anahtar kareleri; aralar dogrusal karistirilir. */
const KEYS: SkyKey[] = [
  { hour: 0, top: rgb(14, 18, 42), bottom: rgb(32, 38, 78), water: rgb(24, 30, 66), light: 0 },
  { hour: 5, top: rgb(24, 28, 62), bottom: rgb(70, 60, 100), water: rgb(40, 44, 84), light: 0.1 },
  { hour: 6.5, top: rgb(90, 90, 160), bottom: rgb(245, 160, 110), water: rgb(120, 110, 150), light: 0.55 },
  { hour: 8, top: rgb(110, 165, 235), bottom: rgb(200, 220, 245), water: rgb(90, 130, 190), light: 1 },
  { hour: 13, top: rgb(80, 150, 235), bottom: rgb(180, 215, 245), water: rgb(70, 125, 195), light: 1 },
  { hour: 17.5, top: rgb(110, 130, 210), bottom: rgb(250, 175, 110), water: rgb(120, 120, 170), light: 0.8 },
  { hour: 19, top: rgb(90, 70, 150), bottom: rgb(240, 120, 130), water: rgb(100, 80, 140), light: 0.45 },
  { hour: 20.5, top: rgb(30, 32, 80), bottom: rgb(120, 70, 120), water: rgb(50, 46, 100), light: 0.12 },
  { hour: 24, top: rgb(14, 18, 42), bottom: rgb(32, 38, 78), water: rgb(24, 30, 66), light: 0 },
]

function mix(a: Rgb, b: Rgb, t: number): Rgb {
  return { r: a.r + (b.r - a.r) * t, g: a.g + (b.g - a.g) * t, b: a.b + (b.b - a.b) * t }
}
const css = (c: Rgb, a = 1) => `rgba(${c.r | 0},${c.g | 0},${c.b | 0},${a})`

function at(hour: number): SkyKey {
  const h = ((hour % 24) + 24) % 24
  for (let i = 0; i < KEYS.length - 1; i++) {
    const a = KEYS[i]!
    const b = KEYS[i + 1]!
    if (h >= a.hour && h <= b.hour) {
      const t = (h - a.hour) / (b.hour - a.hour)
      return { hour: h, top: mix(a.top, b.top, t), bottom: mix(a.bottom, b.bottom, t), water: mix(a.water, b.water, t), light: a.light + (b.light - a.light) * t }
    }
  }
  return KEYS[0]!
}

/** Deterministik sehir: ayni tohum ayni siluet; pencere isiklari da sabit. */
function buildings(w: number, seed = 7): Array<{ x: number; w: number; h: number; lit: number[] }> {
  let s = seed
  const rnd = () => { s = (s * 1103515245 + 12345) & 0x7fffffff; return s / 0x7fffffff }
  const out: Array<{ x: number; w: number; h: number; lit: number[] }> = []
  let x = -6
  while (x < w + 6) {
    const bw = 16 + Math.floor(rnd() * 26)
    const bh = 30 + Math.floor(rnd() * 90)
    const lit: number[] = []
    for (let i = 0; i < 40; i++) if (rnd() < 0.35) lit.push(i)
    out.push({ x, w: bw, h: bh, lit })
    x += bw + 2 + Math.floor(rnd() * 6)
  }
  return out
}

let cache: { key: string; canvas: HTMLCanvasElement } | null = null

/**
 * Pencere dikdortgenine gokyuzunu cizer. Dakikada bir yeniden uretilir (saat degisimi
 * yavas), arada onbellekten kopyalanir.
 */
export function drawSky(ctx: CanvasRenderingContext2D, rect: { x: number; y: number; w: number; h: number }, hour: number, scale: number): void {
  const key = `${rect.w}x${rect.h}@${scale.toFixed(2)}:${Math.round(hour * 60)}`
  if (!cache || cache.key !== key) {
    const c = document.createElement('canvas')
    c.width = Math.ceil(rect.w * scale)
    c.height = Math.ceil(rect.h * scale)
    const g = c.getContext('2d')!
    g.scale(scale, scale)
    paint(g, rect.w, rect.h, hour)
    cache = { key, canvas: c }
  }
  ctx.drawImage(cache.canvas, rect.x, rect.y, rect.w, rect.h)
}

function paint(g: CanvasRenderingContext2D, w: number, h: number, hour: number): void {
  const k = at(hour)
  const horizon = h * 0.72
  const grad = g.createLinearGradient(0, 0, 0, horizon)
  grad.addColorStop(0, css(k.top))
  grad.addColorStop(1, css(k.bottom))
  g.fillStyle = grad
  g.fillRect(0, 0, w, horizon)

  // Yildizlar (gece)
  if (k.light < 0.3) {
    g.fillStyle = `rgba(255,255,255,${(0.3 - k.light) * 2.5})`
    let s = 11
    for (let i = 0; i < 60; i++) {
      s = (s * 1103515245 + 12345) & 0x7fffffff
      const x = (s % 1000) / 1000 * w
      s = (s * 1103515245 + 12345) & 0x7fffffff
      const y = (s % 1000) / 1000 * horizon * 0.8
      g.fillRect(x, y, 1.5, 1.5)
    }
  }

  // Gunes 6-19 arasi bir yay cizer; ay gece.
  const day = hour >= 5.5 && hour <= 19.5
  const t = day ? (hour - 5.5) / 14 : ((hour + 4.5) % 24) / 10
  const cx = w * (0.1 + 0.8 * Math.min(1, Math.max(0, t)))
  const cy = horizon - Math.sin(Math.PI * Math.min(1, Math.max(0, t))) * horizon * 0.75 + 6
  if (day) {
    const r = 12
    const glow = g.createRadialGradient(cx, cy, 2, cx, cy, r * 4)
    glow.addColorStop(0, 'rgba(255,240,200,0.55)')
    glow.addColorStop(1, 'rgba(255,240,200,0)')
    g.fillStyle = glow
    g.fillRect(cx - r * 4, cy - r * 4, r * 8, r * 8)
    g.fillStyle = k.light > 0.9 ? '#fff6d6' : '#ffd98a'
    g.beginPath(); g.arc(cx, cy, r, 0, Math.PI * 2); g.fill()
  } else {
    g.fillStyle = '#e8ecf5'
    g.beginPath(); g.arc(cx, cy, 9, 0, Math.PI * 2); g.fill()
    g.fillStyle = css(k.top)
    g.beginPath(); g.arc(cx + 4, cy - 3, 8, 0, Math.PI * 2); g.fill()
  }

  // Sehir silueti: iki katman
  const far = mix(k.bottom, rgb(40, 40, 90), 0.55)
  const near = mix(k.top, rgb(20, 22, 50), 0.7)
  for (const b of buildings(w, 3)) {
    g.fillStyle = css(far)
    g.fillRect(b.x + 6, horizon - b.h * 0.7, b.w, b.h * 0.7)
  }
  for (const b of buildings(w, 7)) {
    g.fillStyle = css(near)
    g.fillRect(b.x, horizon - b.h, b.w, b.h)
    // Pencereler: gece yanar, gunduz sonuk
    const cols = Math.max(1, Math.floor((b.w - 4) / 5))
    const rows = Math.max(1, Math.floor((b.h - 6) / 7))
    const litAlpha = Math.max(0, 0.9 - k.light)
    for (let i = 0; i < cols * rows && i < 40; i++) {
      const on = b.lit.includes(i)
      g.fillStyle = on ? `rgba(255,220,140,${litAlpha})` : 'rgba(0,0,0,0.12)'
      g.fillRect(b.x + 2 + (i % cols) * 5, horizon - b.h + 3 + Math.floor(i / cols) * 7, 3, 4)
    }
  }

  // Su ve yansima
  g.fillStyle = css(k.water)
  g.fillRect(0, horizon, w, h - horizon)
  g.fillStyle = css(mix(k.bottom, rgb(255, 255, 255), 0.2), 0.25)
  for (let y = horizon + 3; y < h; y += 5) {
    const len = 14 + ((y * 7) % 20)
    g.fillRect(cx - len / 2 + ((y * 3) % 9) - 4, y, len, 1.5)
  }
  g.fillStyle = 'rgba(255,255,255,0.10)'
  for (let y = horizon + 1; y < h; y += 4) g.fillRect(0, y, w, 1)
}

// ------------------------------------------------------------------ vapur

/** Karsiya gecis ve iskelede bekleme suresi: vapur gider, bekler, doner, bekler. */
const FERRY_CROSS_MS = 80_000
const FERRY_DOCK_MS = 20_000

/**
 * Vapur (kullanici istegi 2026-09-26: "camdan disarida denizde vapur gidip gelsin"): pencerenin denizinde ufuk cizgisinin
 * hemen altinda soldan saga gider, pencere disinda bekler, sagdan sola doner. Konum duvar saatinden (Date.now) hesaplanir:
 * sayfa yenilense de iki sekmede de ayni yerdedir. Sehir Hatlari renkleri: beyaz govde, koyu karina, sari baca.
 * Gece pencereleri ve direk feneri yanar. Gokyuzunun USTUNE, arka planin ALTINA cizilir (pencere kayitlari onunde kalir).
 */
export function drawFerry(ctx: CanvasRenderingContext2D, rect: { x: number; y: number; w: number; h: number }, hour: number, wallMs: number): void {
  const period = 2 * (FERRY_CROSS_MS + FERRY_DOCK_MS)
  const t = wallMs % period
  let dir: 1 | -1
  let p: number
  if (t < FERRY_CROSS_MS) { dir = 1; p = t / FERRY_CROSS_MS }
  else if (t < FERRY_CROSS_MS + FERRY_DOCK_MS) return
  else if (t < 2 * FERRY_CROSS_MS + FERRY_DOCK_MS) { dir = -1; p = (t - FERRY_CROSS_MS - FERRY_DOCK_MS) / FERRY_CROSS_MS }
  else return

  // Olcek: sehir binalari 16-42 px; 1.5 kat vapur ufukta okunur ama binalardan kucuk kalir.
  const k1 = 1.5
  const len = 46 * k1
  const span = rect.w + len * 2
  const cx = Math.round(dir > 0 ? rect.x - len + p * span : rect.x + rect.w + len - p * span)
  const wl = Math.round(rect.y + rect.h * 0.72 + 14)
  const k = at(hour)
  const night = Math.max(0, 1 - k.light)

  // Yerel koordinat: pruva +x, yukari -y. `dir` ile aynalanir.
  const box = (x: number, y: number, w: number, h: number, color: string) => {
    ctx.fillStyle = color
    ctx.fillRect(dir > 0 ? cx + x * k1 : cx - (x + w) * k1, wl + y * k1, w * k1, h * k1)
  }
  const body: Array<[number, number, number, number, string]> = [
    [-21, -3, 42, 3, '#2b2f3a'],
    [-22, -6, 42, 3, '#f4f3ee'],
    [20, -6, 3, 2, '#f4f3ee'],
    [-22, -4, 44, 1, '#c8352e'],
    [-16, -11, 30, 5, '#f7f6f1'],
    [-10, -14, 16, 3, '#e8e7e1'],
    [-6, -19, 5, 5, '#f2c230'],
    [-6, -19, 5, 1, '#1f2126'],
  ]

  ctx.save()
  ctx.beginPath()
  ctx.rect(rect.x, rect.y, rect.w, rect.h)
  ctx.clip()

  // Kopuk izi: kicin arkasinda sonen beyaz cizgiler.
  const shimmer = (wallMs / 400) % 1
  ctx.globalAlpha = 0.45
  box(-40 - shimmer * 4, 0, 18, 1, 'rgba(255,255,255,0.8)')
  ctx.globalAlpha = 0.28
  box(-52 - shimmer * 6, 1, 14, 1, 'rgba(255,255,255,0.8)')
  box(22, 0, 4, 1, 'rgba(255,255,255,0.8)')
  ctx.globalAlpha = 1

  for (const [x, y, w, h, c] of body) box(x, y, w, h, c)
  // Gece govde koyulasir (golge tonu), sonra yanan pencereler ustune.
  if (night > 0) for (const [x, y, w, h] of body) box(x, y, w, h, `rgba(12,16,42,${0.6 * night})`)
  const win = night > 0.4 ? '#ffd98a' : '#3b4a66'
  for (let i = 0; i < 7; i++) box(-14 + i * 4, -10, 2, 2, win)
  box(2, -13, 3, 1, win)

  // Baca dumani: arkaya ve yukari suzulur, soner.
  for (let i = 0; i < 3; i++) {
    const age = ((wallMs / 1000 + i * 0.9) % 2.7) / 2.7
    const lx = -4 - age * 16
    const ly = -21 - age * 9
    const r = 1.5 + age * 2.8
    ctx.fillStyle = night > 0.5 ? `rgba(150,150,165,${0.35 * (1 - age)})` : `rgba(240,240,245,${0.5 * (1 - age)})`
    ctx.beginPath()
    ctx.arc(dir > 0 ? cx + lx * k1 : cx - lx * k1, wl + ly * k1, r * k1, 0, Math.PI * 2)
    ctx.fill()
  }

  // Direk feneri ve pruva feneri (gece).
  if (night > 0.4) {
    box(0, -17, 1, 1, '#fff6d6')
    box(21, -7, 1, 1, dir > 0 ? '#5fe07a' : '#ff5a4f')
  }
  ctx.restore()
}
