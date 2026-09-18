import type { AgentState, Facing, MeetKind, Pt, PropDef, SceneConfig, SceneEvent, SeatDef, WorkflowConfig } from './contract'
import { Sprites } from './atlas'
import { NavGrid } from './nav'
import { Agent, Cat, Door, type Action } from './entities'
import { Board } from './board'
import { drawSky } from './sky'

export interface Hud {
  stage: string
  task: string
  round: number
}

interface PlacedProp extends PropDef { h: number }

/**
 * Sahnenin tamami: yerlesim, varliklar, pano, kapi. Olaylari `apply` ile alir,
 * `update`/`draw` ile yasar. Vue reaktivitesi burada YOK; sicak dongu duz TS.
 */
export class World {
  readonly nav: NavGrid
  readonly props: PlacedProp[]
  readonly agents = new Map<string, Agent>()
  readonly cat: Cat
  readonly door = new Door()
  readonly board: Board
  hud: Hud = { stage: '—', task: '—', round: 0 }
  onHud: ((h: Hud) => void) | null = null
  hovered: string | null = null
  /** clock.set ile sabitlenen saat; null = gercek yerel saat. */
  clockHour: number | null = null
  /** cafe.special ile sabitlenen yazi; null = liste sirayla doner. */
  specialOverride: string | null = null

  private bg: HTMLCanvasElement | null = null
  private bgKey = ''
  private lastAmbientAt = 0

  private constructor(
    readonly cfg: SceneConfig,
    readonly wf: WorkflowConfig,
    readonly sprites: Sprites,
  ) {
    this.nav = new NavGrid(cfg)
    this.props = cfg.props.map(p => {
      const size = sprites.objectSize(p.sprite)
      return { ...p, h: p.h ?? (p.w * size.h) / size.w }
    })
    this.board = new Board(cfg.board)
    this.board.setWorkflow(wf)
    this.cat = new Cat(cfg.cat.bed, cfg.cat.spots)

    for (const def of cfg.agents) {
      const sheets = sprites.atlas.characters[def.sprite]
      if (!sheets) throw new Error(`karakter sayfasi yok: ${def.sprite} (${def.key})`)
      const home = this.homeOf(def.key)
      const a = new Agent(def, sheets, home.pos)
      if (home.seat) { a.seated = home.seat; a.facing = home.seat.facing }
      else if (home.look) a.faceTo(home.look)
      else if (home.facing) a.facing = home.facing
      a.ambientReadyAt = performance.now() + 8000 + Math.random() * 30_000
      this.agents.set(def.key, a)
    }
  }

  static async create(apiBase: string): Promise<World> {
    const [cfgRes, wfRes, sprites] = await Promise.all([
      fetch(`${apiBase}/api/v1/scene`),
      fetch(`${apiBase}/api/v1/workflow`),
      Sprites.load(),
    ])
    if (!cfgRes.ok) throw new Error(`scene ${cfgRes.status}`)
    if (!wfRes.ok) throw new Error(`workflow ${wfRes.status}`)
    const cfg = (await cfgRes.json()) as SceneConfig
    const wf = (await wfRes.json()) as WorkflowConfig
    return new World(cfg, wf, sprites)
  }

  // ------------------------------------------------------------------ olaylar

