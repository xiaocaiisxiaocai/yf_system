import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
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

  it('每张表都能在表头选择显示列：第一列和操作列始终显示，选择记在浏览器', async () => {
    type Person = { id: number; name: string; dept: string; email: string }
    const people: Person[] = [{ id: 1, name: '甲', dept: '采购', email: 'a@example.test' }]
    const view = () => render(<ResizableTable<Person> tableId="people" rowKey="id" data={people}
      pagination={{ current: 1, pageSize: 20, total: 1 }}
      columns={[
        { title: '姓名', dataIndex: 'name', width: 120 },
        { title: '部门', dataIndex: 'dept', width: 120 },
        { title: '邮箱', dataIndex: 'email', width: 200 },
        { title: '操作', width: 80, render: () => null },
      ]} />)
    const first = view()
    // Shared pagination: total count and the same page-size choices on every table.
    expect(screen.getByText('共 1 条')).toBeInTheDocument()

    const trigger = screen.getByRole('button', { name: '显示列' })
    expect(trigger.closest('th')).toHaveTextContent('操作')
    await userEvent.setup().click(trigger)
    const picker = await screen.findByRole('group', { name: '显示列' })
    expect(within(picker).getByRole('checkbox', { name: '姓名' })).toBeDisabled()
    expect(within(picker).getByRole('checkbox', { name: '操作' })).toBeDisabled()
    fireEvent.click(within(picker).getByRole('checkbox', { name: '邮箱' }))
    await waitFor(() => expect(screen.queryByRole('columnheader', { name: /邮箱/ })).not.toBeInTheDocument())
    expect(screen.getAllByRole('columnheader').map((cell) => cell.textContent)).toEqual(['姓名', '部门', '操作'])
    first.unmount()

    view()
    expect(screen.queryByRole('columnheader', { name: /邮箱/ })).not.toBeInTheDocument()
  })

  it('没有可隐藏的列时不显示选择按钮', () => {
    renderTable()
    expect(screen.queryByRole('button', { name: '显示列' })).not.toBeInTheDocument()
  })
})
