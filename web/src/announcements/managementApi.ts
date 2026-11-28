import { http } from '../api/client'
import type { AnnouncementDraftRequest } from '../api/generated/api-types'
import type { ApiResponses } from '../api/types'

export type ManagePage = ApiResponses['GET /announcements/manage']
export type ManageRow = ManagePage['list'][number]
export type ManageDetail = ApiResponses['GET /announcements/manage/{id}']
export type ManageOptions = ApiResponses['GET /announcements/manage/options']
export type ReadPage = ApiResponses['GET /announcements/manage/{id}/reads']

export interface DraftValues {
  title: string
  body: string
  internalScope: string
  externalScope: string | null
  departmentIds: number[]
  companyIds: number[]
}

export const managementApi = {
  async list(params: { keyword: string; status?: string; page: number; pageSize: number }, signal: AbortSignal) {
    return (await http.get<ApiResponses['GET /announcements/manage']>('/announcements/manage', { params, signal })).data
  },
  async options(signal: AbortSignal) {
    return (await http.get<ApiResponses['GET /announcements/manage/options']>('/announcements/manage/options', { signal })).data
  },
  async detail(id: number, signal: AbortSignal) {
    return (await http.get<ApiResponses['GET /announcements/manage/{id}']>(`/announcements/manage/${id}`, { signal })).data
  },
  async reads(id: number, page: number, pageSize: number, signal: AbortSignal) {
    return (await http.get<ApiResponses['GET /announcements/manage/{id}/reads']>(`/announcements/manage/${id}/reads`, { params: { page, pageSize }, signal })).data
  },
  async create(values: AnnouncementDraftRequest) {
    return (await http.post<ApiResponses['POST /announcements/manage']>('/announcements/manage', values)).data
  },
  async update(id: number, values: AnnouncementDraftRequest) {
    return (await http.put<ApiResponses['PUT /announcements/manage/{id}']>(`/announcements/manage/${id}`, values)).data
  },
  async publish(id: number, version: number) {
    await http.post<ApiResponses['POST /announcements/manage/{id}/publish']>(`/announcements/manage/${id}/publish`, undefined, { params: { version } })
  },
  async withdraw(id: number) {
    await http.post<ApiResponses['POST /announcements/manage/{id}/withdraw']>(`/announcements/manage/${id}/withdraw`)
  },
  async pin(id: number) {
    await http.post<ApiResponses['POST /announcements/manage/{id}/pin']>(`/announcements/manage/${id}/pin`)
  },
  async unpin(id: number) {
    await http.post<ApiResponses['POST /announcements/manage/{id}/unpin']>(`/announcements/manage/${id}/unpin`)
  },
}
