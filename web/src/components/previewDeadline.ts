/** 下载阶段：持续这么久没有收到新字节才算超时。慢速网络下的大文件只要还在传就不失败。 */
export const PREVIEW_STALL_MS = 60_000
/** 下载完成后留给渲染的时间。 */
export const PREVIEW_RENDER_MS = 60_000

export interface PreviewDeadline {
  /** 下载进度回调；只有字节数增加才续期。 */
  progress: (loadedBytes: number) => void
  /** 内容已到手，从此改按渲染时限计时。 */
  downloaded: () => void
  clear: () => void
}

/**
 * 预览超时分两段：下载阶段按“无进度”计时，下载完成后按渲染时限计时。
 * 预览请求需同时传 `timeout: 0` 关掉 HTTP 客户端的整体超时，否则大文件仍会被 60 秒掐断。
 */
export function createPreviewDeadline(onTimeout: () => void): PreviewDeadline {
  let timer: ReturnType<typeof setTimeout> | undefined
  let loaded = -1
  const clear = () => {
    if (timer !== undefined) clearTimeout(timer)
    timer = undefined
  }
  const arm = (ms: number) => {
    clear()
    timer = setTimeout(() => { timer = undefined; onTimeout() }, ms)
  }
  arm(PREVIEW_STALL_MS)
  return {
    progress: (loadedBytes) => {
      if (timer === undefined || loadedBytes <= loaded) return
      loaded = loadedBytes
      arm(PREVIEW_STALL_MS)
    },
    downloaded: () => arm(PREVIEW_RENDER_MS),
    clear,
  }
}
