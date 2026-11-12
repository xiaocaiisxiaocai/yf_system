import { fireEvent, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { describe, expect, it, vi } from 'vitest'
import { createAppQueryClient } from '../../api/queryClient'
import type { OemApi } from '../../oem/api/OemApi'
import { OemProvider } from '../../oem/OemContext'
import TransferListPage from '../../oem/pages/TransferListPage'

function LocationProbe() {
  const location = useLocation()
  return <span data-testid="location">{location.pathname + location.search}</span>
}

function renderList(realm: 'internal' | 'oem', initial = realm === 'internal' ? '/oem/transfers' : '/oem-portal/transfers', overrides: Partial<OemApi> = {}) {
  const base = realm === 'internal' ? '/oem' : '/oem-portal'
  const api = {
    departments: vi.fn().mockResolvedValue([{ id: 1, name: '工程事业部', children: [{ id: 2, name: '研发部', children: [{ id: 3, name: '电控课', children: [] }] }] }]),
    listTransfers: vi.fn().mockResolvedValue({ list: [], total: 0, page: 1, pageSize: 20 }),
    companyOptions: vi.fn().mockResolvedValue([
      { id: 9, name: '甲厂', canReceive: true },
      { id: 12, name: '乙厂', canReceive: true },
    ]),
    ...overrides,
  } as unknown as OemApi
  render(
    <QueryClientProvider client={createAppQueryClient()}>
      <MemoryRouter initialEntries={[initial]}>
        <OemProvider value={{ api, realm, base, permissions: new Set(['oem:transfer_view']), userId: 7, queryScope: ['vendor-tabs'] }}>
          <Routes><Route path={`${base}/transfers`} element={<><TransferListPage /><LocationProbe /></>} /></Routes>
        </OemProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  )
  return api
}

const lastQuery = (api: OemApi) => (api.listTransfers as ReturnType<typeof vi.fn>).mock.calls.at(-1)![0] as { companyId?: number; departmentId?: number; page: number; sender?: string }

describe('ODM/OEMA 传递单按厂商分页签', () => {
  it('“全部”在前，每个厂商一个页签；切换页签只查该厂商', async () => {
    const user = userEvent.setup()
    const api = renderList('internal')

    await screen.findByRole('tab', { name: '乙厂' })
    const tabs = screen.getAllByRole('tab')
    // 厂商页签在前，其下是「最新 / 历史（已删除）」范围页签。
    expect(tabs.map((tab) => tab.textContent)).toEqual(['全部', '甲厂', '乙厂', '最新', '历史（已删除）'])
    expect(lastQuery(api).companyId).toBeUndefined()
    expect(screen.queryByRole('combobox', { name: '往来厂商' })).not.toBeInTheDocument()

    await user.click(screen.getByRole('tab', { name: '乙厂' }))
    await vi.waitFor(() => expect(lastQuery(api).companyId).toBe(12))
    expect(screen.getByTestId('location')).toHaveTextContent('companyId=12')

    await user.click(screen.getByRole('tab', { name: '全部' }))
    await vi.waitFor(() => expect(lastQuery(api).companyId).toBeUndefined())
    expect(screen.getByTestId('location')).not.toHaveTextContent('companyId')
    expect(screen.getByText('SAA发出')).toBeInTheDocument()
  })

  it('从厂商目录带 companyId 进入时直接选中该厂商页签', async () => {
    const api = renderList('internal', '/oem/transfers?companyId=9')
    expect(await screen.findByRole('tab', { name: '甲厂', selected: true })).toBeInTheDocument()
    expect(lastQuery(api).companyId).toBe(9)
  })

  it('厂商门户不显示厂商页签', async () => {
    const api = renderList('oem')
    await vi.waitFor(() => expect(api.listTransfers).toHaveBeenCalled())
    expect(screen.getAllByRole('tab').map((tab) => tab.textContent)).toEqual(['最新', '历史（已删除）'])
    expect(screen.queryByLabelText('组织架构')).not.toBeInTheDocument()
    expect(api.departments).not.toHaveBeenCalled()
  })

  it('组织树可搜索选择，和厂商、发送人组合；清空仅移除组织条件', async () => {
    const user = userEvent.setup()
    const api = renderList('internal', '/oem/transfers?companyId=9')
    await screen.findByRole('tab', { name: '甲厂' })
    const senderInput = screen.getByRole('textbox', { name: '发送人' })
    await user.type(senderInput, '测试员工')
    fireEvent.keyDown(senderInput, { key: 'Enter', code: 'Enter', keyCode: 13, which: 13 })
    await vi.waitFor(() => expect(lastQuery(api).sender).toBe('测试员工'))
    const organization = screen.getByLabelText('组织架构')
    await user.click(organization)
    const input = organization.querySelector('input')!
    fireEvent.change(input, { target: { value: '电控课' } })
    const match = await screen.findByText('电控课', { selector: '.arco-tree-select-highlight' })
    fireEvent.click(match.closest('.arco-tree-node-title')!)
    await vi.waitFor(() => expect(lastQuery(api)).toMatchObject({ departmentId: 3, companyId: 9, sender: '测试员工', page: 1 }))
    expect(screen.getByTestId('location')).toHaveTextContent('departmentId=3')
    await user.hover(organization)
    fireEvent.click(organization.querySelector('.arco-tree-select-clear-icon')!)
    await vi.waitFor(() => expect(lastQuery(api).departmentId).toBeUndefined())
    expect(lastQuery(api)).toMatchObject({ companyId: 9, sender: '测试员工', page: 1 })
    expect(screen.getByTestId('location')).not.toHaveTextContent('departmentId')
  })

  it('组织筛选随地址恢复', async () => {
    const api = renderList('internal', '/oem/transfers?departmentId=2&companyId=12')
    await vi.waitFor(() => expect(lastQuery(api)).toMatchObject({ departmentId: 2, companyId: 12 }))
    expect(await screen.findByText('研发部')).toBeInTheDocument()
  })

  it('厂商门户不向接口转发手工添加的组织参数', async () => {
    const api = renderList('oem', '/oem-portal/transfers?departmentId=2')
    await vi.waitFor(() => expect(api.listTransfers).toHaveBeenCalled())
    expect(lastQuery(api).departmentId).toBeUndefined()
    expect(api.departments).not.toHaveBeenCalled()
  })

  it('从后续页选择父级组织时重置到第一页', async () => {
    const user = userEvent.setup()
    const api = renderList('internal', '/oem/transfers', {
      listTransfers: vi.fn().mockResolvedValue({ list: [], total: 41, page: 1, pageSize: 20 }),
    })
    await user.click(await screen.findByLabelText('下一页'))
    await vi.waitFor(() => expect(lastQuery(api).page).toBe(2))
    await user.click(screen.getByLabelText('组织架构'))
    const division = await screen.findByText('工程事业部')
    fireEvent.click(division.closest('.arco-tree-node-title')!)
    await vi.waitFor(() => expect(lastQuery(api)).toMatchObject({ page: 1, departmentId: 1 }))
  })

  it('组织加载失败可单独重试，保留地址中的筛选条件', async () => {
    const user = userEvent.setup()
    const departments = vi.fn().mockRejectedValueOnce(new Error('offline')).mockResolvedValueOnce([{ id: 2, name: '研发部', children: [] }])
    const api = renderList('internal', '/oem/transfers?departmentId=2&companyId=9', { departments })
    await user.click(await screen.findByRole('button', { name: '重试组织架构' }))
    expect(await screen.findByText('研发部')).toBeInTheDocument()
    expect(screen.queryByText('组织架构加载失败，请重试')).not.toBeInTheDocument()
    expect(lastQuery(api)).toMatchObject({ departmentId: 2, companyId: 9 })
    expect(departments).toHaveBeenCalledTimes(2)
  })
})
