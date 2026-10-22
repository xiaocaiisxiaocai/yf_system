import { act, renderHook } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { SIGNAL_COALESCE_MS, useCoalescedRefresh } from '../hooks/useCoalescedRefresh'

describe('useCoalescedRefresh', () => {
  beforeEach(() => { vi.useFakeTimers() })
  afterEach(() => { vi.useRealTimers() })

  it('merges a burst of signals inside one window into a single refresh', () => {
    const { result, rerender } = renderHook(({ signal }) => useCoalescedRefresh(signal), { initialProps: { signal: 0 } })
    expect(result.current.refreshKey).toBe(0)
    for (let signal = 1; signal <= 5; signal += 1) rerender({ signal })
    act(() => { vi.advanceTimersByTime(SIGNAL_COALESCE_MS - 1) })
    expect(result.current.refreshKey).toBe(0)
    act(() => { vi.advanceTimersByTime(1) })
    expect(result.current.refreshKey).toBe(1)
  })

  it('does not refresh while a request is in flight; refreshes once after it ends', () => {
    const { result, rerender } = renderHook(({ signal }) => useCoalescedRefresh(signal), { initialProps: { signal: 0 } })
    act(() => { result.current.begin() })
    rerender({ signal: 1 })
    rerender({ signal: 2 })
    act(() => { vi.advanceTimersByTime(SIGNAL_COALESCE_MS) })
    expect(result.current.refreshKey).toBe(0)
    act(() => { result.current.end() })
    expect(result.current.refreshKey).toBe(1)
    act(() => { result.current.begin(); result.current.end() })
    expect(result.current.refreshKey).toBe(1)
  })

  it('a request that starts inside the window absorbs the pending signal', () => {
    const { result, rerender } = renderHook(({ signal }) => useCoalescedRefresh(signal), { initialProps: { signal: 'a' } })
    rerender({ signal: 'b' })
    act(() => { result.current.begin() })
    act(() => { vi.advanceTimersByTime(SIGNAL_COALESCE_MS * 2) })
    act(() => { result.current.end() })
    expect(result.current.refreshKey).toBe(0)
  })

  it('ignores re-renders with an unchanged signal', () => {
    const { result, rerender } = renderHook(({ signal }) => useCoalescedRefresh(signal), { initialProps: { signal: 'same' } })
    rerender({ signal: 'same' })
    act(() => { vi.advanceTimersByTime(SIGNAL_COALESCE_MS * 2) })
    expect(result.current.refreshKey).toBe(0)
  })
})
