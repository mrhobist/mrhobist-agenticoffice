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
