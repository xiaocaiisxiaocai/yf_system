import { http } from '../api/client'
import type { ApiResponses } from '../api/types'

export type OrderSnapshot = ApiResponses['GET /announcements/manage/ordering']
export type OrderRow = OrderSnapshot['list'][number]
export type OrderChange = Pick<OrderRow, 'id' | 'sortOrder'>

export const announcementOrderApi = {
  async get(pinned: boolean, signal: AbortSignal) {
    return (await http.get<ApiResponses['GET /announcements/manage/ordering']>('/announcements/manage/ordering',
      { params: { pinned }, signal })).data
  },
  async save(pinned: boolean, revision: string, changes: OrderChange[]) {
    await http.put<ApiResponses['PUT /announcements/manage/ordering']>('/announcements/manage/ordering',
      { pinned, revision, changes })
  },
}
