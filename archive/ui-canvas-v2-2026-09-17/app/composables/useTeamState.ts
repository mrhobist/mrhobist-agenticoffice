/**
 * Ekip durumunun TEK kaynagi.
 *
 * Iki gorsel dil (akis grafigi ve piksel ofis) ayni nesneyi okur. Faz 5'te
 * `GET /api/v1/runs/{id}/events` SSE akisi buraya baglanacak; simdilik gercek
 * bir calismanin ritmini taklit eden bir zamanlayici besliyor.
 *
 * Boylece iki render katmani birbirinden habersiz kalir ve veri sekli
 * degistiginde tek yerde degisir.
 */

export type AgentState = 'idle' | 'working' | 'blocked' | 'waiting' | 'done'

export interface AgentView {
  key: string
  name: string
  short: string
  /** Hem three.js (0x) hem canvas (#) icin; tek yerde tanimli. */
  hex: string
  state: AgentState
  task: string | null
}

export type EdgeKind = 'pipeline' | 'reject' | 'ask'

export interface EdgeView {
  from: string
  to: string
  kind: EdgeKind
  /** Su an is akiyor mu — parcacik/animasyon bununla tetiklenir. */
  active: boolean
}

export interface RunView {
  stage: string
  task: string
  round: number
  agents: AgentView[]
  edges: EdgeView[]
  tasks: TaskView[]
}

export type StageKind = 'analyze' | 'design' | 'implement' | 'review' | 'handoff'

export interface StageView {
  id: string
  title: string
  kind: StageKind
  agent: string
}

export type TaskState = 'queued' | 'active' | 'blocked' | 'done'

export interface TaskView {
  id: string
  title: string
  /** Bulundugu adimin id'si. */
  stage: string
  state: TaskState
  round: number
}

/** Scrum board sutunlari — config/workflow.json'in UI karsiligi. */
export const STAGES: StageView[] = [
  { id: 'analiz', title: 'Analiz', kind: 'analyze', agent: 'analyst' },
  { id: 'tasarim', title: 'Tasarim', kind: 'design', agent: 'designer' },
  { id: 'devir-1', title: 'Devir', kind: 'handoff', agent: 'organizer' },
  { id: 'gelistirme', title: 'Gelistirme', kind: 'implement', agent: 'developer' },
  { id: 'test', title: 'Test', kind: 'review', agent: 'tester' },
  { id: 'onay', title: 'Onay', kind: 'review', agent: 'tester' },
]

const AGENTS: ReadonlyArray<Omit<AgentView, 'state' | 'task'>> = [
  { key: 'analyst', name: 'Analist', short: 'ANL', hex: '#4f8ef7' },
  { key: 'designer', name: 'Tasarimci', short: 'TSR', hex: '#e0699a' },
  { key: 'organizer', name: 'Organizator', short: 'ORG', hex: '#d4663a' },
  { key: 'developer', name: 'Developer', short: 'DEV', hex: '#35b98a' },
  { key: 'tester', name: 'Testci', short: 'TST', hex: '#d99b3a' },
  { key: 'manager', name: 'Manager', short: 'MNG', hex: '#8b7ff0' },
]

/**
 * Rol -> renk. TEK KAYNAK.
 * Onceden ScrumBoard, MiniBoardFlow ve main.css'te ayri ayri duruyordu;
 * bir rengi degistirmek dort dosya dokunmayi gerektiriyordu.
 */
export const AGENT_HEX: Record<string, string> =
  Object.fromEntries(AGENTS.map(a => [a.key, a.hex]))

export const agentHex = (key: string) => AGENT_HEX[key] ?? '#5a6070'

const EDGES: ReadonlyArray<Omit<EdgeView, 'active'>> = [
  { from: 'analyst', to: 'designer', kind: 'pipeline' },
  { from: 'designer', to: 'organizer', kind: 'pipeline' },
  { from: 'organizer', to: 'developer', kind: 'pipeline' },
  { from: 'developer', to: 'tester', kind: 'pipeline' },
  { from: 'tester', to: 'developer', kind: 'reject' },
  { from: 'developer', to: 'manager', kind: 'ask' },
  { from: 'manager', to: 'developer', kind: 'ask' },
]

/** Bir calismanin senaryosu. Her adim birkac saniye surer. */
interface Beat {
  stage: string
  task: string
  round: number
  /** Rol -> durum. Yazilmayan roller `idle`. */
  states: Partial<Record<string, AgentState>>
  /** Su an akan hatlar: "from>to". */
  flowing: string[]
  /** Rol -> gorunen kisa metin. */
  tasks?: Partial<Record<string, string>>
  /** Board: gorev -> bulundugu adim + durum. */
  board: Array<[string, string, TaskState]>
  ms: number
}

const TASK_TITLES: Record<string, string> = {
  T1: 'slugify fonksiyonu',
  T2: 'Turkce karakter tablosu',
  T3: 'Pytest testleri',
  T4: 'Modul dokumantasyonu',
}

