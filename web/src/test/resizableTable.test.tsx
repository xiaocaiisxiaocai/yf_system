import { fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import ResizableTable from '../components/ResizableTable'
import { useAuth, type UserBrief } from '../store/auth'

type Row = { id: number; name: string; size: string }

const rows: Row[] = [{ id: 1, name: '很长的文件名.pdf', size: '1 KB' }]

function renderTable(tableId = 'demo') {
  return render(
    <ResizableTable<Row>
      tableId={tableId}
      rowKey="id"
      data={rows}
      pagination={false}
      columns={[
        { title: '文件名', dataIndex: 'name', width: 200 },
        { title: <span data-testid="rich-title">大小</span>, key: 'size', dataIndex: 'size', width: 100 },
      ]}
    />,
  )
}

const stored = (tableId: string, userId: number) => JSON.parse(localStorage.getItem(`yf:table-columns:${tableId}:${userId}`) ?? 'null') as
  { widths: Record<string, number> } | null

describe('ResizableTable', () => {
  beforeEach(() => {
    localStorage.clear()
    useAuth.setState({ user: { id: 42, employeeNo: 'E42', realName: '测试', userType: 'INTERNAL' } as UserBrief })
  })
  afterEach(() => useAuth.setState({ user: null }))

  it('every column gets a resize handle and the original rich title stays', () => {
    renderTable()
    expect(screen.getByRole('separator', { name: '调整文件名列宽' })).toHaveAttribute('aria-valuenow', '200')
    expect(screen.getByRole('separator', { name: '调整size列宽' })).toHaveAttribute('aria-valuenow', '100')
    expect(screen.getByTestId('rich-title')).toHaveTextContent('大小')
  })

  it('remembers a width per table and user in the browser and restores it', () => {
    const first = renderTable()
    fireEvent.keyDown(screen.getByRole('separator', { name: '调整文件名列宽' }), { key: 'ArrowRight' })
    expect(screen.getByRole('separator', { name: '调整文件名列宽' })).toHaveAttribute('aria-valuenow', '216')
    expect(stored('demo', 42)?.widths).toEqual({ name: 216 })
    first.unmount()

    renderTable()
    expect(screen.getByRole('separator', { name: '调整文件名列宽' })).toHaveAttribute('aria-valuenow', '216')
    // Home restores the default and clears the saved value.
    fireEvent.keyDown(screen.getByRole('separator', { name: '调整文件名列宽' }), { key: 'Home' })
    expect(screen.getByRole('separator', { name: '调整文件名列宽' })).toHaveAttribute('aria-valuenow', '200')
    expect(stored('demo', 42)).toBeNull()
  })

  it('keeps other tables and other users separate', () => {
    const view = renderTable('demo')
    fireEvent.keyDown(screen.getByRole('separator', { name: '调整文件名列宽' }), { key: 'ArrowRight' })
    view.unmount()

    renderTable('other')
    expect(screen.getByRole('separator', { name: '调整文件名列宽' })).toHaveAttribute('aria-valuenow', '200')
  })
})
