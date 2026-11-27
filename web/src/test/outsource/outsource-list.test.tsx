import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import * as XLSX from 'xlsx'
import http from '../../api/client'
import { fmtTime } from '../../api/types'
import { normalizeImportDate } from '../../outsource/importDate'
import * as workbook from '../../utils/excelWorkbook'
import * as downloads from '../../utils/saveBlob'
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
  outsourceDate: '2026-11-04', customer: '超穎(泰國)', machineModel: 'ST-201L', supervisor: '王家琦',
  mechanicalOwner: null, electricalOwner: null, workOrderIssuedOn: '2026-09-30', bomOn: null, drawingReleasedOn: null,
  quantity: 1, originalDueDate: '2026-12-31', latestDueDate: '2026-12-31', remark: null, closed: false,
  ipmsSyncedAt: '2026-11-10T00:00:00Z', electricalHandoff: false, electricalHandoffAt: null, electricalHandoffSigner: null,
}
const items = [
  { ...base, id: 1, serialNo: 71, workOrderNo: '21089901', ipmsSyncedAt: null, vendorId: 1, vendorName: '鼎亚', typeCode: 'OEMA', typeId: 5, typeLabel: 'OEMA【进料+制造】',
    status: 'PAUSED', sendStatus: 'UNSENT', matchState: 'OK', updatedAt: '2026-11-10T01:02:03.456Z' },
  { ...base, id: 2, serialNo: 69, workOrderNo: '21089961', vendorId: 2, vendorName: '元控', typeCode: 'OEMA', typeId: 5, typeLabel: 'OEMA【进料+制造】',
    status: 'NORMAL', sendStatus: 'SENT', matchState: 'VENDOR_MISMATCH', updatedAt: '2026-11-10T01:02:03.456Z',
    electricalHandoff: true, electricalHandoffAt: '2026-11-24T01:02:03Z', electricalHandoffSigner: '王小明' },
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

const FIELD_ORDER: [string, string, string, boolean][] = [
  ['outsourceDate', '外发日期', 'PROCESS', true], ['status', '状态', 'PROCESS', true], ['electricalHandoff', '转迅得研发电控设计', 'FIXED', false],
  ['workOrderNo', '工令', 'FIXED', false],
  ['customer', '客户', 'IPMS', false], ['machineModel', '机型', 'IPMS', false], ['type', '外发类型', 'PROCESS', true],
  ['vendor', '外发厂商', 'PROCESS', true], ['supervisor', '机构课别', 'IPMS', false], ['mechanicalOwner', '机构执行人', 'IPMS', false],
  ['electricalOwner', '电控执行人', 'IPMS', false], ['workOrderIssuedOn', '工令下发', 'IPMS', false], ['bomOn', 'BOM时间', 'IMPORTED', false],
  ['drawingReleasedOn', '发图', 'IMPORTED', false], ['originalDueDate', '原始交期', 'IPMS', false], ['latestDueDate', '最新交期', 'PROCESS', true],
  ['quantity', '数量', 'IPMS', false], ['remark', '备注', 'MANUAL', true],
]
/** The default columns (外协清单契约 → 栏位设定): today's built-in columns, each one an editor may fill in marked canEdit. */
const fieldLayout = {
  fields: FIELD_ORDER.map(([key, label, category, canEdit]) => ({
    key, label, dataType: key === 'electricalHandoff' ? 'BOOLEAN' : 'TEXT', options: [] as string[], category,
    pinned: ['outsourceDate', 'status', 'electricalHandoff', 'workOrderNo'].includes(key),
    vendorVisible: true, vendorEditable: false, canEdit,
  })),
}

/** The same columns for an account that fills none of them in. */
const readOnlyLayout = { fields: fieldLayout.fields.map((field) => ({ ...field, canEdit: false })) }

function mockApi(layout: typeof fieldLayout = fieldLayout, total = items.length, list = items) {
  return vi.spyOn(http, 'get').mockImplementation(async (url: string, config) => {
    if (url === '/outsource/items') {
      const { vendorId, page = 1 } = (config?.params ?? {}) as { vendorId?: number; page?: number }
      const visible = vendorId ? list.filter(item => item.vendorId === vendorId) : list
      return { data: { list: visible, total: vendorId ? visible.length : total, page, pageSize: 100 } } as never
    }
    return { data: url === '/outsource/items/export' ? new Blob()
      : url === '/outsource/fields' ? layout : url === '/outsource/sync-status' ? syncStatus
      : url.endsWith('/customers') ? [{ fullName: '华东元件有限公司', shortName: '华东' }]
      : url.endsWith('/vendors') ? vendors : url.endsWith('/types') ? types : [],
    } as never
  })
}

// Arco disables pointer events during popup entrance animations.
async function readyForPointer<T extends HTMLElement>(element: T) {
  await waitFor(() => expect(getComputedStyle(element).pointerEvents).not.toBe('none'))
  return element
}

async function openWorkOrderImport(user: ReturnType<typeof userEvent.setup>, orders: string[]) {
  setSession(['outsource:manage'])
  mockApi()
  render(<MemoryRouter><OutsourceListPage /></MemoryRouter>)
  await screen.findByText('21089901')
  await user.click(screen.getByRole('button', { name: '导入' }))
  const dialog = within(await screen.findByRole('dialog', { name: '汇入外发工令' }))
  const bytes = await workbook.createExcelWorkbook([{
    name: '数据', headers: ['外发日期', '工令', '外发类型', '外发厂商', '备注'],
    rows: orders.map(order => ['2026-11-27', order, 'OEMA', '鼎亚', '已复核']),
  }])
  await user.upload(dialog.getByLabelText('选择 Excel 文件'), new File([bytes], '更新工令.xlsx'))
  return dialog
}

describe('外发工令页面', () => {
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

  it('没有任何外协权限、但被指定填写某栏的人可以编辑，不能汇入、删除、标记完成或同步', async () => {
    const user = userEvent.setup()
    setSession([])
    mockApi({ fields: readOnlyLayout.fields.map((field) => field.key === 'remark' ? { ...field, canEdit: true } : field) })
    render(<MemoryRouter><OutsourceListPage /></MemoryRouter>)

    expect(await screen.findByRole('button', { name: '编辑工令 21089901' })).toBeEnabled()
    await user.click(within(screen.getByText('21089901').closest('tr')!).getByRole('checkbox'))
    expect(screen.getByRole('button', { name: /批量修改 \(1\)/ })).toBeInTheDocument()
    for (const name of ['导入', '删除', '标记完成', /立即同步|同步所选/, /发送邮件/]) {
      expect(screen.queryByRole('button', { name })).not.toBeInTheDocument()
    }
  })

  it('按厂商页签筛选并导出，暂停整行标红，类型与厂商不符时提示', async () => {
    setSession([])
    const get = mockApi(readOnlyLayout)
    render(<MemoryRouter><OutsourceListPage /></MemoryRouter>)

    const paused = (await screen.findByText('21089901')).closest('tr')!
    expect(paused).toHaveClass('outsource-row--paused')
    const mismatch = screen.getByText('21089961').closest('tr')!
    expect(paused.cells[0]).toHaveTextContent(/^1$/)
    expect(mismatch.cells[0]).toHaveTextContent(/^2$/)
    expect(within(mismatch).getByText('不符')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /^导入$/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /编辑工令/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '重置筛选' })).not.toBeInTheDocument()

    expect(screen.getAllByRole('tab').map((tab) => tab.textContent)).toEqual(['全部', '鼎亚', '元控'])
    expect(screen.getByRole('tab', { name: '全部', selected: true })).toBeVisible()
    vi.spyOn(downloads, 'saveBlob').mockImplementation(() => undefined)
    expect(get).not.toHaveBeenCalledWith('/outsource/items/summary', expect.anything())
    const user = userEvent.setup()
    await user.click(screen.getByRole('tab', { name: '元控' }))
    await waitFor(() => expect(screen.queryByText('21089901')).not.toBeInTheDocument())
    expect(screen.getByText('21089961').closest('tr')!.cells[0]).toHaveTextContent(/^1$/)
    await waitFor(() => expect(get).toHaveBeenCalledWith('/outsource/items', expect.objectContaining({
      params: expect.objectContaining({ vendorId: 2, page: 1 }),
    })))
    await user.click(screen.getByRole('button', { name: '导出' }))
    expect(get).toHaveBeenCalledWith('/outsource/items/export', expect.objectContaining({
      params: expect.objectContaining({ vendorId: 2 }),
    }))
    get.mockClear()
    await user.click(screen.getByRole('tab', { name: '全部' }))
    await waitFor(() => expect(get).toHaveBeenCalledWith('/outsource/items', expect.objectContaining({
      params: expect.objectContaining({ vendorId: undefined, page: 1 }),
    })))
    expect(screen.queryByRole('button', { name: '重置筛选' })).not.toBeInTheDocument()
  })

  it('仅在未完成且尚未同步的工令号旁显示待同步提示', async () => {
    const user = userEvent.setup()
    setSession([])
    const closed = { ...items[0], id: 3, workOrderNo: '21089962', closed: true }
    mockApi(readOnlyLayout, 3, [...items, closed])
    render(<MemoryRouter><OutsourceListPage /></MemoryRouter>)

    const cell = (order: string) => within(screen.getByText(order).closest('td')!)
    await screen.findByText('21089901')
    const tag = cell('21089901').getByRole('img', { name: '待同步' })
    expect(tag).toBeVisible()
    expect(cell('21089961').queryByRole('img', { name: '待同步' })).not.toBeInTheDocument()
    expect(cell(closed.workOrderNo).queryByRole('img', { name: '待同步' })).not.toBeInTheDocument()
    await user.hover(tag)
    expect(await screen.findByText('IPMS 尚无此工令资料；若长期不消失，请确认工令号是否正确')).toBeVisible()
  })

  it('工令表头可组合多个工令与同步状态，筛选回到第一页，导出和重置复用筛选条件', async () => {
    const user = userEvent.setup()
    setSession([])
    const get = mockApi(readOnlyLayout, 101)
    const saveBlob = vi.spyOn(downloads, 'saveBlob').mockImplementation(() => undefined)
    render(<MemoryRouter><OutsourceListPage /></MemoryRouter>)

    const title = await screen.findByText('工令', { selector: '.arco-table-th-item-title' })
    const filter = title.closest('th')!.querySelector<HTMLElement>('.arco-table-filters')!
    expect(filter).toBeInTheDocument()
    expect(filter).not.toHaveClass('arco-table-filters-active')
    await user.click(await screen.findByText('2', { selector: '.arco-pagination-item' }))
    await waitFor(() => expect(get).toHaveBeenCalledWith('/outsource/items', expect.objectContaining({
      params: expect.objectContaining({ page: 2 }),
    })))
    expect(screen.getByText('21089901').closest('tr')!.cells[0]).toHaveTextContent(/^101$/)
    get.mockClear()

    await user.click(filter)
    await user.click(await readyForPointer(await screen.findByRole('combobox', { name: '同步状态' })))
    await user.click(await readyForPointer(await screen.findByRole('option', { name: '尚未同步' })))
    await user.click(screen.getByRole('button', { name: /^筛选$/ }))
    const columnFilters = JSON.stringify({ ipmsSyncStatus: 'unsynced' })
    await waitFor(() => expect(get).toHaveBeenCalledWith('/outsource/items', expect.objectContaining({
      params: expect.objectContaining({ workOrders: undefined, columnFilters, page: 1 }),
    })))
    expect(filter).toHaveClass('arco-table-filters-active')
    expect(screen.getByText('21089901').closest('tr')!.cells[0]).toHaveTextContent(/^1$/)

    await user.click(filter)
    const input = await screen.findByPlaceholderText('筛选工令，可粘贴多个')
    await waitFor(() => expect(getComputedStyle(input).pointerEvents).not.toBe('none'))
    await user.click(input)
    const workOrders = '21089901\n21089961'
    await user.paste(workOrders)
    await user.click(screen.getByRole('button', { name: /^筛选$/ }))
    await waitFor(() => expect(get).toHaveBeenCalledWith('/outsource/items', expect.objectContaining({
      params: expect.objectContaining({ workOrders, columnFilters, page: 1 }),
    })))
    expect(filter).toHaveClass('arco-table-filters-active')

    await user.click(screen.getByRole('button', { name: /^导出$/ }))
    await waitFor(() => expect(saveBlob).toHaveBeenCalled())
    expect(get).toHaveBeenCalledWith('/outsource/items/export', expect.objectContaining({
      params: expect.objectContaining({ workOrders, columnFilters }), responseType: 'blob',
    }))

    get.mockClear()
    await user.click(screen.getByRole('button', { name: '重置筛选' }))
    await waitFor(() => expect(get).toHaveBeenCalledWith('/outsource/items', expect.objectContaining({
      params: expect.objectContaining({ workOrders: undefined, columnFilters: undefined, page: 1 }),
    })))
    expect(filter).not.toHaveClass('arco-table-filters-active')
    expect(screen.queryByRole('button', { name: '重置筛选' })).not.toBeInTheDocument()
  })

  it('数据列表头均可筛选，厂商与页签同步，自定义选项保留空格且导出复用组合条件', async () => {
    const user = userEvent.setup()
    setSession(['outsource:manage'])
    const sample = readOnlyLayout.fields[0]
    const get = mockApi({ fields: [...readOnlyLayout.fields,
      { ...sample, key: 'files', label: '档案', pinned: false },
      { ...sample, key: 'custom_phase', label: '阶段', category: 'CUSTOM', dataType: 'SELECT', options: ['Stage A', 'Stage B'], pinned: false },
    ] })
    vi.spyOn(downloads, 'saveBlob').mockImplementation(() => undefined)
    render(<MemoryRouter><OutsourceListPage /></MemoryRouter>)
    const row = (await screen.findByText('21089901')).closest('tr')!
    await user.click(within(row).getByRole('checkbox'))
    const filter = (label: string) => screen.getByText(label, { selector: '.arco-table-th-item-title' }).closest('th')!
      .querySelector<HTMLElement>('.arco-table-filters')!
    for (const label of [...readOnlyLayout.fields.map(field => field.label), '档案', '阶段', '发送状态', '导入人', '导入时间', '修改人', '修改时间']) {
      expect(filter(label)).toBeInTheDocument()
    }
    expect(filter('操作')).toBeNull()
    expect(filter('序号')).toBeNull()
    expect(screen.queryByPlaceholderText('工令、客户或机型')).not.toBeInTheDocument()
    for (const label of ['外发类型', '状态', '发送状态']) {
      expect(screen.queryByRole('combobox', { name: label })).not.toBeInTheDocument()
    }
    expect(screen.queryByPlaceholderText('外发日期起')).not.toBeInTheDocument()
    expect(screen.queryByPlaceholderText('外发日期止')).not.toBeInTheDocument()

    await user.click(filter('外发厂商'))
    await user.click(await readyForPointer(await screen.findByRole('combobox', { name: '筛选外发厂商' })))
    await user.click(await readyForPointer(await screen.findByRole('option', { name: '元控' })))
    await user.click(screen.getByRole('button', { name: /^筛选$/ }))
    expect(await screen.findByRole('tab', { name: '元控', selected: true })).toBeVisible()
    expect(within(row).getByRole('checkbox')).not.toBeChecked()

    await user.click(filter('阶段'))
    await user.click(await readyForPointer(await screen.findByRole('combobox', { name: '筛选阶段' })))
    await user.click(await readyForPointer(await screen.findByRole('option', { name: 'Stage A' })))
    await user.click(screen.getByRole('button', { name: /^筛选$/ }))
    const conditions = { vendorId: 2, columnFilters: JSON.stringify({ custom_phase: 'Stage A' }) }
    await waitFor(() => expect(get).toHaveBeenCalledWith('/outsource/items', expect.objectContaining({
      params: expect.objectContaining({ ...conditions, page: 1 }),
    })))
    await user.click(screen.getByRole('button', { name: /^导出$/ }))
    expect(get).toHaveBeenCalledWith('/outsource/items/export', expect.objectContaining({ params: expect.objectContaining(conditions) }))
    await user.click(screen.getByRole('button', { name: '重置筛选' }))
    expect(screen.getByRole('tab', { name: '全部', selected: true })).toBeVisible()
    expect(filter('阶段')).not.toHaveClass('arco-table-filters-active')
    await user.click(filter('外发日期'))
    expect(await screen.findByPlaceholderText('开始日期')).toBeVisible()
    expect(screen.getByPlaceholderText('结束日期')).toBeVisible()
  })

  it('客户表头接受简称字典和自由关键字，审计日期按显示时区筛选', async () => {
    const user = userEvent.setup()
    setSession([])
    const get = mockApi(readOnlyLayout)
    render(<MemoryRouter><OutsourceListPage /></MemoryRouter>)
    await screen.findByText('21089901')
    const openFilter = (label: string) => user.click(screen.getByText(label, { selector: '.arco-table-th-item-title' })
      .closest('th')!.querySelector<HTMLElement>('.arco-table-filters')!)
    const apply = () => user.click(screen.getByRole('button', { name: /^筛选$/ }))
    await openFilter('客户')
    const input = await readyForPointer(await screen.findByPlaceholderText('输入或选择客户'))
    await user.type(input, '华东')
    await user.click(await readyForPointer(await screen.findByRole('option', { name: '华东（华东元件有限公司）' })))
    await apply()
    await waitFor(() => expect(get).toHaveBeenCalledWith('/outsource/items', expect.objectContaining({
      params: expect.objectContaining({ columnFilters: JSON.stringify({ customer: '华东元件有限公司' }) }),
    })))
    await openFilter('客户')
    const freeText = await readyForPointer(await screen.findByPlaceholderText('输入或选择客户'))
    await user.clear(freeText)
    await user.type(freeText, '未建档')
    await apply()
    await openFilter('修改时间')
    const day = await readyForPointer(await screen.findByPlaceholderText('选择日期'))
    await user.click(day)
    fireEvent.change(day, { target: { value: '2026-11-25' } })
    fireEvent.keyDown(day, { key: 'Enter', keyCode: 13 })
    await apply()
    await waitFor(() => expect(get).toHaveBeenCalledWith('/outsource/items', expect.objectContaining({
      params: expect.objectContaining({
        columnFilters: JSON.stringify({ customer: '未建档', modifiedAt: '2026-11-25' }),
        columnFilterTimeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
      }),
    })))
  })

  it('采购编辑时带上版本，厂商只能选该类型允许的厂商', async () => {
    const user = userEvent.setup()
    setSession(['outsource:manage'])
    mockApi()
    const put = vi.spyOn(http, 'put').mockResolvedValue({ data: items[0] } as never)
    render(<MemoryRouter><OutsourceListPage /></MemoryRouter>)

    await user.click(await screen.findByRole('button', { name: '编辑工令 21089901' }))
    const dialog = await screen.findByRole('dialog')
    await user.click(within(dialog).getByRole('button', { name: '保存' }))
    await waitFor(() => expect(put).toHaveBeenCalledWith('/outsource/items/1', {
      vendorId: 1, typeId: 5, status: 'PAUSED', remark: '', expectedUpdatedAt: '2026-11-10T01:02:03.456Z',
    }))
  })

  it('转迅得电控列紧跟状态，显示签名与时间，管理员也没有编辑入口', async () => {
    const user = userEvent.setup()
    setSession(['outsource:manage', 'outsource:settings'])
    useAuth.setState({ user: { ...useAuth.getState().user!, isSystemAdmin: true } })
    mockApi()
    render(<MemoryRouter><OutsourceListPage /></MemoryRouter>)

    const handoff = await screen.findByRole('columnheader', { name: /转迅得研发电控设计/ })
    expect(handoff.previousElementSibling).toHaveTextContent('状态')
    expect(handoff.nextElementSibling).toHaveTextContent('工令')
    const first = within(screen.getByText('21089901').closest('tr')!)
    const second = within(screen.getByText('21089961').closest('tr')!)
    expect(first.getByText('否')).toBeInTheDocument()
    expect(first.getByText('否').closest('td')).not.toHaveClass('outsource-cell-handed-off')
    expect(second.getByText('是').closest('td')).toHaveClass('outsource-cell-handed-off')
    await user.hover(second.getByText('是'))
    expect(await screen.findByText(/签名：王小明.*时间：/)).toHaveTextContent(fmtTime(items[1].electricalHandoffAt))

    await user.click(first.getByRole('button', { name: '编辑工令 21089901' }))
    const dialog = await screen.findByRole('dialog', { name: /编辑工令/ })
    expect(within(dialog).queryByText('转迅得研发电控设计')).not.toBeInTheDocument()
    await user.click(within(dialog).getByRole('button', { name: '取消' }))
    await user.click(first.getByRole('checkbox'))
    await user.click(screen.getByRole('button', { name: /批量修改/ }))
    const batch = await screen.findByRole('dialog', { name: /批量修改/ })
    expect(within(batch).queryByText('转迅得研发电控设计')).not.toBeInTheDocument()
  })

  it('编辑时可改外发类型，厂商下拉随所选类型过滤，原厂商不适用时要重新选择', async () => {
    const user = userEvent.setup()
    setSession(['outsource:manage'])
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
    fireEvent.click(await readyForPointer(await screen.findByRole('option', { name: '元控' })))
    await user.click(within(dialog).getByRole('button', { name: '保存' }))
    await waitFor(() => expect(put).toHaveBeenCalledWith('/outsource/items/1', {
      vendorId: 2, typeId: 6, status: 'PAUSED', remark: '', expectedUpdatedAt: '2026-11-10T01:02:03.456Z',
    }))
  })

  it('类型不在字典中的记录要先选择外发类型才能保存', async () => {
    const user = userEvent.setup()
    setSession(['outsource:manage'])
    vi.spyOn(http, 'get').mockImplementation((url: string) => Promise.resolve({
      data: url === '/outsource/items'
        ? { list: [{ ...items[0], typeId: null, typeLabel: 'XYZ', typeCode: 'XYZ', matchState: 'UNKNOWN_TYPE' }], total: 1, page: 1, pageSize: 20 }
          : url === '/outsource/fields' ? fieldLayout : url === '/outsource/sync-status' ? syncStatus : url.endsWith('/customers') ? [{ fullName: '华东元件有限公司', shortName: '华东' }]
          : url.endsWith('/vendors') ? vendors : url.endsWith('/types') ? types : [],
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
    setSession(['outsource:manage'])
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
      vendorId: undefined, typeId: 5, status: undefined, remark: undefined,
    }))
  })

  it('勾选后可标记已完成；已完成的记录只能重新开启', async () => {
    const user = userEvent.setup()
    setSession(['outsource:manage'])
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
          : url === '/outsource/fields' ? fieldLayout : url === '/outsource/sync-status' ? syncStatus : url.endsWith('/customers') ? [{ fullName: '华东元件有限公司', shortName: '华东' }]
          : url.endsWith('/vendors') ? vendors : url.endsWith('/types') ? types : [],
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

  it('旧四列表可跳过范例；IPMS 暂无资料的工令可预览并确认导入', async () => {
    const user = userEvent.setup()
    setSession(['outsource:manage'])
    mockApi()
    const pending = 'IPMS 暂无资料，可先导入，后续同步补齐'
    const post = vi.spyOn(http, 'post').mockResolvedValueOnce({ data: { ready: 1, skipped: 0, invalid: 0, rows: [{
      rowNumber: 3, workOrderNo: '21089901', status: 'READY', message: pending, vendorName: '鼎亚', outsourceDate: '2026-11-04',
      customer: null, machineModel: null, typeLabel: 'OEMA【进料+制造】', quantity: null, latestDueDate: null,
    }] } } as never).mockResolvedValueOnce({ data: { created: 1, skipped: 0, failed: 0, rows: [
      { rowNumber: 3, status: 'CREATED' },
    ] } } as never)
    render(<MemoryRouter><OutsourceListPage /></MemoryRouter>)

    await screen.findByText('21089901') // the toolbar keeps changing until the list has loaded
    await user.click(screen.getByRole('button', { name: '导入' }))
    const dialog = await screen.findByRole('dialog')
    const buffer = await workbook.createExcelWorkbook([{
      name: '数据', headers: ['外发日期', '工令', '外发类型', '外发厂商'],
      rows: [['2026-11-23', '21000001', 'OEMA', '示例厂商'], ['2026-11-04', '21089901', 'OEMA', '鼎亚']],
    }])
    await user.upload(within(dialog).getByLabelText('选择 Excel 文件') as HTMLInputElement, new File([buffer], 'items.xlsx'))
    await waitFor(() => expect(post).toHaveBeenCalledWith('/outsource/items/import/preview', {
      rows: [{ rowNumber: 3, outsourceDate: '2026-11-04', workOrderNo: '21089901', vendorName: '鼎亚', typeName: 'OEMA' }],
    }))
    expect(await within(dialog).findByText(pending)).toBeVisible()
    expect(within(dialog).getByText('已忽略范例行')).toBeVisible()
    expect(post).toHaveBeenCalledTimes(1)
    const confirm = within(dialog).getByRole('button', { name: '确认导入' })
    expect(confirm).toBeEnabled()
    await user.click(confirm)
    expect(post).toHaveBeenCalledWith('/outsource/items/import', post.mock.calls[0][1])
  })

  it('下载的全列模板可填自定义和选填资料，按服务端最终值预览，确认后才提交', async () => {
    const user = userEvent.setup()
    setSession(['outsource:manage', 'outsource:date_import'])
    const layout = { fields: [...fieldLayout.fields,
      { ...fieldLayout.fields[0], key: 'files', label: '档案', category: 'FIXED', canEdit: false },
      { ...fieldLayout.fields[0], key: 'custom_stage', label: '加工阶段', category: 'CUSTOM', dataType: 'SELECT', options: ['一阶段'] },
    ] }
    mockApi(layout)
    const save = vi.spyOn(downloads, 'saveBlob').mockImplementation(() => undefined)
    const post = vi.spyOn(http, 'post').mockResolvedValueOnce({ data: { ready: 1, skipped: 0, invalid: 0, rows: [{
      rowNumber: 4, workOrderNo: 'WO-FULL', status: 'READY', vendorName: '鼎亚', outsourceDate: '2026-11-04',
      typeLabel: 'OEMA【进料+制造】', customer: 'IPMS客户', machineModel: 'Excel机型', latestDueDate: null, quantity: null,
      fieldValues: { customer: 'IPMS客户', machineModel: 'Excel机型', custom_stage: '一阶段', remark: '留样', bomOn: '2026-11-08' },
    }] } } as never).mockResolvedValueOnce({ data: { created: 1, skipped: 0, failed: 0, rows: [
      { rowNumber: 4, status: 'CREATED' },
    ] } } as never)
    render(<MemoryRouter><OutsourceListPage /></MemoryRouter>)

    await screen.findByText('21089901')
    await user.click(screen.getByRole('button', { name: '导入' }))
    const dialog = await screen.findByRole('dialog', { name: '汇入外发工令' })
    await user.click(within(dialog).getByRole('button', { name: '下载模板' }))
    await waitFor(() => expect(save).toHaveBeenCalledWith(expect.any(Blob), '外发工令汇入模板.xlsx'))
    const book = XLSX.read(await save.mock.calls[0][0].arrayBuffer(), { type: 'array' })
    const sheet = book.Sheets['数据']
    const [headers] = XLSX.utils.sheet_to_json<string[]>(sheet, { header: 1 })
    expect(headers).toEqual(['序号', ...layout.fields.map(field => field.label).filter(label => !['状态', '转迅得研发电控设计', '档案'].includes(label))])
    const rules = XLSX.utils.sheet_to_json<string[]>(book.Sheets['说明'], { header: 1 })
    expect(rules.slice(1).map(([label]) => label)).toEqual(headers)
    expect(rules.filter(([, rule]) => rule?.includes('必填')).map(([label]) => label).sort()).toEqual(
      ['外发日期', '工令', '外发类型', '外发厂商'].sort(),
    )
    const values: Record<string, string> = {
      外发日期: '2026-11-04', 工令: 'WO-FULL', 外发类型: 'OEMA', 外发厂商: '鼎亚',
      客户: 'Excel客户', 机型: 'Excel机型', 备注: '留样', 加工阶段: '一阶段', BOM时间: '2026-11-08',
      序号: '239', 档案: '2', 状态: '暂停', 转迅得研发电控设计: '是', 发送状态: '已发送', 导入人: '旧导入人',
      导入时间: '2026-11-03', 修改人: '旧修改人', 修改时间: '2026-11-04',
    }
    const previousHeaders = [...headers, '状态', '转迅得研发电控设计', '档案', '发送状态', '导入人', '导入时间', '修改人', '修改时间']
    XLSX.utils.sheet_add_aoa(sheet, [previousHeaders], { origin: 'A1' })
    XLSX.utils.sheet_add_aoa(sheet, [previousHeaders.map(header => values[header] ?? '')], { origin: 'A4' })
    const bytes = XLSX.write(book, { type: 'array', bookType: 'xlsx' }) as ArrayBuffer
    await user.upload(within(dialog).getByLabelText('选择 Excel 文件'), new File([bytes], '实际填写的全列模板.xlsx'))

    const payload = { rows: [{ rowNumber: 4, workOrderNo: 'WO-FULL', outsourceDate: '2026-11-04', typeName: 'OEMA', vendorName: '鼎亚',
      fieldValues: { customer: 'Excel客户', machineModel: 'Excel机型', remark: '留样', custom_stage: '一阶段', bomOn: '2026-11-08' },
    }] }
    await waitFor(() => expect(post).toHaveBeenCalledWith('/outsource/items/import/preview', payload))
    const preview = (await within(dialog).findByText('WO-FULL')).closest('tr')!
    expect(preview).toHaveTextContent('IPMS客户')
    expect(preview).not.toHaveTextContent('Excel客户')
    expect(preview).toHaveTextContent('Excel机型')
    expect(preview).toHaveTextContent('一阶段')
    expect(post).toHaveBeenCalledTimes(1)
    await user.click(within(dialog).getByRole('button', { name: '确认导入' }))
    await waitFor(() => expect(post).toHaveBeenCalledWith('/outsource/items/import', payload))
  })

  it('缺少必填表头时不能预览，填写无权导入的日期列时保留源行和错误原因', async () => {
    const user = userEvent.setup()
    setSession(['outsource:manage'])
    mockApi()
    const message = '没有导入BOM时间与发图的权限'
    const post = vi.spyOn(http, 'post').mockResolvedValue({ data: { ready: 0, skipped: 0, invalid: 1, rows: [
      { rowNumber: 2, workOrderNo: 'WO-DATE', status: 'INVALID', message },
    ] } } as never)
    render(<MemoryRouter><OutsourceListPage /></MemoryRouter>)
    await screen.findByText('21089901')
    await user.click(screen.getByRole('button', { name: '导入' }))
    const dialog = within(await screen.findByRole('dialog'))
    const upload = async (headers: string[], values: string[]) => {
      const bytes = await workbook.createExcelWorkbook([{ name: '数据', headers, rows: [values] }])
      await user.upload(dialog.getByLabelText('选择 Excel 文件'), new File([bytes], '部分列.xlsx'))
    }
    await upload(['工令', '外发日期', '外发厂商', '备注'], ['WO-DATE', '2026-11-04', '鼎亚', ''])
    expect(await dialog.findByRole('alert')).toHaveTextContent('外发类型')
    expect(dialog.getByRole('button', { name: '确认导入' })).toBeDisabled()
    expect(post).not.toHaveBeenCalled()

    await upload(['工令', '发图', '外发厂商', '外发类型', '外发日期'], ['WO-DATE', '2026-11-08', '鼎亚', 'OEMA', '2026-11-04'])
    expect(await dialog.findByText(message)).toBeVisible()
    expect(post).toHaveBeenCalledWith('/outsource/items/import/preview', { rows: [{
      rowNumber: 2, workOrderNo: 'WO-DATE', vendorName: '鼎亚', typeName: 'OEMA', outsourceDate: '2026-11-04',
      fieldValues: { drawingReleasedOn: '2026-11-08' },
    }] })
    expect(post).toHaveBeenCalledTimes(1)
    expect(dialog.getByRole('button', { name: '确认导入' })).toBeDisabled()
  })

  it('全为已有工令时可确认更新，每行绑定预览时的工令 ID、版本和变更摘要', async () => {
    const user = userEvent.setup()
    const firstVersion = '2026-11-27T01:02:03.123Z'
    const secondVersion = '2026-11-27T02:03:04.456Z'
    const firstHash = '1a'.repeat(32)
    const secondHash = '2b'.repeat(32)
    const post = vi.spyOn(http, 'post').mockResolvedValueOnce({ data: {
      ready: 2, skipped: 0, invalid: 0, rows: [
        { rowNumber: 3, workOrderNo: 'WO-2', status: 'READY_UPDATE', expectedItemId: 7002, expectedUpdatedAt: secondVersion, expectedChangeHash: secondHash,
          changedFields: ['remark'], fieldValues: { remark: '已复核' } },
        { rowNumber: 2, workOrderNo: 'WO-1', status: 'READY_UPDATE', expectedItemId: 7001, expectedUpdatedAt: firstVersion, expectedChangeHash: firstHash,
          changedFields: ['outsourceDate', 'remark'], fieldValues: { remark: '已复核' } },
      ],
    } }).mockResolvedValueOnce({ data: {
      created: 0, updated: 2, skipped: 0, failed: 0, rows: [
        { rowNumber: 2, key: 'WO-1', status: 'UPDATED' }, { rowNumber: 3, key: 'WO-2', status: 'UPDATED' },
      ],
    } })
    const dialog = await openWorkOrderImport(user, ['WO-1', 'WO-2'])
    expect(await dialog.findByText('待更新 2')).toBeVisible()
    expect(dialog.getByText('待新增 0')).toBeVisible()
    const first = (await dialog.findByText('WO-1')).closest('tr')!
    expect(within(first).getByText('待更新')).toBeVisible()
    expect(first).toHaveTextContent('外发日期')
    expect(first).toHaveTextContent('备注')
    const confirm = dialog.getByRole('button', { name: '确认导入' })
    expect(confirm).toBeEnabled()
    const initialRow = { outsourceDate: '2026-11-27', typeName: 'OEMA', vendorName: '鼎亚', fieldValues: { remark: '已复核' } }
    expect(post).toHaveBeenCalledExactlyOnceWith('/outsource/items/import/preview', { rows: [
      { ...initialRow, rowNumber: 2, workOrderNo: 'WO-1' }, { ...initialRow, rowNumber: 3, workOrderNo: 'WO-2' },
    ] })
    await user.click(confirm)
    expect(post).toHaveBeenCalledWith('/outsource/items/import', { rows: [
      { ...initialRow, rowNumber: 2, workOrderNo: 'WO-1', expectedItemId: 7001, expectedUpdatedAt: firstVersion, expectedChangeHash: firstHash },
      { ...initialRow, rowNumber: 3, workOrderNo: 'WO-2', expectedItemId: 7002, expectedUpdatedAt: secondVersion, expectedChangeHash: secondHash },
    ] })
    expect(await screen.findByText('新增 0，更新 2，跳过 0，失败 0')).toBeVisible()
  })

  it('混合导入只提交可新增、可更新的行，结果分别统计新增、更新、跳过和错误', async () => {
    const user = userEvent.setup()
    const version = '2026-11-27T01:02:03.123Z'
    const hash = '3c'.repeat(32)
    const post = vi.spyOn(http, 'post').mockResolvedValueOnce({ data: {
      ready: 2, skipped: 1, invalid: 1, rows: [
        { rowNumber: 2, workOrderNo: 'WO-NEW', status: 'READY' },
        { rowNumber: 3, workOrderNo: 'WO-EDIT', status: 'READY_UPDATE', changedFields: ['remark'], expectedItemId: 79, expectedUpdatedAt: version, expectedChangeHash: hash },
        { rowNumber: 4, workOrderNo: 'WO-SAME', status: 'SKIPPED', message: '没有变更' },
        { rowNumber: 5, workOrderNo: 'WO-CLOSED', status: 'INVALID', message: '工令已完成' },
      ],
    } }).mockResolvedValueOnce({ data: { created: 1, updated: 1, skipped: 0, failed: 0, rows: [
      { rowNumber: 2, key: 'WO-NEW', status: 'CREATED' }, { rowNumber: 3, key: 'WO-EDIT', status: 'UPDATED' },
    ] } })
    const dialog = await openWorkOrderImport(user, ['WO-NEW', 'WO-EDIT', 'WO-SAME', 'WO-CLOSED'])
    expect(await dialog.findByText('待更新 1')).toBeVisible()
    for (const count of ['待新增 1', '跳过 1', '错误 1']) expect(dialog.getByText(count)).toBeVisible()
    await user.click(dialog.getByRole('button', { name: '确认导入' }))
    const values = { outsourceDate: '2026-11-27', typeName: 'OEMA', vendorName: '鼎亚', fieldValues: { remark: '已复核' } }
    expect(post).toHaveBeenCalledWith('/outsource/items/import', { rows: [
      { ...values, rowNumber: 2, workOrderNo: 'WO-NEW' },
      { ...values, rowNumber: 3, workOrderNo: 'WO-EDIT', expectedItemId: 79, expectedUpdatedAt: version, expectedChangeHash: hash },
    ] })
    expect(await screen.findByText('新增 1，更新 1，跳过 1，失败 1')).toBeVisible()
    expect(dialog.getByText('已更新 1')).toBeVisible()
    expect(dialog.getByText('WO-EDIT').closest('tr')).toHaveTextContent('已更新')
    expect(dialog.getByText('WO-SAME').closest('tr')).toHaveTextContent('没有变更')
    expect(dialog.getByText('WO-CLOSED').closest('tr')).toHaveTextContent('工令已完成')
    expect(dialog.getByRole('button', { name: '确认导入' })).toBeDisabled()
  })

  it('版本冲突后不能重复提交；重新预览同一文件时全部无差异就只显示跳过', async () => {
    const user = userEvent.setup()
    const row = { rowNumber: 2, workOrderNo: 'WO-CONFLICT' }
    const binding = { expectedItemId: 91, expectedUpdatedAt: '2026-11-27T01:00:00.000Z', expectedChangeHash: '4d'.repeat(32) }
    const post = vi.spyOn(http, 'post').mockResolvedValueOnce({ data: { ready: 1, skipped: 0, invalid: 0, rows: [
      { ...row, status: 'READY_UPDATE', changedFields: ['remark'], ...binding },
    ] } }).mockRejectedValueOnce({ isAxiosError: true, response: { status: 409 } })
      .mockResolvedValueOnce({ data: { ready: 0, skipped: 1, invalid: 0, rows: [
        { ...row, status: 'SKIPPED', message: '没有变更' },
      ] } })
    const dialog = await openWorkOrderImport(user, ['WO-CONFLICT'])
    expect(await dialog.findByText('待更新 1')).toBeVisible()
    const confirm = dialog.getByRole('button', { name: '确认导入' })
    await user.click(confirm)
    const repreview = await dialog.findByRole('button', { name: '重新预览' })
    expect(post).toHaveBeenNthCalledWith(2, '/outsource/items/import', { rows: [expect.objectContaining({ ...row, ...binding })] })
    expect(confirm).toBeDisabled()
    await user.click(repreview)
    expect(await dialog.findByText('跳过 1')).toBeVisible()
    expect(confirm).toBeDisabled()
    expect(post).toHaveBeenNthCalledWith(3, '/outsource/items/import/preview', post.mock.calls[0][1])
    expect(post).toHaveBeenCalledTimes(3)
  })

  it('覆盖预览缺少变更摘要时禁止提交', async () => {
    const user = userEvent.setup()
    const post = vi.spyOn(http, 'post').mockResolvedValueOnce({ data: { ready: 1, skipped: 0, invalid: 0, rows: [
      { rowNumber: 2, workOrderNo: 'WO-INCOMPLETE', status: 'READY_UPDATE', changedFields: ['remark'],
        expectedItemId: 92, expectedUpdatedAt: '2026-11-27T01:00:00.000Z' },
    ] } })
    const dialog = await openWorkOrderImport(user, ['WO-INCOMPLETE'])
    expect(await dialog.findByRole('alert')).toHaveTextContent('变更摘要')
    expect(dialog.getByRole('button', { name: '确认导入' })).toBeDisabled()
    expect(post).toHaveBeenCalledTimes(1)
  })

  it('BOM 时间与发图导入模板的数据表包含一行日期范例', async () => {
    const user = userEvent.setup()
    setSession(['outsource:date_import'])
    mockApi()
    const download = vi.spyOn(workbook, 'downloadExcelWorkbook').mockResolvedValue(undefined)
    render(<MemoryRouter><OutsourceListPage /></MemoryRouter>)

    await screen.findByText('21089901')
    await user.click(screen.getByRole('button', { name: '导入BOM/发图' }))
    const dialog = await screen.findByRole('dialog', { name: '导入BOM时间与发图' })
    await user.click(within(dialog).getByRole('button', { name: '下载模板' }))
    await waitFor(() => expect(download).toHaveBeenCalledWith('BOM时间与发图导入模板.xlsx', [{
      name: '数据', headers: ['工令', 'BOM时间', '发图'], rows: [['21000001', '2026-11-24', '2026-11-25']],
    }]))
  })

  it('显示最近一次 IPMS 同步，采购可立即同步所选记录', async () => {
    const user = userEvent.setup()
    setSession(['outsource:manage'])
    mockApi()
    const post = vi.spyOn(http, 'post').mockResolvedValue({ data: { ...syncStatus, lastProcessed: 1, lastChanged: 1 } } as never)
    render(<MemoryRouter><OutsourceListPage /></MemoryRouter>)

    expect(await screen.findByText(`IPMS 同步 ${fmtTime(syncStatus.lastSuccessAt).slice(5, 16)}`)).toBeInTheDocument()
    const row = (await screen.findByText('21089901')).closest('tr')!
    await user.click(within(row).getByRole('checkbox'))
    await user.click(screen.getByRole('button', { name: /同步所选 \(1\)/ }))
    await waitFor(() => expect(post).toHaveBeenCalledWith('/outsource/items/sync', { itemIds: [1] }))
  })

  it('同时勾选未完成与已完成记录时，两类批量操作分别按各自集合生效，批量修改按类型过滤厂商', async () => {
    const user = userEvent.setup()
    setSession(['outsource:manage', 'outsource:send'])
    vi.spyOn(http, 'get').mockImplementation((url: string) => Promise.resolve({
      data: url === '/outsource/items' ? { list: [items[0], { ...items[1], closed: true }], total: 2, page: 1, pageSize: 20 }
          : url === '/outsource/fields' ? fieldLayout : url === '/outsource/sync-status' ? syncStatus : url.endsWith('/customers') ? [{ fullName: '华东元件有限公司', shortName: '华东' }]
          : url.endsWith('/vendors') ? vendors : url.endsWith('/types') ? types : [],
    } as never))
    const put = vi.spyOn(http, 'put').mockResolvedValue({ data: { items } } as never)
    render(<MemoryRouter><OutsourceListPage /></MemoryRouter>)

    const openRow = (await screen.findByText('21089901')).closest('tr')!
    const closedRow = screen.getByText('21089961').closest('tr')!
    await user.click(within(openRow).getByRole('checkbox'))
    await user.click(within(closedRow).getByRole('checkbox'))

    expect(screen.getByRole('button', { name: '重新开启 (1)' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '发送邮件 (1)' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '批量修改 (1)' })).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: '批量修改 (1)' }))
    const dialog = await screen.findByRole('dialog')
    await user.click(within(dialog).getByRole('combobox', { name: '外发厂商' }))
    fireEvent.click(await readyForPointer(await screen.findByRole('option', { name: '元控' })))
    await user.click(within(dialog).getByRole('combobox', { name: '外发类型' }))
    fireEvent.click(await screen.findByRole('option', { name: 'OEMA【进料+制造】' }))

    await user.click(within(dialog).getByRole('button', { name: '保存' }))
    await waitFor(() => expect(put).toHaveBeenCalledWith('/outsource/items/batch', {
      items: [{ id: 1, expectedUpdatedAt: '2026-11-10T01:02:03.456Z' }],
      vendorId: undefined, typeId: 5, status: undefined, remark: undefined,
    }))
  })

  it('列与可编辑的栏位来自栏位设定：自定义栏位显示与编辑，不可填写的栏位不出现在编辑框里', async () => {
    const user = userEvent.setup()
    setSession(['outsource:manage'])
    const robotV = { key: 'hasRobotV', label: '是否有RobotV', dataType: 'BOOLEAN', options: [], category: 'CUSTOM', pinned: false,
      vendorVisible: false, vendorEditable: false, canEdit: true }
    const layout = {
      fields: [...fieldLayout.fields.slice(0, 6), robotV, ...fieldLayout.fields.slice(6)]
        .map((field) => field.key === 'remark' ? { ...field, canEdit: false } : field),
    }
    const list = [{ ...items[0], remark: '内部备注', customValues: { hasRobotV: 'true' } }, items[1]]
    vi.spyOn(http, 'get').mockImplementation((url: string) => Promise.resolve({
      data: url === '/outsource/items' ? { list, total: 2, page: 1, pageSize: 20 }
        : url === '/outsource/fields' ? layout : url === '/outsource/sync-status' ? syncStatus
          : url.endsWith('/customers') ? [{ fullName: '华东元件有限公司', shortName: '华东' }]
          : url.endsWith('/vendors') ? vendors : url.endsWith('/types') ? types : [],
    } as never))
    const put = vi.spyOn(http, 'put').mockResolvedValue({ data: items[0] } as never)
    render(<MemoryRouter><OutsourceListPage /></MemoryRouter>)

    expect(await screen.findByRole('columnheader', { name: /是否有RobotV/ })).toBeInTheDocument()
    const row = screen.getByText('21089901').closest('tr')!
    expect(within(row).getByText('是')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: '编辑工令 21089901' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).queryByRole('textbox', { name: '备注' })).not.toBeInTheDocument()
    await user.click(within(dialog).getByRole('combobox', { name: '是否有RobotV' }))
    fireEvent.click(await screen.findByRole('option', { name: '否' }))
    await user.click(within(dialog).getByRole('button', { name: '保存' }))
    // 备注 is sent back unchanged; the custom value goes in `values`.
    await waitFor(() => expect(put).toHaveBeenCalledWith('/outsource/items/1', {
      vendorId: 1, typeId: 5, status: 'PAUSED', remark: '内部备注',
      values: { hasRobotV: 'false' }, expectedUpdatedAt: '2026-11-10T01:02:03.456Z',
    }))
  })

  it('编辑与发送是两个权限：只能编辑的看不到发送邮件，只能发送的可以勾选发送但不能编辑', async () => {
    const user = userEvent.setup()
    setSession(['outsource:manage'])
    mockApi()
    const { unmount } = render(<MemoryRouter><OutsourceListPage /></MemoryRouter>)
    const row = (await screen.findByText('21089901')).closest('tr')!
    await user.click(within(row).getByRole('checkbox'))
    expect(await screen.findByRole('button', { name: /批量修改/ })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /发送邮件/ })).not.toBeInTheDocument()
    unmount()

    setSession(['outsource:send'])
    mockApi(readOnlyLayout)
    render(<MemoryRouter><OutsourceListPage /></MemoryRouter>)
    const sendRow = (await screen.findByText('21089901')).closest('tr')!
    expect(screen.queryByRole('button', { name: /编辑工令/ })).not.toBeInTheDocument()
    await user.click(within(sendRow).getByRole('checkbox'))
    expect(await screen.findByRole('button', { name: '发送邮件 (1)' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /批量修改/ })).not.toBeInTheDocument()
  })

  it('发送人可筛出待发送的记录，并可选择无需通知；从入口带 sendQueue=1 进入时自动筛选', async () => {
    const user = userEvent.setup()
    setSession(['outsource:send'])
    const get = mockApi()
    const put = vi.spyOn(http, 'put').mockResolvedValue({ data: { items: [] } } as never)
    render(<MemoryRouter initialEntries={['/outsource/items?sendQueue=1']}><OutsourceListPage /></MemoryRouter>)

    await waitFor(() => expect(get).toHaveBeenCalledWith('/outsource/items', expect.objectContaining({
      params: expect.objectContaining({ sendQueue: 'true' }),
    })))
    expect(screen.getByRole('checkbox', { name: '待发送' })).toBeChecked()

    // 21089901 is unsent and 21089961 already sent: only the unsent one can be marked as needing no notice.
    for (const no of ['21089901', '21089961']) await user.click(within((await screen.findByText(no)).closest('tr')!).getByRole('checkbox'))
    await user.click(screen.getByRole('button', { name: '无需通知 (1)' }))
    fireEvent.click(await screen.findByRole('button', { name: '确定' }))
    await waitFor(() => expect(put).toHaveBeenCalledWith('/outsource/items/skip-notice', {
      items: [{ id: 1, expectedUpdatedAt: '2026-11-10T01:02:03.456Z' }],
    }))
  })

  it('转迅得电控单元格同时有变更时，保留黄色变更高亮与电控标记', async () => {
    setSession([])
    const changedHandoff = { ...items[1], changedFields: ['electricalHandoff'] }
    vi.spyOn(http, 'get').mockImplementation((url: string) => Promise.resolve({
      data: url === '/outsource/items' ? { list: [changedHandoff], total: 1, page: 1, pageSize: 20 }
        : url === '/outsource/fields' ? readOnlyLayout : url === '/outsource/sync-status' ? syncStatus
          : url.endsWith('/customers') ? [{ fullName: '华东元件有限公司', shortName: '华东' }]
          : url.endsWith('/vendors') ? vendors : url.endsWith('/types') ? types : [],
    } as never))
    render(<MemoryRouter><OutsourceListPage /></MemoryRouter>)

    const row = within((await screen.findByText('21089961')).closest('tr')!)
    const td = row.getByText('是').closest('td')!
    expect(td).toHaveClass('outsource-cell-changed')
    expect(td).toHaveClass('outsource-cell-handed-off')
    expect(td).toHaveClass('outsource-cell-handed-off--changed')
  })
})
