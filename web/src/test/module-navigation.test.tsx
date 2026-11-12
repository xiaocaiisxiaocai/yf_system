import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import App from '../App'
import http from '../api/client'
import AdminLayout from '../layouts/AdminLayout'
import AppRail from '../layouts/AppRail'
import { businessHomes, canManageLeaders, collaborationPages, COLLABORATION_PAGES, pageGuard } from '../layouts/navigation'
import Home from '../pages/Home'
import { useAuth, type UserBrief } from '../store/auth'

const internalUser: UserBrief = {
  id: 71, employeeNo: 'NAV71', realName: '导航测试员工', email: 'navigation@example.test',
  userType: 'INTERNAL', supplierId: null, isSystemAdmin: false,
}

function CurrentLocation() {
  const location = useLocation()
  return <output aria-label="当前地址">{location.pathname}</output>
}

function renderShell(path = '/') {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route element={<AdminLayout />}>
          <Route path="/" element={<Home />} />
          <Route path="/profile" element={<h1>个人资料页面</h1>} />
        </Route>
        <Route path="/oem/admin/audit" element={<><AppRail active="oem" /><h1>OEM 审计页面</h1></>} />
        <Route path="/outsource/settings" element={<><AppRail active="outsource" /><h1>外协设定页面</h1></>} />
        <Route path="/org/depts" element={<h1>组织架构页面</h1>} />
      </Routes>
      <CurrentLocation />
    </MemoryRouter>,
  )
}

describe('按可用功能决定模块导航与首页', () => {
  beforeEach(() => {
    useAuth.setState({
      token: 'navigation-test-token', user: internalUser, menus: [], permissions: [],
      mustChangePassword: false, booted: true, generation: 71,
    })
  })

  afterEach(() => { vi.restoreAllMocks() })

  it('没有任何权限时只显示一个空状态，保留账号入口且不请求业务数据', () => {
    const get = vi.spyOn(http, 'get')
    renderShell()
    expect(screen.getAllByRole('heading', { name: '暂未开通功能权限' })).toHaveLength(1)
    expect(screen.queryByRole('navigation', { name: '业务切换' })).not.toBeInTheDocument()
    expect(screen.queryByText('工作台不可用')).not.toBeInTheDocument()
    expect(screen.queryByText('暂无可用功能，请联系管理员')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: '账号菜单：导航测试员工' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '打开导航菜单' })).not.toBeInTheDocument()
    expect(get).not.toHaveBeenCalled()
  })

  it('仅有 OEM 权限时首页进入其获授权页面，不展示仿真或无意义的单模块切换', async () => {
    useAuth.setState({ permissions: ['oem:audit_view'] })
    renderShell()
    expect(await screen.findByRole('heading', { name: 'OEM 审计页面' })).toBeInTheDocument()
    expect(screen.getByRole('status', { name: '当前地址' })).toHaveTextContent('/oem/admin/audit')
    expect(screen.queryByRole('button', { name: 'Robot仿真协作' })).not.toBeInTheDocument()
    expect(screen.queryByRole('navigation', { name: '业务切换' })).not.toBeInTheDocument()
    expect(screen.queryByText('暂未开通功能权限')).not.toBeInTheDocument()
  })

  it('仅有外协设定权限时首页直接进入设定页', async () => {
    useAuth.setState({ permissions: ['outsource:settings'] })
    renderShell()
    expect(await screen.findByRole('heading', { name: '外协设定页面' })).toBeInTheDocument()
    expect(screen.getByRole('status', { name: '当前地址' })).toHaveTextContent('/outsource/settings')
    expect(screen.queryByRole('button', { name: 'Robot仿真协作' })).not.toBeInTheDocument()
    expect(screen.queryByRole('navigation', { name: '业务切换' })).not.toBeInTheDocument()
  })

  it.each([
    ['supplier:list', 'supplier:account', '/suppliers'],
    ['org:user', 'user:manage', '/org/users'],
    ['rbac:role', 'role:manage', '/rbac/roles'],
    ['log:audit', 'log:view', '/logs'],
    ['system:config', 'config:manage', '/system/config'],
    ['system:dict', 'dict:manage', '/system/dictionaries'],
  ])('管理菜单 %s 缺少操作权限时不可作为模块或首页入口', (menu, permission, path) => {
    const grants = { user: internalUser, menus: [menu], permissions: [menu] }
    expect(collaborationPages(grants)).toEqual([])
    expect(businessHomes(grants).collaboration).toBeNull()
    expect(businessHomes({ ...grants, permissions: [menu, permission] }).collaboration).toBe(path)
  })

  it('侧栏不会显示只分配菜单、没有操作权限的管理功能', () => {
    useAuth.setState({ menus: ['org:user', 'system:config'], permissions: ['org:user', 'system:config'] })
    renderShell()
    expect(screen.getByRole('heading', { name: '暂未开通功能权限' })).toBeInTheDocument()
    expect(screen.queryByRole('menuitem', { name: '用户管理' })).not.toBeInTheDocument()
    expect(screen.queryByRole('menuitem', { name: '系统参数' })).not.toBeInTheDocument()
  })

  it('只有工作台菜单、没有项目查看权限时不进入工作台', () => {
    useAuth.setState({ menus: ['dashboard'], permissions: ['dashboard'] })
    const get = vi.spyOn(http, 'get')
    renderShell()
    expect(screen.getByRole('heading', { name: '暂未开通功能权限' })).toBeInTheDocument()
    expect(get).not.toHaveBeenCalled()
  })

  it('内部主管维护权限即使没有菜单也可作为首页，组织菜单仍支持只读', async () => {
    useAuth.setState({ permissions: ['dept:leader_manage'] })
    renderShell()
    expect(await screen.findByRole('heading', { name: '组织架构页面' })).toBeInTheDocument()
    expect(businessHomes({ user: internalUser, menus: ['org:dept'], permissions: [] }).collaboration).toBe('/org/depts')
    expect(businessHomes({ user: { userType: 'SUPPLIER' }, menus: [], permissions: ['dept:leader_manage'] }).collaboration).toBeNull()
  })

  it('供应商收到内部模块权限字符串也不能获得 OEM 或外协入口', () => {
    useAuth.setState({ user: { ...internalUser, userType: 'SUPPLIER', supplierId: 71 }, permissions: ['oem:audit_view', 'outsource:settings'] })
    renderShell()
    expect(screen.getByRole('heading', { name: '暂未开通功能权限' })).toBeInTheDocument()
    expect(screen.queryByRole('navigation', { name: '业务切换' })).not.toBeInTheDocument()
  })

  it('权限收回时业务入口和导航开关立即消失，个人资料仍保留', async () => {
    useAuth.setState({ menus: ['org:user'], permissions: ['user:manage', 'oem:audit_view'] })
    renderShell('/profile')
    expect(screen.getByRole('button', { name: 'Robot仿真协作' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'ODM/OEMA 文件传递' })).toBeInTheDocument()
    act(() => { useAuth.setState({ permissions: [] }) })
    await waitFor(() => expect(screen.queryByRole('navigation', { name: '业务切换' })).not.toBeInTheDocument())
    expect(screen.queryByRole('menuitem', { name: '用户管理' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '打开导航菜单' })).not.toBeInTheDocument()
    expect(screen.getByRole('heading', { name: '个人资料页面' })).toBeInTheDocument()
  })

  it('无权限空状态可以进入个人资料', async () => {
    const user = userEvent.setup()
    renderShell()
    await user.click(screen.getByRole('button', { name: '个人资料与密码' }))
    expect(await screen.findByRole('heading', { name: '个人资料页面' })).toBeInTheDocument()
    expect(screen.getByRole('status', { name: '当前地址' })).toHaveTextContent('/profile')
  })

  it('仅有 OEM 权限的个人资料页仍提供窄屏导航抽屉并可返回 OEM', async () => {
    const user = userEvent.setup()
    useAuth.setState({ permissions: ['oem:audit_view'] })
    renderShell('/profile')
    await user.click(screen.getByRole('button', { name: '打开导航菜单' }))
    const drawer = await waitFor(() => {
      const element = document.querySelector<HTMLElement>('.mobile-nav-drawer')
      expect(element).not.toBeNull()
      return element!
    })
    expect(within(drawer).queryByRole('button', { name: 'Robot仿真协作' })).not.toBeInTheDocument()
    await user.click(within(drawer).getByRole('button', { name: 'ODM/OEMA 文件传递' }))
    expect(await screen.findByRole('heading', { name: 'OEM 审计页面' })).toBeInTheDocument()
  })

  it.each([
    { menus: [], expected: '暂未开通功能权限', path: '/' },
    { menus: ['project:list'], expected: '无操作权限', path: '/projects' },
  ])('实际项目路由在菜单为 $menus、缺少项目权限时不请求业务数据', async ({ menus, expected, path }) => {
    useAuth.setState({ menus, permissions: [] })
    const get = vi.spyOn(http, 'get')
    render(<MemoryRouter initialEntries={['/projects']}><App /><CurrentLocation /></MemoryRouter>)
    expect(await screen.findByText(expected)).toBeInTheDocument()
    expect(screen.getByRole('status', { name: '当前地址' }).textContent).toBe(path)
    expect(get).not.toHaveBeenCalled()
  })
})

