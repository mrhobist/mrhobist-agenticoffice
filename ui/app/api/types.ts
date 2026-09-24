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
  /** Yetkili MCP sunuculari (md frontmatter `mcp`). Yonetim: MCP paneli. */
  mcp?: string[] | null
  /** Ofisteki karakteri (scene.json → agents[].sprite). PUT/POST'ta null = korunur / otomatik. */
  sprite?: string | null
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
  cacheReadTokens?: number | null
  cacheWriteTokens?: number | null
  /** Tur yarida kesildi: kullanim canli bildirimden, maliyet fiyat tablosundan tahmin. */
  cutShort?: boolean | null
}

export interface RuntimeUsage { inputTokens: number; outputTokens: number; reasoningChars: number; cacheReadTokens: number; cacheWriteTokens: number }

/** GET /api/v1/runs/{id}/live — suren tur: canlilik, akis (arac/metin/dusunce), o ana kadarki kullanim, ajanin baglami. */
export interface LiveTurn {
  agent: string
  task: string | null
  stage: string | null
  startedAt: string
  lastSeenAt: string
  toolCount: number
  usage: RuntimeUsage
  stream: Array<{ ts: string; kind: 'tool' | 'text' | 'thinking'; tool: string | null; target: string | null; text: string | null }>
  context: Array<{ name: string; role: string; chars: number; text: string }>
  /** Hareketsizlik esigi (s): bu kadar hareket olmazsa tur kesilir. */
  idleLimitS: number
}

/** GET /api/v1/usage/split — kim ne harcadi (ofis ajani / Claude Code oturumlari). */
export interface SpendReport {
  since: string
  until: string | null
  weeklyPercent: number | null
  weeklyResetsAt: string | null
  officeRecordedUsd: number
  officeRecordedTurns: number
  sources: Array<{
    key: 'office' | 'sessions'
    label: string
    messages: number
    inputTokens: number
    outputTokens: number
    costUsd: number | null
    quotaPoints: number | null
    lines: Array<{ source: string; project: string; model: string; messages: number; inputTokens: number; outputTokens: number; cacheReadTokens: number; cacheWriteTokens: number; costUsd: number | null }>
    /** Fiyati bilinmeyen modeller: costUsd ve quotaPoints bunlar HARIC (alt sinir). */
    unpricedModels?: string[] | null
  }>
  notes: string[]
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
  /** Projenin butun calismalarinda harcanan girdi token toplami. */
  totalInputTokens?: number
  /** Projenin butun calismalarinda uretilen cikti token toplami. */
  totalOutputTokens?: number
  /** Proje butcesi: $ tavani. null/undefined = sinirsiz. */
  maxCostUsd?: number | null
  /** Proje butcesi: token tavani (girdi + cikti). null/undefined = sinirsiz. */
  maxTokens?: number | null
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
  /** Proje butcesi: $ tavani. null = sinirsiz (varsayilan). */
  maxCostUsd?: number | null
  /** Proje butcesi: token tavani (girdi + cikti). null = sinirsiz (varsayilan). */
  maxTokens?: number | null
}

/** PUT /api/v1/projects/{key} govdesi. */
export interface ProjectModel {
  title: string
  description?: string | null
  workflow?: string | null
  targetDir?: string | null
  color?: string | null
  /** Proje butcesi: $ tavani. null = sinirsiz (varsayilan). */
  maxCostUsd?: number | null
  /** Proje butcesi: token tavani (girdi + cikti). null = sinirsiz (varsayilan). */
  maxTokens?: number | null
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
  /** POST /attachments'in dondurdugu gecici ek kimlikleri. */
  attachments?: string[] | null
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
  /** Kaldigi adim: "yeniden dene" buradan surer (durum bilgisi; `detail` yalniz metin). */
  step?: RunStep | null
  /** Running ama hazir gorevin ajani baska calismada dolu; bir adim kapaninca sunucu yeniden dagitima koyar. */
  waitingSince?: string | null
}

