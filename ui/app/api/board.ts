import type { RunDetail, RunStatus, RunSummary } from './types'
import type { TaskState } from '~/scene/contract'

/** Klasik Kanban seritleri: hem sahnedeki kucuk pano hem buyuk gorunum bunlari kullanir. */
export type BoardLane = 'todo' | 'doing' | 'review' | 'done'

export interface DerivedCard {
  /** `{calisma}:{gorev}` — iki calismanin ayni gorev kimligi carpismasin. */
  id: string
  task: string
  title: string
  state: TaskState
  lane: BoardLane
  stage: string
  run: RunSummary
}

/** Panoda gorunmeyen durumlar: gorevleri hic baslamamis ya da vazgecilmis. */
export const HIDDEN_RUN_STATUS: ReadonlySet<RunStatus> = new Set<RunStatus>(['cancelled', 'policyRejected'])

/**
 * Bir calisma detayindan pano kartlari. Kural sunucudaki <c>BoardTarget</c> ile AYNIDIR ve tek yerdedir
 * (buyuk gorunum de sahnedeki kucuk pano da bunu cagirir):
 * Started → o adimda calisiliyor · Done/Skipped → SONRAKI adimda sirada, son adimsa Bitti ·
 * Rejected → onceki implement adiminda takildi · Failed → ayni adimda takildi · faz yok → ilk adimda sirada.
 * Serit: Bitti → done; calisiliyor → doing; inceleme adimi ya da takildi → review; kalan → todo.
 */
export function deriveCards(r: RunSummary, d: RunDetail): DerivedCard[] {
  const stages = d.workflowDef?.stages.filter(s => s.kind !== 'analyze' && s.kind !== 'handoff') ?? []
  return (d.spec?.tasks ?? []).map((t) => {
    const phases = d.tasks.find(x => x.id === t.id)?.phases ?? []
    const last = phases[phases.length - 1]
    let state: TaskState = 'queued'
    let stage = stages[0]?.id ?? '__done'
    if (last) {
      const i = stages.findIndex(s => s.id === last.stage)
      stage = last.stage
      if (last.status === 'started') state = 'active'
      else if (last.status === 'done' || last.status === 'skipped') {
        if (i >= 0 && i + 1 < stages.length) { stage = stages[i + 1]!.id; state = 'queued' }
        else { stage = '__done'; state = 'done' }
      } else if (last.status === 'rejected') {
        state = 'blocked'
        for (let k = i - 1; k >= 0; k--) if (stages[k]!.kind === 'implement') { stage = stages[k]!.id; break }
      } else state = 'blocked'
    }
    const kind = stages.find(s => s.id === stage)?.kind
    const lane: BoardLane = stage === '__done' ? 'done' : state === 'active' ? 'doing' : (kind === 'review' || state === 'blocked') ? 'review' : 'todo'
    return { id: `${r.id}:${t.id}`, task: t.id, title: t.title, state, lane, stage, run: r }
  })
}
