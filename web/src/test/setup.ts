import '@testing-library/jest-dom/vitest'
import { cleanup, configure } from '@testing-library/react'
import { afterEach, vi } from 'vitest'

// Parallel workers share the CPU; give findBy*/waitFor more headroom than the 1 s default.
configure({ asyncUtilTimeout: 5_000 })

afterEach(async () => {
  cleanup()
  if (typeof window !== 'undefined') {
    const { queryClient } = await import('../api/queryClient')
    await queryClient.cancelQueries()
    queryClient.clear()
  }
  vi.unstubAllGlobals()
  vi.unstubAllEnvs()
  vi.useRealTimers()
  if (typeof window !== 'undefined') {
    window.localStorage.clear()
    window.sessionStorage.clear()
  }
})

if (typeof window !== 'undefined') {
Object.defineProperty(window, 'matchMedia', {
  configurable: true,
  value: (query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
    addListener: () => undefined,
    removeListener: () => undefined,
    dispatchEvent: () => false,
  }),
})

class TestResizeObserver implements ResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}

globalThis.ResizeObserver = TestResizeObserver

window.requestAnimationFrame = (callback) => window.setTimeout(() => callback(performance.now()), 0)
window.cancelAnimationFrame = (handle) => window.clearTimeout(handle)
}
