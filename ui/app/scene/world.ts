import { STATE_LABEL, type AgentState, type Facing, type LightDef, type MeetKind, type Pt, type PropDef, type SceneConfig, type SceneEvent, type SeatDef, type WorkflowConfig } from './contract'
import { Sprites } from './atlas'
import { NavGrid } from './nav'
import { Agent, Cat, Door, drawDrink, type Action, type Drink } from './entities'
import { Board } from './board'
import { drawSky } from './sky'
import { authHeaders } from '~/composables/useAuth'
import { deriveCards, HIDDEN_RUN_STATUS } from '~/api/board'
import type { RunDetail, RunSummary } from '~/api/types'

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
/** Arac adi → tek karakter: balon dar, hedef onemli. */
const TOOL_GLYPH: Record<string, string> = { Write: '✎', Edit: '✎', MultiEdit: '✎', Read: '👁', Glob: '🔍', Grep: '🔍', Bash: '>_' }

export class World {
  readonly nav: NavGrid
  readonly props: PlacedProp[]
  readonly agents = new Map<string, Agent>()
  readonly cat: Cat
  readonly door = new Door()
  readonly board: Board
  /** Tiklanabilir isiklar (mudur odasinin sarkiti): id -> tanim + durum. */
  readonly lights = new Map<string, { def: LightDef; on: boolean }>()
  hud: Hud = { stage: '—', task: '—', round: 0 }
  onHud: ((h: Hud) => void) | null = null
  hovered: string | null = null
  /** Imlecin ustunde durdugu isik / kedi (parlama ve ipucu icin). */
  hoveredLight: string | null = null
  hoveredCat = false
  /** clock.set ile sabitlenen saat; null = gercek yerel saat. */
  clockHour: number | null = null
  /** cafe.special ile sabitlenen yazi; null = liste sirayla doner. */
  specialOverride: string | null = null

  /** Sunucudan pano esitlemesi: son istek zamani ve bitmis calismalarin detay onbellegi. */
  private boardSyncAt = 0
  private readonly runDetails = new Map<string, RunDetail>()

  private bg: HTMLCanvasElement | null = null
  private bgKey = ''
  private lastAmbientAt = 0

