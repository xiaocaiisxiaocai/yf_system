import type { AxiosInstance } from 'axios'
import { http as internalClient, type QuietRequestConfig } from '../api/client'
import type { ApiResponses } from '../api/types'

export type AnnouncementAccess = 'internal' | 'oem' | 'manage'

export type AnnouncementAsset = ApiResponses['POST /announcements/manage/{id}/assets']

export async function loadAnnouncementAsset(announcementId: number, assetId: number, signal: AbortSignal, options: {
  access?: AnnouncementAccess
  client?: AxiosInstance
  thumbnail?: boolean
  download?: boolean
  onProgress?: (loaded: number) => void
} = {}): Promise<Blob> {
  if (options.access === 'oem' && !options.client) throw new Error('缺少厂商登录会话')
  const http = options.client ?? internalClient
  const config: QuietRequestConfig = {
    responseType: 'blob', signal, quietClientError: true, quietNetworkError: true,
    params: { thumbnail: options.thumbnail || undefined, download: options.download || undefined },
    ...(options.onProgress && { timeout: 0, onDownloadProgress: event => options.onProgress?.(event.loaded) }),
  }
  if (options.access === 'oem') {
    return (await http.get<Blob>(`/oem/announcements/${announcementId}/assets/${assetId}`, config)).data
  }
  if (options.access === 'internal') {
    return (await http.get<Blob>(`/announcements/${announcementId}/assets/${assetId}`, config)).data
  }
  return (await http.get<Blob>(`/announcements/manage/${announcementId}/assets/${assetId}`, config)).data
}
