/**
 * docs/API.md sozlesmesinin TypeScript karsiligi.
 *
 * Enum'lar `npm run gen:api` ile uretilen `shared/types/api.ts`'den TURETILIR (CLAUDE.md §5): sunucu bir
 * uye ekler/yeniden adlandirirsa typecheck kirilir, sessizce sapmaz. Nesne tipleri okunurluk icin burada
 * acik yazilir; alan eklenince once sozlesme (Api + docs/API.md), sonra `gen:api`, sonra burasi.
 * Sahne ve is akisi tipleri `~/scene/contract.ts`'de kalir.
 */
import type { components } from '~~/shared/types/api'

type Schemas = components['schemas']

export type Provider = NonNullable<Schemas['Provider']>
/** Serbest metin sozlesmede (string); dort deger Domain/Agents/Efforts.All ile birebir. */
export type Effort = 'low' | 'medium' | 'high' | 'max'

/** GET /api/v1/agents — frontmatter ozeti, prompt yok. */
export interface AgentListItem {
  key: string
  name: string
  summary: string
  officeRoles: string[]
  /** null = varsayilan saglayici. `claude` kabul edilmez (400 agent.invalid_provider). */
  provider: Provider | null
  /** null = saglayici varsayilani. Serbest metin; erisilebilirlik GET /models ile ayri dogrulanir. */
  model: string | null
  /** Akil yurutme eforu; null = varsayilan (high). */
  effort: Effort | null
  /** config/knowledge/ anahtarlari. */
  includes: string[]
  /** Soru sorabildigi ajan anahtari ya da null. */
  canAsk: string | null
}

/** GET/PUT /api/v1/agents/{key} */
export interface AgentDetail extends AgentListItem {
  /** md govdesi: sistem promptu. Bos olamaz (400 agent.prompt_empty). */
  prompt: string
  /** govde + alt md'ler: modele giden metin. Salt okunur. */
  composedPrompt: string
}

/**
 * PUT govdesi = AgentDetail eksi composedPrompt. `key` yoldan gelir; govdede de yollanir
 * (yolla ayni), sunucu yok sayarsa zarar vermez — varsayimla ilerlenir.
 */
export type AgentUpdate = Omit<AgentDetail, 'composedPrompt'>

/** GET /api/v1/knowledge — alt md'ler. */
export interface KnowledgeItem {
  key: string
  title: string
  body: string
}

/** GET /api/v1/models?provider=… — runtime kapaliysa 503 runtime.unavailable. */
export interface ModelInfo {
  provider: Provider
  model: string
  /** Katalogda gorunmek erisilebilir olmak degildir (LESSONS). */
  reachable: boolean
  detail: string | null
}

/** GET /api/v1/providers — kimlik + modeller. UI ilk yuklemede bakar. */
export interface ProviderStatus {
  provider: Provider
  loggedIn: boolean
  /** Oturumda hesap e-postasi; API anahtarinda maskeli son (`sk-…ab12`). */
  account: string | null
  detail: string
  models: ModelInfo[]
  /** `session` (CLI oturumu: Claude Code / Codex) | `apikey` (kayitli anahtar) | null (giris yok). */
  method: 'session' | 'apikey' | null
}

/** POST /api/v1/providers/{provider}/login govdesi. Anthropic: claudeai | console | apikey; OpenAI: chatgpt | apikey. */
export type LoginMode = 'claudeai' | 'console' | 'chatgpt' | 'apikey'
export interface ProviderLoginRequest { mode: LoginMode; email?: string | null; apiKey?: string | null }

/** POST /api/v1/providers/{provider}/login — giris akisi kullanicinin makinesinde basladi (apikey: anahtar dogrulandi ve kaydedildi). */
export interface LoginStarted {
  provider: Provider
  started: boolean
  detail: string
}

/** Saglayicinin bildirdigi kota penceresi; percent = kullanilan yuzde. */
export interface UsageLimit {
  kind: string
  group: string | null
  percent: number
  severity: string | null
  resetsAt: string | null
  scope: string | null
  isActive: boolean
}

