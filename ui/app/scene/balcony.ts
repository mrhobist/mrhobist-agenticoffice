import type { BalconyDef, Pt } from './contract'

/**
 * Balkon (kullanici istegi 2026-09-26): alt duvardaki cam surgulu kapi + balkon korkulugu. Kapi yaninda biri gecerken
 * kendiliginden acilir, bos kalinca kapanir; tiklaninca acik tutulur (yine de gecen olunca acilir: kimse camin icinden
 * yurumez). Kanatlar arka plan gorselinden kesilir ve iki yana kayar; acik boslukta balkon zemini gorunur.
 * Yalniz UI: sahne olayi yayimlanmaz.
 */
export class Balcony {
  /** 0 = kapali, 1 = tam acik. */
  open = 0
  /** Kullanici tikladi: acik tutulur. */
  held = false
  hovered = false
  private lastNear = Number.NEGATIVE_INFINITY

  constructor(readonly def: BalconyDef) {}

  /**
   * `movers`: sahnedeki herkes (+ kedi) ve yuruyor mu. Kapi, ona dogru YURUYEN biri kapiya ~80 px kala acilmaya baslar
   * (0.4 s'de tam acik: kisi cama varmadan). Duran kisi yalniz kapi boslugunun icindeyse tutar: balkonda sohbet eden ya da
   * iceride kapi onunde bekleyen kapiyi acik tutmaz.
   */
  update(dt: number, now: number, movers: Array<{ pos: Pt; walking: boolean }>): void {
    const d = this.def.door
    const near = movers.some(({ pos: p, walking }) => p.x > d.x - 16 && p.x < d.x + d.w + 16 && (walking
      ? p.y > d.y - 80 && p.y < d.y + d.h + 48
      : p.y > d.y - 8 && p.y < d.y + d.h + 10))
    if (near) this.lastNear = now
    const target = this.held || now - this.lastNear < 1200 ? 1 : 0
    // Hedefe varinca sabit kalir (onceden tam acikken her adimda "kapat" dalina dusup 1 <-> 0.93 titriyordu).
    if (this.open === target) return
    const step = dt / 0.4
    this.open = target > this.open ? Math.min(1, this.open + step) : Math.max(0, this.open - step)
  }

  /**
   * Kapi HER ZAMAN buradan cizilir (kapaliyken de): arka plandaki cam yalniz zemin gibi kullanilir. Onceden kanatlar arka plan
   * gorselinden kesiliyordu ve kapi kapaliyken arka plan, aciliyorken kesit gorunuyordu: acilmaya baslar baslamaz kenar
   * dikmeleri bir anda kayboluyor, kapi "sicriyordu" (kullanici 2026-09-26: "cikis animasyonu stabil degil"). Simdi iki cam
   * kanat (cerceve, tutamak, parilti) sabit kenar dikmelerinin arasinda ortadan iki yana kayar, kenarda ust uste biner.
   */
  drawDoor(ctx: CanvasRenderingContext2D, bg: CanvasImageSource, sx: number, sy: number): void {
    const d = this.def.door
    const fl = this.def.floor
    // Ust kasa (lento) arka planda kalir; kanatlar ve bosluk onun altinda.
    const top = d.y + 6
    const h = d.h - 6
    const post = 4
    const inner = { x: d.x + post, w: d.w - post * 2 }
    const half = inner.w / 2
    ctx.save()
    ctx.beginPath()
    ctx.rect(d.x, top, d.w, h)
    ctx.clip()
    // Kapinin ardinda balkon zemini: camdan da, acik bosluktan da o gorunur.
    ctx.drawImage(bg, d.x * sx, (fl.y + 18) * sy, d.w * sx, (fl.h - 22) * sy, d.x, top, d.w, h)
    ctx.fillStyle = 'rgba(20,22,34,0.35)'
    ctx.fillRect(d.x, top, d.w, 3)
    // Kanatlar: kenarda 22 px'lik yigin kalir (kapi oldugu okunur).
    ctx.beginPath()
    ctx.rect(inner.x, top, inner.w, h)
    ctx.clip()
    const shift = Math.round(this.open * (half - 22))
    this.leaf(ctx, Math.round(inner.x - shift), top, Math.round(half), h, 1)
    this.leaf(ctx, Math.round(inner.x + half + shift), top, Math.round(half), h, -1)
    ctx.restore()
    // Sabit kenar dikmeleri ve alt ray: kanatlarin USTUNDE (kanat onlarin arkasina girer).
    ctx.fillStyle = '#3d4356'
    ctx.fillRect(d.x, top, post, h)
    ctx.fillRect(d.x + d.w - post, top, post, h)
    ctx.fillStyle = 'rgba(200,208,224,0.5)'
    ctx.fillRect(d.x + post - 1, top, 1, h)
    ctx.fillRect(d.x + d.w - post, top, 1, h)
    ctx.fillStyle = '#4a5064'
    ctx.fillRect(d.x, top + h - 2, d.w, 2)
  }

