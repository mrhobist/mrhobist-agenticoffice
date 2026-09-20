/**
 * Sahne sozlesmesi: config/scene.json sekli ve Api'nin SSE ile yayimladigi olaylar.
 * Kaynak: docs/SCENE.md. Api tarafi: src/MrHobist.AITeam.Api/Scene/SceneEndpoints.cs
 * (EventTypes). Yeni olay turu iki yere de SONA eklenir.
 */

export type Facing = 'up' | 'down' | 'left' | 'right' | 'upleft' | 'upright' | 'downleft' | 'downright'

export interface Pt { x: number; y: number }
/** Durak. `look` verilirse varan ajan ORAYA bakar (durdugu noktaya gore hesaplanir); yoksa `facing`. */
export interface SpotDef extends Pt {
  facing?: Facing
  look?: Pt
  /** Ayni anda kac kisi (varsayilan 1). Dolu ise gelen `queue` noktasinda bekler. */
  capacity?: number
  queue?: Pt
}
/** Oturulabilir yer (ayak/sandalye tabani). `monitor`: oturulunca acilan monitor prop id'si. */
export interface SeatDef extends Pt { facing: Facing; monitor?: string }

export type PropLayer = 'floor' | 'wall' | 'object'

export interface PropDef {
  id: string
  /** atlas tileset kare adi. */
  sprite: string
  x: number
  y: number
  w: number
  /** Verilirse oran bozulur (esnetme); verilmezse sprite oranindan hesaplanir. */
  h?: number
  layer: PropLayer
  /** Siralama anahtari (alt kenar yerine). Masa ustundeki monitor icin masa alt kenari + 1. */
  sortY?: number
  /** Verilirse: bu prop bir koltugun monitorudur; koltuk bossa bu sprite cizilir (ekran kapali). */
  spriteOff?: string
}

export interface AgentDef {
  key: string
  name: string
  /** atlas.characters anahtari. */
  sprite: string
  home: { seat?: string; spot?: string }
}

export interface SceneConfig {
  world: { w: number; h: number }
  /** Arka plan gorseli (atlas.background). Gomulu mobilya `erase` ile build'de silinir. */
  background: { image: string; erase?: number[][] }
  /** Arka plandan kesilip varliklarin ONUNE cizilen dikdortgenler (cam duvar, masa onu). */
  overlays?: [number, number, number, number][]
  /** Pencere cami: arka planda seffaftir, arkasina saate gore gokyuzu cizilir. */
  window?: { x: number; y: number; w: number; h: number }
  /** Yuruyus alani (dis dikdortgen). */
  walkable: [number, number, number, number]
  props: PropDef[]
  /** Kapi: `w` verilirse o genislige esnetilir (duvar bosluğunu tam doldurmak icin). */
  door?: { x: number; y: number; h: number; w?: number }
  /** Kanban panosu; zeminde ayaksiz durur, tiklaninca buyuk gorunum acilir. */
  board: { x: number; y: number; w: number; h: number; title: string }
  /** Kahve bari panosu: gunun ozeli. */
  cafe?: { board: { x: number; y: number; w: number; h: number }; specials: string[]; intervalMs?: number }
  seats: Record<string, SeatDef>
  spots: Record<string, SpotDef>
  blocked: [number, number, number, number][]
  agents: AgentDef[]
  cat: { bed: Pt; spots: Pt[] }
}

export type StageKind = 'analyze' | 'design' | 'implement' | 'review' | 'handoff'

export interface StageDef {
  id: string
  title: string
  kind: StageKind
  role: string
  officeRole: string
  description: string
}

export interface WorkflowConfig {
  maxReviewRounds: number
  stages: StageDef[]
}

export type AgentState = 'idle' | 'working' | 'thinking' | 'blocked' | 'waiting' | 'done'
export type TaskState = 'queued' | 'active' | 'blocked' | 'done'
export type BubbleKind = 'talk' | 'ask' | 'alert'
export type MeetKind = 'handoff' | 'ask' | 'reject'
export type DoorState = 'closed' | 'open'
export type CatAction = 'sleep' | 'wander' | 'sit'

export interface BoardTask {
  id: string
  title: string
  /** workflow.stages[].id */
  stage: string
  state: TaskState
}

export type SceneEvent =
  | { type: 'agent.state'; data: { agent: string; state: AgentState; note?: string; run?: string; runLabel?: string; task?: string } }
  | { type: 'agent.say'; data: { agent: string; kind: BubbleKind; text?: string; ms?: number } }
  | { type: 'agent.goto'; data: { agent: string; spot: string } }
  | { type: 'agent.home'; data: { agent: string } }
  | { type: 'meet'; data: { from: string; to: string; kind: MeetKind; ms?: number } }
  | { type: 'board.set'; data: { tasks: BoardTask[] } }
  | { type: 'board.move'; data: { task: string; stage: string; state: TaskState } }
  | { type: 'run.stage'; data: { stage: string; task: string; round: number } }
  | { type: 'cat'; data: { action: CatAction; spot?: number } }
  | { type: 'door'; data: { state: DoorState } }
  | { type: 'agent.leave'; data: { agent: string } }
  | { type: 'agent.enter'; data: { agent: string } }
  | { type: 'clock.set'; data: { hour: number | null } }
  | { type: 'cafe.special'; data: { text: string | null } }
  | { type: 'workflow.set'; data: { key: string } }
  | { type: 'scene.reload'; data: { reason?: string } }

export const EVENT_TYPES: ReadonlyArray<SceneEvent['type']> = [
  'agent.state', 'agent.say', 'agent.goto', 'agent.home', 'meet',
  'board.set', 'board.move', 'run.stage', 'cat', 'door',
  'agent.leave', 'agent.enter', 'clock.set', 'cafe.special', 'workflow.set', 'scene.reload',
]

export type FeedStatus = 'connecting' | 'live' | 'reconnecting' | 'mock'

/** Rol -> renk. Board notlari ve HUD ayni tabloyu okur. */
export const ROLE_HEX: Record<string, string> = {
  analyst: '#4f8ef7',
  designer: '#e0699a',
  organizer: '#d9a13a',
  developer: '#35b98a',
  tester: '#b58ad8',
  manager: '#c65c5c',
}

/** Sahne tanimi olmayan (sonradan eklenen) ajanlar icin sirayla verilen renkler. */
export const EXTRA_ROLE_HEX: ReadonlyArray<string> = ['#7fa6c9', '#4f7fd9', '#8fbf6a', '#d98a4f', '#9a7fd9']

export const STATE_LABEL: Record<AgentState, string> = {
  idle: 'boşta',
  working: 'çalışıyor',
  thinking: 'düşünüyor',
  blocked: 'takıldı',
  waiting: 'bekliyor',
  done: 'bitti',
}

export const STATE_HEX: Record<AgentState, string> = {
  idle: '#7b87a0',
  working: '#35b98a',
  thinking: '#4f8ef7',
  blocked: '#e05252',
  waiting: '#d99b3a',
  done: '#a3adc4',
}
