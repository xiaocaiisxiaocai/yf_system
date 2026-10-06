import { useCallback, useEffect, useRef } from 'react'

/**
 * Runs `load` after mount and whenever its identity changes (i.e. its inputs changed).
 * The call is deferred to a microtask and skipped once the effect is cleaned up, so the
 * state updates inside `load` never run synchronously within the effect body — the same
 * pattern the collaboration pages use. A rejected load is swallowed here: pages surface
 * their own error state and the HTTP layer already reports the failure.
 */
export function useLoadEffect(load: () => unknown): void {
  useEffect(() => {
    let active = true
    void Promise.resolve()
      .then(() => (active ? load() : undefined))
      .catch(() => undefined)
    return () => { active = false }
  }, [load])
}

/**
 * Orders overlapping loads of the same view. `begin()` starts a request and returns a check
 * that stays true only while it is the newest request of a still-mounted component, so a
 * slow earlier page/search response can never overwrite a newer one.
 */
export function useLatestRequest(): () => () => boolean {
  const sequence = useRef(0)
  useEffect(() => {
    const current = sequence
    return () => { current.current++ }
  }, [])
  return useCallback(() => {
    const id = ++sequence.current
    return () => sequence.current === id
  }, [])
}
