import { useEffect } from 'react'

/**
 * Runs `load` after mount and whenever its identity changes (i.e. its inputs changed).
 * The call is deferred to a microtask and skipped once the effect is cleaned up, so the
 * state updates inside `load` never run synchronously within the effect body — the same
 * pattern the collaboration pages use.
 */
export function useLoadEffect(load: () => unknown): void {
  useEffect(() => {
    let active = true
    void Promise.resolve().then(() => { if (active) void load() })
    return () => { active = false }
  }, [load])
}
