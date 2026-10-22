import { useCallback, useEffect, useRef, useState } from 'react'

export const SIGNAL_COALESCE_MS = 300

/**
 * 把实时信号合并成重拉键：信号变化后最多等一个窗口（前缘，不随后续信号顺延），窗口内的多次变化只触发一次；
 * 请求进行中到点时只记下“有新变更”，由 end() 在请求结束后再触发一次，不中止进行中的请求。
 * 一个实例只跟踪一条请求：请求开始时调用 begin()，在 finally（且本次 effect 仍有效）时调用 end()。
 */
export function useCoalescedRefresh(signal: unknown, windowMs = SIGNAL_COALESCE_MS) {
  const [refreshKey, setRefreshKey] = useState(0)
  const seen = useRef(signal)
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)
  const inFlight = useRef(false)
  const dirty = useRef(false)

  useEffect(() => {
    if (Object.is(signal, seen.current)) return
    seen.current = signal
    if (timer.current) return
    timer.current = setTimeout(() => {
      timer.current = undefined
      if (inFlight.current) dirty.current = true
      else setRefreshKey((value) => value + 1)
    }, windowMs)
  }, [signal, windowMs])
  useEffect(() => () => { if (timer.current) clearTimeout(timer.current) }, [])

  // 本次请求会读到此前的全部变更：已记下的和尚在合并窗口里的信号都不必再单独重拉。
  const begin = useCallback(() => {
    inFlight.current = true
    dirty.current = false
    if (timer.current) { clearTimeout(timer.current); timer.current = undefined }
  }, [])
  const end = useCallback(() => {
    inFlight.current = false
    if (!dirty.current) return
    dirty.current = false
    setRefreshKey((value) => value + 1)
  }, [])

  return { refreshKey, begin, end }
}
