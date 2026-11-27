import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import axios, { AxiosError } from 'axios'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it, vi } from 'vitest'

import { OemApi } from '../../oem/api/OemApi'
import { OemProvider } from '../../oem/OemContext'
import PortalOutsourcePage from '../../oem/pages/PortalOutsourcePage'

const columns = [
  { key: 'serialNo', title: '序号' }, { key: 'outsourceDate', title: '外发日期' }, { key: 'status', title: '状态' },
  { key: 'electricalHandoff', title: '转迅得研发电控设计' },
  { key: 'workOrderNo', title: '工令' }, { key: 'files', title: '档案' }, { key: 'customer', title: '客户' }, { key: 'remark', title: '备注' },
]
const row = (id: number, no: string, status: string, label: string, electricalHandoff = false, closed = false, canElectricalHandoff = !closed) => ({
  id, status, closed, electricalHandoff, canElectricalHandoff, fileCount: 0, purgedFileCount: 0,
  values: { serialNo: String(id), outsourceDate: '2026-11-04', status: label, workOrderNo: no, customer: '超穎', remark: null,
    electricalHandoff: electricalHandoff ? '是' : closed || canElectricalHandoff ? '否' : null },
})
const page = (list: ReturnType<typeof row>[]) => ({ columns, list, total: list.length, page: 1, pageSize: 100 })

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

describe('ODM/OEMA 门户外发工令', () => {
  it('显示本厂商的记录，按状态着色，可搜索和显示已完成', async () => {
    const user = userEvent.setup()
    const first = row(69, 'A1', 'NORMAL', '正常')
    const second = row(71, 'A2', 'PAUSED', '暂停')
    const last = row(204, 'Z1', 'NORMAL', '正常')
    const outsourceItems = vi.fn<OemApi['outsourceItems']>().mockImplementation(async (params) => {
      if (params.keyword) return page([second])
      if (params.page === 2) return { ...page([last]), total: 101, page: 2 }
      return { ...page([first, second]), total: 101 }
    })
    renderPortal({ outsourceItems })
    const paused = (await screen.findByText('A2')).closest('tr')!
    expect(paused).toHaveClass('outsource-row--paused')
    expect(paused.cells[0]).toHaveTextContent(/^2$/)
    expect(within(paused).getByText('暂停')).toBeInTheDocument()
    expect(screen.getByText('A1').closest('tr')).toHaveClass('outsource-row--normal')
    expect(screen.getAllByRole('columnheader').map((header) => header.textContent)).toEqual(
      expect.arrayContaining(['序号', '外发日期', '状态', '转迅得研发电控设计', '工令', '档案', '客户', '备注', '操作']))
    expect(screen.queryByRole('button', { name: /编辑|删除|发送/ })).not.toBeInTheDocument()
    expect(outsourceItems).toHaveBeenLastCalledWith({ page: 1, pageSize: 100, keyword: '', includeClosed: false })

    await user.click(screen.getByText('2', { selector: '.arco-pagination-item' }))
    expect((await screen.findByText('Z1')).closest('tr')!.cells[0]).toHaveTextContent(/^101$/)
    const search = screen.getByPlaceholderText('工令、客户或机型')
    await user.type(search, 'A2')
    // Arco reads keyCode 13 for Enter, which user-event does not set in jsdom.
    fireEvent.keyDown(search, { key: 'Enter', code: 'Enter', keyCode: 13, charCode: 13, which: 13 })
    await waitFor(() => expect(outsourceItems).toHaveBeenLastCalledWith({ page: 1, pageSize: 100, keyword: 'A2', includeClosed: false }))
    expect((await screen.findByText('A2')).closest('tr')!.cells[0]).toHaveTextContent(/^1$/)
    await user.click(screen.getByText('显示已完成'))
    await waitFor(() => expect(outsourceItems).toHaveBeenLastCalledWith({ page: 1, pageSize: 100, keyword: 'A2', includeClosed: true }))
  })

  it('vendor portal shows the column and loads from the vendor endpoint', async () => {
    const user = userEvent.setup()
    const vendorRow = { ...row(17, 'A17', 'NORMAL', '正常'), fileCount: 1 }
    const file = { fileId: 81, transferId: 19, name: '厂商图纸.pdf', sizeBytes: 2048, direction: 'OEM_TO_INTERNAL',
      vendorName: '本厂商', sentAt: '2026-11-24T01:30:00Z', state: 'AVAILABLE' }
    const portalHttp = axios.create()
    const get = vi.spyOn(portalHttp, 'get').mockResolvedValueOnce({ data: page([vendorRow]) })
      .mockResolvedValueOnce({ data: { canOpen: true, files: [file] } })
    renderPortal(new OemApi(portalHttp))

    const header = await screen.findByRole('columnheader', { name: /^档案/ })
    expect(header.previousElementSibling).toHaveTextContent('工令')
    expect(header).not.toHaveClass('arco-table-col-fixed-left')
    expect(get).toHaveBeenCalledTimes(1)
    await user.click(screen.getByRole('button', { name: '档案：1 个文件' }))
    expect(get).toHaveBeenNthCalledWith(2, '/oem/outsource-items/17/files', { params: undefined })
    const fileRow = within((await screen.findByText(file.name)).closest('li')!)
    expect(fileRow.getByText('厂商发来')).toBeInTheDocument()
    expect(fileRow.getByText(file.vendorName)).toBeInTheDocument()
    expect(fileRow.getByRole('button', { name: `预览 ${file.name}` })).toBeEnabled()
    expect(fileRow.getByRole('button', { name: `下载 ${file.name}` })).toBeEnabled()
  })

  it('仅允许的工令可签名转交，已转交可撤回，已完成隐藏操作', async () => {
    const user = userEvent.setup()
    const ordinary = row(1, 'A1', 'PAUSED', '暂停')
    const handedOff = row(1, 'A1', 'PAUSED', '暂停', true)
    const withdrawn = row(1, 'A1', 'PAUSED', '暂停', false, false, false)
    const otherRows = [
      row(2, 'A2', 'NORMAL', '正常', false, true), row(3, 'A3', 'CANCELLED', '取消', true, true),
      row(4, 'A4', 'NORMAL', '正常', false, false, false),
    ]
    const outsourceItems = vi.fn().mockResolvedValueOnce(page([ordinary, ...otherRows]))
      .mockResolvedValueOnce(page([handedOff, ...otherRows])).mockResolvedValue(page([withdrawn, ...otherRows]))
    const setOutsourceElectricalHandoff = vi.fn().mockResolvedValueOnce(handedOff).mockResolvedValueOnce(withdrawn)
    renderPortal({ outsourceItems, setOutsourceElectricalHandoff })

    const open = within((await screen.findByText('A1')).closest('tr')!)
    expect(open.getByText('否')).toBeInTheDocument()
    const blocked = within(screen.getByText('A4').closest('tr')!)
    expect(blocked.getByText('-')).toBeInTheDocument()
    expect(blocked.queryByText('否')).not.toBeInTheDocument()
    expect(blocked.queryByRole('button', { name: '转迅得电控' })).not.toBeInTheDocument()
    expect(within(screen.getByText('A2').closest('tr')!).queryByRole('button', { name: '转迅得电控' })).not.toBeInTheDocument()
    expect(within(screen.getByText('A3').closest('tr')!).queryByRole('button', { name: '撤回' })).not.toBeInTheDocument()
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
    await waitFor(() => expect(open.getByText('-')).toBeInTheDocument())
    expect(open.queryByRole('button', { name: '转迅得电控' })).not.toBeInTheDocument()
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
