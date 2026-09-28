import type { CatBowlsDef, Pt } from './contract'

/**
 * Kedinin mama ve su kaplari (kullanici istegi 2026-09-25: "sol alt koseye yem ve su yeri, kedi arada gidip yesin icsin").
 * Tileset'te kap yok: kaplar ve paspas prosedurel cizilir (balkon kanatlari gibi). Doluluk yalniz UI'dadir: kedi yerken/icerken
 * azalir, sonra yavasca kendiliginden dolar (otomatik mamalik). Sunucu olayi yoktur.
 */

export type Bowl = 'food' | 'water'

/** Doluluk 0..1 (1 = dolu). Bir ogunde ~%35 azalir, ~4 dakikada kendiliginden dolar. */
const EAT_RATE = 0.05
const DRINK_RATE = 0.08
const REFILL_RATE = 1 / 240

/**
 * Kedinin kaba gore durdugu yer: iki kabin ARASINDA, yuzu kaba donuk (yan profil). Kedi one egilince basi kabin ustune
 * gelsin diye ayak ortasi kabin ~15 px yaninda ve ~5 px gerisinde (kap kedinin onune cizilir, bas kabin icinde gorunur).
 */
export function bowlStand(def: CatBowlsDef, bowl: Bowl): { pos: Pt; facing: 'left' | 'right' } {
  const b = def[bowl]
  const other = def[bowl === 'food' ? 'water' : 'food']
  const side = other.x >= b.x ? 1 : -1
  return { pos: { x: b.x + side * 15, y: b.y - 5 }, facing: side > 0 ? 'left' : 'right' }
}

/** Mama taneleri: sabit sozde-rastgele yerler (her karede ayni), doluluk kadari gorunur. */
const KIBBLE: ReadonlyArray<[number, number, number]> = (() => {
  const out: Array<[number, number, number]> = []
  let s = 7
  const rnd = () => { s = (s * 16807) % 2147483647; return s / 2147483647 }
  for (let i = 0; i < 44; i++) {
    const a = rnd() * Math.PI * 2
    const r = Math.sqrt(rnd())
    out.push([Math.cos(a) * r * 8.5, Math.sin(a) * r * 2.6, Math.floor(rnd() * 3)])
  }
  // Ortadakiler once: azalinca kenarlar bosalir, tepe en son gider.
  return out.sort((p, q) => Math.hypot(p[0], p[1] * 3) - Math.hypot(q[0], q[1] * 3))
})()
const KIBBLE_HEX = ['#8a5a2b', '#a8733a', '#6e4420']

export class CatBowls {
  food = 1
  water = 1
  /** Su icilirken halkalar: baslangic zamanlari. */
  private ripples: number[] = []
  private nextRippleAt = 0

  constructor(readonly def: CatBowlsDef) {}

  update(dt: number, now: number, catMode: string): void {
    if (catMode === 'eat') this.food = Math.max(0.08, this.food - EAT_RATE * dt)
    else this.food = Math.min(1, this.food + REFILL_RATE * dt)
    if (catMode === 'drink') {
      this.water = Math.max(0.15, this.water - DRINK_RATE * dt)
      if (now >= this.nextRippleAt) { this.ripples.push(now); this.nextRippleAt = now + 450 + Math.random() * 250 }
    } else {
      this.water = Math.min(1, this.water + REFILL_RATE * dt)
    }
    this.ripples = this.ripples.filter(t => now - t < 1200)
  }

