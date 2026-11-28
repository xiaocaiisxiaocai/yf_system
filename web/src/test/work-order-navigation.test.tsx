import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import http from '../api/client'
import AppRail from '../layouts/AppRail'
import { OemInternalLayout } from '../oem/layouts/OemLayouts'
import OutsourceLayout from '../outsource/OutsourceLayout'
import { useSendQueue } from '../outsource/sendQueue'
import { useAuth } from '../store/auth'

const features = vi.hoisted(() => ({ OEM_UI_ENABLED: true }))
vi.mock('../features', () => features)

const employee = {
  id: 6, employeeNo: 'E06', realName: '导航测试员', email: 'nav@example.test',
  userType: 'INTERNAL' as const, supplierId: null, isSystemAdmin: false,
}
const allScreens = ['外发工令', '对接联络表', '文件传递单', '审批', '发送记录', '公告中心', 'IPMS 资料', '外协管理设定', '外发工令设定', '文件传递设置', '操作日志', '公告管理']
const allGrants = ['oem:transfer_view', 'oem:flow_approve', 'outsource:send', 'outsource:settings', 'oem:notify_manage', 'oem:audit_view', 'announcement:publish']

function Location() {
  const { pathname, search } = useLocation()
  return <output aria-label="当前地址">{pathname}{search}</output>
}

function renderWorkArea(initialPath = '/outsource/items') {
  return render(
    <MemoryRouter initialEntries={[initialPath]}>
      <Routes>
        <Route path="/profile" element={<AppRail active="collaboration" />} />
        <Route path="/oem/*" element={<OemInternalLayout />} />
        <Route path="/outsource/*" element={<OutsourceLayout />} />
      </Routes>
      <Location />
    </MemoryRouter>,
  )
}

function menuLabels() {
  return screen.getAllByRole('menuitem').map((item) => item.textContent)
}

describe('工令协作模块导航', () => {
  beforeEach(() => {
    features.OEM_UI_ENABLED = true
    useAuth.setState({ user: employee, token: 'nav', permissions: [], menus: [] })
    useSendQueue.setState({ count: 0 })
    vi.spyOn(http, 'get').mockResolvedValue({ data: { count: 0 } })
  })
  afterEach(() => { vi.restoreAllMocks() })

  it('两个路由树使用相同标题、菜单顺序与管理分组，跨区后高亮随路由更新', async () => {
    const user = userEvent.setup()
    useAuth.setState({ permissions: allGrants })
    renderWorkArea('/oem/transfers/18')
    expect(menuLabels()).toEqual(allScreens)
    expect(screen.getByText('ODM/OEM工令协作')).toBeVisible()
    expect(screen.getByRole('menuitem', { name: '文件传递单' })).toHaveClass('arco-menu-selected')
    expect(screen.getByRole('separator').nextElementSibling).toBe(screen.getByRole('menuitem', { name: 'IPMS 资料' }))
    await user.click(screen.getByRole('menuitem', { name: '外发工令' }))
    expect(screen.getByLabelText('当前地址')).toHaveTextContent(/^\/outsource\/items$/)
    expect(screen.getByText('ODM/OEM工令协作')).toBeVisible()
    expect(menuLabels()).toEqual(allScreens)
    expect(screen.getByRole('menuitem', { name: '外发工令' })).toHaveClass('arco-menu-selected')
    await user.click(screen.getByRole('menuitem', { name: '外协管理设定' }))
    expect(screen.getByLabelText('当前地址')).toHaveTextContent('/oem/admin/outsource-settings')
    expect(screen.getByRole('menuitem', { name: '外协管理设定' })).toHaveClass('arco-menu-selected')
  })

  it.each([
    ['', ['外发工令', '对接联络表', '公告中心']],
    ['outsource:manage', ['外发工令', '对接联络表', '公告中心']],
    ['outsource:send', ['外发工令', '对接联络表', '发送记录', '公告中心']],
    ['outsource:settings', ['外发工令', '对接联络表', '公告中心', 'IPMS 资料', '外协管理设定', '外发工令设定']],
    ['oem:company_manage', ['外发工令', '对接联络表', '公告中心', '外协管理设定']],
    ['oem:account_manage', ['外发工令', '对接联络表', '公告中心', '外协管理设定']],
    ['oem:directory_view', ['外发工令', '对接联络表', '公告中心', '外协管理设定']],
    ['oem:transfer_create', ['外发工令', '对接联络表', '文件传递单', '公告中心']],
    ['oem:approval_recover', ['外发工令', '对接联络表', '审批', '公告中心']],
    ['oem:file_policy_manage', ['外发工令', '对接联络表', '公告中心', '文件传递设置']],
    ['oem:audit_view', ['外发工令', '对接联络表', '公告中心', '操作日志']],
    ['announcement:reads', ['外发工令', '对接联络表', '公告中心', '公告管理']],
  ] as [string, string[]][])('仅显示权限 %s 可用的菜单', (permission, expected) => {
    useAuth.setState({ permissions: permission ? [permission] : [] })
    renderWorkArea()
    expect(menuLabels()).toEqual(expected)
  })

  it.each([
    ['oem:transfer_create', '/oem/transfers'],
    ['oem:directory_view', '/oem/admin/outsource-settings'],
    ['outsource:settings', '/outsource/items'],
  ])('切换栏按权限 %s 进入 %s', async (permission, path) => {
    const user = userEvent.setup()
    useAuth.setState({ permissions: [permission] })
    renderWorkArea('/profile')
    await user.click(screen.getByRole('button', { name: 'ODM/OEM工令协作' }))
    expect(screen.getByLabelText('当前地址').textContent).toBe(path)
  })

  it('发送人点击带有队列数量的外发工令时打开待发送筛选', async () => {
    const user = userEvent.setup()
    vi.mocked(http.get).mockResolvedValue({ data: { count: 3 } })
    useAuth.setState({ permissions: ['oem:transfer_view', 'outsource:send'] })
    renderWorkArea('/oem/transfers')
    const item = screen.getByRole('menuitem', { name: /^外发工令/ })
    expect(await within(item).findByText('3')).toBeVisible()
    await user.click(item)
    expect(screen.getByLabelText('当前地址').textContent).toBe('/outsource/items?sendQueue=1')
  })

  it('关闭 OEM 时模块仍然可进入，只显示外发工令相关菜单', async () => {
    features.OEM_UI_ENABLED = false
    const user = userEvent.setup()
    useAuth.setState({ permissions: allGrants })
    renderWorkArea('/profile')
    await user.click(screen.getByRole('button', { name: 'ODM/OEM工令协作' }))
    expect(screen.getByLabelText('当前地址').textContent).toBe('/outsource/items')
    expect(menuLabels()).toEqual(['外发工令', '对接联络表', '发送记录', 'IPMS 资料', '外发工令设定'])
  })
})