  apply(e: SceneEvent): void {
    const now = performance.now()
    switch (e.type) {
      case 'agent.state': {
        const a = this.agents.get(e.data.agent)
        if (!a) return
        a.state = e.data.state
        a.note = e.data.note ?? null
        a.lastCommandAt = now
        if (a.ambient) a.command(this.goHome(a), now)
        if ((e.data.state === 'working' || e.data.state === 'thinking') && !a.seated && !a.busy) a.command(this.goHome(a), now)
        if (e.data.state === 'blocked') a.command([...(a.busy ? [] : []), { t: 'say', kind: 'alert', ms: 2500 }], now)
        if (e.data.state === 'waiting' && !a.busy) a.bubble = { kind: 'ask', until: now + 4000 }
        break
      }
      case 'agent.say': {
        const a = this.agents.get(e.data.agent)
        if (a) a.bubble = { kind: e.data.kind, until: now + (e.data.ms ?? 3000), text: e.data.text }
        break
      }
      case 'agent.goto': {
        const a = this.agents.get(e.data.agent)
        const s = this.cfg.spots[e.data.spot]
        if (!a || !s) return
        a.command(this.tripTo(a, e.data.spot), now)
        break
      }
      case 'agent.home': {
        const a = this.agents.get(e.data.agent)
        if (a) a.command(this.goHome(a), now)
        break
      }
      case 'meet':
        this.meet(e.data.from, e.data.to, e.data.kind, e.data.ms ?? 4500, now)
        break
      case 'board.set':
        this.board.set(e.data.tasks)
        break
      case 'board.move':
        this.board.move(e.data.task, e.data.stage, e.data.state)
        break
      case 'run.stage':
        this.hud = { stage: e.data.stage, task: e.data.task, round: e.data.round }
        this.onHud?.(this.hud)
        break
      case 'cat':
        this.cat.command(e.data.action, e.data.spot, now, this.nav)
        break
      case 'door':
        this.door.set(e.data.state, now)
        break
      case 'agent.leave': {
        const a = this.agents.get(e.data.agent)
        const d = this.cfg.spots['door']
        if (!a || !d || a.offstage) return
        a.command([
          { t: 'walk', to: this.freeNear(d, a) },
          { t: 'call', fn: () => this.door.set('open', performance.now()) },
          { t: 'face', dir: 'up' },
          { t: 'wait', ms: 500 },
          { t: 'call', fn: () => { a.offstage = true; this.door.set('half', performance.now()) } },
        ], now)
        break
      }
      case 'agent.enter': {
        const a = this.agents.get(e.data.agent)
        const d = this.cfg.spots['door']
        if (!a || !d) return
        a.offstage = false
        a.seated = null
        a.pos = { x: d.x, y: d.y }
        a.facing = 'down'
        this.door.set('open', now)
        a.command([{ t: 'wait', ms: 400 }, ...this.goHome(a)], now)
        break
      }
      case 'clock.set':
        this.clockHour = e.data.hour
        break
      case 'cafe.special':
        this.specialOverride = e.data.text
        break
    }
  }

  /** Duragi tutan ajanlar (kapasite sayimi). */
  private occupants(key: string, except?: Agent): Agent[] {
    return [...this.agents.values()].filter(a => a !== except && !a.offstage && a.spot === key)
  }

  spotFree(key: string, except?: Agent): boolean {
    const s = this.cfg.spots[key]
    if (!s) return false
    return this.occupants(key, except).length < (s.capacity ?? 1)
  }

  /**
   * Bir duraga gidis. Dolu ise `queue` noktasinda bekler, bosalinca alir ve girer.
   * Ayni anda bir kisi (varsayilan): sebil, kahve, pano, pencere onu.
   */
  private tripTo(a: Agent, key: string): Action[] {
    const s = this.cfg.spots[key]
    if (!s) return []
    // Talep eylem ICINDE yapilir: command() kuyrugu sifirlarken spot'u da sifirlar,
    // dolayisiyla rezervasyon ancak eylemler islerken konur. Iki ajan ayni anda
    // komut alsa bile sirayla guncellenir; ikinci, dolu gorur ve kuyruk noktasina gider.
    const claim = () => { if (a.spot === key) return true; if (this.spotFree(key, a)) { a.spot = key; return true } return false }
    const q = s.queue ?? { x: s.x + 60, y: s.y + 40 }
    return [
      { t: 'until', pred: claim, timeoutMs: 1 },
      { t: 'walk', to: () => (a.spot === key ? s : this.freeNear(q, a)) },
      { t: 'call', fn: () => a.faceTo(s) },
      { t: 'until', pred: claim, timeoutMs: 30_000 },
      { t: 'walk', to: s },
      ...this.arrive(a, s),
    ]
  }

