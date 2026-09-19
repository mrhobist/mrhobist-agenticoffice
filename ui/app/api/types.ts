/**
 * docs/API.md sozlesmesinin EL YAZIMI TypeScript karsiligi.
 *
 * `npm run gen:api` (openapi-typescript -> shared/types/api.ts) calisir hale gelince bu
 * dosya onunla DEGISTIRILECEK; yeni alan once sozlesmeye (Api + docs/API.md) eklenir,
 * buraya kopyalanir. Sahne ve is akisi tipleri `~/scene/contract.ts`'de kalir.
 */

export type Provider = 'anthropic' | 'nvidia' | 'ollama'

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

/** RFC 9457 govdesi + errorCode. `title`/`detail` EKRANA BASILMAZ; yalniz errorCode eslenir. */
export interface ProblemDetails {
  type?: string
  title?: string
  status?: number
  detail?: string
  instance?: string
  errorCode?: string
}
