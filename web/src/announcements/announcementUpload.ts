import { http, type QuietRequestConfig } from '../api/client'
import type { ApiResponses } from '../api/types'

export type AnnouncementAsset = ApiResponses['POST /announcements/manage/{id}/assets']
export type UploadKind = 'IMAGE' | 'ATTACHMENT'
export const ATTACHMENT_ACCEPT = '.pdf,.doc,.docx,.xls,.xlsx,.ppt,.pptx'

export function validateAnnouncementFile(file: File, kind: UploadKind) {
  const limit = kind === 'IMAGE' ? 10 : 32
  if (file.size === 0) throw new Error('文件为空，请重新选择')
  if (file.size > limit * 1024 * 1024) throw new Error(`文件不能超过 ${limit} MB`)
  if (kind === 'ATTACHMENT' && !/\.(pdf|docx?|xlsx?|pptx?)$/i.test(file.name))
    throw new Error('请选择 PDF、Word、Excel 或 PPT 文件')
  if (kind === 'IMAGE' && !['image/png', 'image/jpeg', 'image/webp', 'image/gif'].includes(file.type))
    throw new Error('请选择 PNG、JPEG、WebP 或 GIF 图片')
}

async function thumbnail(file: File, signal: AbortSignal) {
  signal.throwIfAborted()
  const image = new Image()
  const url = URL.createObjectURL(file)
  try {
    image.src = url
    await image.decode()
    signal.throwIfAborted()
    const ratio = Math.min(1, 320 / Math.max(image.naturalWidth, image.naturalHeight))
    const canvas = document.createElement('canvas')
    canvas.width = Math.max(1, Math.round(image.naturalWidth * ratio))
    canvas.height = Math.max(1, Math.round(image.naturalHeight * ratio))
    const ctx = canvas.getContext('2d')
    if (!ctx) throw new Error('无法创建缩略图')
    ctx.fillStyle = 'white'
    ctx.fillRect(0, 0, canvas.width, canvas.height)
    ctx.drawImage(image, 0, 0, canvas.width, canvas.height)
    const blob = await new Promise<Blob | null>(resolve => canvas.toBlob(resolve, 'image/jpeg', 0.82))
    if (!blob) throw new Error('无法生成图片缩略图')
    return blob
  } finally {
    URL.revokeObjectURL(url)
  }
}

export const announcementUploads = {
  async send(id: number, kind: UploadKind, file: File, uploadKey: string, signal: AbortSignal, onProgress: (percent: number) => void) {
    const data = new FormData()
    data.set('kind', kind)
    data.set('uploadKey', uploadKey)
    // Some clipboard implementations supply only a MIME type and no filename/extension.
    const name = kind === 'IMAGE' && !file.name.includes('.')
      ? `${file.name || '截图'}.${file.type.slice('image/'.length)}` : file.name
    data.set('file', file, name)
    if (kind === 'IMAGE') data.set('thumbnail', await thumbnail(file, signal), 'thumbnail.jpg')
    signal.throwIfAborted()
    const config: QuietRequestConfig = {
      signal, timeout: 300_000, quietNetworkError: true, quietClientError: true,
      onUploadProgress: event => {
        if (event.total) onProgress(Math.min(99, Math.round(event.loaded * 100 / event.total)))
      },
    }
    return (await http.post<ApiResponses['POST /announcements/manage/{id}/assets']>(`/announcements/manage/${id}/assets`, data, config)).data
  },
}
