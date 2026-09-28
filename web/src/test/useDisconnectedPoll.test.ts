import { act, renderHook } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { DISCONNECTED_POLL_BASE_MS, DISCONNECTED_POLL_MAX_MS, useDisconnectedPoll } from '../hooks/useDisconnectedPoll'

async function advance(ms: number) {
  await act(async () => { await vi.advanceTimersByTimeAsync(ms) })
}

function setVisibility(state: 'visible' | 'hidden') {
  Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => state })
  document.dispatchEvent(new Event('visibilitychange'))
}

describe('useDisconnectedPoll', () => {
  beforeEach(() => { vi.useFakeTimers() })
  afterEach(() => {
    vi.useRealTimers()
    setVisibility('visible')
  })

  it('backs off from 5s to a 60s cap on failures and resets after a success', async () => {
    let fail = true
    const poll = vi.fn(async () => { if (fail) throw new Error('offline') })
    renderHook(() => useDisconnectedPoll(true, poll))
    const expected = [5_000, 10_000, 20_000, 40_000, 60_000, 60_000]
    for (const [index, delay] of expected.entries()) {
      await advance(delay - 1)
      expect(poll).toHaveBeenCalledTimes(index)
      await advance(1)
      expect(poll).toHaveBeenCalledTimes(index + 1)
    }
    expect(Math.max(...expected)).toBe(DISCONNECTED_POLL_MAX_MS)
    fail = false
    await advance(DISCONNECTED_POLL_MAX_MS)
    expect(poll).toHaveBeenCalledTimes(expected.length + 1)
    await advance(DISCONNECTED_POLL_BASE_MS)
    expect(poll).toHaveBeenCalledTimes(expected.length + 2)
  })

  it('pauses while hidden, polls at once when visible again and stops when disabled', async () => {
    const poll = vi.fn(async (signal: AbortSignal) => {
      await new Promise<void>((resolve) => signal.addEventListener('abort', () => resolve()))
    })
    const view = renderHook(({ enabled }) => useDisconnectedPoll(enabled, poll), { initialProps: { enabled: true } })
    setVisibility('hidden')
    await advance(DISCONNECTED_POLL_MAX_MS)
    expect(poll).not.toHaveBeenCalled()

    act(() => setVisibility('visible'))
    expect(poll).toHaveBeenCalledTimes(1)
    const signal = poll.mock.calls[0][0]
    view.rerender({ enabled: false })
    expect(signal.aborted).toBe(true)
    await advance(DISCONNECTED_POLL_MAX_MS * 2)
    expect(poll).toHaveBeenCalledTimes(1)
  })
})
