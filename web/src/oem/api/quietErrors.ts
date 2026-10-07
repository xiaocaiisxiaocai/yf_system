import type { QuietRequestConfig } from '../../api/client'

/**
 * Whether the request opted out of the global error toast with the same `QuietRequestConfig`
 * flags (and the same rules) as the collaboration HTTP client. Callers that quiet a request
 * own reporting its final failure.
 */
export function isQuietedError(error: unknown): boolean {
  const failure = error as { config?: QuietRequestConfig; code?: string; response?: { status?: number } } | null
  const config = failure?.config
  if (!config) return false
  const status = failure?.response?.status
  const transient = config.quietNetworkError === true && (
    failure?.code === 'ERR_NETWORK' || failure?.code === 'ECONNABORTED'
    || status === 404 || status === 408 || status === 429 || (typeof status === 'number' && status >= 500))
  const client = config.quietClientError === true && typeof status === 'number' && [400, 403, 404, 413, 422].includes(status)
  return transient || client
}
