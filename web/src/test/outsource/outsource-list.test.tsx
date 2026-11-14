import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import http from '../../api/client'
import { fmtTime } from '../../api/types'
import { normalizeImportDate } from '../../outsource/importDate'
import * as workbook from '../../utils/excelWorkbook'
import { outsourceHome } from '../../outsource/outsourceNavigation'
import OutsourceListPage from '../../outsource/pages/OutsourceListPage'
import { useAuth } from '../../store/auth'

function setSession(permissions: string[]) {
  useAuth.setState({
    token: 'token',
    user: { id: 7, employeeNo: 'buyer', realName: '采购', email: 'buyer@example.test', userType: 'INTERNAL', supplierId: null, isSystemAdmin: false },
    permissions, menus: [], mustChangePassword: false, booted: true, generation: 1,
  })
}

const base = {
  outsourceDate: '2026-11-04', customer: '超穎(泰國)', machineModel: 'ST-201L', mechanicalSection: '4.王家琦',
  mechanicalOwner: null, electricalOwner: null, workOrderIssuedOn: '2026-09-30', bomOn: null, drawingReleasedOn: null,
  quantity: 1, originalDueDate: '2026-12-31', latestDueDate: '2026-12-31', remark: null, closed: false,
  ipmsSyncedAt: '2026-11-10T00:00:00Z',
}
const items = [
  { ...base, id: 1, workOrderNo: '21089901', vendorId: 1, vendorName: '鼎亚', typeCode: 'OEMA', typeId: 5, typeLabel: 'OEMA【进料+制造】',
    status: 'PAUSED', sendStatus: 'UNSENT', matchState: 'OK', updatedAt: '2026-11-10T01:02:03.456Z' },
  { ...base, id: 2, workOrderNo: '21089961', vendorId: 2, vendorName: '元控', typeCode: 'OEMA', typeId: 5, typeLabel: 'OEMA【进料+制造】',
    status: 'NORMAL', sendStatus: 'SENT', matchState: 'VENDOR_MISMATCH', updatedAt: '2026-11-10T01:02:03.456Z' },
]
const vendors = [
  { id: 1, name: '鼎亚', remark: null, status: 'ACTIVE', sortNo: 1, contacts: [] },
  { id: 2, name: '元控', remark: null, status: 'ACTIVE', sortNo: 2, contacts: [] },
]
const types = [
  { id: 5, code: 'OEMA', name: '进料+制造', status: 'ACTIVE', sortNo: 1, vendorIds: [1] },
  { id: 6, code: 'ODM-12345', name: '含售前售后', status: 'ACTIVE', sortNo: 2, vendorIds: [2] },
]
const syncStatus = {
  syncTime: '02:00', ipmsConfigured: true, running: false, lastStartedAt: '2026-11-09T18:00:00Z',
  lastFinishedAt: '2026-11-09T18:00:05Z', lastSuccessAt: '2026-11-09T18:00:05Z', lastError: null, lastTrigger: 'SCHEDULED',
  lastProcessed: 2, lastChanged: 0, lastClosed: 0, lastMissing: 0,
}

function mockApi() {
  return vi.spyOn(http, 'get').mockImplementation((url: string) => Promise.resolve({
    data: url === '/outsource/items' ? { list: items, total: 2, page: 1, pageSize: 20 }
        : url === '/outsource/sync-status' ? syncStatus
          : url.endsWith('/vendors') ? vendors : url.endsWith('/types') ? types : [],
  } as never))
}

