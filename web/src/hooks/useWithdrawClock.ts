import { useEffect, useState } from 'react'

const MAXIMUM_TIMEOUT = 2 ** 31 - 1

/** Whether a supplier may still withdraw an item whose server-provided deadline is `deadline`. */
export const canWithdraw = (deadline: string | null | undefined, now: number) =>
  !!deadline && Date.parse(deadline) > now

/**
 * Current time for withdraw buttons. It re-renders exactly when the earliest still-open deadline passes,
 * so an expired button disappears without polling; the server remains the authority on the window.
 */
export function useWithdrawClock(deadlines: ReadonlyArray<string | null | undefined>): number {
  const [now, setNow] = useState(() => Date.now())
  let next: number | null = null
  for (const deadline of deadlines) {
    const at = deadline ? Date.parse(deadline) : Number.NaN
    if (Number.isFinite(at) && at > now && (next === null || at < next)) next = at
  }
  useEffect(() => {
    if (next === null) return undefined
    const timer = window.setTimeout(() => setNow(Date.now()), Math.min(Math.max(next - Date.now(), 0) + 50, MAXIMUM_TIMEOUT))
    return () => window.clearTimeout(timer)
  }, [next])
  return now
}
