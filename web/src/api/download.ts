import { Message } from '@arco-design/web-react'
import http from './client'
import type { ApiResponses } from './types'

/**
 * 下载交给隐藏 iframe 导航：成功时响应是附件，由浏览器下载管理器接管（不会触发 load）；
 * 失败时服务端返回同源 JSON 错误页，iframe 触发 load，这里读出中文原因提示用户，
 * 而不是只留下浏览器笼统的“下载失败”。
 */
const FRAME_LIFETIME_MS = 10 * 60_000
const NATIVE_PATH = /^\/api\/v1\/files\/(?:[1-9]\d*\/native-download|batch-download)\/[0-9a-f]{32}$/

function readError(frame: HTMLIFrameElement): string | null {
  let text: string | undefined
  try {
    text = frame.contentDocument?.body?.textContent?.trim()
  } catch {
    // 无法读取（非同源错误页）时仍按失败处理。
    return '下载失败，请稍后重试'
  }
  if (!text) return null
  try {
    const body = JSON.parse(text) as { message?: unknown }
    return typeof body.message === 'string' && body.message.trim() ? body.message : '下载失败，请稍后重试'
  } catch {
    return '下载失败，请稍后重试'
  }
}

function startNativeDownload(path: string) {
  const target = new URL(path, window.location.origin)
  if (target.origin !== window.location.origin
      || !NATIVE_PATH.test(target.pathname)
      || target.search.length > 0
      || target.hash.length > 0) {
    throw new Error('服务端返回了无效的下载地址')
  }
  const frame = document.createElement('iframe')
  frame.hidden = true
  frame.tabIndex = -1
  frame.setAttribute('aria-hidden', 'true')
  frame.className = 'native-download-frame'
  let timer: ReturnType<typeof setTimeout> | undefined
  const dispose = () => {
    if (timer !== undefined) clearTimeout(timer)
    frame.remove()
  }
  frame.addEventListener('load', () => {
    const error = readError(frame)
    if (error === null) return
    Message.error(error)
    dispose()
  })
  // 下载已交给浏览器后 iframe 不再有用；保留一段时间只为接住迟到的错误页。
  timer = setTimeout(dispose, FRAME_LIFETIME_MS)
  document.body.appendChild(frame)
  frame.src = target.href
}

export async function downloadFile(id: number): Promise<void> {
  const response = await http.post<ApiResponses['POST /files/{id}/download-grant']>(`/files/${id}/download-grant`)
  startNativeDownload(response.data.url)
}

export async function downloadFiles(ids: number[]): Promise<void> {
  const response = await http.post<ApiResponses['POST /files/batch-download-grant']>('/files/batch-download-grant', { ids })
  startNativeDownload(response.data.url)
}
