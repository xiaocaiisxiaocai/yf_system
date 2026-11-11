import { act, fireEvent, renderHook } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { ColumnLayoutSpec } from '../components/tableColumnLayout'

// 容器宽度由 DOM 量出，jsdom 里恒为 0；固定成 1000px，让文件名列有 330px 的多余宽度可吃。
vi.mock('../components/tableColumnLayout', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../components/tableColumnLayout')>()),
  measureTableWidth: () => 1000,
}))

import { useAdjustableColumns } from '../components/useAdjustableColumns'

const specs: ColumnLayoutSpec[] = [
  { key: 'name', title: '文件名', defaultWidth: 360, minWidth: 160, maxWidth: 2400, locked: true },
  { key: 'size', title: '大小', defaultWidth: 110, minWidth: 80 },
  { key: 'actions', title: '操作', defaultWidth: 200, minWidth: 120, locked: true },
]
const columns = specs.map((spec) => ({ key: spec.key, title: spec.title }))

class ResizeObserverStub { observe() {} disconnect() {} unobserve() {} }

function setup(growFixed: boolean) {
  vi.stubGlobal('ResizeObserver', ResizeObserverStub)
  const hostRef = { current: document.createElement('div') }
  return renderHook(() => useAdjustableColumns('grow-test', 1, specs, columns, 'name', hostRef, true, growFixed))
}
const widths = (result: ReturnType<typeof setup>['result']) =>
  Object.fromEntries(result.current.columns.map((column) => [column.key, column.width]))

describe('拖动列宽时左边固定', () => {
  beforeEach(() => { localStorage.clear() })

  it('文件名吃掉多余宽度；拖右边的列时它按当前宽度固定，被拖的边跟着鼠标走，右边的列位移', () => {
    const { result } = setup(true)
    expect(widths(result)).toEqual({ name: 690, size: 110, actions: 200 })

    act(() => result.current.actions.beginResize('size', 700))
    fireEvent.pointerMove(window, { clientX: 740 })
    fireEvent.pointerUp(window, { clientX: 740 })

    // 文件名保持拖动前的 690，大小从实际的 110 变成 150，操作列只是整体右移，总宽超出容器。
    expect(widths(result)).toEqual({ name: 690, size: 150, actions: 200 })
    expect(result.current.scrollX).toBe(1040)

    // 往回缩：左边仍然不动，右侧出现空白而不是被文件名吃回去。
    act(() => result.current.actions.beginResize('size', 740))
    fireEvent.pointerMove(window, { clientX: 700 })
    fireEvent.pointerUp(window, { clientX: 700 })
    expect(widths(result)).toEqual({ name: 690, size: 110, actions: 200 })
  })

  it('双击文件名列边缘或恢复默认后重新铺满', () => {
    const { result } = setup(true)
    act(() => result.current.actions.beginResize('size', 700))
    fireEvent.pointerMove(window, { clientX: 740 })
    fireEvent.pointerUp(window, { clientX: 740 })
    expect(widths(result).name).toBe(690)

    act(() => result.current.reset())
    expect(widths(result)).toEqual({ name: 690, size: 110, actions: 200 })
    act(() => result.current.actions.beginResize('size', 700))
    fireEvent.pointerMove(window, { clientX: 740 })
    fireEvent.pointerUp(window, { clientX: 740 })
    act(() => result.current.actions.setWidth('name', null))
    // 大小列仍是 150，文件名重新吃掉剩下的 1000 - (150 + 200) = 650。
    expect(widths(result).name).toBe(650)
  })

  it('键盘调整也从实际宽度开始并固定文件名', () => {
    const { result } = setup(true)
    expect(result.current.actions.widthOf('name')).toBe(690)
    act(() => result.current.actions.setWidth('size', result.current.actions.widthOf('size') + 16))
    expect(widths(result)).toEqual({ name: 690, size: 126, actions: 200 })
  })

  it('不开 growFixed 的表格行为不变：多余宽度由其余列分摊', () => {
    const { result } = setup(false)
    expect(widths(result)).toEqual({ name: 360, size: 110 + 165, actions: 200 + 165 })
  })
})