  /** Saat (0-24, kesirli). Pencere manzarasi bunu okur. */
  hourNow(): number {
    if (this.clockHour !== null) return this.clockHour
    const d = new Date()
    return d.getHours() + d.getMinutes() / 60
  }

  /**
   * Bir noktanin yakininda, baska bir ajanin durmadigi ve hedeflemedigi acik hucre.
   * Iki kisi ayni durakta ust uste binmez; konusmaya gelen yanina durur.
   */
  freeNear(p: Pt, self: Agent): Pt {
    const others = [...this.agents.values()].filter(o => o !== self && !o.offstage)
    const taken = (q: Pt) => others.some(o =>
      Math.hypot(o.pos.x - q.x, o.pos.y - q.y) < 26
      || (o.target !== null && Math.hypot(o.target.x - q.x, o.target.y - q.y) < 26))
    const offsets: Pt[] = [{ x: 0, y: 0 }]
    const dirs: Array<[number, number]> = [[1, 0], [-1, 0], [0, 1], [1, 1], [-1, 1], [0, -1], [1, -1], [-1, -1]]
    for (const r of [28, 52, 76]) for (const [dx, dy] of dirs) offsets.push({ x: dx * r, y: dy * r * 0.7 })
    for (const o of offsets) {
      const q = { x: p.x + o.x, y: p.y + o.y }
      if (this.nav.isOpenAt(q) && !taken(q)) return q
    }
    return this.nav.nearestOpen(p)
  }

  /** Duraga varinca bakis: `look` varsa durulan noktadan oraya, yoksa sabit `facing`. */
  private arrive(a: Agent, s: { facing?: Facing; look?: Pt }): Action[] {
    if (s.look) { const look = s.look; return [{ t: 'call', fn: () => a.faceTo(look) }] }
    if (s.facing) return [{ t: 'face', dir: s.facing }]
    return []
  }

  private homeOf(key: string): { pos: Pt; seat?: SeatDef; facing?: Facing; look?: Pt } {
    const def = this.cfg.agents.find(a => a.key === key)
    if (def?.home.seat) {
      const seat = this.cfg.seats[def.home.seat]
      if (seat) return { pos: { x: seat.x, y: seat.y }, seat }
    }
    if (def?.home.spot) {
      const spot = this.cfg.spots[def.home.spot]
      if (spot) return { pos: { x: spot.x, y: spot.y }, facing: spot.facing, look: spot.look }
    }
    return { pos: { x: this.cfg.world.w / 2, y: this.cfg.world.h / 2 } }
  }

  private goHome(a: Agent): Action[] {
    const home = this.homeOf(a.key)
    if (home.seat) {
      const approach = this.nav.nearestOpen(home.seat)
      return [{ t: 'walk', to: approach }, { t: 'sit', seat: home.seat }]
    }
    const spotKey = this.cfg.agents.find(d => d.key === a.key)?.home.spot
    if (spotKey && this.cfg.spots[spotKey]) return this.tripTo(a, spotKey)
    return [{ t: 'walk', to: this.freeNear(home.pos, a) }, ...this.arrive(a, home)]
  }

  /** `from`, `to`'nun yanina yurur; konusurlar; `from` evine doner. */
  private meet(fromKey: string, toKey: string, kind: MeetKind, ms: number, now: number): void {
    const from = this.agents.get(fromKey)
    const to = this.agents.get(toKey)
    if (!from || !to || to.offstage || from.offstage) return
    const target = this.freeNear(to.seated ? this.nav.nearestOpen(to.seated) : { x: to.pos.x + 34, y: to.pos.y + 6 }, from)
    const fromKind = kind === 'ask' ? 'ask' : kind === 'reject' ? 'alert' : 'talk'
    const half = ms / 2
    from.command([
      { t: 'walk', to: target },
      { t: 'call', fn: () => { from.faceTo(to.pos); to.faceTo(from.pos); if (to.seated) to.facing = 'down' } },
      { t: 'say', kind: fromKind, ms: half },
      { t: 'call', fn: () => { to.bubble = { kind: 'talk', until: performance.now() + half } } },
      { t: 'wait', ms: half },
      { t: 'call', fn: () => { if (to.seated) to.facing = to.seated.facing } },
      ...this.goHome(from),
    ], now)
  }

