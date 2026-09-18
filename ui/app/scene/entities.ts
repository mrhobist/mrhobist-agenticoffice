import type { AgentDef, AgentState, BubbleKind, CatAction, DoorState, Facing, Pt, SeatDef } from './contract'
import { ROLE_HEX, STATE_HEX } from './contract'
import type { CharacterSheets, Sprites } from './atlas'
import type { NavGrid } from './nav'

/** Sirali eylem kuyrugu: yonetmen (director) bunlari ekler, update tuketir. */
export type Action =
  /** Hedef, eylem basladiginda cozulur (durak dolu mu bosta mi o anda belli olur). */
  | { t: 'walk'; to: Pt | (() => Pt) }
  | { t: 'wait'; ms: number }
  | { t: 'face'; dir: Facing }
  | { t: 'say'; kind: BubbleKind; ms: number; text?: string }
  | { t: 'sit'; seat: SeatDef }
  | { t: 'stand' }
  | { t: 'call'; fn: () => void }
  /** Kosul saglanana (ya da sure dolana) kadar bekle: dolu durak bosalsin. */
  | { t: 'until'; pred: () => boolean; timeoutMs: number }

const WALK_SPEED = 96 // dunya px / s
const WALK_FPS = 9
const TYPE_FPS = 4

/** 8 yon: yatay/dikey baskinsa duz, degilse capraz. */
function faceTowards(from: Pt, to: Pt): Facing {
  const dx = to.x - from.x
  const dy = to.y - from.y
  const ax = Math.abs(dx)
  const ay = Math.abs(dy)
  if (ax < 1e-3 && ay < 1e-3) return 'down'
  const diagonal = Math.min(ax, ay) / Math.max(ax, ay) > 0.45
  if (!diagonal) {
    if (ax > ay) return dx > 0 ? 'right' : 'left'
    return dy > 0 ? 'down' : 'up'
  }
  if (dy > 0) return dx > 0 ? 'downright' : 'downleft'
  return dx > 0 ? 'upright' : 'upleft'
}

export class Agent {
  pos: Pt
  facing: Facing = 'down'
  state: AgentState = 'idle'
  note: string | null = null
  seated: SeatDef | null = null
  bubble: { kind: BubbleKind; until: number; text?: string } | null = null
  /** Kuyruk arka uctan gelen bir komutla temizlenir; ambient eylemler bu bayragi tasir. */
  queue: Action[] = []
  ambient = false
  /** Bu zamandan once ambient davranis baslamaz. */
  ambientReadyAt = 0
  lastCommandAt = 0
  /** Yuruyus hedefi (baskalari ayni noktaya gitmesin diye dunya bunu okur). */
  target: Pt | null = null
  /** Kapidan cikti: cizilmez, ambient almaz. */
  offstage = false
  /** Su an tuttugu/hedefledigi durak (kapasite sayimi icin). */
  spot: string | null = null

  private path: Pt[] = []
  private walkT = 0
  private waitUntil = 0
  private current: Action | null = null

  constructor(
    readonly def: AgentDef,
    readonly sheets: CharacterSheets,
    start: Pt,
  ) {
    this.pos = { ...start }
  }

  get key() { return this.def.key }
  get hex() { return ROLE_HEX[this.def.key] ?? '#8a93a8' }
  get busy() { return this.current !== null || this.queue.length > 0 }
  get walking() { return this.current?.t === 'walk' }

  /** Ciziliste kullanilan siralama anahtari: oturuyorsa masanin hemen ustune. */
  sortY(propBottom: number | null): number {
    return this.seated && propBottom !== null ? propBottom + 0.5 : this.pos.y
  }

  /** Bir komut icin kuyrugu sifirla (ambient dahil) ve yeni eylemleri koy. */
  command(actions: Action[], now: number): void {
    this.spot = null
    this.queue = actions
    this.current = null
    this.path = []
    this.ambient = false
    this.lastCommandAt = now
    this.bubble = null
  }

  enqueueAmbient(actions: Action[], now: number): void {
    this.spot = null
    this.queue = actions
    this.current = null
    this.ambient = true
    this.ambientReadyAt = now + 25_000 + Math.random() * 45_000
  }

