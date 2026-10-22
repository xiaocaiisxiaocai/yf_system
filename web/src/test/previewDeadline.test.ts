import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPreviewDeadline, PREVIEW_RENDER_MS, PREVIEW_STALL_MS } from '../components/previewDeadline'

describe('createPreviewDeadline', () => {
  beforeEach(() => { vi.useFakeTimers() })
  afterEach(() => { vi.useRealTimers() })

  it('keeps a slow download alive while bytes keep arriving', () => {
    const onTimeout = vi.fn()
    const deadline = createPreviewDeadline(onTimeout)
    for (let step = 1; step <= 5; step += 1) {
      vi.advanceTimersByTime(PREVIEW_STALL_MS - 1)
      deadline.progress(step * 1024)
    }
    expect(onTimeout).not.toHaveBeenCalled()
  })

  it('times out a stalled download; repeated progress with the same byte count does not renew', () => {
    const onTimeout = vi.fn()
    const deadline = createPreviewDeadline(onTimeout)
    deadline.progress(10)
    vi.advanceTimersByTime(PREVIEW_STALL_MS - 1)
    deadline.progress(10)
    vi.advanceTimersByTime(1)
    expect(onTimeout).toHaveBeenCalledOnce()
  })

  it('switches to the render budget once the content has arrived, and clear stops it', () => {
    const onTimeout = vi.fn()
    const deadline = createPreviewDeadline(onTimeout)
    vi.advanceTimersByTime(PREVIEW_STALL_MS - 1)
    deadline.downloaded()
    vi.advanceTimersByTime(PREVIEW_RENDER_MS - 1)
    expect(onTimeout).not.toHaveBeenCalled()
    vi.advanceTimersByTime(1)
    expect(onTimeout).toHaveBeenCalledOnce()

    const cleared = vi.fn()
    const other = createPreviewDeadline(cleared)
    other.clear()
    vi.advanceTimersByTime(PREVIEW_STALL_MS * 2)
    expect(cleared).not.toHaveBeenCalled()
  })
})
