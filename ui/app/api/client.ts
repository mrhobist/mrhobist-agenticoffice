import type { ProblemDetails } from './types'

/**
 * Api istegi basarisiz oldu. `status === 0` → ag hatasi, yanit hic gelmedi.
 * `errorCode` Problem Details govdesinden okunur; metne cevirme `~/api/errors.ts`'de.
 */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly errorCode: string | null,
    readonly path: string,
  ) {
    super(errorCode ?? (status ? `HTTP ${status}` : 'network'))
    this.name = 'ApiError'
  }

  /** Uc henuz yazilmamis ya da Api kapali: ag hatasi ya da errorCode'suz 404. */
  get endpointMissing(): boolean {
    return this.status === 0 || (this.status === 404 && !this.errorCode)
  }
}

export function isApiError(e: unknown): e is ApiError {
  return e instanceof ApiError
}

export interface ApiClient {
  get<T>(path: string): Promise<T>
  put<T>(path: string, body: unknown): Promise<T>
  /** Govdesiz POST icin body verilmez. */
  post<T>(path: string, body?: unknown): Promise<T>
}

/** Setup icinde cagrilir (useRuntimeConfig). Donen nesne sonradan her yerde kullanilir. */
export function useApiClient(): ApiClient {
  const base = useRuntimeConfig().public.apiBase as string
  return {
    get: path => request(base, path, {}),
    put: (path, body) => request(base, path, {
      method: 'PUT',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(body),
    }),
    post: (path, body) => request(base, path, {
      method: 'POST',
      headers: body === undefined ? {} : { 'content-type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
    }),
  }
}

async function request<T>(base: string, path: string, init: RequestInit): Promise<T> {
  let res: Response
  try {
    res = await fetch(`${base}${path}`, { ...init, headers: { accept: 'application/json', ...init.headers } })
  } catch {
    throw new ApiError(0, null, path)
  }
  if (!res.ok) throw new ApiError(res.status, await readErrorCode(res), path)
  if (res.status === 204) return undefined as T
  return (await res.json()) as T
}

async function readErrorCode(res: Response): Promise<string | null> {
  try {
    const p = (await res.json()) as ProblemDetails
    return typeof p.errorCode === 'string' && p.errorCode ? p.errorCode : null
  } catch {
    return null
  }
}
