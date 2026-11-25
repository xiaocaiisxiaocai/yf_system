import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import ResizableTable from '../components/ResizableTable'
import { usePortalAuth, type PortalAccount } from '../oem/api/portalSession'
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
  afterEach(() => {
    useAuth.setState({ user: null })
    usePortalAuth.setState({ token: null, account: null, mustChangePassword: false, booted: false, generation: 0 })
  })

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

  it('在「显示列」里拖动或用方向键调整列顺序、把列固定在左侧，操作列始终在最右，设置记在浏览器', async () => {
    type Person = { id: number; name: string; dept: string; email: string }
    const people: Person[] = [{ id: 1, name: '甲', dept: '采购', email: 'a@example.test' }]
    const view = () => render(<ResizableTable<Person> tableId="ordered" rowKey="id" data={people} pagination={false}
      columns={[
        { title: '姓名', dataIndex: 'name', width: 120 },
        { title: '部门', dataIndex: 'dept', width: 120 },
        { title: '邮箱', dataIndex: 'email', width: 200 },
        { title: '操作', width: 80, fixed: 'right', render: () => null },
      ]} />)
    const headers = () => screen.getAllByRole('columnheader').map((cell) => cell.textContent)
    const first = view()
    await userEvent.setup().click(screen.getByRole('button', { name: '显示列' }))
    const picker = await screen.findByRole('group', { name: '显示列' })
    expect(within(picker).queryByRole('button', { name: '移动操作列' })).not.toBeInTheDocument()

    // Drag 邮箱 onto 姓名: it moves to the front.
    const items = () => within(picker).getAllByRole('listitem')
    const transfer = { data: '', setData(_: string, value: string) { this.data = value }, getData() { return this.data }, effectAllowed: '' }
    fireEvent.dragStart(items()[2], { dataTransfer: transfer })
    fireEvent.dragOver(items()[0], { dataTransfer: transfer })
    fireEvent.drop(items()[0], { dataTransfer: transfer })
    await waitFor(() => expect(headers()).toEqual(['邮箱', '姓名', '部门', '操作']))

    // Keyboard: 部门 one step up.
    fireEvent.keyDown(within(picker).getByRole('button', { name: '移动部门列' }), { key: 'ArrowUp' })
    await waitFor(() => expect(headers()).toEqual(['邮箱', '部门', '姓名', '操作']))

    // Pinning 姓名 brings it to the left edge as a fixed column.
    fireEvent.click(within(picker).getByRole('button', { name: '固定姓名列' }))
    await waitFor(() => expect(headers()).toEqual(['姓名', '邮箱', '部门', '操作']))
    expect(within(picker).getByRole('button', { name: '取消固定姓名列' })).toHaveAttribute('aria-pressed', 'true')
    first.unmount()

    view()
    expect(headers()).toEqual(['姓名', '邮箱', '部门', '操作'])
    expect(screen.getByRole('columnheader', { name: /姓名/ })).toHaveClass('arco-table-col-fixed-left')
  })
  it('分组表头的叶子列同样可拖宽、在「显示列」里隐藏，分组表头随显示的列重新合并', async () => {
    type Order = { id: number; a: string; b: string; c: string }
    const data: Order[] = [{ id: 1, a: 'A1', b: 'B1', c: 'C1' }]
    render(<ResizableTable<Order> tableId="grouped" rowKey="id" data={data} pagination={false}
      columns={[
        { title: '序号', key: 'seq', width: 60, render: () => 1 },
        { title: '影响', key: 'g', children: [
          { title: '甲', key: 'a', dataIndex: 'a', width: 80 },
          { title: '乙', key: 'b', dataIndex: 'b', width: 80 },
        ] },
        { title: '丙', key: 'c', dataIndex: 'c', width: 90 },
      ]} />)
    expect(screen.getByText('影响')).toBeInTheDocument()
    fireEvent.keyDown(screen.getByRole('separator', { name: '调整甲列宽' }), { key: 'ArrowRight' })
    expect(screen.getByRole('separator', { name: '调整甲列宽' })).toHaveAttribute('aria-valuenow', '96')
    expect(screen.queryByRole('separator', { name: '调整影响列宽' })).toBeNull()
    await userEvent.setup().click(screen.getByRole('button', { name: '显示列' }))
    fireEvent.click(within(await screen.findByRole('group', { name: '显示列' })).getByRole('checkbox', { name: '影响-乙' }))
    await waitFor(() => expect(screen.queryByText('B1')).toBeNull())
    expect(screen.getByText('A1')).toBeInTheDocument()
    expect(screen.getByText('影响')).toBeInTheDocument()
  })
  it('「显示列」里的「自适应列宽」按内容宽度一次调好所有显示列，记在浏览器', async () => {
    const original = Range.prototype.getBoundingClientRect
    // jsdom has no layout: report 100px of text for every measured piece.
    Range.prototype.getBoundingClientRect = () => ({ width: 100 } as DOMRect)
    try {
      renderTable('fit')
      await userEvent.setup().click(screen.getByRole('button', { name: '显示列' }))
      fireEvent.click(await screen.findByRole('button', { name: '自适应列宽' }))
      await waitFor(() => expect(Object.keys(stored('fit', 42)?.widths ?? {}).sort()).toEqual(['name', 'size']))
      // 100px of text plus a little room, whatever the column was written with (200 and 100).
      expect(Number(screen.getByRole('separator', { name: '调整文件名列宽' }).getAttribute('aria-valuenow'))).toBeLessThan(110)
      expect(Number(screen.getByRole('separator', { name: '调整size列宽' }).getAttribute('aria-valuenow'))).toBeGreaterThan(100)
    } finally {
      Range.prototype.getBoundingClientRect = original
    }
  })

  it('OEM 门户路由按厂商账号独立保存列布局，不与内部账号串用', () => {
    useAuth.setState({ user: { id: 42, employeeNo: 'E42', realName: '内部测试', userType: 'INTERNAL' } as UserBrief })
    usePortalAuth.setState({
      token: 'portal-token',
      account: { id: 42, employeeNo: 'OEM42', companyId: 9, companyName: '测试厂' } as PortalAccount,
    })

    const portalView = render(
      <MemoryRouter initialEntries={['/oem-portal/outsource']}>
        <ResizableTable<Row>
          tableId="shared-table"
          rowKey="id"
          data={rows}
          pagination={false}
          columns={[
            { title: '文件名', dataIndex: 'name', width: 200 },
            { title: '大小', dataIndex: 'size', width: 100 },
          ]}
        />
      </MemoryRouter>,
    )
    fireEvent.keyDown(screen.getByRole('separator', { name: '调整文件名列宽' }), { key: 'ArrowRight' })
    expect(JSON.parse(localStorage.getItem('yf:table-columns:shared-table:oem:42') ?? 'null')?.widths).toEqual({ name: 216 })
    expect(localStorage.getItem('yf:table-columns:shared-table:42')).toBeNull()
    portalView.unmount()

    render(
      <MemoryRouter initialEntries={['/outsource/items']}>
        <ResizableTable<Row>
          tableId="shared-table"
          rowKey="id"
          data={rows}
          pagination={false}
          columns={[
            { title: '文件名', dataIndex: 'name', width: 200 },
            { title: '大小', dataIndex: 'size', width: 100 },
          ]}
        />
      </MemoryRouter>,
    )
    expect(screen.getByRole('separator', { name: '调整文件名列宽' })).toHaveAttribute('aria-valuenow', '200')
  })

  it('allows pagination.showTotal=false to suppress duplicate totals and hides empty state while loading', () => {
    const view = render(
      <ResizableTable<Row>
        tableId="loading-and-total"
        rowKey="id"
        loading
        data={[]}
        noDataElement={<div>自定义空态</div>}
        pagination={{ current: 1, pageSize: 20, total: 100, showTotal: false }}
        columns={[{ title: '文件名', dataIndex: 'name', width: 200 }]}
      />,
    )
    expect(screen.queryByText('自定义空态')).not.toBeInTheDocument()
    expect(screen.queryByText('暂无数据')).not.toBeInTheDocument()
    expect(screen.queryByText('共 100 条')).not.toBeInTheDocument()

    view.rerender(
      <ResizableTable<Row>
        tableId="loading-and-total"
        rowKey="id"
        loading={false}
        data={[]}
        noDataElement={<div>自定义空态</div>}
        pagination={{ current: 1, pageSize: 20, total: 100, showTotal: false }}
        columns={[{ title: '文件名', dataIndex: 'name', width: 200 }]}
      />,
    )
    expect(screen.getByText('自定义空态')).toBeInTheDocument()
    expect(screen.queryByText('共 100 条')).not.toBeInTheDocument()
  })
})