/** Calismanin kaldigi adim (adiyla tasinir; yeni uye sona). */
export type RunStep = 'analyze' | 'approval' | 'dispatch'

/** Fazi kim kapatti: `agent` ajanin sonucu; digerleri sistem kaynakli (tur sayilmaz). Eski kayitlarda yok. */
export type PhaseCause = 'agent' | 'limit' | 'cancelled' | 'interrupted' | 'timeout'

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

/**
 * GET/PUT /api/v1/settings — saglayici basina limit korumasi esigi (%) ve Anthropic istem onbelleginin omru
 * (`5m` | `1h` | null = CLI varsayilani, bugun 1 sa).
 */
export interface AppSettings { limitGuards: Record<string, number>; cacheTtl?: '5m' | '1h' | null }

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
  cause?: PhaseCause | null
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
  /** Takilan ajanin sorusunu kim cevaplar: null = ajanin kendi can_ask'i · 'user' = kullanici · ajan anahtari. */
  askRole: string | null
  /** Analistin planini kim onaylar: null / 'user' = kullanici · ajan anahtari. */
  planApprover: string | null
}

/** GET /api/v1/runs/{id} */
export interface RunDetail extends RunSummary {
  workflowDef: WorkflowDetail | null
  spec: Spec | null
  /** topolojik yurutme sirasi */
  order: string[]
  tasks: Array<{ id: string; phases: Phase[] }>
  messages: RunMessage[]
  /** Is verilirken eklenen dosyalar; indirme GET /runs/{id}/attachments/{fileName}. */
  attachments?: RunAttachment[] | null
}

// ------------------------------------------------------------------ ekler (docs/DOMAIN.md → Ekler)

export type AttachmentKind = NonNullable<Schemas['AttachmentKind']>
/** POST /api/v1/attachments ogesi: yuklendi, henuz bir ise bagli degil. */
export type StagedAttachment = Schemas['StagedAttachment']
export type RunAttachment = Schemas['RunAttachment']
/** GET /api/v1/attachments/rules */
export type AttachmentRules = Schemas['AttachmentRulesView']

// ------------------------------------------------------------------ MCP (docs/DOMAIN.md → MCP sunuculari)

export type McpTransport = NonNullable<Schemas['McpTransport']>
/** GET /api/v1/mcp ogesi. env/headers degerleri DONMEZ; yalniz ad + kayitli mi. */
export type McpServerView = Schemas['McpServerView']
/** POST/PUT govdesi. env/headers satirinda value null → kayitli deger korunur. */
export type McpServerRequest = Schemas['McpServerRequest']
export type McpTestResult = Schemas['McpTestResult']
/** GET /api/v1/mcp/catalog ogesi (config/mcp-catalog.json): hazir sunucu ve baglanti yontemleri. Sir tasimaz. */
export type McpCatalogEntry = Schemas['McpCatalogEntry']
export type McpAuthOption = Schemas['McpAuthOption']
export type McpCatalogField = Schemas['McpCatalogField']
/** POST /api/v1/mcp/catalog/{key}/install govdesi. */
export type McpInstallRequest = Schemas['McpInstallRequest']
/** PUT /api/v1/mcp/{key}/tools govdesi: ajana acilacak araclar, null = hepsi. */
export type McpToolsRequest = Schemas['McpToolsRequest']
/** GET /api/v1/mcp/usage */
export type McpUsageReport = Schemas['McpUsageReport']
export type McpServerUsage = Schemas['McpServerUsage']
/** POST /api/v1/mcp/{key}/oauth/start */
export type McpOAuthStartRequest = Schemas['McpOAuthStartRequest']
export type McpOAuthStart = Schemas['McpOAuthStart']

/** RFC 9457 govdesi + errorCode. `title`/`detail` EKRANA BASILMAZ; yalniz errorCode eslenir. */
export interface ProblemDetails {
  type?: string
  title?: string
  status?: number
  detail?: string
  instance?: string
  errorCode?: string
}
