import '@testing-library/jest-dom/vitest'
import { cleanup, configure, waitFor } from '@testing-library/react'
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
    // Modal.confirm 和 Message 挂在 Testing Library 的容器之外，cleanup 不会卸载它们；
    // 关闭动画结束前它们还在 DOM 里，会让下一个用例查到残留的「确定」等按钮。等它们真正移除。
    // 部分用例 mock 了 Arco，取不到 Modal/Message 时（Vitest 会直接抛错）跳过即可。
    let clearMessages = () => {}
    try {
      const arco = await import('@arco-design/web-react')
      arco.Modal.destroyAll()
      clearMessages = () => arco.Message.clear()
      clearMessages()
    } catch {
      // 本文件 mock 了 Arco，没有真实的全局弹窗需要清理。
    }
    // 消息的退出动画由 react-transition-group 的 Node 计时器驱动，不经过上面的 requestAnimationFrame 替身；
    // 文件结束前不等它移除，计时器会在 jsdom 销毁后触发并报 window is not defined。
    // 用例结束后才落地的请求失败仍会弹出新消息，所以等待期间重复清理。
    await waitFor(() => {
      if (document.querySelector('.arco-modal-wrapper')) throw new Error('上一个用例的全局弹窗仍未移除')
      if (document.querySelector('.arco-message')) {
        clearMessages()
        throw new Error('上一个用例的全局消息仍未移除')
      }
    }, { timeout: 3_000 })
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