const SCRIPT: Beat[] = [
  {
    stage: 'Analiz', task: 'T1', round: 1, ms: 3200,
    states: { analyst: 'working' },
    tasks: { analyst: 'Brief cozumleniyor' },
    flowing: [],
    board: [['T1','analiz','active'],['T2','analiz','queued'],['T3','analiz','queued'],['T4','analiz','queued']],
  },
  {
    stage: 'Analiz', task: 'T1', round: 1, ms: 1600,
    states: { analyst: 'done', designer: 'idle' },
    tasks: { analyst: '4 gorev cikti' },
    flowing: ['analyst>designer'],
    board: [['T1','tasarim','queued'],['T2','tasarim','queued'],['T3','tasarim','queued'],['T4','tasarim','queued']],
  },
  {
    stage: 'Tasarim', task: 'T1', round: 1, ms: 3000,
    states: { analyst: 'done', designer: 'working' },
    tasks: { designer: 'Akis ve ekranlar' },
    flowing: [],
    board: [['T1','tasarim','active'],['T2','tasarim','queued'],['T3','tasarim','queued'],['T4','tasarim','queued']],
  },
  {
    stage: 'Devir', task: 'T1', round: 1, ms: 1800,
    states: { analyst: 'done', designer: 'done', organizer: 'working' },
    tasks: { organizer: 'Devir notu' },
    flowing: ['designer>organizer', 'organizer>developer'],
    board: [['T1','devir-1','active'],['T2','tasarim','done'],['T3','tasarim','queued'],['T4','tasarim','queued']],
  },
  {
    stage: 'Gelistirme', task: 'T1', round: 1, ms: 3400,
    states: { analyst: 'done', designer: 'done', organizer: 'done', developer: 'working' },
    tasks: { developer: 'slugify fonksiyonu' },
    flowing: [],
    board: [['T1','gelistirme','active'],['T2','gelistirme','queued'],['T3','tasarim','done'],['T4','tasarim','queued']],
  },
  {
    stage: 'Gelistirme', task: 'T1', round: 1, ms: 2600,
    states: {
      analyst: 'done', designer: 'done', organizer: 'done',
      developer: 'waiting', manager: 'working',
    },
    tasks: { developer: 'Manager bekleniyor', manager: "'i' mi 'l' mi?" },
    flowing: ['developer>manager', 'manager>developer'],
    board: [['T1','gelistirme','blocked'],['T2','gelistirme','queued'],['T3','tasarim','done'],['T4','tasarim','queued']],
  },
  {
    stage: 'Test', task: 'T1', round: 1, ms: 3000,
    states: {
      analyst: 'done', designer: 'done', organizer: 'done',
      developer: 'done', tester: 'working',
    },
    tasks: { tester: 'Kurallar denetleniyor' },
    flowing: ['developer>tester'],
    board: [['T1','test','active'],['T2','gelistirme','queued'],['T3','tasarim','done'],['T4','tasarim','queued']],
  },
  {
    stage: 'Test', task: 'T1', round: 1, ms: 2800,
    states: {
      analyst: 'done', designer: 'done', organizer: 'done',
      developer: 'blocked', tester: 'blocked',
    },
    tasks: { tester: '2 ihlal', developer: 'Duzeltiliyor' },
    flowing: ['tester>developer'],
    board: [['T1','test','blocked'],['T2','gelistirme','queued'],['T3','tasarim','done'],['T4','tasarim','queued']],
  },
  {
    stage: 'Gelistirme', task: 'T1', round: 2, ms: 3000,
    states: {
      analyst: 'done', designer: 'done', organizer: 'done', developer: 'working',
    },
    tasks: { developer: 'tur 2/3 — docstring' },
    flowing: [],
    board: [['T1','gelistirme','active'],['T2','gelistirme','queued'],['T3','tasarim','done'],['T4','tasarim','queued']],
  },
  {
    stage: 'Onay', task: 'T1', round: 2, ms: 3200,
    states: {
      analyst: 'done', designer: 'done', organizer: 'done',
      developer: 'done', tester: 'done',
    },
    tasks: { tester: 'Onaylandi' },
    flowing: ['developer>tester'],
    board: [['T1','onay','done'],['T2','gelistirme','active'],['T3','gelistirme','queued'],['T4','tasarim','done']],
  },
]

export function useTeamState() {
  const beat = ref(0)

  const run = computed<RunView>(() => {
    const b = SCRIPT[beat.value % SCRIPT.length]!
    return {
      stage: b.stage,
      task: b.task,
      round: b.round,
      agents: AGENTS.map(a => ({
        ...a,
        state: b.states[a.key] ?? 'idle',
        task: b.tasks?.[a.key] ?? null,
      })),
      edges: EDGES.map(e => ({
        ...e,
        active: b.flowing.includes(`${e.from}>${e.to}`),
      })),
      tasks: b.board.map(([id, stage, state]) => ({
        id,
        title: TASK_TITLES[id] ?? id,
        stage,
        state,
        round: id === 'T1' ? b.round : 1,
      })),
    }
  })

  let timer: ReturnType<typeof setTimeout> | undefined
  function schedule() {
    const b = SCRIPT[beat.value % SCRIPT.length]!
    timer = setTimeout(() => {
      beat.value += 1
      schedule()
    }, b.ms)
  }

  onMounted(schedule)
  onBeforeUnmount(() => clearTimeout(timer))

  return { run }
}

/** Durum -> renk. Her iki gorsel dil ayni esleme kullanir. */
export const STATE_TINT: Record<AgentState, string> = {
  idle: '#5a6070',
  working: '#35b98a',
  blocked: '#e05252',
  waiting: '#d99b3a',
  done: '#7f8aa3',
}

/** Gorev durumu -> renk. Board ve iki mini board ayni esleme kullanir. */
export const TASK_TINT: Record<TaskState, string> = {
  queued: '#7b87a0',
  active: '#35b98a',
  blocked: '#e05252',
  done: '#4d5875',
}

export const TASK_LABEL: Record<TaskState, string> = {
  queued: 'sirada',
  active: 'calisiliyor',
  blocked: 'takildi',
  done: 'bitti',
}

export const STATE_LABEL: Record<AgentState, string> = {
  idle: 'bosta',
  working: 'calisiyor',
  blocked: 'takildi',
  waiting: 'bekliyor',
  done: 'bitti',
}