describe('菜单与路由守卫共用同一份规则', () => {
  // 与 App.tsx 的 Guard 判定一致：缺菜单则不放行；声明了操作权限时须持有其一。
  const guardAllows = (guard: { menu: string; anyPermission?: string[] }, menus: string[], permissions: string[]) =>
    menus.includes(guard.menu) && (!guard.anyPermission || guard.anyPermission.some(code => permissions.includes(code)))

  it('每个页面：菜单可见当且仅当直接访问不会被拒（只有菜单、只有权限、都有、都没有）', () => {
    for (const page of COLLABORATION_PAGES) {
      if (page.code === 'dashboard') continue
      const guard = pageGuard(page.code)
      const required = [...page.anyPermission]
      for (const [menus, permissions] of [[[], []], [[page.code], []], [[], required], [[page.code], required]] as [string[], string[]][]) {
        const grants = { menus, permissions, user: internalUser }
        const visible = collaborationPages(grants).some(item => item.code === page.code)
        const allowed = (page.code === 'org:dept' && canManageLeaders(grants)) || guardAllows(guard, menus, permissions)
        expect({ page: page.code, menus, permissions, visible }).toEqual({ page: page.code, menus, permissions, visible: allowed })
      }
    }
  })

  it('组织架构：主管维护权限不需要菜单，供应商即使带同名权限也不行', () => {
    const leader = { menus: [], permissions: ['dept:leader_manage'], user: internalUser }
    expect(canManageLeaders(leader)).toBe(true)
    expect(collaborationPages(leader).map(page => page.code)).toEqual(['org:dept'])
    const supplier = { ...leader, user: { ...internalUser, userType: 'SUPPLIER' } }
    expect(canManageLeaders(supplier)).toBe(false)
    expect(collaborationPages(supplier)).toEqual([])
  })
})
