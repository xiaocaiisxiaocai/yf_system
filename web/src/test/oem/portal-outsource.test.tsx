import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { AxiosError } from 'axios'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it, vi } from 'vitest'

import type { OemApi } from '../../oem/api/OemApi'
import { OemProvider } from '../../oem/OemContext'
import PortalOutsourcePage from '../../oem/pages/PortalOutsourcePage'

const columns = [
  { key: 'serialNo', title: '序号' }, { key: 'outsourceDate', title: '外发日期' }, { key: 'status', title: '状态' },
  { key: 'electricalHandoff', title: '是否转迅得电控' },
  { key: 'workOrderNo', title: '工令' }, { key: 'customer', title: '客户' }, { key: 'remark', title: '备注' },
]
const row = (id: number, no: string, status: string, label: string, electricalHandoff = false, closed = false) => ({
  id, status, closed, electricalHandoff,
  values: { serialNo: String(id), outsourceDate: '2026-11-04', status: label, workOrderNo: no, customer: '超穎', remark: null,
    electricalHandoff: electricalHandoff ? '是' : '否' },
})
const page = (list: ReturnType<typeof row>[]) => ({ columns, list, total: list.length, page: 1, pageSize: 20 })

function renderPortal(api: Pick<OemApi, 'outsourceItems'> & Partial<Pick<OemApi, 'setOutsourceElectricalHandoff'>>) {
  return render(
    <MemoryRouter>
      <OemProvider value={{
        api: api as OemApi, realm: 'oem', base: '/oem-portal', permissions: new Set(), userId: 9, queryScope: ['portal', 9],
      }}>
        <PortalOutsourcePage />
      </OemProvider>
    </MemoryRouter>,
  )
}

describe('ODM/OEMA 门户外协清单', () => {
  it('显示本厂商的记录，按状态着色，可搜索和显示已完成', async () => {
    const user = userEvent.setup()
    const outsourceItems = vi.fn().mockResolvedValue(page([row(1, 'A1', 'NORMAL', '正常'), row(2, 'A2', 'PAUSED', '暂停')]))
    renderPortal({ outsourceItems })
    const paused = (await screen.findByText('A2')).closest('tr')!
    expect(paused).toHaveClass('outsource-row--paused')
    expect(within(paused).getByText('暂停')).toBeInTheDocument()
    expect(screen.getByText('A1').closest('tr')).toHaveClass('outsource-row--normal')
    expect(screen.getAllByRole('columnheader').map((header) => header.textContent)).toEqual(
      expect.arrayContaining(['序号', '外发日期', '状态', '是否转迅得电控', '工令', '客户', '备注', '操作']))
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

  it('按转交状态提供签名操作，两种状态的已完成记录均禁用操作', async () => {
    const user = userEvent.setup()
    const ordinary = row(1, 'A1', 'PAUSED', '暂停')
    const handedOff = row(1, 'A1', 'PAUSED', '暂停', true)
    const closed = [row(2, 'A2', 'NORMAL', '正常', false, true), row(3, 'A3', 'CANCELLED', '取消', true, true)]
    const outsourceItems = vi.fn().mockResolvedValueOnce(page([ordinary, ...closed]))
      .mockResolvedValueOnce(page([handedOff, ...closed])).mockResolvedValue(page([ordinary, ...closed]))
    const setOutsourceElectricalHandoff = vi.fn().mockResolvedValueOnce(handedOff).mockResolvedValueOnce(ordinary)
    renderPortal({ outsourceItems, setOutsourceElectricalHandoff })

    const open = within((await screen.findByText('A1')).closest('tr')!)
    expect(open.getByText('否')).toBeInTheDocument()
    expect(within(screen.getByText('A2').closest('tr')!).getByRole('button', { name: '转迅得电控' })).toBeDisabled()
    expect(within(screen.getByText('A3').closest('tr')!).getByRole('button', { name: '撤回' })).toBeDisabled()
    await user.click(open.getByRole('button', { name: '转迅得电控' }))
    const dialog = within(await screen.findByRole('dialog'))
    const confirm = dialog.getByRole('button', { name: '确认' })
    const signature = dialog.getByRole('textbox', { name: '签名' })
    expect(confirm).toBeDisabled()
    await user.type(signature, '  ')
    expect(confirm).toBeDisabled()
    fireEvent.change(signature, { target: { value: '名'.repeat(33) } })
    expect(dialog.getByText('签名不能超过 32 个字符')).toBeInTheDocument()
    expect(confirm).toBeDisabled()
    expect(setOutsourceElectricalHandoff).not.toHaveBeenCalled()

    fireEvent.change(signature, { target: { value: '  王小明  ' } })
    await user.click(confirm)
    expect(setOutsourceElectricalHandoff).toHaveBeenLastCalledWith(1, { handoff: true, expected: false, signerName: '王小明' })
    await waitFor(() => expect(open.getByText('是')).toBeInTheDocument())
    const withdraw = open.getByRole('button', { name: '撤回' })
    await waitFor(() => expect(withdraw).toBeEnabled())
    await user.click(withdraw)
    const withdrawDialog = within(await screen.findByRole('dialog', { name: /撤回 · 工令 A1/ }))
    await user.type(withdrawDialog.getByRole('textbox', { name: '签名' }), '赵一')
    await user.click(withdrawDialog.getByRole('button', { name: '确认' }))
    expect(setOutsourceElectricalHandoff).toHaveBeenLastCalledWith(1, { handoff: false, expected: true, signerName: '赵一' })
    await waitFor(() => expect(open.getByText('否')).toBeInTheDocument())
  })

  it('遇到 40903 刷新列表，保留签名，下一步由厂商确认', async () => {
    const user = userEvent.setup()
    const outsourceItems = vi.fn().mockResolvedValueOnce(page([row(1, 'A1', 'NORMAL', '正常')]))
      .mockResolvedValueOnce(page([row(1, 'A1', 'NORMAL', '正常', true)]))
    const conflict = new AxiosError('数据已被他人修改，请刷新后重试')
    conflict.response = { status: 409, data: { code: 40903 } } as typeof conflict.response
    const setOutsourceElectricalHandoff = vi.fn().mockRejectedValueOnce(conflict)
    renderPortal({ outsourceItems, setOutsourceElectricalHandoff })

    await user.click(await screen.findByRole('button', { name: '转迅得电控' }))
    const dialog = within(await screen.findByRole('dialog'))
    await user.type(dialog.getByRole('textbox', { name: '签名' }), '原签名')
    await user.click(dialog.getByRole('button', { name: '确认' }))
    const withdraw = await screen.findByRole('button', { name: '撤回' })
    await waitFor(() => expect(withdraw).toBeEnabled())
    expect(outsourceItems).toHaveBeenCalledTimes(2)
    expect(setOutsourceElectricalHandoff).toHaveBeenCalledTimes(1)
    await user.click(withdraw)
    expect(within(await screen.findByRole('dialog', { name: /撤回 · 工令 A1/ })).getByRole('textbox', { name: '签名' })).toHaveValue('原签名')
  })
})
