import axios from 'axios'

const NETWORK_MESSAGE = '网络连接失败，请稍后重试'
const TIMEOUT_MESSAGE = '请求超时，请稍后重试'

/**
 * User-facing text for a failed OEM call. A server message wins. Axios's own English
 * text (status lines, "canceled") never does: transport failures use the fixed Chinese
 * network/timeout copy, and every other Axios error uses the caller's fallback.
 * Non-Axios errors may still surface their own message.
 */
export function oemRequestError(error: unknown, fallback: string): string {
  const message = (error as { response?: { data?: { message?: unknown } } } | null)?.response?.data?.message
  if (typeof message === 'string' && message.trim()) return message
  if (axios.isAxiosError(error)) {
    if (error.code === 'ERR_NETWORK') return NETWORK_MESSAGE
    if (error.code === 'ECONNABORTED') return TIMEOUT_MESSAGE
    return fallback
  }
  if (error instanceof Error && error.message.trim()) return error.message
  return fallback
}
