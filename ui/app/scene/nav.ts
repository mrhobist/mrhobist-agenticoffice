import type { Pt, SceneConfig } from './contract'

/**
 * Yuruyus izgarasi: `walkable` dikdortgeni icinde, `blocked` disinda kalan hucreler.
 * A* 8 komsulu; kose kesmez. Sonuc dogru gorus hattiyla sadelestirilir ki
 * karakterler merdiven gibi degil, duz yurusun.
 */
export class NavGrid {
  readonly cell: number
  readonly cols: number
  readonly rows: number
  private readonly open: Uint8Array

  constructor(cfg: SceneConfig, cell = 16, margin = 6) {
    this.cell = cell
    this.cols = Math.ceil(cfg.world.w / cell)
    this.rows = Math.ceil(cfg.world.h / cell)
    this.open = new Uint8Array(this.cols * this.rows)
    const [fx, fy, fw, fh] = cfg.walkable
    for (let r = 0; r < this.rows; r++) {
      for (let c = 0; c < this.cols; c++) {
        const x = c * cell + cell / 2
        const y = r * cell + cell / 2
        let ok = x > fx + margin && x < fx + fw - margin && y > fy + margin && y < fy + fh - margin
        if (ok) {
          for (const [bx, by, bw, bh] of cfg.blocked) {
            if (x > bx - margin && x < bx + bw + margin && y > by - margin && y < by + bh + margin) { ok = false; break }
          }
        }
        this.open[r * this.cols + c] = ok ? 1 : 0
      }
    }
  }

  isOpen(c: number, r: number): boolean {
    return c >= 0 && r >= 0 && c < this.cols && r < this.rows && this.open[r * this.cols + c] === 1
  }

  isOpenAt(p: Pt): boolean {
    return this.isOpen(Math.floor(p.x / this.cell), Math.floor(p.y / this.cell))
  }

  /** Verilen noktaya en yakin acik hucre merkezi (mobilya icindeki koltuk icin yaklasma noktasi). */
  nearestOpen(p: Pt): Pt {
    const c0 = Math.floor(p.x / this.cell)
    const r0 = Math.floor(p.y / this.cell)
    let best: Pt | null = null
    let bestD = Infinity
    for (let rad = 0; rad < 40 && !best; rad++) {
      for (let dr = -rad; dr <= rad; dr++) {
        for (let dc = -rad; dc <= rad; dc++) {
          if (Math.max(Math.abs(dr), Math.abs(dc)) !== rad) continue
          const c = c0 + dc
          const r = r0 + dr
          if (!this.isOpen(c, r)) continue
          const q = { x: c * this.cell + this.cell / 2, y: r * this.cell + this.cell / 2 }
          // Masanin onu (asagisi) tercih edilir: karakter masaya arkasi donuk oturur.
          const bias = q.y < p.y ? 1.8 : 1
          const d = ((q.x - p.x) ** 2 + (q.y - p.y) ** 2) * bias
          if (d < bestD) { bestD = d; best = q }
        }
      }
    }
    return best ?? p
  }

  path(from: Pt, to: Pt): Pt[] {
    const start = this.toCell(this.isOpenAt(from) ? from : this.nearestOpen(from))
    const goalPt = this.isOpenAt(to) ? to : this.nearestOpen(to)
    const goal = this.toCell(goalPt)
    const key = (c: number, r: number) => r * this.cols + c
    const g = new Map<number, number>()
    const parent = new Map<number, number>()
    const closed = new Set<number>()
    const openList: Array<{ k: number; f: number }> = []
    const h = (c: number, r: number) => Math.hypot(c - goal.c, r - goal.r)
    const sk = key(start.c, start.r)
    g.set(sk, 0)
    openList.push({ k: sk, f: h(start.c, start.r) })

    let found = false
    while (openList.length) {
      let bi = 0
      for (let i = 1; i < openList.length; i++) if (openList[i]!.f < openList[bi]!.f) bi = i
      const cur = openList.splice(bi, 1)[0]!
      if (closed.has(cur.k)) continue
      closed.add(cur.k)
      const cc = cur.k % this.cols
      const cr = Math.floor(cur.k / this.cols)
      if (cc === goal.c && cr === goal.r) { found = true; break }
      for (let dr = -1; dr <= 1; dr++) {
        for (let dc = -1; dc <= 1; dc++) {
          if (!dr && !dc) continue
          const nc = cc + dc
          const nr = cr + dr
          if (!this.isOpen(nc, nr)) continue
          if (dr && dc && (!this.isOpen(cc + dc, cr) || !this.isOpen(cc, cr + dr))) continue
          const nk = key(nc, nr)
          if (closed.has(nk)) continue
          const ng = (g.get(cur.k) ?? 0) + (dr && dc ? 1.4142 : 1)
          if (ng < (g.get(nk) ?? Infinity)) {
            g.set(nk, ng)
            parent.set(nk, cur.k)
            openList.push({ k: nk, f: ng + h(nc, nr) })
          }
        }
      }
    }
    if (!found) return [goalPt]

    const cells: Pt[] = []
    let k: number | undefined = key(goal.c, goal.r)
    while (k !== undefined) {
      cells.push({ x: (k % this.cols) * this.cell + this.cell / 2, y: Math.floor(k / this.cols) * this.cell + this.cell / 2 })
      k = parent.get(k)
    }
    cells.reverse()
    cells[cells.length - 1] = goalPt
    return this.smooth(cells)
  }

  private smooth(pts: Pt[]): Pt[] {
    if (pts.length <= 2) return pts
    const out: Pt[] = [pts[0]!]
    let i = 0
    while (i < pts.length - 1) {
      let j = pts.length - 1
      while (j > i + 1 && !this.lineClear(pts[i]!, pts[j]!)) j--
      out.push(pts[j]!)
      i = j
    }
    return out
  }

  private lineClear(a: Pt, b: Pt): boolean {
    const n = Math.ceil(Math.hypot(b.x - a.x, b.y - a.y) / (this.cell / 2))
    for (let s = 1; s < n; s++) {
      const t = s / n
      if (!this.isOpenAt({ x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t })) return false
    }
    return true
  }

  private toCell(p: Pt): { c: number; r: number } {
    return {
      c: Math.min(this.cols - 1, Math.max(0, Math.floor(p.x / this.cell))),
      r: Math.min(this.rows - 1, Math.max(0, Math.floor(p.y / this.cell))),
    }
  }
}