  // ------------------------------------------------------------------ yasam

  update(dt: number, now: number): void {
    for (const a of this.agents.values()) a.update(dt, now, this.nav)
    this.cat.update(dt, now, this.nav)
    this.door.update(now)
    this.board.update(dt)
    this.ambient(now)
  }

  /**
   * Ambient hayat: bosta duran ajanlar ara sira kahve alir, su icer, panoya bakar ya da
   * bir arkadasina ugrar. Arka uctan komut gelince kesilir. Ayni anda en fazla iki kisi.
   */
  private ambient(now: number): void {
    if (now - this.lastAmbientAt < 6000) return
    const away = [...this.agents.values()].filter(a => a.busy || (!a.seated && this.homeOf(a.key).seat)).length
    if (away >= 2) return
    const idle = [...this.agents.values()].filter(a =>
      !a.offstage && !a.busy && (a.state === 'idle' || a.state === 'done') && now > a.ambientReadyAt && now - a.lastCommandAt > 10_000)
    if (!idle.length) return
    const a = idle[Math.floor(Math.random() * idle.length)]!
    const r = Math.random()
    const spots = this.cfg.spots
    const trip = (spot: string, dwell: number, talk = false): Action[] => {
      const s = spots[spot]
      // Ambient: dolu duraga gidip beklemek yerine bu turu atla.
      if (!s || !this.spotFree(spot, a)) return []
      return [
        ...this.tripTo(a, spot),
        ...(talk ? [{ t: 'say', kind: 'talk', ms: Math.min(dwell, 3000) } as Action] : []),
        { t: 'wait', ms: dwell },
        ...this.goHome(a),
      ]
    }
    let actions: Action[]
    if (r < 0.35) actions = trip('coffee', 4000 + Math.random() * 4000)
    else if (r < 0.5) actions = trip('water', 3000 + Math.random() * 2000)
    else if (r < 0.65) actions = trip('board', 4000 + Math.random() * 3000)
    else if (r < 0.75) actions = trip('window', 5000 + Math.random() * 3000)
    else {
      // Bir arkadasa ugra: yerinde oturan birini sec.
      const others = [...this.agents.values()].filter(o => o !== a && o.seated && !o.busy && !o.offstage)
      const o = others[Math.floor(Math.random() * others.length)]
      if (!o) return
      const target = this.freeNear(this.nav.nearestOpen(o.seated!), a)
      const ms = 3500 + Math.random() * 2500
      actions = [
        { t: 'walk', to: target },
        { t: 'call', fn: () => { a.faceTo(o.pos); o.facing = 'down' } },
        { t: 'say', kind: 'talk', ms: ms / 2 },
        { t: 'call', fn: () => { o.bubble = { kind: 'talk', until: performance.now() + ms / 2 } } },
        { t: 'wait', ms: ms / 2 },
        { t: 'call', fn: () => { if (o.seated) o.facing = o.seated.facing } },
        ...this.goHome(a),
      ]
    }
    if (!actions.length) return
    a.enqueueAmbient(actions, now)
    this.lastAmbientAt = now
  }

  // ------------------------------------------------------------------ cizim

