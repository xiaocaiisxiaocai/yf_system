import { render, screen } from '@testing-library/react'
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

function renderList(realm: 'internal' | 'oem', initial = realm === 'internal' ? '/oem/transfers' : '/oem-portal/transfers') {
  const base = realm === 'internal' ? '/oem' : '/oem-portal'
  const api = {
    listTransfers: vi.fn().mockResolvedValue({ list: [], total: 0, page: 1, pageSize: 20 }),
    companyOptions: vi.fn().mockResolvedValue([
      { id: 9, name: '甲厂', canReceive: true },
      { id: 12, name: '乙厂', canReceive: true },
    ]),
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

const lastQuery = (api: OemApi) => (api.listTransfers as ReturnType<typeof vi.fn>).mock.calls.at(-1)![0] as { companyId?: number }

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
  })
})
