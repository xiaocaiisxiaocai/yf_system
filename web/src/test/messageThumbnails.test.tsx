import { act, render, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({ get: vi.fn() }))
vi.mock('../api/client', () => ({ default: { get: mocks.get } }))

import { MessageImages } from '../components/MessageImages'

type ObserverRecord = { callback: IntersectionObserverCallback; element?: Element }
const observers: ObserverRecord[] = []

class FakeIntersectionObserver {
  private readonly record: ObserverRecord
  constructor(callback: IntersectionObserverCallback) {
    this.record = { callback }
    observers.push(this.record)
  }
  observe(element: Element) { this.record.element = element }
  disconnect() { this.record.element = undefined }
  unobserve() {}
  takeRecords() { return [] }
}

function setVisible(index: number, isIntersecting: boolean) {
  const record = observers[index]
  act(() => {
    record.callback([{ isIntersecting, target: record.element! } as IntersectionObserverEntry], {} as IntersectionObserver)
  })
}

const images = Array.from({ length: 5 }, (_, index) => ({
  id: index + 1, name: `p${index + 1}.png`, sizeBytes: 10, mimeType: 'image/png',
}))

describe('remote message thumbnails', () => {
  const signals: AbortSignal[] = []
  beforeEach(() => {
    observers.length = 0
    signals.length = 0
    vi.stubGlobal('IntersectionObserver', FakeIntersectionObserver)
    mocks.get.mockImplementation((_url: string, config: { signal: AbortSignal }) => {
      signals.push(config.signal)
      return new Promise(() => undefined)
    })
  })
  afterEach(() => {
    vi.unstubAllGlobals()
    mocks.get.mockReset()
  })

  it('loads nothing off-screen, caps parallel downloads, and aborts thumbnails that leave the viewport', async () => {
    const view = render(<MessageImages messageId={7} images={images as never} watermarkEmployeeNo="E1" watermarkRealName="R" />)
    expect(mocks.get).not.toHaveBeenCalled()
    images.forEach((_, index) => setVisible(index, true))
    await waitFor(() => expect(mocks.get).toHaveBeenCalledTimes(3))
    setVisible(0, false)
    await waitFor(() => expect(signals[0].aborted).toBe(true))
    // 中止释放的名额让排队中的第 4 张开始下载。
    await waitFor(() => expect(mocks.get).toHaveBeenCalledTimes(4))
    view.unmount()
    expect(signals.every((signal) => signal.aborted)).toBe(true)
  })
})