  draw(ctx: CanvasRenderingContext2D, scale: number, now: number): void {
    const { w, h } = this.cfg.world
    if (this.cfg.window) drawSky(ctx, this.cfg.window, this.hourNow(), scale)
    ctx.drawImage(this.background(scale), 0, 0, w, h)

    if (!this.cfg.board.legs) this.board.draw(ctx, now)
    this.drawCafeSpecial(ctx, now)
    if (this.cfg.door) this.door.draw(ctx, this.sprites, this.cfg.door.x, this.cfg.door.y, this.cfg.door.h, this.cfg.door.w)

    // Nesneler + varliklar alt kenara gore siralanir.
    type Item = { y: number; draw: () => void }
    const items: Item[] = []
    const propBottom = new Map<string, number>()
    const litMonitors = new Set<string>()
    for (const a of this.agents.values()) if (a.seated?.monitor && !a.offstage) litMonitors.add(a.seated.monitor)
    for (const p of this.props) {
      if (p.layer !== 'object') continue
      propBottom.set(p.id, p.y + p.h)
      const off = p.spriteOff && !litMonitors.has(p.id) ? p.spriteOff : null
      items.push({ y: p.sortY ?? p.y + p.h, draw: () => {
        if (!off) { this.sprites.drawObject(ctx, p.sprite, p.x, p.y, p.w, p.h); return }
        // Kapali ekran: ayni genislik, kendi orani, alt kenara hizali.
        const sz = this.sprites.objectSize(off)
        const h2 = (p.w * sz.h) / sz.w
        this.sprites.drawObject(ctx, off, p.x, p.y + p.h - h2, p.w, h2)
      } })
    }
    // Arka plandan kesitler (cam duvar onu gibi): varliklarin onune, alt kenara gore.
    const bgMeta = this.cfg.background ? this.sprites.atlas.background : undefined
    if (bgMeta) {
      const bgImg = this.sprites.img(bgMeta.image)
      const sx = bgMeta.w / this.cfg.world.w
      const sy = bgMeta.h / this.cfg.world.h
      for (const [ox, oy, ow, oh] of this.cfg.overlays ?? []) {
        items.push({ y: oy + oh, draw: () => ctx.drawImage(bgImg, ox * sx, oy * sy, ow * sx, oh * sy, ox, oy, ow, oh) })
      }
    }
    for (const a of this.agents.values()) {
      const pb = a.seated?.prop ? propBottom.get(a.seated.prop) ?? null : null
      items.push({ y: a.sortY(pb), draw: () => a.draw(ctx, this.sprites, now) })
    }
    items.push({ y: this.cat.pos.y, draw: () => this.cat.draw(ctx, this.sprites, now) })
    if (this.cfg.board.legs) {
      const b = this.cfg.board
      items.push({ y: b.y + b.h + (b.legs ?? 0), draw: () => this.board.draw(ctx, now) })
    }
    items.sort((p, q) => p.y - q.y)
    for (const it of items) it.draw()

    this.drawFront(ctx)
    for (const a of this.agents.values()) a.drawBubble(ctx, this.sprites, now)
    this.drawLabels(ctx)
  }

