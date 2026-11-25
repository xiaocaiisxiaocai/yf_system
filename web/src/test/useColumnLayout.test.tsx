import { act, renderHook } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import type { ReactNode } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const messages = vi.hoisted(() => ({ warning: vi.fn() }))
const navigate = vi.hoisted(() => vi.fn())
vi.mock('@arco-design/web-react', async (original) => ({
  ...(await original<typeof import('@arco-design/web-react')>()),
  Message: { warning: messages.warning },
}))
vi.mock('react-router-dom', async (original) => ({
  ...(await original<typeof import('react-router-dom')>()),
  useNavigate: () => navigate,
}))

import { useColumnLayout } from '../components/useColumnLayout'
import { columnLayoutStorageKey, type ColumnLayoutSpec } from '../components/tableColumnLayout'
import { useProjectAccessLost } from '../hooks/useProjectAccessLost'

const SPECS: readonly ColumnLayoutSpec[] = [
  { key: 'name', title: '名称', defaultWidth: 160, locked: true },
  { key: 'owner', title: '负责人', defaultWidth: 120 },
  { key: 'due', title: '期限', defaultWidth: 100, minWidth: 80, maxWidth: 200 },
]

describe('useColumnLayout', () => {
  beforeEach(() => { localStorage.clear() })

  it('keeps layouts per user and never hides a locked column', () => {
    const first = renderHook(() => useColumnLayout(columnLayoutStorageKey('t', 1), SPECS))
    act(() => { first.result.current.toggle('owner', false) })
    act(() => { first.result.current.toggle('name', false) })
    expect(first.result.current.hidden).toEqual(['owner'])
    const columns = first.result.current.apply(SPECS.map((spec) => ({ key: spec.key, title: spec.title })))
    expect(columns.map((column) => column.key)).toEqual(['name', 'due'])
    expect(JSON.parse(localStorage.getItem(columnLayoutStorageKey('t', 1))!)).toMatchObject({ hidden: ['owner'] })

    const other = renderHook(() => useColumnLayout(columnLayoutStorageKey('t', 2), SPECS))
    expect(other.result.current.hidden).toEqual([])
  })

  it('clamps to the minimum only (no maximum), reports limits, writes only after a drag ends, and reset clears storage', () => {
    const key = columnLayoutStorageKey('t', 1)
    const { result } = renderHook(() => useColumnLayout(key, SPECS))
    expect(result.current.actions.limitsOf('due')).toEqual({ min: 80 })
    act(() => { result.current.actions.setWidth('due', 999) })
    expect(result.current.actions.widthOf('due')).toBe(999)
    act(() => { result.current.actions.setWidth('due', 10) })
    expect(result.current.actions.widthOf('due')).toBe(80)

    act(() => { result.current.actions.beginResize('owner', 100) })
    act(() => { window.dispatchEvent(new PointerEvent('pointermove', { clientX: 140 })) })
    expect(result.current.actions.widthOf('owner')).toBe(160)
    expect(JSON.parse(localStorage.getItem(key)!).widths.owner).toBeUndefined()
    act(() => { window.dispatchEvent(new PointerEvent('pointerup', { clientX: 140 })) })
    expect(JSON.parse(localStorage.getItem(key)!).widths.owner).toBe(160)

    act(() => { result.current.reset() })
    expect(localStorage.getItem(key)).toBeNull()
  })

  it('preserves and restores dynamic column layout across partial-to-full specs updates without clobbering storage', () => {
    const key = columnLayoutStorageKey('outsource', 1)
    const fullSpecs: readonly ColumnLayoutSpec[] = [
      ...SPECS,
      { key: 'dynA', title: '动态甲', defaultWidth: 110 },
      { key: 'dynB', title: '动态乙', defaultWidth: 110 },
    ]
    const saved = {
      hidden: ['dynB'],
      widths: { owner: 180, dynA: 260 },
      order: ['name', 'dynA', 'owner', 'due', 'dynB'],
      pinned: ['name', 'dynA'],
    }
    localStorage.setItem(key, JSON.stringify(saved))

    // Initial mount with only static specs must not strip dynamic keys from localStorage.
    const { result, rerender } = renderHook(({ specs }) => useColumnLayout(key, specs), {
      initialProps: { specs: SPECS },
    })
    expect(result.current.widths).toEqual({ owner: 180 })
    expect(JSON.parse(localStorage.getItem(key)!)).toEqual(saved)

    // Mutating a static column while dynamic specs are absent must preserve dynamic keys,
    // even when all known columns return to default widths.
    act(() => { result.current.actions.setWidth('owner', null) })
    expect(JSON.parse(localStorage.getItem(key)!)).toEqual({
      hidden: ['dynB'],
      widths: { dynA: 260 },
      order: ['name', 'dynA', 'owner', 'due', 'dynB'],
      pinned: ['name', 'dynA'],
    })

    // Once dynamic specs arrive, previously saved settings for them are immediately restored.
    rerender({ specs: fullSpecs })
    expect(result.current.widths).toEqual({ dynA: 260 })
    expect(result.current.hidden).toEqual(['dynB'])
    expect(result.current.layout.order).toEqual(['name', 'dynA', 'owner', 'due', 'dynB'])
    expect(result.current.layout.pinned).toEqual(['name', 'dynA'])

    // Explicit reset clears the entire stored layout.
    act(() => { result.current.reset() })
    expect(localStorage.getItem(key)).toBeNull()
  })
})

describe('useProjectAccessLost', () => {
  const wrapper = ({ children }: { children: ReactNode }) => <MemoryRouter>{children}</MemoryRouter>
  afterEach(() => { messages.warning.mockReset(); navigate.mockReset() })

  it('warns once with the matching reason and replaces the route with the project list', () => {
    const { result } = renderHook(() => useProjectAccessLost('项目'), { wrapper })
    expect(result.current(404)).toBe(true)
    expect(result.current(403)).toBe(true)
    expect(messages.warning).toHaveBeenCalledOnce()
    expect(messages.warning).toHaveBeenCalledWith('该项目已被删除')
    expect(navigate).toHaveBeenCalledOnce()
    expect(navigate).toHaveBeenCalledWith('/projects', { replace: true })
  })

  it('does nothing after the page has unmounted', () => {
    const { result, unmount } = renderHook(() => useProjectAccessLost(), { wrapper })
    const handle = result.current
    unmount()
    expect(handle(403)).toBe(true)
    expect(messages.warning).not.toHaveBeenCalled()
    expect(navigate).not.toHaveBeenCalled()
  })
})
