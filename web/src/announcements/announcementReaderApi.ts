import type { AxiosInstance } from 'axios'
import type { QuietRequestConfig } from '../api/client'
import type { ApiResponses } from '../api/types'
import type { Realm } from '../oem/api/types'

type ListPath = 'GET /announcements' | 'GET /oem/announcements'
type DetailPath = 'GET /announcements/{id}' | 'GET /oem/announcements/{id}'
export type AnnouncementInbox = ApiResponses[ListPath]
export type AnnouncementContent = ApiResponses[DetailPath]

const quiet: QuietRequestConfig = { quietClientError: true, quietNetworkError: true }

export async function listAnnouncements(http: AxiosInstance, realm: Realm, params: {
  keyword: string; unreadOnly: boolean; page: number; pageSize: number
}, signal: AbortSignal) {
  const config = { ...quiet, params, signal }
  return realm === 'oem'
    ? (await http.get<ApiResponses['GET /oem/announcements']>('/oem/announcements', config)).data
    : (await http.get<ApiResponses['GET /announcements']>('/announcements', config)).data
}

export async function getAnnouncement(http: AxiosInstance, realm: Realm, id: string, signal: AbortSignal) {
  const config = { ...quiet, signal }
  return realm === 'oem'
    ? (await http.get<ApiResponses['GET /oem/announcements/{id}']>(`/oem/announcements/${id}`, config)).data
    : (await http.get<ApiResponses['GET /announcements/{id}']>(`/announcements/${id}`, config)).data
}

export async function recordAnnouncementRead(http: AxiosInstance, realm: Realm, id: number) {
  if (realm === 'oem') {
    await http.post<ApiResponses['POST /oem/announcements/{id}/read']>(`/oem/announcements/${id}/read`, undefined, quiet)
  } else {
    await http.post<ApiResponses['POST /announcements/{id}/read']>(`/announcements/${id}/read`, undefined, quiet)
  }
}