  update(dt: number, now: number, nav: NavGrid): void {
    if (this.bubble && now > this.bubble.until) this.bubble = null

    if (!this.current) {
      this.current = this.queue.shift() ?? null
      if (!this.current) return
      this.begin(this.current, now, nav)
    }
    const a = this.current
    switch (a.t) {
      case 'walk': {
        const next = this.path[0]
        if (!next) { this.finish(); break }
        const dx = next.x - this.pos.x
        const dy = next.y - this.pos.y
        const dist = Math.hypot(dx, dy)
        const step = WALK_SPEED * dt
        if (dist <= step) {
          this.pos = { ...next }
          this.path.shift()
          if (!this.path.length) this.finish()
        } else {
          this.pos.x += (dx / dist) * step
          this.pos.y += (dy / dist) * step
          this.facing = faceTowards({ x: 0, y: 0 }, { x: dx, y: dy })
        }
        this.walkT += dt
        break
      }
      case 'wait':
      case 'say':
        if (now >= this.waitUntil) this.finish()
        break
      case 'until':
        if (a.pred() || now >= this.waitUntil) this.finish()
        break
      default:
        this.finish()
    }
  }

  private begin(a: Action, now: number, nav: NavGrid): void {
    switch (a.t) {
      case 'walk':
        if (this.seated) {
          // Kalk: koltuktan en yakin acik hucreye kay, sonra yuru.
          this.pos = nav.nearestOpen(this.seated)
          this.seated = null
        }
        {
          const to = typeof a.to === 'function' ? a.to() : a.to
          this.path = nav.path(this.pos, to)
          this.target = to
          this.walkT = 0
        }
        break
      case 'wait':
        this.waitUntil = now + a.ms
        break
      case 'until':
        this.waitUntil = now + a.timeoutMs
        break
      case 'say':
        this.bubble = { kind: a.kind, until: now + a.ms, text: a.text }
        this.waitUntil = now + a.ms
        break
      case 'face':
        this.facing = a.dir
        break
      case 'sit':
        this.seated = a.seat
        this.pos = { x: a.seat.x, y: a.seat.y }
        this.facing = a.seat.facing
        break
      case 'stand':
        if (this.seated) {
          this.pos = nav.nearestOpen(this.seated)
          this.facing = 'down'
          this.seated = null
        }
        break
      case 'call':
        a.fn()
        break
    }
  }

  private finish(): void {
    if (this.current?.t === 'walk') this.target = null
    this.current = null
    if (!this.queue.length) this.ambient = false
  }

  faceTo(p: Pt): void {
    this.facing = faceTowards(this.pos, p)
  }

  draw(ctx: CanvasRenderingContext2D, sprites: Sprites, now: number): void {
    if (this.offstage) return
    if (this.seated) {
      this.drawSeated(ctx, sprites, now)
    } else {
      const walk = this.sheets.walk
      const row = walk.dirRows?.[this.facing] ?? 0
      const frame = this.walking ? Math.floor(this.walkT * WALK_FPS) % walk.cols : 0
      ctx.fillStyle = 'rgba(30,30,40,0.28)'
      ctx.beginPath()
      ctx.ellipse(this.pos.x, this.pos.y - 1, 14, 4.5, 0, 0, Math.PI * 2)
      ctx.fill()
      sprites.drawFrame(ctx, walk, frame, row, this.pos.x, this.pos.y)
    }

    // Durum noktasi (calisiyor: nabiz)
    if (this.state !== 'idle') {
      const top = this.headY()
      const pulse = this.state === 'working' || this.state === 'thinking' ? 0.6 + 0.4 * Math.sin(now / 220) : 1
      ctx.globalAlpha = pulse
      ctx.fillStyle = STATE_HEX[this.state]
      ctx.fillRect(this.pos.x - 3, top - 9, 6, 6)
      ctx.globalAlpha = 1
    }
  }

