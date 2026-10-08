import '@testing-library/jest-dom/vitest'
import { cleanup, configure } from '@testing-library/react'
import { afterAll, afterEach, vi } from 'vitest'

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

// Arco animations re-arm a frame on every tick. Cancel what is still pending when the
// file ends, or the callback fires after jsdom is gone and calls an undefined
// requestAnimationFrame.
const animationFrames = new Set<number>()
window.requestAnimationFrame = (callback) => {
  const handle = window.setTimeout(() => {
    animationFrames.delete(handle)
    callback(performance.now())
  }, 0)
  animationFrames.add(handle)
  return handle
}
window.cancelAnimationFrame = (handle) => {
  animationFrames.delete(handle)
  window.clearTimeout(handle)
}
afterAll(() => {
  for (const handle of animationFrames) window.clearTimeout(handle)
  animationFrames.clear()
})
}
