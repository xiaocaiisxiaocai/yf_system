import http from './client'

type DownloadGrantResponse = {
  url: string
  expiresInSeconds: number
}

function startNativeDownload(path: string) {
  const target = new URL(path, window.location.origin)
  const nativePath = /^\/api\/v1\/files\/(?:[1-9]\d*\/native-download|batch-download)\/[0-9a-f]{32}$/
  if (target.origin !== window.location.origin
      || !nativePath.test(target.pathname)
      || target.search.length > 0
      || target.hash.length > 0) {
    throw new Error('服务端返回了无效的下载地址')
  }
  const anchor = document.createElement('a')
  anchor.href = target.href
  anchor.download = ''
  anchor.rel = 'noopener'
  document.body?.appendChild(anchor)
  anchor.click()
  anchor.remove?.()
}

export async function downloadFile(id: number): Promise<void> {
  const response = await http.post<DownloadGrantResponse>(`/files/${id}/download-grant`)
  startNativeDownload(response.data.url)
}

export async function downloadFiles(ids: number[]): Promise<void> {
  const response = await http.post<DownloadGrantResponse>('/files/batch-download-grant', { ids })
  startNativeDownload(response.data.url)
}