  /**
   * Oturma: sandalye karenin icindedir (V2 katalog). Arkasi donukse "typing" arka kareleri
   * (3-5), calisirken animasyonlu; yuzu donukse on kareler (0-2); yandan ise sit karesi 3,
   * sola bakiyorsa aynalanir.
   */
  private drawSeated(ctx: CanvasRenderingContext2D, sprites: Sprites, now: number): void {
    const busy = this.state === 'working' || this.state === 'thinking'
    const f = this.facing
    if (f === 'up' || f === 'upleft' || f === 'upright') {
      const frame = busy ? 3 + (Math.floor(now / (1000 / TYPE_FPS)) % 3) : 3
      sprites.drawFrame(ctx, this.sheets.type, frame, 0, this.pos.x, this.pos.y)
    } else if (f === 'down' || f === 'downleft' || f === 'downright') {
      const frame = busy ? Math.floor(now / (1000 / TYPE_FPS)) % 3 : 0
      sprites.drawFrame(ctx, this.sheets.type, frame, 0, this.pos.x, this.pos.y)
    } else {
      sprites.drawFrame(ctx, this.sheets.sit, 3, 0, this.pos.x, this.pos.y, { flipX: f === 'left' })
    }
  }

  /** Basin ustu (dunya). Balon ve etiket buraya asilir. */
  headY(): number {
    const meta = this.seated ? this.sheets.type : this.sheets.walk
    return this.pos.y - meta.frameH / 2 + 4
  }

  drawBubble(ctx: CanvasRenderingContext2D, sprites: Sprites, now: number): void {
    if (!this.bubble || this.offstage) return
    const meta = sprites.atlas.fx.bubble
    const x = this.pos.x + 20
    const y = this.headY() - 2
    if (this.bubble.kind === 'talk' && !this.bubble.text) {
      const f = Math.floor(now / 380) % meta.cols
      sprites.drawFrame(ctx, meta, f, 0, x, y)
      return
    }
    sprites.drawFrame(ctx, meta, 0, 0, x, y)
    const glyph = this.bubble.text ?? (this.bubble.kind === 'ask' ? '?' : '!')
    ctx.font = 'bold 15px "Segoe UI", system-ui, sans-serif'
    ctx.textAlign = 'center'
    ctx.textBaseline = 'middle'
    ctx.fillStyle = this.bubble.kind === 'alert' ? '#d23b3b' : '#2a2f3d'
    const bw = meta.frameW / sprites.worldScale
    const bh = meta.frameH / sprites.worldScale
    ctx.fillText(glyph, x, y - bh * 0.56 - 1, bw - 10)
  }
}

// --------------------------------------------------------------------------- //

type CatMode = 'sleep' | 'walk' | 'sit' | 'lie'

export class Cat {
  pos: Pt
  facing: Facing = 'down'
  mode: CatMode = 'sleep'
  private path: Pt[] = []
  private walkT = 0
  private nextAt = 0
  private plan: Array<() => void> = []

  constructor(readonly bed: Pt, readonly spots: Pt[]) {
    this.pos = { ...bed }
    this.nextAt = performance.now() + 45_000 + Math.random() * 60_000
  }

  /** Arka uctan komut: plani sifirla. */
  command(action: CatAction, spotIdx: number | undefined, now: number, nav: NavGrid): void {
    this.plan = []
    if (action === 'sleep') { this.goto(this.bed, nav, () => { this.mode = 'sleep'; this.nextAt = performance.now() + 120_000 }) }
    else if (action === 'sit') { this.mode = 'sit'; this.nextAt = now + 15_000 }
    else {
      const s = this.spots[spotIdx ?? Math.floor(Math.random() * this.spots.length)] ?? this.bed
      this.goto(s, nav, () => { this.mode = 'sit'; this.nextAt = now + 8_000 })
    }
  }

  /** Hedef engelli alandaysa (koltuk) yol en yakin acik hucrede biter; varinca hedefe kayilir. */
  private snapTo: Pt | null = null

  private goto(to: Pt, nav: NavGrid, then: () => void): void {
    this.mode = 'walk'
    this.path = nav.path(this.pos, to)
    this.snapTo = nav.isOpenAt(to) ? null : to
    this.walkT = 0
    this.plan = [then]
  }

  private arrived(now: number, fallbackMs: number): void {
    if (this.snapTo) { this.pos = { ...this.snapTo }; this.snapTo = null }
    this.mode = 'sit'
    this.nextAt = now + fallbackMs
    const next = this.plan.shift()
    if (next) next()
  }

