import http, { type QuietRequestConfig } from './client'
import type { InitUploadRequest, UploadInitResponse, UploadedChunkDigestResponse } from './generated/api-types'
import type { ApiResponses } from './types'

export type UploadedChunkDigest = UploadedChunkDigestResponse
export type UploadInitResult = UploadInitResponse

export function initUpload(request: InitUploadRequest) {
  return http.post<UploadInitResult>('/uploads/init', request)
}

export function putUploadChunk(
  sessionId: string,
  index: number,
  blob: Blob,
  sha256: string,
  config: Pick<QuietRequestConfig, 'signal' | 'quietNetworkError'>,
) {
  return http.put<ApiResponses['PUT /uploads/{sessionId}/chunks/{index}']>(`/uploads/${sessionId}/chunks/${index}`, blob, {
    ...config,
    headers: {
      'Content-Type': 'application/octet-stream',
      'X-Chunk-SHA256': sha256,
    },
    timeout: 300000,
  } as QuietRequestConfig)
}

export function submitUploadMd5(sessionId: string, fileMd5: string) {
  return http.post<ApiResponses['POST /uploads/{sessionId}/md5']>(`/uploads/${sessionId}/md5`, { fileMd5 })
}

export function mergeUpload(sessionId: string) {
  return http.post<ApiResponses['POST /uploads/{sessionId}/merge']>(`/uploads/${sessionId}/merge`, undefined, { timeout: 10 * 60 * 1000 })
}

export function abortUpload(sessionId: string) {
  return http.delete<ApiResponses['DELETE /uploads/{sessionId}']>(`/uploads/${sessionId}`)
}
