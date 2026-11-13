import { render, screen, waitFor, within } from '@testing-library/react'
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
  id: 1, name: '鼎亚', remark: null, status: 'ACTIVE', sortNo: 10,
  contacts: [
    { id: 11, vendorId: 1, name: null, email: 'first@vendor.test', status: 'ACTIVE', sortNo: 1 },
    { id: 12, vendorId: 1, name: null, email: 'old@vendor.test', status: 'DISABLED', sortNo: 2 },
  ],
}]
const types = [{ id: 5, code: 'OEMA', name: '进料+制造', status: 'ACTIVE', sortNo: 10, vendorIds: [1] }]
const cc = [{ userId: 21, employeeNo: 'pm', realName: '生管甲', email: 'pm@example.test', status: 'ACTIVE' }]

function mockSettings() {
  return vi.spyOn(http, 'get').mockImplementation((url: string) => Promise.resolve({
    data: url.endsWith('/vendors') ? vendors : url.endsWith('/types') ? types : url.endsWith('/default-cc') ? cc : [],
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

  it('显示厂商的启用邮箱、类型的可用厂商和默认抄送', async () => {
    const user = userEvent.setup()
    setInternalSession(['outsource:settings'])
    mockSettings()
    render(<MemoryRouter><OutsourceSettingsPage /></MemoryRouter>)

    const vendorRow = (await screen.findByText('鼎亚')).closest('tr')!
    expect(within(vendorRow).getByText('first@vendor.test')).toBeInTheDocument()
    expect(within(vendorRow).queryByText('old@vendor.test')).not.toBeInTheDocument()

    await user.click(screen.getByRole('tab', { name: '外发类型' }))
    const typeRow = (await screen.findByText('OEMA【进料+制造】')).closest('tr')!
    expect(within(typeRow).getByText('鼎亚')).toBeInTheDocument()

    await user.click(screen.getByRole('tab', { name: '默认抄送' }))
    expect(await screen.findByText('生管甲')).toBeInTheDocument()
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

  it('新增厂商提交去除空白后的名称并重新加载', async () => {
    const user = userEvent.setup()
    setInternalSession(['outsource:settings'])
    const get = mockSettings()
    const post = vi.spyOn(http, 'post').mockResolvedValue({ data: { ...vendors[0], id: 2, name: '合科', contacts: [] } } as never)
    render(<MemoryRouter><OutsourceSettingsPage /></MemoryRouter>)
    await screen.findByText('鼎亚')

    await user.click(screen.getByRole('button', { name: /新增厂商/ }))
    const dialog = await screen.findByRole('dialog')
    await user.type(within(dialog).getByLabelText('厂商名称'), '  合科  ')
    await user.click(within(dialog).getByRole('button', { name: '创建' }))

    await waitFor(() => expect(post).toHaveBeenCalledWith('/outsource/settings/vendors', { name: '合科', remark: null, sortNo: 20 }))
    await waitFor(() => expect(get.mock.calls.filter(([url]) => url === '/outsource/settings/vendors')).toHaveLength(2))
  })
})
