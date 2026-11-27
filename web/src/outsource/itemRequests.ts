import http from '../api/client'
import type { ApiResponses } from '../api/types'

type Parameters = Record<string, string | number | undefined>
type ItemPage = ApiResponses['GET /outsource/items']

// IIS defaults to 2048 query bytes. Measure the percent-encoded query, including non-ASCII values.
function longQueryBody(params: Parameters): Record<string, string> | undefined {
  const query = new URLSearchParams()
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined) query.set(key, String(value))
  }
  return query.toString().length > 1800 ? Object.fromEntries(query) : undefined
}

export function listOutsourceItems(params: Parameters) {
  const body = longQueryBody(params)
  return body
    ? http.post<ItemPage>('/outsource/items/query', body)
    : http.get<ItemPage>('/outsource/items', { params })
}

export function exportOutsourceItems(params: Parameters) {
  const body = longQueryBody(params)
  const config = { responseType: 'blob' as const, timeout: 0 }
  return body
    ? http.post<Blob>('/outsource/items/export', body, config)
    : http.get<Blob>('/outsource/items/export', { ...config, params })
}