/** GET /api/v1/limits?refresh= — saglayici basina kalan kullanim (ust bar). */
export interface ProviderLimits {
  provider: Provider
  available: boolean
  detail: string
  subscription: string | null
  fetchedAt: string | null
  limits: UsageLimit[]
}

/** GET /api/v1/runs/{id}/turns — bir ajanin tek LLM cagrisi, tam prompt/cikti ile. */
export interface Turn {
  ts: string
  agent: string
  stage: string | null
  task: string | null
  round: number | null
  provider: string
  model: string
  destination: string
  durationS: number
  promptChars: number
  outputChars: number
  costUsd: number | null
  prompt: string | null
  output: string | null
  inputTokens: number | null
  outputTokens: number | null
  /** Ajanin arac cagrilari (Write/Edit/Bash...), sirali; arac yoksa bos/null. */
  toolUses?: ToolUse[] | null
  /** Ajan dongusunun tur sayisi. */
  turns?: number | null
}

export interface ToolUse { tool: string; target: string | null }

/** GET /api/v1/agents/{key}/work — ajanin calisma basina isi (Isler sekmesi). */
export interface AgentRunWork {
  run: RunSummary
  turns: Turn[]
  messages: RunMessage[]
  phases: Phase[]
}

/** GET /api/v1/usage — runs/ turlarindan saglayici+model bazinda toplam. Abonelik limiti DEGIL. */
export interface UsageItem {
  provider: string
  model: string
  turns: number
  runs: number
  inputTokens: number
  outputTokens: number
  costUsd: number
  lastAt: string | null
}

// ------------------------------------------------------------------ calismalar (docs/API.md → Calismalar)

/** HTTP'de enum'lar camelCase adiyla tasinir (Api JSON secenekleri); dosyada (run.json) PascalCase. */
export type Sensitivity = Schemas['Sensitivity']
export type RunStatus = Schemas['RunStatus']
export type PhaseStatus = Schemas['PhaseStatus']
export type MessageKind = Schemas['MessageKind']

/** GET /api/v1/projects — proje karti: tanim + islerin ozeti. */
export interface ProjectCard {
  key: string
  title: string
  description: string
  workflow: string
  targetDir: string
  ownerId: string
  createdAt: string
  runs: number
  running: number
  awaitingApproval: number
  paused: number
  failed: number
  completed: number
  totalCostUsd: number
  lastActivityAt: string | null
  /** Kokte run.cmd var: "Projeyi baslat" dugmesi acik. */
  launchable?: boolean
  /** Proje rengi (#rrggbb): ray karti, Kanban "Tumu". */
  color?: string
  /** Ray ve Kanban sirasi (kucuk once). */
  order?: number
}

/** POST /api/v1/projects/reorder govdesi. */
export interface ReorderRequest { keys: string[] }

/** POST /api/v1/projects/{key}/launch yaniti. */
export interface LaunchResult { key: string; processId: number; launcher: string }

/** DELETE /api/v1/projects/{key}?deleteFiles= yaniti: gecmis projeyle gitti; dosyalar istendiyse ve varsa silindi. */
export interface ProjectDeleteResult { key: string; targetDir: string; runsDeleted: number; filesDeleted: boolean }

/** GET /api/v1/projects/dirs?path= — klasor secicinin bir seviyesi (depo kokune gore yollar). */
export interface WorkspaceDirectory { name: string; path: string }
export interface DirectoryListing { path: string; parent: string | null; dirs: WorkspaceDirectory[] }

/** POST /api/v1/projects govdesi. */
export interface CreateProjectRequest {
  key: string
  title: string
  description?: string | null
  workflow?: string | null
  targetDir?: string | null
  color?: string | null
}

/** PUT /api/v1/projects/{key} govdesi. */
export interface ProjectModel {
  title: string
  description?: string | null
  workflow?: string | null
  targetDir?: string | null
  color?: string | null
}

