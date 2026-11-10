import { Message } from '@arco-design/web-react'
import { startOemDownload } from '../../api/download'
import type { OemApi } from '../api/OemApi'

/**
 * Starts one OEM file download: a session grant (path-scoped HttpOnly cookie), then a hidden-iframe
 * navigation. Success goes to the browser's download manager; failures (expired grant, purged file,
 * 4xx/5xx) only show a message instead of taking the tab to a JSON error page. Only this file's own
 * download path is accepted.
 */
export async function downloadOemFile(api: OemApi, fileId: number): Promise<void> {
  const session = await api.startDownload(fileId)
  try {
    startOemDownload(session.url, fileId)
  } catch {
    Message.error('下载地址无效，请刷新后重试')
  }
}
