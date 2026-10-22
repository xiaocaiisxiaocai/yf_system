import type { QuietRequestConfig } from './client'

/**
 * 请求是否通过 QuietRequestConfig 关闭了全局错误提示。协作端与 OEM 门户共用这一套规则；
 * 关闭提示的调用方自行负责展示最终失败。
 */
export function isQuietRequestError(error: unknown): boolean {
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