  /** Tek cam kanat. `inward`: tutamagin oldugu (ortaya bakan) kenar; +1 = sag kenar. */
  private leaf(ctx: CanvasRenderingContext2D, x: number, y: number, w: number, h: number, inward: 1 | -1): void {
    ctx.fillStyle = 'rgba(150,188,222,0.38)'
    ctx.fillRect(x, y, w, h)
    // Parilti: iki egik serit.
    ctx.fillStyle = 'rgba(255,255,255,0.16)'
    for (const [ox, bw] of [[10, 8], [24, 3]] as const) {
      ctx.beginPath()
      ctx.moveTo(x + ox, y + h)
      ctx.lineTo(x + ox + bw, y + h)
      ctx.lineTo(x + ox + bw + 22, y)
      ctx.lineTo(x + ox + 22, y)
      ctx.closePath()
      ctx.fill()
    }
    // Cerceve.
    ctx.fillStyle = '#4f566b'
    ctx.fillRect(x, y, 3, h)
    ctx.fillRect(x + w - 3, y, 3, h)
    ctx.fillRect(x, y, w, 2)
    ctx.fillRect(x, y + h - 3, w, 3)
    // Tutamak: ortaya bakan kenarda.
    ctx.fillStyle = '#c9d0de'
    ctx.fillRect(inward > 0 ? x + w - 8 : x + 6, y + h / 2 - 7, 2, 14)
  }

  /** Korkuluk: balkonda duranlarin ONUNDE (alt kenara gore siralanir). Cam panel + ust tirabzan + dikmeler. */
  drawRail(ctx: CanvasRenderingContext2D): void {
    const r = this.def.rail
    ctx.save()
    ctx.fillStyle = 'rgba(170,205,235,0.20)'
    ctx.fillRect(r.x, r.y + 3, r.w, r.h - 3)
    ctx.fillStyle = 'rgba(255,255,255,0.18)'
    for (let x = r.x + 10; x < r.x + r.w - 10; x += 56) ctx.fillRect(x, r.y + 6, 3, r.h - 10)
    ctx.fillStyle = '#5d6478'
    for (let x = r.x; x <= r.x + r.w - 3; x += (r.w - 3) / 6) ctx.fillRect(Math.round(x), r.y, 3, r.h)
    ctx.fillStyle = '#8a93a8'
    ctx.fillRect(r.x - 2, r.y - 1, r.w + 4, 4)
    ctx.fillStyle = '#c9d0de'
    ctx.fillRect(r.x - 2, r.y - 1, r.w + 4, 1)
    ctx.fillStyle = '#434a5c'
    ctx.fillRect(r.x, r.y + r.h - 2, r.w, 2)
    ctx.restore()
  }

  /** Imlec kapinin ustundeyken cerceve + "Balkon kapısı · açık/kapalı" ipucu. */
  drawHover(ctx: CanvasRenderingContext2D): void {
    if (!this.hovered) return
    const d = this.def.door
    const text = `Balkon kapısı · ${this.held ? 'açık tutuluyor' : this.open > 0.5 ? 'açık' : 'kapalı'}`
    ctx.save()
    ctx.strokeStyle = this.held ? 'rgba(255,216,148,0.9)' : 'rgba(180,192,216,0.85)'
    ctx.lineWidth = 1.5
    ctx.setLineDash([4, 3])
    ctx.strokeRect(d.x, d.y, d.w, d.h)
    ctx.setLineDash([])
    ctx.font = '600 10px "Segoe UI", system-ui, sans-serif'
    ctx.textAlign = 'center'
    ctx.textBaseline = 'middle'
    const tw = ctx.measureText(text).width + 12
    ctx.fillStyle = 'rgba(20,24,34,0.85)'
    ctx.fillRect(d.x + d.w / 2 - tw / 2, d.y - 20, tw, 16)
    ctx.fillStyle = '#e8ecf5'
    ctx.fillText(text, d.x + d.w / 2, d.y - 11.5)
    ctx.restore()
  }

  hit(p: Pt): boolean {
    const d = this.def.door
    return p.x >= d.x && p.x <= d.x + d.w && p.y >= d.y && p.y <= d.y + d.h
  }

  /** Durulan kisi balkon zemininde mi (sohbet sayimi). */
  onFloor(p: Pt): boolean {
    const f = this.def.floor
    return p.x > f.x - 10 && p.x < f.x + f.w + 10 && p.y > f.y + 10 && p.y < f.y + f.h + 4
  }
}
