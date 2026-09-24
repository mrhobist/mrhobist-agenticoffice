import type { AgentDef, AgentState, BubbleKind, CatAction, DoorState, Facing, Pt, SeatDef } from './contract'
import { ROLE_HEX, STATE_HEX } from './contract'
import type { CharacterSheets, Sprites } from './atlas'
import type { NavGrid } from './nav'

/** Sirali eylem kuyrugu: yonetmen (director) bunlari ekler, update tuketir. */
export type Action =
  /** Hedef, eylem basladiginda cozulur (durak dolu mu bosta mi o anda belli olur). */
  | { t: 'walk'; to: Pt | (() => Pt) }
  /**
   * Yol bulmadan DUZ yuru (kisa mesafe): kapi boslugundan iceri/disari, engelli alandaki koltuga son adim.
   * Isinlanma yok (kullanici istegi 2026-09-24): sahnede konum hicbir zaman bir karede sicramaz.
   */
  | { t: 'step'; to: Pt }
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

/** Ajanin getirdigi icecek: kahve bari kupasi ya da sebil bardagi. */
export type Drink = 'coffee' | 'water'

/**
 * Icecek. Elde tasinirken ve masada dururken ayni cizim kullanilir; `w` dunya genisligi,
 * (x, y) sol-ust. Kahve tileset kupasidir (+ buhar), su prosedurel bir bardaktir
 * (tileset'te bardak yok, kupayi maviye boyamak yaniltici olurdu).
 */
export function drawDrink(
  ctx: CanvasRenderingContext2D, sprites: Sprites, x: number, y: number, w: number, now: number, kind: Drink = 'coffee',
): void {
  if (kind === 'water') { drawGlass(ctx, x, y, w * 0.8, now); return }
  const sz = sprites.objectSize('mug-white')
  const h = (w * sz.h) / sz.w
  sprites.drawObject(ctx, 'mug-white', x, y, w, h)
  ctx.save()
  ctx.fillStyle = 'rgba(255,255,255,0.55)'
  for (let i = 0; i < 2; i++) {
    const t = (now / 900 + i * 0.5) % 1
    ctx.globalAlpha = 0.5 * (1 - t)
    ctx.fillRect(x + w * (0.3 + i * 0.35), y - 3 - t * 9, 1.6, 4)
  }
  ctx.restore()
}

/** Su bardagi: hafifce salinan su yuzeyi; buhar yok. */
function drawGlass(ctx: CanvasRenderingContext2D, x: number, y: number, w: number, now: number): void {
  const h = w * 1.25
  const level = y + h * 0.34 + Math.sin(now / 700) * 0.4
  ctx.save()
  ctx.fillStyle = 'rgba(228,240,250,0.85)'
  ctx.fillRect(x, y, w, h)
  ctx.fillStyle = '#5fb6e6'
  ctx.fillRect(x + 1, level, w - 2, y + h - level - 1)
  ctx.fillStyle = 'rgba(255,255,255,0.8)'
  ctx.fillRect(x + 1.5, y + 2, 1.2, h - 5)
  ctx.strokeStyle = 'rgba(40,60,80,0.5)'
  ctx.lineWidth = 1
  ctx.strokeRect(x + 0.5, y + 0.5, w - 1, h - 1)
  ctx.restore()
}

