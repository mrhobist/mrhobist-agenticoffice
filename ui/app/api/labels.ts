import type { Effort, InboxKind, Provider, RunStatus } from './types'

/** Kullaniciya donuk sabit metinler; sozlesmenin parcasi degil, gen:api gelince de kalir. */
export const PROVIDER_LABEL: Record<Provider, string> = {
  anthropic: 'Anthropic',
  nvidia: 'NVIDIA',
  ollama: 'Ollama',
}

export const PROVIDERS = Object.keys(PROVIDER_LABEL) as Provider[]

export const EFFORT_LABEL: Record<Effort, string> = {
  low: 'düşük',
  medium: 'orta',
  high: 'yüksek',
  max: 'en yüksek',
}

export const EFFORTS = Object.keys(EFFORT_LABEL) as Effort[]

/** Calisma durumu → kullaniciya donuk metin (docs/DOMAIN.md yasam dongusu). */
export const RUN_STATUS_LABEL: Record<RunStatus, string> = {
  running: 'çalışıyor',
  completed: 'tamamlandı',
  failed: 'başarısız',
  interrupted: 'yarıda kaldı',
  budgetExceeded: 'bütçe aşıldı',
  policyRejected: 'politika reddi',
  awaitingApproval: 'plan onay bekliyor',
  paused: 'durakladı',
  cancelled: 'iptal edildi',
}

/** Gelen kutusu turu → kisa rozet. Soru = senden cevap bekleniyor; karar = calisma durdu, sen secersin. */
export const INBOX_KIND_LABEL: Record<InboxKind, string> = {
  approval: 'soru · plan onayı',
  decision: 'karar',
  question: 'soru',
}

/** Durum → hangi kullanici eylemleri gecerli (docs/API.md → retry / cancel). */
export const RUN_CANCELLABLE: ReadonlySet<RunStatus> = new Set<RunStatus>(['running', 'awaitingApproval', 'paused', 'failed', 'interrupted', 'budgetExceeded'])
export const RUN_RETRYABLE: ReadonlySet<RunStatus> = new Set<RunStatus>(['failed', 'interrupted', 'budgetExceeded', 'cancelled'])

export function providerLabel(p: Provider | null): string {
  return p ? PROVIDER_LABEL[p] : 'varsayılan sağlayıcı'
}