/** POST /api/v1/runs govdesi. Is yalniz bir projenin icinde baslar. */
export interface RunRequest {
  project: string
  brief: string
  workflow?: string | null
  sensitivity?: Sensitivity | null
  label?: string | null
  /** Butce ust siniri ($); asilinca calisma BudgetExceeded ile durur. null = sinir yok. */
  maxCostUsd?: number | null
}

/** run.json */
export interface RunSummary {
  id: string
  label: string
  brief: string
  sensitivity: Sensitivity
  workflow: string
  status: RunStatus
  startedAt: string
  finishedAt: string | null
  totalCostUsd: number
  detail: string | null
  /** Butce tavani (USD); null = sinir yok. */
  maxCostUsd: number | null
  /** "Yeniden dene" sayisi. */
  retries: number
  /** Ait oldugu proje anahtari; is yalniz bir projenin icinde baslar. */
  project: string
  ownerId: string
  /** AwaitingInput: akis takildi, kullanicidan secim bekleniyor (docs/DOMAIN.md → Takilma). */
  question?: UserQuestion | null
  /** Paused + resumeAt: limit korumasi bekletti; bu saatte kendisi surer. */
  resumeAt?: string | null
}

export interface QuestionOption { id: string; label: string; detail: string; needsNote: boolean }
export interface UserQuestion {
  ts: string
  agent: string
  text: string
  options: QuestionOption[]
  task: string | null
  stage: string | null
  context: string | null
}
/** POST /api/v1/runs/{id}/answer */
export interface AnswerRequest { choice: string; note?: string | null }

/** GET/PUT /api/v1/settings — saglayici basina limit korumasi esigi (%). */
export interface AppSettings { limitGuards: Record<string, number> }

/** Gelen kutusu satirinin turu: soru (plan onayi), karar (durdu: yeniden dene / iptal), soru (ajan ask). */
export type InboxKind = Schemas['InboxKind']

/** GET /api/v1/runs/overview → inbox[]: bir calismanin kullanicidan bekledigi tek sey. */
export interface InboxItem {
  runId: string
  label: string
  status: RunStatus
  kind: InboxKind
  ts: string
  title: string
  detail: string | null
  task: string | null
}

/** GET /api/v1/runs/overview — kac is var, ne durumda, kaci bir sey bekliyor. failed tum dusme turlerini toplar. */
export interface RunsOverview {
  total: number
  running: number
  awaitingApproval: number
  paused: number
  failed: number
  completed: number
  cancelled: number
  inbox: InboxItem[]
  /** Takilip senden secim bekleyenler (AwaitingInput). */
  awaitingInput?: number
}

export interface RunTask {
  id: string
  title: string
  description: string
  files: string[]
  acceptance: string[]
  dependsOn: string[]
}

export interface Spec {
  summary: string
  architecture: string
  rules: string[]
  tasks: RunTask[]
}

export interface Phase {
  ts: string
  task: string
  stage: string
  stageTitle: string
  kind: string
  agent: string
  round: number
  status: PhaseStatus
  durationS: number | null
  detail: string | null
}

export interface RunMessage {
  ts: string
  kind: MessageKind
  from: string
  to: string
  body: string
  task: string | null
  stage: string | null
  ref: string | null
  subject: string
}

/** GET /api/v1/workflows */
export interface WorkflowListItem {
  key: string
  title: string
  isDefault: boolean
  stageCount: number
  roles: string[]
}

export interface WorkflowDetail {
  key: string
  title: string
  maxReviewRounds: number
  handoffRole: string | null
  stages: Array<{ id: string; title: string; kind: string; role: string; officeRole: string; description: string }>
}

/** GET /api/v1/runs/{id} */
export interface RunDetail extends RunSummary {
  workflowDef: WorkflowDetail | null
  spec: Spec | null
  /** topolojik yurutme sirasi */
  order: string[]
  tasks: Array<{ id: string; phases: Phase[] }>
  messages: RunMessage[]
}

/** RFC 9457 govdesi + errorCode. `title`/`detail` EKRANA BASILMAZ; yalniz errorCode eslenir. */
export interface ProblemDetails {
  type?: string
  title?: string
  status?: number
  detail?: string
  instance?: string
  errorCode?: string
}