  /** Statik arka plan: zemin, duvarlar, duvar ve zemin katmani nesneleri. Olcek degisince yenilenir. */
  private background(scale: number): HTMLCanvasElement {
    const key = scale.toFixed(3)
    if (this.bg && this.bgKey === key) return this.bg
    const { w, h } = this.cfg.world
    const c = document.createElement('canvas')
    c.width = Math.ceil(w * scale)
    c.height = Math.ceil(h * scale)
    const g = c.getContext('2d')!
    g.scale(scale, scale)
    g.imageSmoothingEnabled = true
    g.imageSmoothingQuality = 'high'

    if (this.cfg.background && this.sprites.atlas.background) {
      // Arka plan gorseli: pencere cami seffaftir, altina gokyuzu her kare ayrica cizilir.
      g.drawImage(this.sprites.img(this.sprites.atlas.background.image), 0, 0, w, h)
      for (const p of this.props) {
        if (p.layer === 'floor' || p.layer === 'wall') this.sprites.drawObject(g, p.sprite, p.x, p.y, p.w, p.h)
      }
      this.bg = c
      this.bgKey = key
      return c
    }

    g.fillStyle = '#1e222c'
    g.fillRect(0, 0, w, h)
    if (!this.cfg.floor || !this.cfg.walls) throw new Error('scene.json: background yoksa floor ve walls zorunlu')
    // Zemin karolari
    const [fx, fy, fw, fh] = this.cfg.floor.rect
    const ts = this.cfg.floor.tileSize
    const tiles = this.cfg.floor.tiles
    g.save()
    g.beginPath(); g.rect(fx, fy, fw, fh); g.clip()
    for (let ty = fy, r = 0; ty < fy + fh; ty += ts, r++) {
      for (let tx = fx, cidx = 0; tx < fx + fw; tx += ts, cidx++) {
        const pick = ((r * 7 + cidx * 13) % 11) < 8 ? 0 : ((r + cidx) % 2 === 0 ? 1 : 2)
        const name = tiles[Math.min(pick, tiles.length - 1)] ?? tiles[0]!
        this.sprites.drawObject(g, name, tx, ty, ts + 0.5, ts + 0.5)
      }
    }
    g.restore()

    // Duvarlar
    const W = this.cfg.walls
    const t = W.thickness
    g.fillStyle = W.colorOuter
    g.fillRect(0, 0, w, t); g.fillRect(0, 0, t, h); g.fillRect(w - t, 0, t, h)
    g.fillRect(0, h - t - 36, W.gate.from, t + 36); g.fillRect(W.gate.to, h - t - 36, w - W.gate.to, t + 36)
    g.fillStyle = W.colorInner
    g.fillRect(t * 0.35, t * 0.35, w - t * 0.7, t * 0.65)
    g.fillRect(t * 0.35, 0, t * 0.65, h); g.fillRect(w - t, 0, t * 0.65, h)
    g.fillStyle = W.colorTop
    g.fillRect(0, 0, w, 4); g.fillRect(0, 0, 4, h); g.fillRect(w - 4, 0, 4, h)

    // Kapi acikligi: zemin disari dogru devam eder (giris)
    g.fillStyle = '#2a2f3b'
    g.fillRect(W.gate.from, h - t - 36, W.gate.to - W.gate.from, t + 36)
    g.fillStyle = '#39404f'
    g.fillRect(W.gate.from + 12, h - t - 30, W.gate.to - W.gate.from - 24, 6)

    for (const p of this.props) {
      if (p.layer === 'floor') this.sprites.drawObject(g, p.sprite, p.x, p.y, p.w, p.h)
    }
    for (const p of this.props) {
      if (p.layer === 'wall') this.sprites.drawObject(g, p.sprite, p.x, p.y, p.w, p.h)
    }
    // Toplanti odasi cam kutu: sprite'lar arka duvar; on cerceve prosedurel.
    g.strokeStyle = 'rgba(210,225,240,0.55)'
    g.lineWidth = 3
    g.strokeRect(400, 124, 300, 236)
    g.fillStyle = 'rgba(180,205,230,0.10)'
    g.fillRect(400, 124, 300, 236)

    this.bg = c
    this.bgKey = key
    return c
  }

