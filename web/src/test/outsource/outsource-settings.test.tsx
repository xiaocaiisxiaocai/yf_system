import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import http from '../../api/client'
import AppRail from '../../layouts/AppRail'
import OutsourceSettingsPage from '../../outsource/pages/OutsourceSettingsPage'
import { useAuth } from '../../store/auth'

function CurrentLocation() {
  const location = useLocation()
  return <output aria-label="当前地址">{location.pathname}</output>
}

function setInternalSession(permissions: string[]) {
  useAuth.setState({
    token: 'internal-token',
    user: { id: 7, employeeNo: 'buyer', realName: '采购', email: 'buyer@example.test', userType: 'INTERNAL', supplierId: null, isSystemAdmin: false },
    permissions,
    menus: [],
    mustChangePassword: false,
    booted: true,
    generation: 3,
  })
}

const vendors = [{
  id: 1, name: '鼎亚', status: 'ACTIVE',
}]
const types = [{ id: 5, code: 'OEMA', name: '进料+制造', status: 'ACTIVE', sortNo: 10, vendorIds: [1] }]
const aliases = [
  { id: 31, fullName: '庆鼎精密电子(淮安)有限公司', shortName: '庆鼎', manual: false, updatedAt: '2026-11-17T06:00:00.000Z' },
  { id: 32, fullName: '超颖电子电路股份有限公司', shortName: '超穎', manual: true, updatedAt: '2026-11-17T06:00:00.000Z' },
]
const cc = [{ userId: 21, employeeNo: 'pm', realName: '生管甲', email: 'pm@example.test', status: 'ACTIVE' }]

function mockSettings() {
  return vi.spyOn(http, 'get').mockImplementation((url: string) => Promise.resolve({
    data: url.endsWith('/vendors') ? vendors : url.endsWith('/types') ? types : url.endsWith('/default-cc') ? cc
      : url.endsWith('/customers') ? aliases : [],
  } as never))
}