  /** Paspas: zemin seviyesinde, her seyin altinda. */
  drawMat(ctx: CanvasRenderingContext2D): void {
    const [x, y, w, h] = this.def.mat
    ctx.save()
    ctx.fillStyle = 'rgba(30,30,40,0.18)'
    ctx.beginPath(); ctx.roundRect(x + 1, y + 2, w, h, 6); ctx.fill()
    ctx.fillStyle = '#5f8f8a'
    ctx.beginPath(); ctx.roundRect(x, y, w, h, 6); ctx.fill()
    ctx.strokeStyle = '#86b5ab'
    ctx.lineWidth = 1.5
    ctx.setLineDash([3, 2.5])
    ctx.beginPath(); ctx.roundRect(x + 3, y + 3, w - 6, h - 6, 4); ctx.stroke()
    ctx.setLineDash([])
    // Ortada kucuk pati izi.
    const px = x + w / 2
    const py = y + h / 2 + 1
    ctx.fillStyle = '#86b5ab'
    ctx.beginPath(); ctx.ellipse(px, py + 1.5, 3, 2.2, 0, 0, Math.PI * 2); ctx.fill()
    for (const [dx, dy] of [[-3.6, -2], [-1.2, -3.4], [1.2, -3.4], [3.6, -2]] as const) {
      ctx.beginPath(); ctx.ellipse(px + dx, py + dy, 1.1, 1, 0, 0, Math.PI * 2); ctx.fill()
    }
    ctx.restore()
  }

  /** Siralama anahtari: kabin on kenari. Kedi (ayak y = kap y - 5) kabin ARKASINDA kalir. */
  sortY(bowl: Bowl): number { return this.def[bowl].y + 2 }

  drawBowl(ctx: CanvasRenderingContext2D, bowl: Bowl, now: number): void {
    const { x, y } = this.def[bowl]
    const food = bowl === 'food'
    const body = food ? '#c85a4a' : '#4f7fc9'
    const rim = food ? '#e07d6c' : '#79a3e3'
    const inner = food ? '#6b2a22' : '#243f66'
    const top = y - 6
    ctx.save()
    // Golge
    ctx.fillStyle = 'rgba(30,30,40,0.28)'
    ctx.beginPath(); ctx.ellipse(x, y, 14, 3, 0, 0, Math.PI * 2); ctx.fill()
    // Govde: alt elips + yanlar
    ctx.fillStyle = body
    ctx.beginPath(); ctx.ellipse(x, y - 1, 11, 3, 0, 0, Math.PI * 2); ctx.fill()
    ctx.beginPath()
    ctx.moveTo(x - 13, top); ctx.lineTo(x + 13, top); ctx.lineTo(x + 11, y - 1); ctx.lineTo(x - 11, y - 1)
    ctx.closePath(); ctx.fill()
    // Agiz kenari + ic
    ctx.fillStyle = rim
    ctx.beginPath(); ctx.ellipse(x, top, 13, 5, 0, 0, Math.PI * 2); ctx.fill()
    ctx.fillStyle = inner
    ctx.beginPath(); ctx.ellipse(x, top, 10.5, 3.5, 0, 0, Math.PI * 2); ctx.fill()
    ctx.beginPath(); ctx.ellipse(x, top, 10.5, 3.5, 0, 0, Math.PI * 2); ctx.clip()
    if (food) {
      const n = Math.round(KIBBLE.length * this.food)
      for (let i = 0; i < n; i++) {
        const [kx, ky, c] = KIBBLE[i]!
        ctx.fillStyle = KIBBLE_HEX[c]!
        ctx.fillRect(x + kx - 1.4, top + ky - 1.1, 2.8, 2.2)
      }
    } else {
      // Su yuzeyi doluluk kadar genis; ustunde kayan parilti ve icilirken halkalar.
      const k = 0.55 + 0.45 * this.water
      ctx.fillStyle = '#7cc8ee'
      ctx.beginPath(); ctx.ellipse(x, top + (1 - k) * 1.2, 10.5 * k, 3.5 * k, 0, 0, Math.PI * 2); ctx.fill()
      const g = (now / 1600) % 1
      ctx.fillStyle = 'rgba(255,255,255,0.7)'
      ctx.fillRect(x - 6 + g * 8, top - 1.2, 3, 1)
      ctx.strokeStyle = 'rgba(255,255,255,0.75)'
      ctx.lineWidth = 0.8
      for (const t0 of this.ripples) {
        const t = (now - t0) / 1200
        ctx.globalAlpha = 1 - t
        ctx.beginPath(); ctx.ellipse(x, top, 2 + t * 8, 0.7 + t * 2.6, 0, 0, Math.PI * 2); ctx.stroke()
      }
    }
    ctx.restore()
  }
}