  /** Kahve bari panosu: gunun ozeli, listeden sirayla; cafe.special ile sabitlenir. */
  private drawCafeSpecial(ctx: CanvasRenderingContext2D, now: number): void {
    const cafe = this.cfg.cafe
    if (!cafe || !cafe.specials.length) return
    const { x, y, w, h } = cafe.board
    const interval = cafe.intervalMs ?? 20_000
    const idx = Math.floor(now / interval) % cafe.specials.length
    const text = this.specialOverride ?? cafe.specials[idx] ?? ''
    // Gecis: ilk 400 ms'de sonuk.
    const phase = (now % interval) / 400
    const alpha = this.specialOverride ? 1 : Math.min(1, phase)
    ctx.save()
    ctx.beginPath(); ctx.rect(x, y, w, h); ctx.clip()
    ctx.fillStyle = '#1d222c'
    ctx.fillRect(x, y, w, h)
    ctx.fillStyle = '#f0c26a'
    ctx.font = 'bold 9px "Segoe UI", system-ui, sans-serif'
    ctx.textAlign = 'left'
    ctx.textBaseline = 'alphabetic'
    ctx.fillText('GÜNÜN ÖZELİ', x + 8, y + 15)
    ctx.fillStyle = 'rgba(240,194,106,0.5)'
    ctx.fillRect(x + 8, y + 19, w - 16, 1)
    ctx.globalAlpha = alpha
    ctx.fillStyle = '#f7f1e3'
    ctx.font = 'bold 13px "Segoe UI", system-ui, sans-serif'
    ctx.fillText(text, x + 8, y + 40, w - 16)
    // Kupa ve buhar
    ctx.fillStyle = '#f7f1e3'
    ctx.fillRect(x + w - 30, y + h - 24, 16, 12)
    ctx.fillRect(x + w - 14, y + h - 21, 4, 6)
    ctx.globalAlpha = 0.6 + 0.4 * Math.sin(now / 500)
    ctx.fillRect(x + w - 26, y + h - 32, 2, 5)
    ctx.fillRect(x + w - 20, y + h - 34, 2, 6)
    ctx.restore()
  }

  /** Onde kalan yapisal parcalar: giris sutunlari ve citler. */
  private drawFront(ctx: CanvasRenderingContext2D): void {
    const W = this.cfg.walls
    if (!W) return
    for (const [x, y, w, h] of W.columns) {
      ctx.fillStyle = '#2b323f'; ctx.fillRect(x, y, w, h)
      ctx.fillStyle = '#3d4655'; ctx.fillRect(x + 6, y + 6, w - 12, h - 6)
      ctx.fillStyle = '#4c5666'; ctx.fillRect(x + 6, y + 6, w - 12, 4)
    }
    for (const [x, y, len] of W.hedges) {
      for (let i = 0; i < len; i += 22) {
        ctx.fillStyle = '#2f6d3a'
        ctx.beginPath(); ctx.ellipse(x + i + 11, y + 12, 12, 9, 0, 0, Math.PI * 2); ctx.fill()
        ctx.fillStyle = '#4d9a52'
        ctx.beginPath(); ctx.ellipse(x + i + 9, y + 9, 8, 6, 0, 0, Math.PI * 2); ctx.fill()
      }
    }
  }

  private drawLabels(ctx: CanvasRenderingContext2D): void {
    for (const a of this.agents.values()) {
      const show = a.key === this.hovered || (a.note && a.state !== 'idle' && a.state !== 'done')
      if (!show) continue
      const text = a.key === this.hovered ? a.def.name : (a.note ?? a.def.name)
      ctx.font = '600 10px "Segoe UI", system-ui, sans-serif'
      ctx.textAlign = 'center'
      ctx.textBaseline = 'middle'
      const tw = ctx.measureText(text).width + 12
      const x = a.pos.x
      const y = a.headY() - 20 - (a.bubble ? 0 : 0)
      ctx.fillStyle = 'rgba(20,24,34,0.82)'
      ctx.fillRect(x - tw / 2, y - 8, tw, 16)
      ctx.fillStyle = a.hex
      ctx.fillRect(x - tw / 2, y - 8, 3, 16)
      ctx.fillStyle = '#e8ecf5'
      ctx.fillText(text, x + 1, y + 0.5)
    }
  }

  /** Dunya noktasinda bir ajan var mi (tiklama/hover icin). */
  pick(p: Pt): Agent | null {
    let best: Agent | null = null
    for (const a of this.agents.values()) {
      const top = a.headY()
      if (p.x > a.pos.x - 24 && p.x < a.pos.x + 24 && p.y > top - 10 && p.y < a.pos.y + 4) {
        if (!best || a.pos.y > best.pos.y) best = a
      }
    }
    return best
  }

  agentState(key: string): AgentState {
    return this.agents.get(key)?.state ?? 'idle'
  }
}
