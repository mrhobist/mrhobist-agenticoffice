import type { Provider } from './types'

/** Kullaniciya donuk sabit metinler; sozlesmenin parcasi degil, gen:api gelince de kalir. */
export const PROVIDER_LABEL: Record<Provider, string> = {
  anthropic: 'Anthropic',
  nvidia: 'NVIDIA',
  ollama: 'Ollama',
}

export const PROVIDERS = Object.keys(PROVIDER_LABEL) as Provider[]

export function providerLabel(p: Provider | null): string {
  return p ? PROVIDER_LABEL[p] : 'varsayılan sağlayıcı'
}
