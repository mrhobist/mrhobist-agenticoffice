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
  background?: SingleMeta
  objects: {
    sofaSet: SingleMeta
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
    files.add(atlas.objects.sofaSet.image); files.add(atlas.objects.tileset.image)
    if (atlas.background) files.add(atlas.background.image)

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
    if (name.startsWith('@')) {
      const key = name.slice(1) as 'sofaSet'
      const m = this.atlas.objects[key]
      if (!m || !('w' in m)) throw new Error(`tekil nesne yok: ${name}`)
      return { w: m.w / this.worldScale, h: m.h / this.worldScale }
    }
    const f = this.atlas.objects.tileset.frames[name]
    if (!f) throw new Error(`tileset karesi yok: ${name}`)
    return { w: f.w, h: f.h }
  }

  drawObject(ctx: CanvasRenderingContext2D, name: string, x: number, y: number, w: number, h: number): void {
    if (name.startsWith('@')) {
      const key = name.slice(1) as 'sofaSet'
      const m = this.atlas.objects[key]
      if (!m || !('w' in m)) return
      ctx.drawImage(this.img(m.image), 0, 0, m.w, m.h, x, y, w, h)
      return
    }
    const f = this.atlas.objects.tileset.frames[name]
    if (!f) return
    ctx.drawImage(this.img(this.atlas.objects.tileset.image), f.x, f.y, f.w, f.h, x, y, w, h)
  }

  /**
   * Bir sayfa karesini alt-orta noktasi (x, y) olacak sekilde dunya boyutunda cizer.
   * `flipX` kareyi yatay aynalar (yandan oturma: sol/sag tek kaynaktan).
   */
  drawFrame(
    ctx: CanvasRenderingContext2D, meta: SheetMeta, col: number, row: number,
    x: number, y: number, opts: { scale?: number; alpha?: number; flipX?: boolean } = {},
  ): void {
    const s = (opts.scale ?? 1) / this.worldScale
    const dw = meta.frameW * s
    const dh = meta.frameH * s
    const prev = ctx.globalAlpha
    if (opts.alpha !== undefined) ctx.globalAlpha = opts.alpha
    if (opts.flipX) {
      ctx.save()
      ctx.translate(x, 0)
      ctx.scale(-1, 1)
      ctx.drawImage(this.img(meta.image), col * meta.frameW, row * meta.frameH, meta.frameW, meta.frameH, -dw / 2, y - dh, dw, dh)
      ctx.restore()
    } else {
      ctx.drawImage(this.img(meta.image), col * meta.frameW, row * meta.frameH, meta.frameW, meta.frameH, x - dw / 2, y - dh, dw, dh)
    }
    ctx.globalAlpha = prev
  }

  drawSingle(ctx: CanvasRenderingContext2D, meta: SingleMeta, x: number, y: number, scale = 1): void {
    const s = scale / this.worldScale
    const dw = meta.w * s
    const dh = meta.h * s
    ctx.drawImage(this.img(meta.image), 0, 0, meta.w, meta.h, x - dw / 2, y - dh, dw, dh)
  }
}
