import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it, vi } from 'vitest'

import type { OemApi } from '../../oem/api/OemApi'
import { OemProvider } from '../../oem/OemContext'
import PortalOutsourcePage from '../../oem/pages/PortalOutsourcePage'

const columns = [
  { key: 'serialNo', title: '序号' }, { key: 'outsourceDate', title: '外发日期' }, { key: 'status', title: '状态' },
  { key: 'workOrderNo', title: '工令' }, { key: 'customer', title: '客户' }, { key: 'remark', title: '备注' },
]
const row = (id: number, no: string, status: string, label: string) => ({
  id, status, closed: false,
  values: { serialNo: String(id), outsourceDate: '2026-11-04', status: label, workOrderNo: no, customer: '超穎', remark: null },
})

describe('ODM/OEMA 门户外协清单', () => {
  it('只读显示本厂商的记录，按状态着色，可搜索和显示已完成', async () => {
    const user = userEvent.setup()
    const outsourceItems = vi.fn().mockResolvedValue({
      columns, list: [row(1, 'A1', 'NORMAL', '正常'), row(2, 'A2', 'PAUSED', '暂停')], total: 2, page: 1, pageSize: 20,
    })
    render(
      <MemoryRouter>
        <OemProvider value={{
          api: { outsourceItems } as unknown as OemApi, realm: 'oem', base: '/oem-portal', permissions: new Set(), userId: 9, queryScope: ['portal', 9],
        }}>
          <PortalOutsourcePage />
        </OemProvider>
      </MemoryRouter>,
    )
    const paused = (await screen.findByText('A2')).closest('tr')!
    expect(paused).toHaveClass('outsource-row--paused')
    expect(within(paused).getByText('暂停')).toBeInTheDocument()
    expect(screen.getByText('A1').closest('tr')).toHaveClass('outsource-row--normal')
    expect(screen.getAllByRole('columnheader').map((header) => header.textContent)).toEqual(
      expect.arrayContaining(['序号', '外发日期', '状态', '工令', '客户', '备注']))
    expect(screen.queryByRole('button', { name: /编辑|删除|发送/ })).not.toBeInTheDocument()
    expect(outsourceItems).toHaveBeenLastCalledWith({ page: 1, pageSize: 20, keyword: '', includeClosed: false })

    const search = screen.getByPlaceholderText('工令、客户或机型')
    await user.type(search, 'A2')
    // Arco reads keyCode 13 for Enter, which user-event does not set in jsdom.
    fireEvent.keyDown(search, { key: 'Enter', code: 'Enter', keyCode: 13, charCode: 13, which: 13 })
    await waitFor(() => expect(outsourceItems).toHaveBeenLastCalledWith({ page: 1, pageSize: 20, keyword: 'A2', includeClosed: false }))
    await user.click(screen.getByText('显示已完成'))
    await waitFor(() => expect(outsourceItems).toHaveBeenLastCalledWith({ page: 1, pageSize: 20, keyword: 'A2', includeClosed: true }))
  })
})
