import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import http from '../../api/client'
import { normalizeImportDate } from '../../outsource/importDate'
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
const types = [{ id: 5, code: 'OEMA', name: '进料+制造', status: 'ACTIVE', sortNo: 1, vendorIds: [1] }]

function mockApi() {
  return vi.spyOn(http, 'get').mockImplementation((url: string) => Promise.resolve({
    data: url === '/outsource/items' ? { list: items, total: 2, page: 1, pageSize: 20 }
      : url === '/outsource/items/summary' ? { total: 2, vendors: [{ vendorId: 1, count: 1 }, { vendorId: 2, count: 1 }] }
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

  it('总表与厂商分页显示笔数，暂停整行标红，类型与厂商不符时提示', async () => {
    setSession(['outsource:view'])
    const get = mockApi()
    render(<MemoryRouter><OutsourceListPage /></MemoryRouter>)

    expect(await screen.findByRole('tab', { name: '总表 (2)' })).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: '鼎亚 (1)' })).toBeInTheDocument()
    const paused = (await screen.findByText('21089901')).closest('tr')!
    expect(paused).toHaveClass('outsource-row--paused')
    const mismatch = screen.getByText('21089961').closest('tr')!
    expect(within(mismatch).getByText('不符')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /汇入/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /编辑工令/ })).not.toBeInTheDocument()

    await userEvent.setup().click(screen.getByRole('tab', { name: '元控 (1)' }))
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
      outsourceDate: '2026-11-04', vendorId: 1, status: 'PAUSED', remark: '', expectedUpdatedAt: '2026-11-10T01:02:03.456Z',
    }))
  })
})