export class Agent {
  pos: Pt
  facing: Facing = 'down'
  state: AgentState = 'idle'
  note: string | null = null
  seated: SeatDef | null = null
  /** `card`: kagit kutucuk (odak): coklu satir, sprite balonuna sigmayan metin (ajanin isleri). */
  bubble: { kind: BubbleKind; until: number; text?: string; card?: string[] } | null = null
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
  /** Kullanici tikladi: hareket durur, izleyiciye bakar, balon acik kalir; ambient almaz. */
  frozen = false
  /** Su an yaptigi is: calisma etiketi + gorev (agent.state olayindan). Balon ve pusula okur. */
  job: string | null = null
  /** Su an tuttugu/hedefledigi durak (kapasite sayimi icin). */
  spot: string | null = null
  /** Ambient disari cikista donus zamani; arka uc komutuyla cikanda null (kendi donmez). */
  returnAt: number | null = null
  /** Masasi yok (`home: {}`): disarida yasar, ara sira panoya bakmaya ugrar. */
  visitor = false
  /** Elinde ne tasiyor (kahve bari / su sebili); yoksa null. */
  carrying: Drink | null = null
  /** Masaya birakilan icecek: koltugu, turu ve ne zamana kadar durdugu (icilince kaybolur). */
  mugSeat: SeatDef | null = null
  mugKind: Drink = 'coffee'
  mugUntil = 0

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
  /** Yolda mi (yuruyus, duz adim, koltuga yuruyus, kalkis): yuruyus kareleri bunu okur. */
  get walking() { return this.path.length > 0 }

  /**
   * Kuyrugu sifirla ve yeni eylemleri koy. Arka uc komutu (varsayilan) balonu da siler ve
   * `lastCommandAt`'i gunceller; ambient eylem ise kesilebilir olarak isaretlenir ve
   * bir sonraki ambient icin bekleme suresi kurulur.
   */
  command(actions: Action[], now: number, opts: { ambient?: boolean } = {}): void {
    this.spot = null
    this.queue = actions
    this.current = null
    this.path = []
    this.ambient = opts.ambient === true
    if (this.ambient) {
      this.ambientReadyAt = now + 25_000 + Math.random() * 45_000
    } else {
      this.lastCommandAt = now
      this.bubble = null
    }
  }

