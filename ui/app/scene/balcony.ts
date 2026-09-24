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

  update(dt: number, now: number, movers: Pt[]): void {
    const d = this.def.door
    const near = movers.some(p => p.x > d.x - 16 && p.x < d.x + d.w + 16 && p.y > d.y - 56 && p.y < d.y + d.h + 26)
    if (near) this.lastNear = now
    const target = this.held || now - this.lastNear < 1200 ? 1 : 0
    const step = dt / 0.45
    this.open = target > this.open ? Math.min(1, this.open + step) : Math.max(0, this.open - step)
  }

  /** Kapi kanatlari: arka planin ALTINA degil USTUNE, varliklardan once cizilir. */
  drawDoor(ctx: CanvasRenderingContext2D, bg: CanvasImageSource, sx: number, sy: number): void {
    const f = this.open
    if (f <= 0) return
    const d = this.def.door
    const fl = this.def.floor
    // Ust kasa (lento) arka planda kalir; kanatlar ve bosluk onun altinda.
    const lintel = 6
    const top = d.y + lintel
    const h = d.h - lintel
    ctx.save()
    ctx.beginPath()
    ctx.rect(d.x, top, d.w, h)
    ctx.clip()
    // Acik bosluk: balkonun zemini kapi yuksekligine yayilir (zemin kapidan disari devam eder).
    ctx.drawImage(bg, d.x * sx, (fl.y + 18) * sy, d.w * sx, (fl.h - 22) * sy, d.x, top, d.w, h)
    ctx.fillStyle = 'rgba(20,22,34,0.35)'
    ctx.fillRect(d.x, top, d.w, 3)
    // Kanatlar: sol yarim sola, sag yarim saga kayar; kenarlarda direklerin arkasina girer (kirpma).
    const half = d.w / 2
    // Kanatlar tam kaybolmaz: kenarlarda ust uste binmis cam okunur, kapi oldugu anlasilir.
    const shift = f * (half - 30)
    ctx.drawImage(bg, d.x * sx, top * sy, half * sx, h * sy, d.x - shift, top, half, h)
    ctx.drawImage(bg, (d.x + half) * sx, top * sy, half * sx, h * sy, d.x + half + shift, top, half, h)
    // Ray: kapinin altinda ince metal serit.
    ctx.fillStyle = 'rgba(150,158,178,0.55)'
    ctx.fillRect(d.x, top + h - 2, d.w, 2)
    ctx.restore()
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
