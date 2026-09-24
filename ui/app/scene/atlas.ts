/**
 * Sprite atlasi: scripts/build-sprites.py ciktisi (ui/public/sprites/atlas.json).
 * Karakter/kedi sayfalari dunya olceginin `worldScale` kati cozunurlukte tutulur;
 * tileset nesneleri kaynak cozunurluktedir ve yerlesim hedef genislik verir.
 */

export interface SheetMeta {
  image: string
  frameW: number
  frameH: number
  cols: number
  rows: number
  dirRows?: Record<string, number>
}

export interface SingleMeta { image: string; w: number; h: number }

/** V2 karakter: 8 yon yuruyus (6 kare), yandan otur/kalk (8), yazma (3 on + 3 arka). */
export interface CharacterSheets { walk: SheetMeta; sit: SheetMeta; type: SheetMeta }

export interface AtlasJson {
  worldScale: number
  characters: Record<string, CharacterSheets>
  cat: { walk: SheetMeta; sleep: SingleMeta; sit: SingleMeta; lie: SingleMeta }
  fx: { bubble: SheetMeta; door: SheetMeta }
  background: SingleMeta
  objects: {
    tileset: { image: string; frames: Record<string, { x: number; y: number; w: number; h: number }> }
  }
}

function loadImage(src: string): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const img = new Image()
    img.onload = () => resolve(img)
    img.onerror = () => reject(new Error(`sprite yuklenemedi: ${src}`))
    img.src = src
  })
}

export class Sprites {
  private constructor(
    readonly atlas: AtlasJson,
    private readonly images: Map<string, HTMLImageElement>,
  ) {}

  static async load(base = '/sprites/'): Promise<Sprites> {
    const res = await fetch(`${base}atlas.json`, { cache: 'no-cache' })
    if (!res.ok) throw new Error(`atlas.json ${res.status}`)
    const atlas = (await res.json()) as AtlasJson

    const files = new Set<string>()
    for (const c of Object.values(atlas.characters)) { files.add(c.walk.image); files.add(c.sit.image); files.add(c.type.image) }
    files.add(atlas.cat.walk.image); files.add(atlas.cat.sleep.image)
    files.add(atlas.cat.sit.image); files.add(atlas.cat.lie.image)
    files.add(atlas.fx.bubble.image); files.add(atlas.fx.door.image)
    files.add(atlas.objects.tileset.image)
    files.add(atlas.background.image)

    const images = new Map<string, HTMLImageElement>()
    await Promise.all([...files].map(async f => images.set(f, await loadImage(base + f))))
    return new Sprites(atlas, images)
  }

  get worldScale() { return this.atlas.worldScale }

  img(file: string): HTMLImageElement {
    const i = this.images.get(file)
    if (!i) throw new Error(`atlas gorseli yok: ${file}`)
    return i
  }

  /** Bir nesnenin kaynak boyutu; yerlesimde h verilmediginde oran buradan gelir. */
  objectSize(name: string): { w: number; h: number } {
    const f = this.atlas.objects.tileset.frames[name]
    if (!f) throw new Error(`tileset karesi yok: ${name}`)
    return { w: f.w, h: f.h }
  }

  /** Nesne onbellegi: ekran olceginde bir kez yeniden orneklenmis kareler. Olcek degisince bosaltilir. */
  private readonly objectCache = new Map<string, HTMLCanvasElement>()
  private objectCacheScale = 0

  /**
   * Tileset nesnesi kaynak cozunurluktedir (monitor ~30 px) ve buyuk ekranda ~3.5 kat buyutulur. Her karede `high`
   * kalite suzgecle yeniden orneklemek yuksek cozunurlukte kare basina ~40 ms tutuyordu: ajanlar yururken sahne
   * takiliyordu (olcum 2026-09-26: 2400 px tuvalde kare 41 ms, 40'i bu cagri). Nesne o anki cihaz olceginde BIR KEZ
   * cizilip saklanir, sonra 1:1 kopyalanir (arka plan onbellegiyle ayni yontem).
   */
  drawObject(ctx: CanvasRenderingContext2D, name: string, x: number, y: number, w: number, h: number): void {
    const f = this.atlas.objects.tileset.frames[name]
    if (!f) return
    const m = ctx.getTransform()
    const scale = Math.hypot(m.a, m.b)
    if (Math.abs(scale - this.objectCacheScale) > 1e-3) { this.objectCache.clear(); this.objectCacheScale = scale }
    const key = `${name}|${w.toFixed(2)}|${h.toFixed(2)}`
    let c = this.objectCache.get(key)
    if (!c) {
      c = document.createElement('canvas')
      c.width = Math.max(1, Math.ceil(w * scale))
      c.height = Math.max(1, Math.ceil(h * scale))
      const g = c.getContext('2d')!
      g.imageSmoothingEnabled = true
      g.imageSmoothingQuality = 'high'
      g.drawImage(this.img(this.atlas.objects.tileset.image), f.x, f.y, f.w, f.h, 0, 0, c.width, c.height)
      this.objectCache.set(key, c)
    }
    ctx.drawImage(c, x, y, w, h)
  }

  /**
   * Bir sayfa karesini alt-orta noktasi (x, y) olacak sekilde dunya boyutunda cizer.
   * `flipX` kareyi yatay aynalar (yandan oturma: sol/sag tek kaynaktan).
   */
  drawFrame(
    ctx: CanvasRenderingContext2D, meta: SheetMeta, col: number, row: number,
    x: number, y: number, opts: { flipX?: boolean } = {},
  ): void {
    const dw = meta.frameW / this.worldScale
    const dh = meta.frameH / this.worldScale
    if (opts.flipX) {
      ctx.save()
      ctx.translate(x, 0)
      ctx.scale(-1, 1)
      ctx.drawImage(this.img(meta.image), col * meta.frameW, row * meta.frameH, meta.frameW, meta.frameH, -dw / 2, y - dh, dw, dh)
      ctx.restore()
    } else {
      ctx.drawImage(this.img(meta.image), col * meta.frameW, row * meta.frameH, meta.frameW, meta.frameH, x - dw / 2, y - dh, dw, dh)
    }
  }

  drawSingle(ctx: CanvasRenderingContext2D, meta: SingleMeta, x: number, y: number): void {
    const dw = meta.w / this.worldScale
    const dh = meta.h / this.worldScale
    ctx.drawImage(this.img(meta.image), 0, 0, meta.w, meta.h, x - dw / 2, y - dh, dw, dh)
  }
}