  update(dt: number, now: number, nav: NavGrid): void {
    if (this.bubble && now > this.bubble.until) this.bubble = null
    if (this.frozen) return // dondu: kuyruk bekler, balon kalir

    if (!this.current) {
      this.current = this.queue.shift() ?? null
      if (!this.current) return
      this.begin(this.current, now, nav)
    }
    const a = this.current
    switch (a.t) {
      case 'walk':
      case 'step':
      case 'stand':
        if (this.advance(dt)) this.finish()
        break
      case 'sit':
        // Koltuga once yurunur (engelli alandaki koltuk dahil), varinca oturulur: konum sicramaz.
        if (this.advance(dt)) { this.seatNow(a.seat); this.finish() }
        break
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

  /** Yol boyunca bir kare ilerler; yol bittiyse (ya da hic yoksa) true. Yuzu gidis yonune doner. */
  private advance(dt: number): boolean {
    const next = this.path[0]
    if (!next) return true
    const dx = next.x - this.pos.x
    const dy = next.y - this.pos.y
    const dist = Math.hypot(dx, dy)
    const step = WALK_SPEED * dt
    this.walkT += dt
    if (dist <= step) {
      this.pos = { x: next.x, y: next.y }
      this.path.shift()
      return this.path.length === 0
    }
    this.pos.x += (dx / dist) * step
    this.pos.y += (dy / dist) * step
    this.facing = faceTowards({ x: 0, y: 0 }, { x: dx, y: dy })
    return false
  }

  private seatNow(seat: SeatDef): void {
    this.seated = seat
    this.pos = { x: seat.x, y: seat.y }
    this.facing = seat.facing
  }

  private begin(a: Action, now: number, nav: NavGrid): void {
    switch (a.t) {
      case 'walk': {
        // Oturuyorsa once koltuktan en yakin acik hucreye YURUR (onceden oraya isinlaniyordu), sonra yola devam.
        const to = typeof a.to === 'function' ? a.to() : a.to
        const start = this.seated ? nav.nearestOpen(this.seated) : this.pos
        this.path = [...(this.seated ? [start] : []), ...nav.path(start, to)]
        this.seated = null
        this.target = to
        this.walkT = 0
        break
      }
      case 'step':
        this.path = [{ x: a.to.x, y: a.to.y }]
        this.target = a.to
        this.walkT = 0
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
        this.path = Math.hypot(a.seat.x - this.pos.x, a.seat.y - this.pos.y) > 1.5 ? [{ x: a.seat.x, y: a.seat.y }] : []
        this.walkT = 0
        break
      case 'stand':
        if (this.seated) {
          const to = nav.nearestOpen(this.seated)
          this.seated = null
          this.facing = 'down'
          this.path = [to]
        } else {
          this.path = []
        }
        break
      case 'call':
        a.fn()
        break
    }
  }

  private finish(): void {
    if (this.current?.t === 'walk' || this.current?.t === 'step') this.target = null
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
      if (this.carrying) {
        // Icecek elde: yuruyus salinimiyla birlikte hafifce oynar.
        const side = this.facing === 'left' || this.facing === 'upleft' || this.facing === 'downleft' ? -1 : 1
        const bob = this.walking ? Math.sin(this.walkT * WALK_FPS * 0.7) * 1.2 : 0
        drawDrink(ctx, sprites, this.pos.x + side * 13 - 7, this.pos.y - 30 + bob, 14, now, this.carrying)
      }
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
    if (this.bubble.card) { this.drawCard(ctx, this.bubble.card); return }
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

  /** Kafa ustunde kagit kutucuk: ilk satir baslik (kalin), digerleri isler. Sahne kagidiyla ayni renkler. */
  private drawCard(ctx: CanvasRenderingContext2D, lines: string[]): void {
    const font = '7px "Segoe UI", system-ui, sans-serif'
    const maxW = 120
    ctx.font = font
    const wrapped: Array<{ t: string; bold: boolean }> = []
    lines.forEach((line, i) => {
      ctx.font = (i === 0 ? 'bold ' : '') + font
      const words = line.split(' ')
      let cur = ''
      for (const w of words) {
        const next = cur ? `${cur} ${w}` : w
        if (ctx.measureText(next).width > maxW - 10 && cur) { wrapped.push({ t: cur, bold: i === 0 }); cur = w }
        else cur = next
      }
      if (cur) wrapped.push({ t: cur, bold: i === 0 })
    })
    const lh = 9
    const w = maxW
    const h = wrapped.length * lh + 8
    const x = Math.round(this.pos.x - w / 2)
    const y = Math.round(this.headY() - h - 10)
    ctx.save()
    ctx.fillStyle = 'rgba(0,0,0,0.25)'
    ctx.fillRect(x + 2, y + 3, w, h)
    ctx.fillStyle = '#ede9dc'
    ctx.fillRect(x, y, w, h)
    ctx.strokeStyle = '#6b4a2b'
    ctx.lineWidth = 1.5
    ctx.strokeRect(x, y, w, h)
    // kuyruk
    ctx.fillStyle = '#ede9dc'
    ctx.beginPath(); ctx.moveTo(this.pos.x - 4, y + h); ctx.lineTo(this.pos.x + 4, y + h); ctx.lineTo(this.pos.x, y + h + 5); ctx.closePath(); ctx.fill(); ctx.stroke()
    ctx.textAlign = 'left'
    ctx.textBaseline = 'top'
    wrapped.forEach((l, i) => {
      ctx.font = (l.bold ? 'bold ' : '') + font
      ctx.fillStyle = l.bold ? '#23283a' : '#4a5068'
      ctx.fillText(l.t, x + 5, y + 4 + i * lh)
    })
    ctx.restore()
  }
}

// --------------------------------------------------------------------------- //

type CatMode = 'sleep' | 'walk' | 'sit' | 'lie'

/**
 * Kedinin yuruyus sayfasi 4 yonlu (asagi/sol/sag/yukari). 8 yonlu bakis dogrudan kullanilinca capraz yuruyuste satir
 * bulunamiyor ve "asagi" (kameraya donuk) kare ciziliyordu: kedi sol-yukari giderken bize bakarak kayiyordu (2026-09-24).
 * Caprazda yan gorunus secilir: yan profil gidis yonunu en iyi anlatir.
 */
function catDir(f: Facing): 'down' | 'left' | 'right' | 'up' {
  if (f === 'left' || f === 'upleft' || f === 'downleft') return 'left'
  if (f === 'right' || f === 'upright' || f === 'downright') return 'right'
  return f === 'up' ? 'up' : 'down'
}

export class Cat {
  pos: Pt
  facing: Facing = 'down'
  mode: CatMode = 'sleep'
  private path: Pt[] = []
  private walkT = 0
  private nextAt = 0
  private plan: Array<() => void> = []

  /** Oksama: kalp balonu bu zamana kadar gorunur. */
  heartUntil = 0
  /** Kac kez oksandi: ucuncude uzanip keyif yapar. */
  pets = 0
  /** Birisi seviyor / yanina geliyor: bu zamana kadar yeni gezinti baslatmaz. */
  holdUntil = 0
  /** Kisa soz balonu ("miyav"). */
  private sayText = ''
  private sayUntil = 0

  constructor(readonly bed: Pt, readonly spots: Pt[]) {
    this.pos = { ...bed }
    this.nextAt = performance.now() + 45_000 + Math.random() * 60_000
  }

  /**
   * Kullanici kediye tikladi: uyanir, izleyiciye doner, kalp cikarir. Ucuncu oksamada
   * sirtustu uzanir ve daha uzun kalir; bu sirada kendi ritmi beklemeye alinir.
   */
  pet(now: number): void {
    this.plan = []
    this.path = []
    this.pets += 1
    this.facing = 'down'
    this.mode = this.pets % 3 === 0 ? 'lie' : 'sit'
    this.heartUntil = now + 2600
    this.nextAt = now + (this.mode === 'lie' ? 14_000 : 9_000)
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

  get awake(): boolean { return this.mode !== 'sleep' }

  /** Birisi yanina geliyor: yuruyorsa durur, `until`'e kadar yerinden kalkmaz. 0 = serbest birak. */
  hold(until: number): void {
    this.holdUntil = until
    if (until && this.mode === 'walk') { this.path = []; this.plan = []; this.mode = 'sit' }
  }

  /** Bir ajan/misafir sevdi: uyaniksa oturur (uyuyorsa uyanir), kalp + "mirr"; ara sira uzanir. */
  pettedBy(now: number): void {
    this.pet(now)
  }

  /** Kisa soz balonu. */
  meow(now: number, text = 'miyav'): void {
    this.sayText = text
    this.sayUntil = now + 1800
  }

  /** Bir noktaya git; varinca `then` (yoksa oturur). Hedef engelliyse son parca DUZ yurunur, isinlanmaz. */
  visit(to: Pt, nav: NavGrid, then?: () => void): void {
    this.holdUntil = 0
    this.goto(to, nav, then ?? (() => { this.mode = 'sit'; this.nextAt = performance.now() + 8000 + Math.random() * 6000 }))
  }

  private goto(to: Pt, nav: NavGrid, then: () => void): void {
    this.mode = 'walk'
    this.path = nav.path(this.pos, to)
    // Hedef engelli alandaysa yol en yakin acik hucrede biter: kalan kisa mesafe duz yurunur (onceden oraya zipliyordu).
    if (!nav.isOpenAt(to)) this.path.push({ x: to.x, y: to.y })
    this.walkT = 0
    this.plan = [then]
  }

  private arrived(now: number, fallbackMs: number): void {
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
        this.facing = catDir(faceTowards({ x: 0, y: 0 }, { x: dx, y: dy }))
      }
      this.walkT += dt
      return
    }
    if (now < this.nextAt || now < this.holdUntil) return
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
    this.drawHearts(ctx, now)
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
    const row = cat.walk.dirRows?.[catDir(this.facing)] ?? 0
    const frame = Math.floor(this.walkT * 6) % cat.walk.cols
    sprites.drawFrame(ctx, cat.walk, frame, row, this.pos.x, this.pos.y)
  }

  /** Kisa soz balonu: kedinin basinin ustunde kucuk beyaz kutu. */
  private drawSay(ctx: CanvasRenderingContext2D, now: number): void {
    if (now > this.sayUntil || !this.sayText) return
    const left = (this.sayUntil - now) / 1800
    ctx.save()
    ctx.globalAlpha = Math.min(1, left * 3)
    ctx.font = '600 10px "Segoe UI", system-ui, sans-serif'
    ctx.textAlign = 'center'
    ctx.textBaseline = 'middle'
    const w = ctx.measureText(this.sayText).width + 10
    const x = this.pos.x
    const y = this.pos.y - 44
    ctx.fillStyle = 'rgba(255,255,255,0.95)'
    ctx.strokeStyle = 'rgba(40,44,60,0.55)'
    ctx.lineWidth = 1
    ctx.beginPath()
    ctx.roundRect(x - w / 2, y - 8, w, 16, 5)
    ctx.fill()
    ctx.stroke()
    ctx.beginPath()
    ctx.moveTo(x - 3, y + 8); ctx.lineTo(x, y + 12); ctx.lineTo(x + 3, y + 8)
    ctx.fill()
    ctx.fillStyle = '#2a2f3d'
    ctx.fillText(this.sayText, x, y + 0.5)
    ctx.restore()
  }

  /** Oksandiktan sonra yukari suzulen kalpler + "mirr". */
  private drawHearts(ctx: CanvasRenderingContext2D, now: number): void {
    this.drawSay(ctx, now)
    if (now > this.heartUntil) return
    const left = (this.heartUntil - now) / 2600
    ctx.save()
    ctx.textAlign = 'center'
    ctx.textBaseline = 'alphabetic'
    for (let i = 0; i < 3; i++) {
      const t = ((now / 700 + i * 0.33) % 1)
      ctx.globalAlpha = Math.min(1, left * 1.6) * (1 - t) * 0.9
      ctx.font = `${9 + i}px "Segoe UI", system-ui, sans-serif`
      ctx.fillStyle = '#e0699a'
      ctx.fillText('♥', this.pos.x + (i - 1) * 9, this.pos.y - 26 - t * 22)
    }
    ctx.globalAlpha = Math.min(1, left * 2)
    ctx.font = '600 9px "Segoe UI", system-ui, sans-serif'
    ctx.fillStyle = '#e8ecf5'
    ctx.fillText('mırr', this.pos.x, this.pos.y + 12)
    ctx.restore()
  }
}

// --------------------------------------------------------------------------- //

/** Iki kare: kapali / acik. Giren ya da cikan once acar, kisa sure sonra kapanir. */
export class Door {
  state: DoorState = 'closed'
  private autoCloseAt = 0

  set(state: DoorState, now: number): void {
    this.state = state === 'closed' ? 'closed' : 'open'
    this.autoCloseAt = this.state === 'closed' ? 0 : now + 1400
  }

  update(now: number): void {
    if (this.autoCloseAt && now > this.autoCloseAt) { this.state = 'closed'; this.autoCloseAt = 0 }
  }

  draw(ctx: CanvasRenderingContext2D, sprites: Sprites, x: number, y: number, h: number, w?: number): void {
    const meta = sprites.atlas.fx.door
    const col = this.state === 'closed' ? 0 : 2
    const dw = w ?? (meta.frameW / sprites.worldScale) * (h * sprites.worldScale / meta.frameH)
    ctx.drawImage(sprites.img(meta.image), col * meta.frameW, 0, meta.frameW, meta.frameH, x, y, dw, h)
  }
}