describe('外协清单页面', () => {
  afterEach(() => {
    vi.restoreAllMocks()
    useAuth.setState({ token: null, user: null, permissions: [], menus: [] })
  })

  it('汇入日期支持年份在前和 Excel 默认的月/日/年格式', () => {
    expect(normalizeImportDate('2026/11/4')).toBe('2026-11-04')
    expect(normalizeImportDate('2026.11.04')).toBe('2026-11-04')
    expect(normalizeImportDate('11/4/26')).toBe('2026-11-04')
    expect(normalizeImportDate('11/4/2026')).toBe('2026-11-04')
    expect(normalizeImportDate('2026-02-30')).toBe('2026-02-30')
    expect(normalizeImportDate('下周')).toBe('下周')
  })

  it('有查看权限时首页进入清单，只有设定权限时进入设定', () => {
    expect(outsourceHome(['outsource:view', 'outsource:settings'])).toBe('/outsource/items')
    expect(outsourceHome(['outsource:settings'])).toBe('/outsource/settings')
    expect(outsourceHome(['outsource:import'])).toBeNull()
  })

  it('不分厂商页签，可按厂商筛选，暂停整行标红，类型与厂商不符时提示', async () => {
    setSession(['outsource:view'])
    const get = mockApi()
    render(<MemoryRouter><OutsourceListPage /></MemoryRouter>)

    const paused = (await screen.findByText('21089901')).closest('tr')!
    expect(paused).toHaveClass('outsource-row--paused')
    const mismatch = screen.getByText('21089961').closest('tr')!
    expect(within(mismatch).getByText('不符')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /汇入/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /编辑工令/ })).not.toBeInTheDocument()

    expect(screen.queryByRole('tab')).not.toBeInTheDocument()
    expect(get).not.toHaveBeenCalledWith('/outsource/items/summary', expect.anything())
    const user = userEvent.setup()
    await user.click(screen.getByRole('combobox', { name: '外发厂商' }))
    fireEvent.click(await screen.findByRole('option', { name: '元控' }))
    await waitFor(() => expect(get).toHaveBeenCalledWith('/outsource/items', expect.objectContaining({
      params: expect.objectContaining({ vendorId: 2, page: 1 }),
    })))
  })

  it('采购编辑时带上版本，厂商只能选该类型允许的厂商', async () => {
    const user = userEvent.setup()
    setSession(['outsource:view', 'outsource:manage'])
    mockApi()
    const put = vi.spyOn(http, 'put').mockResolvedValue({ data: items[0] } as never)
    render(<MemoryRouter><OutsourceListPage /></MemoryRouter>)

    await user.click(await screen.findByRole('button', { name: '编辑工令 21089901' }))
    const dialog = await screen.findByRole('dialog')
    await user.click(within(dialog).getByRole('button', { name: '保存' }))
    await waitFor(() => expect(put).toHaveBeenCalledWith('/outsource/items/1', {
      outsourceDate: '2026-11-04', vendorId: 1, typeId: 5, status: 'PAUSED', remark: '', expectedUpdatedAt: '2026-11-10T01:02:03.456Z',
    }))
  })

  it('编辑时可改外发类型，厂商下拉随所选类型过滤，原厂商不适用时要重新选择', async () => {
    const user = userEvent.setup()
    setSession(['outsource:view', 'outsource:manage'])
    mockApi()
    const put = vi.spyOn(http, 'put').mockResolvedValue({ data: items[0] } as never)
    render(<MemoryRouter><OutsourceListPage /></MemoryRouter>)

    await user.click(await screen.findByRole('button', { name: '编辑工令 21089901' }))
    const dialog = await screen.findByRole('dialog')
    await user.click(within(dialog).getByRole('combobox', { name: '外发类型' }))
    fireEvent.click(await screen.findByRole('option', { name: 'ODM-12345【含售前售后】' }))

    await user.click(within(dialog).getByRole('button', { name: '保存' }))
    expect(put).not.toHaveBeenCalled()
    expect(await within(dialog).findByText('请选择外发厂商')).toBeInTheDocument()

    await user.click(within(dialog).getByRole('combobox', { name: '外发厂商' }))
    expect(screen.queryByRole('option', { name: '鼎亚' })).not.toBeInTheDocument()
    fireEvent.click(await screen.findByRole('option', { name: '元控' }))
    await user.click(within(dialog).getByRole('button', { name: '保存' }))
    await waitFor(() => expect(put).toHaveBeenCalledWith('/outsource/items/1', {
      outsourceDate: '2026-11-04', vendorId: 2, typeId: 6, status: 'PAUSED', remark: '', expectedUpdatedAt: '2026-11-10T01:02:03.456Z',
    }))
  })

  it('类型不在字典中的记录要先选择外发类型才能保存', async () => {
    const user = userEvent.setup()
    setSession(['outsource:view', 'outsource:manage'])
    vi.spyOn(http, 'get').mockImplementation((url: string) => Promise.resolve({
      data: url === '/outsource/items'
        ? { list: [{ ...items[0], typeId: null, typeLabel: 'XYZ', typeCode: 'XYZ', matchState: 'UNKNOWN_TYPE' }], total: 1, page: 1, pageSize: 20 }
          : url === '/outsource/sync-status' ? syncStatus : url.endsWith('/vendors') ? vendors : url.endsWith('/types') ? types : [],
    } as never))
    const put = vi.spyOn(http, 'put').mockResolvedValue({ data: items[0] } as never)
    render(<MemoryRouter><OutsourceListPage /></MemoryRouter>)

    await user.click(await screen.findByRole('button', { name: '编辑工令 21089901' }))
    const dialog = await screen.findByRole('dialog')
    await user.click(within(dialog).getByRole('button', { name: '保存' }))
    expect(put).not.toHaveBeenCalled()
    expect(await within(dialog).findByText('请选择外发类型')).toBeInTheDocument()
  })

  it('批量修改可选外发类型，选了才提交', async () => {
    const user = userEvent.setup()
    setSession(['outsource:view', 'outsource:manage'])
    mockApi()
    const put = vi.spyOn(http, 'put').mockResolvedValue({ data: { items } } as never)
    render(<MemoryRouter><OutsourceListPage /></MemoryRouter>)

    const row = (await screen.findByText('21089901')).closest('tr')!
    await user.click(within(row).getByRole('checkbox'))
    await user.click(screen.getByRole('button', { name: /批量修改 \(1\)/ }))
    const dialog = await screen.findByRole('dialog')
    await user.click(within(dialog).getByRole('combobox', { name: '外发类型' }))
    fireEvent.click(await screen.findByRole('option', { name: 'OEMA【进料+制造】' }))
    await user.click(within(dialog).getByRole('button', { name: '保存' }))
    await waitFor(() => expect(put).toHaveBeenCalledWith('/outsource/items/batch', {
      items: [{ id: 1, expectedUpdatedAt: '2026-11-10T01:02:03.456Z' }],
      outsourceDate: undefined, vendorId: undefined, typeId: 5, status: undefined, remark: undefined,
    }))
  })

  it('勾选后可标记已完成；已完成的记录只能重新开启', async () => {
    const user = userEvent.setup()
    setSession(['outsource:view', 'outsource:manage'])
    mockApi()
    const put = vi.spyOn(http, 'put').mockResolvedValue({ data: { items } } as never)
    const { unmount } = render(<MemoryRouter><OutsourceListPage /></MemoryRouter>)

    const row = (await screen.findByText('21089901')).closest('tr')!
    await user.click(within(row).getByRole('checkbox'))
    await user.click(screen.getByRole('button', { name: '标记完成' }))
    fireEvent.click(await screen.findByRole('button', { name: '确定' }))
    await waitFor(() => expect(put).toHaveBeenCalledWith('/outsource/items/completion', {
      items: [{ id: 1, expectedUpdatedAt: '2026-11-10T01:02:03.456Z' }], completed: true,
    }))
    unmount()

    vi.spyOn(http, 'get').mockImplementation((url: string) => Promise.resolve({
      data: url === '/outsource/items' ? { list: [{ ...items[1], closed: true }], total: 1, page: 1, pageSize: 20 }
          : url === '/outsource/sync-status' ? syncStatus : url.endsWith('/vendors') ? vendors : url.endsWith('/types') ? types : [],
    } as never))
    put.mockClear()
    render(<MemoryRouter><OutsourceListPage /></MemoryRouter>)
    const closedRow = (await screen.findByText('21089961')).closest('tr')!
    expect(within(closedRow).getByText('已完成')).toBeInTheDocument()
    await user.click(within(closedRow).getByRole('checkbox'))
    expect(screen.queryByRole('button', { name: /发送邮件/ })).toBeNull()
    expect(screen.queryByRole('button', { name: '标记完成' })).toBeNull()
    await user.click(screen.getByRole('button', { name: '重新开启 (1)' }))
    await waitFor(() => expect(put).toHaveBeenCalledWith('/outsource/items/completion', {
      items: [{ id: 2, expectedUpdatedAt: '2026-11-10T01:02:03.456Z' }], completed: false,
    }))
  })

  it('汇入模板与提交带外发类型，提示里写明要填类型', async () => {
    const user = userEvent.setup()
    setSession(['outsource:view', 'outsource:import'])
    mockApi()
    const post = vi.spyOn(http, 'post').mockResolvedValue({ data: { ready: 1, skipped: 0, invalid: 0, rows: [{
      rowNumber: 2, workOrderNo: '21089901', status: 'READY', message: null, vendorName: '鼎亚', outsourceDate: '2026-11-04',
      customer: '超穎(泰國)', machineModel: 'ST-201L', typeLabel: 'OEMA【进料+制造】', quantity: 1, latestDueDate: '2026-12-31',
    }] } } as never)
    const download = vi.spyOn(workbook, 'downloadExcelWorkbook').mockResolvedValue(undefined)
    render(<MemoryRouter><OutsourceListPage /></MemoryRouter>)

    await screen.findByText('21089901') // the toolbar keeps changing until the list has loaded
    await user.click(screen.getByRole('button', { name: '汇入' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByText(/填写外发日期、工令、外发厂商和外发类型/)).toBeInTheDocument()
    await user.click(within(dialog).getByRole('button', { name: '下载模板' }))
    await waitFor(() => expect(download).toHaveBeenCalled())
    expect(download.mock.calls[0][1][0].headers).toEqual(['外发日期', '工令', '外发厂商', '外发类型'])

    const buffer = await workbook.createExcelWorkbook([{ name: '数据', headers: ['外发日期', '工令', '外发厂商', '外发类型'], rows: [['2026-11-04', '21089901', '鼎亚', 'OEMA']] }])
    await user.upload(within(dialog).getByLabelText('选择 Excel 文件') as HTMLInputElement, new File([buffer], 'items.xlsx'))
    await waitFor(() => expect(post).toHaveBeenCalledWith('/outsource/items/import/preview', {
      rows: [{ rowNumber: 2, outsourceDate: '2026-11-04', workOrderNo: '21089901', vendorName: '鼎亚', typeName: 'OEMA' }],
    }))
  })

  it('显示最近一次 IPMS 同步，采购可立即同步所选记录', async () => {
    const user = userEvent.setup()
    setSession(['outsource:view', 'outsource:manage'])
    mockApi()
    const post = vi.spyOn(http, 'post').mockResolvedValue({ data: { ...syncStatus, lastProcessed: 1, lastChanged: 1 } } as never)
    render(<MemoryRouter><OutsourceListPage /></MemoryRouter>)

    expect(await screen.findByText(`IPMS 同步 ${fmtTime(syncStatus.lastSuccessAt).slice(5, 16)}`)).toBeInTheDocument()
    const row = (await screen.findByText('21089901')).closest('tr')!
    await user.click(within(row).getByRole('checkbox'))
    await user.click(screen.getByRole('button', { name: /同步所选 \(1\)/ }))
    await waitFor(() => expect(post).toHaveBeenCalledWith('/outsource/items/sync', { itemIds: [1] }))
  })
})
