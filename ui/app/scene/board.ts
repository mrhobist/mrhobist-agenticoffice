import type { BoardTask, TaskState, WorkflowConfig } from './contract'

interface Column { id: string; title: string; hex: string }

const COLUMN_HEX = ['#f3c34a', '#4fa3e0', '#ef6f9a', '#a889e6', '#7cc46b', '#e0995c']
const DONE_HEX = '#7cc46b'

/**
 * Sprint panosu: sutunlar config/workflow.json'dan (devir adimlari haric), notlar
 * board.set / board.move olaylarindan. Tamamen prosedurel cizilir; asset yok.
 *
 * "Ileriyi yansitma": aktif gorevin bir sonraki sutununda kesikli hayalet not durur,
 * boylece pano yalniz simdiyi degil, isin nereye akacagini da gosterir.
 */
export class Board {
  columns: Column[] = []
  tasks: BoardTask[] = []
  /** Gorev -> hareket animasyonu (0..1). */
  private anim = new Map<string, { fromCol: number; fromRow: number; t: number }>()
  private lastCol = new Map<string, number>()

  constructor(readonly rect: { x: number; y: number; w: number; h: number; title: string }) {}

  setWorkflow(wf: WorkflowConfig): void {
    const stages = wf.stages.filter(s => s.kind !== 'handoff')
    this.columns = stages.map((s, i) => ({ id: s.id, title: s.title, hex: COLUMN_HEX[i % COLUMN_HEX.length]! }))
    this.columns.push({ id: '__done', title: 'Bitti', hex: DONE_HEX })
  }

  set(tasks: BoardTask[]): void {
    this.tasks = tasks.map(t => ({ ...t }))
    this.anim.clear()
    this.lastCol.clear()
  }

  move(id: string, stage: string, state: TaskState): void {
    const t = this.tasks.find(x => x.id === id)
    if (!t) { this.tasks.push({ id, title: id, stage, state }); return }
    const fromCol = this.colIndex(t)
    const fromRow = this.rowOf(t)
    t.stage = stage
    t.state = state
    if (this.colIndex(t) !== fromCol) this.anim.set(id, { fromCol, fromRow, t: 0 })
  }

  update(dt: number): void {
    for (const [id, a] of this.anim) {
      a.t += dt / 0.9
      if (a.t >= 1) this.anim.delete(id)
    }
  }

  /** Devir adimlari sutun degildir: gorev bir sonraki gercek sutuna yazilir. */
  private colIndex(t: BoardTask): number {
    if (t.state === 'done' && this.isLastStage(t.stage)) return this.columns.length - 1
    const i = this.columns.findIndex(c => c.id === t.stage)
    if (i >= 0) return i
    return Math.max(0, this.lastCol.get(t.id) ?? 0)
  }

  private isLastStage(stage: string): boolean {
    const real = this.columns.filter(c => c.id !== '__done')
    return real[real.length - 1]?.id === stage
  }

  private rowOf(t: BoardTask): number {
    const col = this.colIndex(t)
    return this.tasks.filter(x => this.colIndex(x) === col).indexOf(t)
  }

