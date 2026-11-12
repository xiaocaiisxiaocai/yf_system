import { fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('../store/auth', () => ({
  useAuth: (selector?: (state: unknown) => unknown) => {
    const state = { user: { id: 1 } }
    return selector ? selector(state) : state
  },
}))

import ResizableTable from '../components/ResizableTable'

const columns = [
  { title: '甲', dataIndex: 'a', width: 100 },
  { title: '乙', dataIndex: 'b', width: 100 },
  { title: '丙', dataIndex: 'c', width: 100 },
]

function widths(container: HTMLElement) {
  return Array.from(container.querySelectorAll<HTMLTableColElement>('colgroup col')).map((col) => parseInt(col.style.width || col.getAttribute('width') || '0', 10))
}

describe('可拖动列宽', () => {
  beforeEach(() => {
    localStorage.clear()
    vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockReturnValue(1000)
  })
  afterEach(() => vi.restoreAllMocks())

  it('拖动一列只改变这一列，其余列保持原来显示的宽度，且没有最大宽度限制', () => {
    const { container } = render(<ResizableTable tableId="drag-test" rowKey="a" data={[{ a: '1', b: '2', c: '3' }]} columns={columns} pagination={false} />)
    const before = widths(container)
    // The spare container width is shared out while nothing has been dragged.
    expect(before.every((width) => width > 100)).toBe(true)

    const handle = screen.getByRole('separator', { name: '调整甲列宽' })
    fireEvent.pointerDown(handle, { button: 0, clientX: 100 })
    fireEvent.pointerMove(window, { clientX: 1600 })
    fireEvent.pointerUp(window)

    const after = widths(container)
    expect(after[0]).toBe(before[0] + 1500)
    expect(after[1]).toBe(before[1])
    expect(after[2]).toBe(before[2])
  })
})