describe('外协清单入口与设定', () => {
  afterEach(() => {
    vi.restoreAllMocks()
    useAuth.setState({ token: null, user: null, permissions: [], menus: [] })
  })

  it('有设定权限的内部员工可从业务切换栏进入外协清单', async () => {
    const user = userEvent.setup()
    setInternalSession(['outsource:settings'])
    render(
      <MemoryRouter initialEntries={['/']}>
        <Routes><Route path="*" element={<><AppRail active="collaboration" /><CurrentLocation /></>} /></Routes>
      </MemoryRouter>,
    )
    expect(screen.queryByRole('button', { name: 'ODM/OEMA 文件传递' })).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: '外协清单' }))
    expect(screen.getByRole('status', { name: '当前地址' })).toHaveTextContent('/outsource/settings')
  })

  it('没有外协权限时不显示入口', () => {
    setInternalSession(['dashboard'])
    render(<MemoryRouter><AppRail active="collaboration" /></MemoryRouter>)
    expect(screen.queryByRole('button', { name: '外协清单' })).not.toBeInTheDocument()
  })

  it('不再维护厂商，类型的可用厂商显示 ODM/OEMA 厂商名', async () => {
    const user = userEvent.setup()
    setInternalSession(['outsource:settings'])
    mockSettings()
    render(<MemoryRouter><OutsourceSettingsPage /></MemoryRouter>)

    expect(screen.queryByRole('tab', { name: '外协厂商' })).not.toBeInTheDocument()
    const typeRow = (await screen.findByText('OEMA【进料+制造】')).closest('tr')!
    expect(within(typeRow).getByText('鼎亚')).toBeInTheDocument()

    await user.click(screen.getByRole('tab', { name: '默认抄送' }))
    expect(await screen.findByText('生管甲')).toBeInTheDocument()
  })

  it('默认抄送的人员选项列出全部内部账号，可本地搜索，未填邮箱的不可选', async () => {
    const user = userEvent.setup()
    setInternalSession(['outsource:settings'])
    const people = Array.from({ length: 30 }, (_, i) => ({
      userId: 100 + i, employeeNo: `e${String(i).padStart(2, '0')}`, realName: `员工${i}`, email: `e${i}@example.test`, status: 'ACTIVE',
    }))
    const options = [...people, { userId: 200, employeeNo: 'nomail', realName: '无邮箱', email: '', status: 'ACTIVE' }]
    const get = vi.spyOn(http, 'get').mockImplementation((url: string) => Promise.resolve({
      data: url.endsWith('/cc-user-options') ? options : url.endsWith('/default-cc') ? cc : url.endsWith('/vendors') ? vendors : [],
    } as never))
    render(<MemoryRouter><OutsourceSettingsPage /></MemoryRouter>)
    await user.click(await screen.findByRole('tab', { name: '默认抄送' }))
    await screen.findByText('生管甲')

    const picker = screen.getByRole('combobox', { name: '选择抄送人员' })
    await user.click(picker)
    await user.keyboard('员工29')
    expect(await screen.findByRole('option', { name: /员工29/ })).toBeInTheDocument()
    expect(screen.queryByRole('option', { name: /员工28/ })).not.toBeInTheDocument()
    await user.clear(picker.querySelector('input') ?? picker)
    await user.keyboard('无邮箱')
    expect(await screen.findByRole('option', { name: /未填写邮箱/ })).toHaveClass('arco-select-option-disabled')
    expect(get.mock.calls.filter(([url]) => url === '/outsource/settings/cc-user-options')).toHaveLength(1)
    expect(get).toHaveBeenCalledWith('/outsource/settings/cc-user-options')
  })

  it('更新外协清单的定时同步可以停止和开启', async () => {
    const user = userEvent.setup()
    setInternalSession(['outsource:settings'])
    const syncStatus = {
      syncTime: '03:30', scheduleEnabled: true, ipmsConfigured: true, running: false, lastStartedAt: null, lastFinishedAt: null,
      lastSuccessAt: null, lastError: null, lastTrigger: null, lastProcessed: 0, lastChanged: 0, lastClosed: 0, lastMissing: 0,
    }
    const mirrorSettings = {
      baseUrl: 'http://183.238.37.108:6001', configBaseUrl: null, saved: true, account: 'reader', passwordSet: true,
      accountConfigured: true, syncTime: '03:00', scheduleEnabled: true,
    }
    const mirrorStatus = {
      phase: 'IDLE', runMode: null, page: null, pageSize: 100, fetched: 0, pages: 0, total: null, pendingReason: null, startedAt: null,
      lastFinishedAt: null, lastSuccessAt: null, lastError: null, lastTrigger: null, lastCount: 0, mirrorCount: 0, preview: [],
    }
    vi.spyOn(http, 'get').mockImplementation((url: string) => Promise.resolve({
      data: url.endsWith('/sync-status') ? syncStatus : url.endsWith('/mes-mirror/settings') ? mirrorSettings
        : url.endsWith('/mes-mirror/status') ? mirrorStatus : url.endsWith('/vendors') ? vendors : [],
    } as never))
    const put = vi.spyOn(http, 'put').mockResolvedValue({ data: { ...syncStatus, scheduleEnabled: false } } as never)
    render(<MemoryRouter><OutsourceSettingsPage /></MemoryRouter>)

    await user.click(await screen.findByRole('tab', { name: 'IPMS 同步' }))
    const toggle = await screen.findByRole('switch', { name: '定时同步' })
    expect(toggle).toBeChecked()
    await user.click(toggle)
    expect(put).toHaveBeenCalledWith('/outsource/settings/sync-schedule', { enabled: false })
    await waitFor(() => expect(toggle).not.toBeChecked())
  })


  it('客户简称：从 IPMS 同步、搜索、编辑，以及为缺少简称的客户新增', async () => {
    const user = userEvent.setup()
    setInternalSession(['outsource:settings'])
    let current = aliases
    let missing = [{ fullName: '深圳市泰雪科技有限公司', mesWorkOrders: 3, outsourceItems: 0 }]
    vi.spyOn(http, 'get').mockImplementation((url: string) => Promise.resolve({
      data: url.endsWith('/customers/missing') ? missing : url.endsWith('/customers') ? current
        : url.endsWith('/vendors') ? vendors : url.endsWith('/types') ? types : url.endsWith('/default-cc') ? cc : [],
    } as never))
    const synced = [...aliases, { id: 33, fullName: '广州广合科技股份有限公司', shortName: '廣合', manual: false, updatedAt: '2026-11-17T07:00:00.000Z' }]
    const post = vi.spyOn(http, 'post').mockImplementation((url: string) => {
      if (url.endsWith('/sync')) { current = synced; return Promise.resolve({ data: { rows: 9, customers: 3, added: 1, updated: 0, keptManual: 0, aliases: synced } } as never) }
      const added = { id: 34, fullName: '深圳市泰雪科技有限公司', shortName: '泰雪', manual: true, updatedAt: '2026-11-17T08:00:00.000Z' }
      current = [...current, added]; missing = []
      return Promise.resolve({ data: added } as never)
    })
    const put = vi.spyOn(http, 'put').mockImplementation(() => {
      current = current.map((row) => row.id === 31 ? { ...row, shortName: '庆鼎淮安', manual: true } : row)
      return Promise.resolve({ data: current[0] } as never)
    })
    render(<MemoryRouter><OutsourceSettingsPage /></MemoryRouter>)

    await user.click(await screen.findByRole('tab', { name: '客户简称' }))
    const row = (await screen.findByText('庆鼎精密电子(淮安)有限公司')).closest('tr')!
    expect(within(row).getByText('庆鼎')).toBeInTheDocument()
    expect(within(row).getByText('IPMS')).toBeInTheDocument()
    expect(within(row).getByRole('button', { name: '删除' })).toBeDisabled()
    expect(within(screen.getByText('超颖电子电路股份有限公司').closest('tr')!).getByText('手动')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: /从 IPMS 同步/ }))
    await waitFor(() => expect(post).toHaveBeenCalledWith('/outsource/settings/customers/sync'))
    expect(await screen.findByText('广州广合科技股份有限公司')).toBeInTheDocument()

    await user.type(screen.getByPlaceholderText('客户全称或简称'), '廣合')
    expect(screen.queryByText('庆鼎精密电子(淮安)有限公司')).not.toBeInTheDocument()
    await user.clear(screen.getByPlaceholderText('客户全称或简称'))

    const qingding = (await screen.findByText('庆鼎精密电子(淮安)有限公司')).closest('tr')!
    await user.click(within(qingding).getByRole('button', { name: '编辑' }))
    const input = await screen.findByLabelText('客户简称')
    await user.clear(input)
    await user.type(input, ' 庆鼎淮安 ')
    await user.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(put).toHaveBeenCalledWith('/outsource/settings/customers/31', { shortName: '庆鼎淮安' }))
    expect(await screen.findByText('庆鼎淮安')).toBeInTheDocument()

    // 新增 starts in the full name; for a missing customer the full name is filled in and the short name has the focus.
    await user.click(screen.getByRole('button', { name: /新增/ }))
    await waitFor(() => expect(screen.getByLabelText('客户全称')).toHaveFocus())
    await user.click(screen.getByRole('button', { name: '取消' }))

    // Customers without a short name are listed; setting one adds a manual pair.
    await user.click(screen.getByText(/缺少简称 1/))
    const missingRow = (await screen.findByText('深圳市泰雪科技有限公司')).closest('tr')!
    await user.click(within(missingRow).getByRole('button', { name: '设置简称' }))
    expect(await screen.findByLabelText('客户全称')).toHaveValue('深圳市泰雪科技有限公司')
    await waitFor(() => expect(screen.getByLabelText('客户简称')).toHaveFocus())
    await user.type(screen.getByLabelText('客户简称'), '泰雪')
    await user.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(post).toHaveBeenCalledWith('/outsource/settings/customers', { fullName: '深圳市泰雪科技有限公司', shortName: '泰雪' }))
    expect(await screen.findByText(/缺少简称 0/)).toBeInTheDocument()
  })

  it('只有栏位设定权限时只显示栏位设定：厂商可见可切换，固定显示的栏位不能关闭，内部填写可指定角色', async () => {
    const user = userEvent.setup()
    setInternalSession(['outsource', 'outsource:field_settings'])
    const field = (id: number, key: string, label: string, extra: Record<string, unknown> = {}) => ({
      id, key, builtIn: true, label, dataType: 'TEXT', options: [], category: 'MANUAL', sortNo: id * 10, status: 'ACTIVE', pinned: false,
      vendorVisible: true, vendorVisibleLocked: false, vendorEditable: false, vendorEditableAllowed: true, editorScope: 'ALL',
      editorScopeAllowed: true, editorRoleIds: [], inUse: false, updatedAt: '2026-11-19T00:00:00.000Z', ...extra,
    })
    const settings = {
      fields: [
        field(3, 'workOrderNo', '工令', { category: 'FIXED', pinned: true, vendorVisibleLocked: true, editorScope: 'NONE', editorScopeAllowed: false }),
        field(6, 'hasRobotV', '是否有RobotV', { builtIn: false, dataType: 'BOOLEAN', category: 'CUSTOM', vendorVisible: false }),
        field(18, 'remark', '备注'),
      ],
      roles: [{ id: 71, name: '采购' }, { id: 72, name: '机构' }],
    }
    const get = vi.spyOn(http, 'get').mockResolvedValue({ data: settings } as never)
    const put = vi.spyOn(http, 'put').mockResolvedValue({ data: settings } as never)
    render(<MemoryRouter><OutsourceSettingsPage /></MemoryRouter>)

    expect(await screen.findByRole('tab', { name: '栏位设定' })).toBeInTheDocument()
    expect(screen.queryByRole('tab', { name: '外发类型' })).not.toBeInTheDocument()
    expect(get).not.toHaveBeenCalledWith('/outsource/settings/types')
    expect(await screen.findByRole('switch', { name: '工令 厂商可见' })).toBeDisabled()

    await user.click(screen.getByRole('switch', { name: '是否有RobotV 厂商可见' }))
    await waitFor(() => expect(put).toHaveBeenCalledWith('/outsource/settings/fields/6', { vendorVisible: true }))

    const remark = screen.getByText('备注').closest('tr')!
    await user.click(within(remark).getByRole('button', { name: '编辑' }))
    const dialog = await screen.findByRole('dialog')
    await user.click(within(dialog).getByRole('radio', { name: '指定角色' }))
    await user.click(within(dialog).getByRole('combobox', { name: /可填写的角色/ }))
    // In multiple mode role=option is a wrapper; the inner option element takes the click.
    const option = (await screen.findAllByRole('option', { name: '机构' })).at(-1)!
    fireEvent.click(option.querySelector('.arco-select-option') ?? option)
    await user.click(within(dialog).getByRole('button', { name: '保存' }))
    await waitFor(() => expect(put).toHaveBeenCalledWith('/outsource/settings/fields/18', { editorScope: 'ROLES', editorRoleIds: [72], editorUserIds: [] }))
  })
})