  draw(ctx: CanvasRenderingContext2D, now: number): void {
    const { x, y, w, h, title } = this.rect
    // Cerceve
    ctx.fillStyle = '#2b323f'
    ctx.fillRect(x - 4, y - 4, w + 8, h + 8)
    ctx.fillStyle = '#e9e6dc'
    ctx.fillRect(x, y, w, h)
    ctx.fillStyle = '#cfcabb'
    ctx.fillRect(x, y + h - 3, w, 3)

    ctx.font = 'bold 13px "Segoe UI", system-ui, sans-serif'
    ctx.textAlign = 'left'
    ctx.textBaseline = 'alphabetic'
    ctx.fillStyle = '#2a2f3d'
    ctx.fillText(title, x + 10, y + 19)

    if (!this.columns.length) return
    const pad = 8
    const gap = 5
    const top = y + 28
    const colW = (w - pad * 2 - gap * (this.columns.length - 1)) / this.columns.length
    const noteW = Math.min(22, colW - 8)
    const noteH = 17
    const perRow = Math.max(1, Math.floor((colW - 4) / (noteW + 3)))

    const colX = (i: number) => x + pad + i * (colW + gap)
    const notePos = (col: number, row: number) => ({
      x: colX(col) + 3 + (row % perRow) * (noteW + 3),
      y: top + 24 + Math.floor(row / perRow) * (noteH + 4),
    })

    // Sutun basliklari
    ctx.font = 'bold 8px "Segoe UI", system-ui, sans-serif'
    ctx.textAlign = 'center'
    for (let i = 0; i < this.columns.length; i++) {
      const c = this.columns[i]!
      ctx.fillStyle = c.hex
      ctx.fillRect(colX(i), top, colW, 16)
      ctx.fillStyle = '#1f2430'
      ctx.fillText(c.title.toUpperCase(), colX(i) + colW / 2, top + 11, colW - 4)
      ctx.strokeStyle = 'rgba(0,0,0,0.08)'
      ctx.lineWidth = 1
      ctx.beginPath()
      ctx.moveTo(colX(i) + colW + gap / 2, top)
      ctx.lineTo(colX(i) + colW + gap / 2, y + h - 6)
      if (i < this.columns.length - 1) ctx.stroke()
    }

    // Notlar
    const rows = new Map<number, number>()
    for (const t of this.tasks) {
      const col = this.colIndex(t)
      this.lastCol.set(t.id, col)
      const row = rows.get(col) ?? 0
      rows.set(col, row + 1)
      let p = notePos(col, row)
      const a = this.anim.get(t.id)
      if (a) {
        const from = notePos(a.fromCol, a.fromRow)
        const e = 1 - (1 - a.t) ** 3
        p = { x: from.x + (p.x - from.x) * e, y: from.y + (p.y - from.y) * e - Math.sin(e * Math.PI) * 14 }
      }
      const hex = this.columns[col]?.hex ?? '#ccc'
      this.drawNote(ctx, p.x, p.y, noteW, noteH, hex, t.state, now, t.id)

      // Ileri: aktif gorevin bir sonraki sutununda hayalet not.
      if (t.state === 'active' && col < this.columns.length - 1) {
        const nextRow = rows.get(col + 1) ?? 0
        const g = notePos(col + 1, nextRow)
        ctx.setLineDash([2, 2])
        ctx.strokeStyle = 'rgba(42,47,61,0.45)'
        ctx.lineWidth = 1
        ctx.strokeRect(g.x + 0.5, g.y + 0.5, noteW - 1, noteH - 1)
        ctx.setLineDash([])
      }
    }
  }

  private drawNote(
    ctx: CanvasRenderingContext2D, x: number, y: number, w: number, h: number,
    hex: string, state: TaskState, now: number, id: string,
  ): void {
    const wobble = state === 'active' ? Math.sin(now / 300 + x) * 0.6 : 0
    ctx.save()
    ctx.translate(x + w / 2, y + h / 2)
    ctx.rotate(wobble * 0.03)
    ctx.globalAlpha = state === 'queued' ? 0.55 : 1
    ctx.fillStyle = 'rgba(0,0,0,0.12)'
    ctx.fillRect(-w / 2 + 1, -h / 2 + 2, w, h)
    ctx.fillStyle = hex
    ctx.fillRect(-w / 2, -h / 2, w, h)
    ctx.fillStyle = 'rgba(255,255,255,0.35)'
    ctx.fillRect(-w / 2, -h / 2, w, 2)
    if (state === 'blocked') {
      ctx.strokeStyle = '#d23b3b'
      ctx.lineWidth = 1.5
      ctx.strokeRect(-w / 2 + 0.75, -h / 2 + 0.75, w - 1.5, h - 1.5)
    }
    if (state === 'done') {
      ctx.strokeStyle = 'rgba(30,60,30,0.75)'
      ctx.lineWidth = 1.5
      ctx.beginPath()
      ctx.moveTo(-w / 4, 0)
      ctx.lineTo(-w / 12, h / 4)
      ctx.lineTo(w / 3, -h / 4)
      ctx.stroke()
    } else {
      ctx.fillStyle = 'rgba(30,30,40,0.55)'
      ctx.font = 'bold 7px "Segoe UI", system-ui, sans-serif'
      ctx.textAlign = 'center'
      ctx.textBaseline = 'middle'
      ctx.fillText(id.slice(0, 3), 0, 1, w - 2)
    }
    ctx.restore()
  }
}