  update(dt: number, now: number, nav: NavGrid): void {
    if (this.mode === 'walk') {
      const next = this.path[0]
      if (!next) { this.arrived(now, 5000); return }
      const dx = next.x - this.pos.x
      const dy = next.y - this.pos.y
      const dist = Math.hypot(dx, dy)
      const step = 70 * dt
      if (dist <= step) {
        this.pos = { ...next }
        this.path.shift()
        if (!this.path.length) this.arrived(now, 6000 + Math.random() * 6000)
      } else {
        this.pos.x += (dx / dist) * step
        this.pos.y += (dy / dist) * step
        this.facing = faceTowards({ x: 0, y: 0 }, { x: dx, y: dy })
      }
      this.walkT += dt
      return
    }
    if (now < this.nextAt) return
    // Kendi ritmi: uyu -> gez -> otur -> (bazen uzan) -> yataga don.
    if (this.mode === 'sleep') {
      const s = this.spots[Math.floor(Math.random() * this.spots.length)] ?? this.bed
      this.goto(s, nav, () => { this.mode = Math.random() < 0.3 ? 'lie' : 'sit'; this.nextAt = now + 8000 + Math.random() * 10_000 })
    } else if (Math.random() < 0.35) {
      const s = this.spots[Math.floor(Math.random() * this.spots.length)] ?? this.bed
      this.goto(s, nav, () => { this.mode = 'sit'; this.nextAt = now + 6000 + Math.random() * 8000 })
    } else {
      this.goto(this.bed, nav, () => { this.mode = 'sleep'; this.nextAt = performance.now() + 60_000 + Math.random() * 90_000 })
    }
  }

  draw(ctx: CanvasRenderingContext2D, sprites: Sprites, now: number): void {
    const cat = sprites.atlas.cat
    if (this.mode === 'sleep') {
      sprites.drawSingle(ctx, cat.sleep, this.pos.x, this.pos.y)
      // zZz
      const t = (now / 900) % 3
      ctx.font = 'bold 10px "Segoe UI", system-ui, sans-serif'
      ctx.fillStyle = 'rgba(255,255,255,0.85)'
      ctx.textAlign = 'left'
      ctx.textBaseline = 'alphabetic'
      for (let i = 0; i < 3; i++) {
        const a = Math.max(0, 1 - Math.abs(t - i)) * 0.9
        ctx.globalAlpha = a
        ctx.fillText('z', this.pos.x + 14 + i * 7, this.pos.y - 36 - i * 8)
      }
      ctx.globalAlpha = 1
      return
    }
    ctx.fillStyle = 'rgba(30,30,40,0.25)'
    ctx.beginPath()
    ctx.ellipse(this.pos.x, this.pos.y - 1, 12, 4, 0, 0, Math.PI * 2)
    ctx.fill()
    if (this.mode === 'sit') { sprites.drawSingle(ctx, cat.sit, this.pos.x, this.pos.y); return }
    if (this.mode === 'lie') { sprites.drawSingle(ctx, cat.lie, this.pos.x, this.pos.y); return }
    const row = cat.walk.dirRows?.[this.facing] ?? 0
    const frame = Math.floor(this.walkT * 6) % cat.walk.cols
    sprites.drawFrame(ctx, cat.walk, frame, row, this.pos.x, this.pos.y)
  }
}

// --------------------------------------------------------------------------- //

export class Door {
  state: DoorState = 'closed'
  private autoCloseAt = 0

  set(state: DoorState, now: number): void {
    this.state = state
    this.autoCloseAt = state === 'closed' ? 0 : now + 6000
  }

  update(now: number): void {
    if (this.autoCloseAt && now > this.autoCloseAt) { this.state = 'closed'; this.autoCloseAt = 0 }
  }

  draw(ctx: CanvasRenderingContext2D, sprites: Sprites, x: number, y: number, h: number, w?: number): void {
    const meta = sprites.atlas.fx.door
    const col = this.state === 'closed' ? 0 : this.state === 'half' ? 1 : 2
    const dw = w ?? (meta.frameW / sprites.worldScale) * (h * sprites.worldScale / meta.frameH)
    ctx.drawImage(sprites.img(meta.image), col * meta.frameW, 0, meta.frameW, meta.frameH, x, y, dw, h)
  }
}
