import { useEffect, useRef } from 'react'

export const DISCONNECTED_POLL_BASE_MS = 5_000
export const DISCONNECTED_POLL_MAX_MS = 60_000

/**
 * 实时通道未连接时的兜底轮询：从 5 秒开始，请求失败后指数退避到 60 秒上限；
 * 成功或页面重新可见时回到 5 秒（可见时立即补一次）。页面隐藏时暂停，
 * enabled 变为 false（实时恢复）或卸载时停止并中止进行中的请求。
 * poll 抛错视为失败；它由调用方用 ref 持有，重新渲染不会重启计时。
 */
export function useDisconnectedPoll(enabled: boolean, poll: (signal: AbortSignal) => Promise<unknown>) {
  const pollRef = useRef(poll)
  useEffect(() => { pollRef.current = poll })

  useEffect(() => {
    if (!enabled) return undefined
    let disposed = false
    let delay = DISCONNECTED_POLL_BASE_MS
    let timer: ReturnType<typeof setTimeout> | undefined
    let controller: AbortController | null = null
    const hidden = () => typeof document !== 'undefined' && document.visibilityState === 'hidden'

    const schedule = () => {
      if (timer !== undefined) clearTimeout(timer)
      timer = undefined
      if (disposed || hidden()) return
      timer = setTimeout(() => { void run() }, delay)
    }

    const run = async () => {
      timer = undefined
      if (disposed || controller) return
      const current = new AbortController()
      controller = current
      let ok = true
      try {
        await pollRef.current(current.signal)
      } catch {
        ok = false
      }
      if (controller === current) controller = null
      if (disposed || current.signal.aborted) return
      delay = ok ? DISCONNECTED_POLL_BASE_MS : Math.min(delay * 2, DISCONNECTED_POLL_MAX_MS)
      schedule()
    }

    const onVisibility = () => {
      if (hidden()) {
        if (timer !== undefined) clearTimeout(timer)
        timer = undefined
        return
      }
      delay = DISCONNECTED_POLL_BASE_MS
      if (timer !== undefined) clearTimeout(timer)
      timer = undefined
      void run()
    }

    schedule()
    document.addEventListener('visibilitychange', onVisibility)
    return () => {
      disposed = true
      if (timer !== undefined) clearTimeout(timer)
      controller?.abort()
      controller = null
      document.removeEventListener('visibilitychange', onVisibility)
    }
  }, [enabled])
}