  private constructor(
    readonly cfg: SceneConfig,
    readonly wf: WorkflowConfig,
    readonly sprites: Sprites,
    private readonly apiBase: string,
  ) {
    this.nav = new NavGrid(cfg)
    this.props = cfg.props.map(p => {
      const size = sprites.objectSize(p.sprite)
      return { ...p, h: p.h ?? (p.w * size.h) / size.w }
    })
    for (const l of cfg.lights ?? []) this.lights.set(l.id, { def: l, on: l.on !== false })
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
      // Ne koltugu ne duragi olan ajan (Api masa bulamadi): ziyaretci. Disarida baslar,
      // 15-45 s sonra kapidan girip panoya bakar (docs/SCENE.md - Ajan yerlesimi ve ziyaretci).
      if (!def.home.seat && !def.home.spot) {
        const d = cfg.spots['door']
        a.visitor = true
        a.offstage = true
        a.returnAt = performance.now() + 15_000 + Math.random() * 30_000
        if (d) a.pos = { x: d.x, y: d.y }
      }
      this.agents.set(def.key, a)
    }
  }

  static async create(apiBase: string): Promise<World> {
    const [cfgRes, wfRes, sprites] = await Promise.all([
      fetch(`${apiBase}/api/v1/scene`, { headers: authHeaders() }),
      fetch(`${apiBase}/api/v1/workflows/default`, { headers: authHeaders() }),
      Sprites.load(),
    ])
    if (!cfgRes.ok) throw new Error(`scene ${cfgRes.status}`)
    if (!wfRes.ok) throw new Error(`workflow ${wfRes.status}`)
    const cfg = (await cfgRes.json()) as SceneConfig
    const wf = (await wfRes.json()) as WorkflowConfig
    return new World(cfg, wf, sprites, apiBase)
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
        a.job = e.data.runLabel ? `${e.data.runLabel}${e.data.task ? ` · ${e.data.task}` : ''}` : null
        if (a.frozen) { this.refreshFocusBubble(a); break }
        a.lastCommandAt = now
        if (a.ambient) a.command(this.goHome(a), now)
        if ((e.data.state === 'working' || e.data.state === 'thinking') && !a.seated && !a.busy) a.command(this.goHome(a), now)
        if (e.data.state === 'blocked') a.bubble = { kind: 'alert', until: now + 2500 }
        if (e.data.state === 'waiting' && !a.busy) a.bubble = { kind: 'ask', until: now + 4000 }
        if (a.visitor) this.visitorState(a, e.data.state, now)
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
        this.board.set(e.data.tasks, e.data.run)
        break
      case 'board.move':
        this.board.move(e.data.task, e.data.stage, e.data.state, e.data.run)
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
        a.command(this.leaveActions(a, null), now)
        break
      }
      case 'agent.enter': {
        const a = this.agents.get(e.data.agent)
        if (!a) return
        this.enter(a, now, this.goHome(a))
        break
      }
      case 'clock.set':
        this.clockHour = e.data.hour
        break
      case 'cafe.special':
        this.specialOverride = e.data.text
        break
      case 'light': {
        const l = this.lights.get(e.data.id)
        if (l) l.on = e.data.state === 'on'
        break
      }
      case 'agent.tool': {
        // Canli arac akisi: ajanin basinda kisa balon ("Write Program.cs"); odaklanmis (frozen) ajanin karti korunur.
        const a = this.agents.get(e.data.agent)
        if (!a || a.frozen) break
        a.bubble = { kind: 'talk', until: now + 2500, text: `${TOOL_GLYPH[e.data.tool] ?? '⚙'} ${e.data.target ?? e.data.tool}` }
        break
      }
      case 'workflow.set':
        // Calisma hangi akisla kosuyorsa pano onu kurar (docs/SCENE.md). Yukleme asenkron; bu arada eski sutunlar kalir.
        void this.loadWorkflow(e.data.key)
        break
    }
  }

  private async loadWorkflow(key: string): Promise<void> {
    try {
      const res = await fetch(`${this.apiBase}/api/v1/workflows/${encodeURIComponent(key)}`, { headers: authHeaders() })
      if (!res.ok) return
      this.board.setWorkflow((await res.json()) as WorkflowConfig)
    } catch (err) {
      console.warn('workflow.set', key, err)
    }
  }

  /**
   * Kullanici bir ajana tikladi (kullanici istegi 2026-09-19): animasyon durur, ajan izleyiciye (asagi) bakar,
   * basinda isini yazan balon acik kalir. Panel kapaninca `unfocus` eski akisi surdurur.
   */
  focus(key: string): void {
    for (const a of this.agents.values()) if (a.frozen && a.key !== key) this.unfocusAgent(a)
    const a = this.agents.get(key)
    if (!a || a.offstage) return
    a.frozen = true
    a.facing = 'down'
    this.refreshFocusBubble(a)
  }

  unfocus(): void {
    for (const a of this.agents.values()) if (a.frozen) this.unfocusAgent(a)
  }

  private unfocusAgent(a: Agent): void {
    a.frozen = false
    a.bubble = null
    if (a.seated) a.facing = a.seated.facing
    a.lastCommandAt = performance.now()
  }

  private refreshFocusBubble(a: Agent): void {
    const state = STATE_LABEL[a.state]
    const card = [a.def.name, a.job ? `▸ ${a.job}` : '▸ iş yok — masasında bekliyor', a.note ? `${state} · ${a.note}` : state]
    a.bubble = { kind: a.state === 'blocked' ? 'alert' : 'talk', until: Number.POSITIVE_INFINITY, card }
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
    // Evi olmayan (ziyaretci) ajan icin ev = pano onu: calisirken orada durur.
    const board = this.cfg.spots['board']
    if (board) return { pos: { x: board.x, y: board.y }, facing: board.facing, look: board.look }
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
      { t: 'call', fn: () => { from.faceTo(to.pos); to.faceTo(from.pos); if (to.seated && !to.frozen) to.facing = 'down' } },
      { t: 'say', kind: fromKind, ms: half },
      { t: 'call', fn: () => { to.bubble = { kind: 'talk', until: performance.now() + half } } },
      { t: 'wait', ms: half },
      { t: 'call', fn: () => { if (to.seated && !to.frozen) to.facing = to.seated.facing } },
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
    this.returns(now)
    // Pano sunucudan da beslenir (kullanici istegi 2026-09-20: "bitmis isi Bitti'de gormuyorum"):
    // SSE yalniz bu oturumda olani tasir, bitmis isler ve yenilenen sayfa buradan gelir.
    if (now - this.boardSyncAt > 6000) { this.boardSyncAt = now; void this.syncBoard() }
    // Kupa icildi sayilir: sure dolunca masadan (ya da elden) kalkar.
    for (const a of this.agents.values()) {
      if (a.mugUntil && now > a.mugUntil) { a.mugUntil = 0; a.mugSeat = null; a.carrying = null }
    }
  }

  /** Ambient cikista olan ajanlar zamani gelince kapidan girer ve evine yurur. */
  private returns(now: number): void {
    const d = this.cfg.spots['door']
    if (!d) return
    for (const a of this.agents.values()) {
      if (!a.offstage || a.returnAt === null || now < a.returnAt) continue
      const busy = a.state === 'working' || a.state === 'thinking'
      this.enter(a, now, a.visitor && !busy ? this.visitTour(a) : this.goHome(a), { ambient: true })
    }
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
      !a.offstage && !a.frozen && !a.busy && (a.state === 'idle' || a.state === 'done') && now > a.ambientReadyAt && now - a.lastCommandAt > 10_000)
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
    const someoneOut = [...this.agents.values()].some(o => o.offstage)
    if (r < 0.12 && !someoneOut && this.cfg.spots['door']) {
      // Disari cik: kapiya yuru, cik, 20-50 s sonra kapidan don.
      const d = this.cfg.spots['door']
      actions = [
        { t: 'walk', to: this.freeNear(d, a) },
        { t: 'face', dir: 'up' },
        { t: 'call', fn: () => this.door.set('open', performance.now()) },
        { t: 'wait', ms: 450 },
        { t: 'call', fn: () => {
          a.offstage = true
          a.returnAt = performance.now() + 20_000 + Math.random() * 30_000
          this.door.set('closed', performance.now())
        } },
      ]
    } else if (r < 0.4) actions = this.drinkTrip(a, 'coffee')
    else if (r < 0.5) actions = this.drinkTrip(a, 'water')
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
        { t: 'call', fn: () => { a.faceTo(o.pos); if (!o.frozen) o.facing = 'down' } },
        { t: 'say', kind: 'talk', ms: ms / 2 },
        { t: 'call', fn: () => { o.bubble = { kind: 'talk', until: performance.now() + ms / 2 } } },
        { t: 'wait', ms: ms / 2 },
        { t: 'call', fn: () => { if (o.seated && !o.frozen) o.facing = o.seated.facing } },
        ...this.goHome(a),
      ]
    }
    if (!actions.length) return
    a.command(actions, now, { ambient: true })
    this.lastAmbientAt = now
  }

  /**
   * Icecek turu (kullanici istegi 2026-09-20): kahve barina ya da su sebiline gider,
   * doldurulmasini bekler, bardagi/kupayi eline alip masasina doner ve masaya birakir.
   * Bir sure sonra icilmis sayilip kalkar. Masasi olmayan ajan (organizator) elinde tasir.
   */
  private drinkTrip(a: Agent, kind: Drink): Action[] {
    const spot = kind === 'coffee' ? 'coffee' : 'water'
    if (!this.cfg.spots[spot] || !this.spotFree(spot, a)) return []
    // Kahve demlenir, su hemen dolar.
    const fill = kind === 'coffee' ? 2500 + Math.random() * 1500 : 1500 + Math.random() * 1000
    return [
      ...this.tripTo(a, spot),
      { t: 'wait', ms: fill },
      { t: 'call', fn: () => {
        a.carrying = kind
        a.bubble = { kind: 'talk', until: performance.now() + 1600 }
      } },
      { t: 'wait', ms: 700 },
      ...this.goHome(a),
      { t: 'call', fn: () => {
        const seat = a.seated ?? this.homeOf(a.key).seat ?? null
        a.mugUntil = performance.now() + (kind === 'coffee' ? 120_000 : 90_000)
        a.mugKind = kind
        if (seat?.mug) { a.mugSeat = seat; a.carrying = null }
      } },
    ]
  }

  /** Kapidan girer: kapi acilir, ajan sahneye konur ve verilen plani isler. */
  private enter(a: Agent, now: number, then: Action[], opts: { ambient?: boolean } = {}): void {
    const d = this.cfg.spots['door']
    if (!d) return
    a.offstage = false
    a.returnAt = null
    a.seated = null
    a.pos = { x: d.x, y: d.y }
    a.facing = 'down'
    this.door.set('open', now)
    a.command([{ t: 'wait', ms: 400 }, ...then], now, opts)
  }

  /** Kapidan cikar. `returnMs` verilirse o kadar sonra kendi doner; null ise donmez. */
  private leaveActions(a: Agent, returnMs: number | null): Action[] {
    const d = this.cfg.spots['door']
    if (!d) return []
    return [
      { t: 'walk', to: () => this.freeNear(d, a) },
      { t: 'call', fn: () => this.door.set('open', performance.now()) },
      { t: 'face', dir: 'up' },
      { t: 'wait', ms: 500 },
      { t: 'call', fn: () => {
        a.offstage = true
        a.returnAt = returnMs === null ? null : performance.now() + returnMs
        this.door.set('closed', performance.now())
      } },
    ]
  }

  /**
   * Ziyaretci turu: masasi olmayan ajan kapidan girer, 2-3 durak dolasir (kahve, pano, su, pencere)
   * ve cikar -- baska bir ofisten ugramis gibi. Duraklar her turda karisir, ayni sira tekrar etmesin.
   * Kullanici karari 2026-09-22: ofise masa eklemek yerine ziyaretci hayatini zenginlestir.
   *
   * Durak rezervasyonu (`a.spot`) yalniz YENI KOMUTTA sifirlanir (entities.ts `command`), bu yuzden cok duraklı
   * turda her duragi elle birakmak gerekir; yoksa ziyaretci panoda dururken kahve makinesini de tutar.
   */
  private visitTour(a: Agent): Action[] {
    const stops = ['coffee', 'board', 'water', 'window']
      .filter(key => this.cfg.spots[key] && this.spotFree(key, a))
      .sort(() => Math.random() - 0.5)
      .slice(0, 2 + Math.floor(Math.random() * 2))

    const actions: Action[] = []
    for (const key of stops) {
      actions.push(...this.tripTo(a, key))
      if (key === 'coffee' || key === 'water') {
        const kind: Drink = key === 'coffee' ? 'coffee' : 'water'
        actions.push(
          { t: 'wait', ms: kind === 'coffee' ? 2500 + Math.random() * 1500 : 1500 + Math.random() * 1000 },
          { t: 'call', fn: () => { a.carrying = kind; a.bubble = { kind: 'talk', until: performance.now() + 1600 } } },
          { t: 'wait', ms: 700 },
        )
      } else {
        actions.push({ t: 'wait', ms: 4000 + Math.random() * 4000 })
      }

      actions.push({ t: 'call', fn: () => { a.spot = null } }) // durak birakilir: sira bekleyen kilitlenmesin
    }

    // Butun duraklar doluysa tur bos kalmasin: panonun onunde bekleyip cikar.
    if (!actions.length) {
      actions.push(...this.tripTo(a, 'board'), { t: 'wait', ms: 6000 + Math.random() * 4000 }, { t: 'call', fn: () => { a.spot = null } })
    }

    // Kupa disari tasinmasin: ziyaretci elini bosaltip cikar.
    actions.push({ t: 'call', fn: () => { a.carrying = null } })
    return [...actions, ...this.leaveActions(a, 60_000 + Math.random() * 90_000)]
  }

  /**
   * Ziyaretci ajanin durum olayina tepkisi: is alinca (working/thinking) hemen girer ve
   * panonun onunde durur; is bitince (idle/done) 3 s sonra cikar, sonra yine ugrar.
   */
  private visitorState(a: Agent, state: AgentState, now: number): void {
    if (state === 'working' || state === 'thinking') {
      if (a.offstage) this.enter(a, now, this.goHome(a))
      else if (!a.busy) a.command(this.goHome(a), now)
      return
    }
    if (!a.offstage && (state === 'idle' || state === 'done')) {
      a.command([{ t: 'wait', ms: 3000 }, ...this.leaveActions(a, 60_000 + Math.random() * 90_000)], now, { ambient: true })
    }
  }

  /**
   * Panoyu sunucudan esitle: son calismalar + detaylari, kart kurali `~/api/board` (buyuk gorunumle ayni).
   * Bitmis calismanin detayi degismez, bir kez cekilir. Hata sessizce yutulur: pano kozmetiktir.
   */
  private async syncBoard(): Promise<void> {
    try {
      const res = await fetch(`${this.apiBase}/api/v1/runs?limit=20`, { headers: authHeaders() })
      if (!res.ok) return
      const runs = ((await res.json()) as RunSummary[]).filter(r => !HIDDEN_RUN_STATUS.has(r.status)).slice(0, 8)
      const settled = new Set(['completed', 'failed', 'interrupted', 'budgetExceeded'])
      await Promise.all(runs.map(async (r) => {
        if (this.runDetails.has(r.id) && settled.has(r.status)) return
        const d = await fetch(`${this.apiBase}/api/v1/runs/${encodeURIComponent(r.id)}`, { headers: authHeaders() })
        if (d.ok) this.runDetails.set(r.id, (await d.json()) as RunDetail)
      }))
      const cards = runs.flatMap(r => { const d = this.runDetails.get(r.id); return d ? deriveCards(r, d) : [] })
      if (cards.length) this.board.sync(cards)
    } catch (err) {
      console.warn('pano esitlenemedi', err)
    }
  }

  /** Kedi oksandi: uyanir, izleyiciye doner, kalp cikarir (yerel; sahne olayi degil). */
  petCat(): void {
    this.cat.pet(performance.now())
  }

  /** Isigi cevir; digerleri de gorsun diye mutlak durumu yayimla (yankisi zararsiz). */
  toggleLight(id: string): void {
    const l = this.lights.get(id)
    if (!l) return
    l.on = !l.on
    void this.publish('light', { id, state: l.on ? 'on' : 'off' })
  }

  private async publish(type: string, data: unknown): Promise<void> {
    try {
      await fetch(`${this.apiBase}/api/v1/scene/commands`, {
        method: 'POST',
        headers: { 'content-type': 'application/json', ...authHeaders() },
        body: JSON.stringify({ type, data }),
      })
    } catch (err) {
      // Sahne kozmetiktir: yayin gitmese de yerel durum degisti.
      console.warn('scene.command', type, err)
    }
  }

  // ------------------------------------------------------------------ cizim

  draw(ctx: CanvasRenderingContext2D, scale: number, now: number): void {
    const { w, h } = this.cfg.world
    if (this.cfg.window) drawSky(ctx, this.cfg.window, this.hourNow(), scale)
    ctx.drawImage(this.background(scale), 0, 0, w, h)

    this.drawCafeSpecial(ctx, now)
    if (this.cfg.door) this.door.draw(ctx, this.sprites, this.cfg.door.x, this.cfg.door.y, this.cfg.door.h, this.cfg.door.w)

    // Nesneler + varliklar alt kenara gore siralanir.
    type Item = { y: number; draw: () => void }
    const items: Item[] = []
    const litMonitors = new Set<string>()
    for (const a of this.agents.values()) if (a.seated?.monitor && !a.offstage) litMonitors.add(a.seated.monitor)
    for (const p of this.props) {
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
    const bgMeta = this.sprites.atlas.background
    const bgImg = this.sprites.img(bgMeta.image)
    const sx = bgMeta.w / w
    const sy = bgMeta.h / h
    for (const [ox, oy, ow, oh] of this.cfg.overlays ?? []) {
      items.push({ y: oy + oh, draw: () => ctx.drawImage(bgImg, ox * sx, oy * sy, ow * sx, oh * sy, ox, oy, ow, oh) })
    }
    for (const a of this.agents.values()) {
      items.push({ y: a.pos.y, draw: () => a.draw(ctx, this.sprites, now) })
      // Masaya birakilan kupa: masanin ustunde, oturan ajanin arkasinda.
      const seat = a.mugSeat
      if (seat?.mug && !a.offstage && now < a.mugUntil) {
        const mug = seat.mug
        items.push({ y: seat.y - 12, draw: () => drawDrink(ctx, this.sprites, mug.x, mug.y, mug.w, now, a.mugKind) })
      }
    }
    items.push({ y: this.cat.pos.y, draw: () => this.cat.draw(ctx, this.sprites, now) })
    // Pano zeminde bir nesnedir: ajanlar onunden ve arkasindan gecer.
    items.push({ y: this.cfg.board.y + this.cfg.board.h, draw: () => this.board.draw(ctx, now) })
    items.sort((p, q) => p.y - q.y)
    for (const it of items) it.draw()

    this.drawLights(ctx, now)
    for (const a of this.agents.values()) a.drawBubble(ctx, this.sprites, now)
    this.drawLabels(ctx)
  }

  /** Arka plan gorseli olcege gore bir kez cizilir; pencere cami seffaftir, gokyuzu her kare altina ayrica cizilir. */
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
    g.drawImage(this.sprites.img(this.sprites.atlas.background.image), 0, 0, w, h)
    for (const p of this.props) {
      if (p.layer !== 'object') this.sprites.drawObject(g, p.sprite, p.x, p.y, p.w, p.h)
    }
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
    // Yerlesim tahta olcusunden turetilir: pano buyurse yazi da buyur, sabit sayi kalmaz.
    const pad = Math.round(w * 0.06)
    const titleSize = Math.max(9, Math.round(h * 0.13))
    const textSize = Math.max(12, Math.round(h * 0.18))
    const ruleY = y + pad + titleSize + 5
    const cup = Math.max(12, Math.round(h * 0.17))
    ctx.save()
    ctx.beginPath(); ctx.rect(x, y, w, h); ctx.clip()
    ctx.fillStyle = '#f0c26a'
    ctx.font = `bold ${titleSize}px "Segoe UI", system-ui, sans-serif`
    ctx.textAlign = 'left'
    ctx.textBaseline = 'alphabetic'
    ctx.fillText('GÜNÜN ÖZELİ', x + pad, y + pad + titleSize)
    ctx.fillStyle = 'rgba(240,194,106,0.5)'
    ctx.fillRect(x + pad, ruleY, w - 2 * pad, 1)
    ctx.globalAlpha = alpha
    ctx.fillStyle = '#f7f1e3'
    ctx.font = `bold ${textSize}px "Segoe UI", system-ui, sans-serif`
    ctx.textAlign = 'center'
    // Cizgi ile kupa serisi arasinin ortasi: buyuk tahtada yazi tepeye yapismaz.
    ctx.fillText(text, x + w / 2, (ruleY + y + h - cup) / 2 + textSize * 0.36, w - 2 * pad)
    // Kupa ve buhar
    ctx.textAlign = 'left'
    const cx = x + w - pad - cup, cy = y + h - pad - Math.round(cup * 0.75)
    ctx.fillStyle = '#f7f1e3'
    ctx.fillRect(cx, cy, cup, Math.round(cup * 0.75))
    ctx.fillRect(cx + cup, cy + Math.round(cup * 0.2), Math.round(cup * 0.25), Math.round(cup * 0.38))
    ctx.globalAlpha = 0.6 + 0.4 * Math.sin(now / 500)
    ctx.fillRect(cx + Math.round(cup * 0.25), cy - Math.round(cup * 0.5), 2, Math.round(cup * 0.32))
    ctx.fillRect(cx + Math.round(cup * 0.6), cy - Math.round(cup * 0.62), 2, Math.round(cup * 0.4))
    ctx.restore()
  }

  /**
   * Isiklar: kapaliysa oda (icindekilerle birlikte) karartilir, aciksa lambanin altina
   * sicak bir hale duser. Imlec lambanin ustundeyse cerceve ve ipucu gorunur.
   */
  private drawLights(ctx: CanvasRenderingContext2D, now: number): void {
    for (const [id, l] of this.lights) {
      const room = l.def.room
      if (room) {
        const [rx, ry, rw, rh] = room
        ctx.save()
        if (l.on) {
          const g = l.def.glow
          if (g) {
            ctx.beginPath(); ctx.rect(rx, ry, rw, rh); ctx.clip()
            const grad = ctx.createRadialGradient(g.x, g.y, 4, g.x, g.y, g.r)
            grad.addColorStop(0, `rgba(255,216,148,${0.2 + 0.02 * Math.sin(now / 1300)})`)
            grad.addColorStop(1, 'rgba(255,216,148,0)')
            ctx.fillStyle = grad
            ctx.fillRect(rx, ry, rw, rh)
          }
        } else {
          ctx.fillStyle = 'rgba(9,13,28,0.62)'
          ctx.fillRect(rx, ry, rw, rh)
        }
        ctx.restore()
      }
      // Abajur: sonunce YALNIZ baslik koyulasir. `multiply` arka planin kendi dokusunu
      // korur (duz bir dikdortgen yapistirmak yerine sicak sariyi soguga cevirir).
      if (l.def.bulb && !l.on) {
        const [bx, by, bw, bh] = l.def.bulb
        ctx.save()
        ctx.globalCompositeOperation = 'multiply'
        ctx.fillStyle = '#5a6480'
        ctx.fillRect(bx, by, bw, bh)
        ctx.restore()
      }

      if (this.hoveredLight !== id) continue
      const [hx, hy, hw, hh] = l.def.hit
      const text = `${l.def.name ?? 'Işık'} · ${l.on ? 'açık' : 'kapalı'}`
      ctx.save()
      ctx.strokeStyle = l.on ? 'rgba(255,216,148,0.9)' : 'rgba(180,192,216,0.85)'
      ctx.lineWidth = 1.5
      ctx.setLineDash([4, 3])
      ctx.strokeRect(hx, hy, hw, hh)
      ctx.setLineDash([])
      ctx.font = '600 10px "Segoe UI", system-ui, sans-serif'
      ctx.textAlign = 'center'
      ctx.textBaseline = 'middle'
      const tw = ctx.measureText(text).width + 12
      ctx.fillStyle = 'rgba(20,24,34,0.85)'
      ctx.fillRect(hx + hw / 2 - tw / 2, hy - 20, tw, 16)
      ctx.fillStyle = '#e8ecf5'
      ctx.fillText(text, hx + hw / 2, hy - 11.5)
      ctx.restore()
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
      const y = a.headY() - 20
      ctx.fillStyle = 'rgba(20,24,34,0.82)'
      ctx.fillRect(x - tw / 2, y - 8, tw, 16)
      ctx.fillStyle = a.hex
      ctx.fillRect(x - tw / 2, y - 8, 3, 16)
      ctx.fillStyle = '#e8ecf5'
      ctx.fillText(text, x + 1, y + 0.5)
    }
  }

  /** Pano dikdortgeni icinde mi (tiklaninca buyuk gorunum). */
  hitBoard(p: Pt): boolean {
    const b = this.cfg.board
    return p.x >= b.x - 6 && p.x <= b.x + b.w + 6 && p.y >= b.y - 6 && p.y <= b.y + b.h + 6
  }

  /** Nokta bir lambanin tiklama dikdortgeninde mi; id doner. */
  hitLight(p: Pt): string | null {
    for (const [id, l] of this.lights) {
      const [x, y, w, h] = l.def.hit
      if (p.x >= x && p.x <= x + w && p.y >= y && p.y <= y + h) return id
    }
    return null
  }

  /** Nokta kedinin uzerinde mi (oksamak icin). */
  hitCat(p: Pt): boolean {
    const c = this.cat.pos
    return p.x > c.x - 22 && p.x < c.x + 22 && p.y > c.y - 30 && p.y < c.y + 6
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

}
