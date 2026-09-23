import { readFileSync, statSync } from 'node:fs'
import { resolve } from 'node:path'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import '../../../index.css'

const mocks = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  delete: vi.fn(),
  logout: vi.fn(),
  setUser: vi.fn(),
  auth: {
    generation: 1,
    token: 'test-token',
    menus: ['dashboard', 'project:list', 'supplier:list', 'org:user', 'org:dept', 'rbac:role', 'log:audit'],
    permissions: ['project:list', 'project:create', 'project:update', 'project:delete', 'supplier:manage', 'supplier:account', 'supplier:delete', 'user:delete', 'role:delete'],
    user: {
      id: 8,
      employeeNo: 'supplier8',
      realName: '供应商人员',
      email: 'before@example.invalid',
      userType: 'SUPPLIER',
      supplierId: 3,
    },
  },
}))

vi.mock('../../../api/client', () => ({
  default: { get: mocks.get, post: mocks.post, put: mocks.put, delete: mocks.delete },
  withAuthLock: async (operation: () => unknown) => operation(),
}))

vi.mock('../../../store/auth', () => ({
  useAuth: (selector?: (state: unknown) => unknown) => {
    const state = {
      ...mocks.auth,
      hasPerm: (code: string) => mocks.auth.permissions.includes(code),
      logout: mocks.logout,
      setUser: mocks.setUser,
    }
    return selector ? selector(state) : state
  },
}))

vi.mock('../../../components/CollaborationNotifications', () => ({
  default: () => <aside aria-label="协作通知" />,
}))

import AdminLayout from '../../../layouts/AdminLayout'
import Profile from '../../../pages/Profile'
import ProjectList from '../../../pages/project/ProjectList'
import UserList from '../../../pages/org/UserList'
import RoleList from '../../../pages/rbac/RoleList'
import SupplierList from '../../../pages/supplier/SupplierList'
import AuditLog from '../../../pages/system/AuditLog'
import { actionSlots } from '../../../components/ActionSlots'

function LocationProbe() {
  const location = useLocation()
  return <output aria-label="当前地址">{location.pathname}</output>
}

function renderLayout(initialEntry = '/') {
  return render(
    <MemoryRouter initialEntries={[initialEntry]}>
      <Routes>
        <Route element={<><AdminLayout /><LocationProbe /></>}>
          <Route index element={<div>工作台内容</div>} />
          <Route path="profile" element={<div>资料路由内容</div>} />
        </Route>
      </Routes>
    </MemoryRouter>,
  )
}

function page(list: unknown[] = [], pageSize = 10) {
  return { list, total: list.length, page: 1, pageSize }
}

function installApplicationStyles() {
  const style = document.createElement('style')
  style.dataset.testStyles = 'application'
  style.textContent = readFileSync(resolve(process.cwd(), 'src/index.css'), 'utf8')
  document.head.append(style)
  return () => style.remove()
}

describe('管理员布局与个人资料', () => {
  beforeEach(() => {
    localStorage.clear()
    mocks.auth.menus = ['dashboard', 'project:list', 'supplier:list', 'org:user', 'org:dept', 'rbac:role', 'log:audit']
    mocks.auth.permissions = ['project:list', 'project:create', 'project:update', 'project:delete', 'supplier:manage', 'supplier:account', 'supplier:delete', 'user:delete', 'role:delete']
    mocks.auth.user = {
      id: 8,
      employeeNo: 'supplier8',
      realName: '供应商人员',
      email: 'before@example.invalid',
      userType: 'SUPPLIER',
      supplierId: 3,
    }
    mocks.get.mockImplementation(async (url: string) => {
      if (url === '/departments' || url === '/admin/user-role-options' || url === '/permissions' || url === '/supplier-options' || url === '/project-dictionaries') return { data: [] }
      if (url === '/admin/roles' || url === '/admin/audit-logs') return { data: page([], 20) }
      return { data: page() }
    })
    mocks.post.mockResolvedValue({ data: {} })
    mocks.put.mockResolvedValue({ data: {} })
    mocks.delete.mockResolvedValue({ data: {} })
  })

  it('account menu opens personal profile maintenance inside the authenticated layout', async () => {
    const user = userEvent.setup()
    renderLayout()

    await user.click(screen.getByRole('button', { name: '账号菜单：供应商人员' }))
    fireEvent.click((await screen.findAllByText('个人资料')).at(-1)!.closest('.arco-dropdown-menu-item')!)

    expect(screen.getByRole('status', { name: '当前地址' })).toHaveTextContent('/profile')
    expect(screen.queryByText('修改密码', { selector: '.arco-dropdown-menu-item' })).not.toBeInTheDocument()
  })

  it('personal profile submits only own email and keeps password change in the same page', async () => {
    const updatedUser = { ...mocks.auth.user, email: 'after@example.invalid' }
    mocks.put.mockImplementation(async (url: string, body: Record<string, unknown>) => {
      if (url === '/auth/profile') return { data: { user: { ...updatedUser, email: body.email } } }
      return { data: {} }
    })
    const user = userEvent.setup()
    render(<MemoryRouter><Profile /></MemoryRouter>)

    expect(screen.getByRole('note')).toHaveTextContent('账号信息由企业管理员维护')
    const email = screen.getByPlaceholderText('请输入联系邮箱')
    await user.clear(email)
    await user.type(email, 'after@example.invalid')
    await user.click(screen.getByRole('button', { name: '保存资料' }))
    await waitFor(() => expect(mocks.put).toHaveBeenCalledWith('/auth/profile', { email: 'after@example.invalid' }))
    expect(mocks.setUser).toHaveBeenCalledWith(updatedUser)

    await user.type(screen.getByPlaceholderText('请输入当前密码'), 'Old#2026')
    await user.type(screen.getByPlaceholderText('6-20 位'), 'New#2026')
    await user.type(screen.getByPlaceholderText('请再次输入新密码'), 'New#2026')
    await user.click(screen.getByRole('button', { name: '修改密码' }))
    await waitFor(() => expect(mocks.put).toHaveBeenCalledWith('/auth/password', {
      oldPassword: 'Old#2026', newPassword: 'New#2026',
    }))
    expect(mocks.logout).toHaveBeenCalledOnce()
  })

  it('profile locks both forms during save, rejects overlapping requests and unlocks after failure', async () => {
    let rejectSave: (reason?: unknown) => void = () => undefined
    mocks.put.mockImplementation(() => new Promise((_resolve, reject) => { rejectSave = reject }))
    const user = userEvent.setup()
    render(<MemoryRouter><Profile /></MemoryRouter>)

    const email = screen.getByPlaceholderText('请输入联系邮箱')
    await user.clear(email)
    await user.type(email, 'after@example.invalid')
    await user.click(screen.getByRole('button', { name: '保存资料' }))

    await waitFor(() => expect(email).toBeDisabled())
    expect(screen.getByPlaceholderText('请输入当前密码')).toBeDisabled()
    await user.click(screen.getByRole('button', { name: '保存资料' }))
    await user.click(screen.getByRole('button', { name: '修改密码' }))
    expect(mocks.put).toHaveBeenCalledTimes(1)

    rejectSave(new Error('simulated save failure'))
    await waitFor(() => expect(email).toBeEnabled())
    await user.click(screen.getByRole('button', { name: '保存资料' }))
    await waitFor(() => expect(mocks.put).toHaveBeenCalledTimes(2))
  })

  it('SAA branding is wired to the application logo and favicon', () => {
    renderLayout()
    const logo = screen.getByRole('img', { name: 'SAA' })
    expect(logo).toHaveAttribute('src', '/saa-logo.svg')

    const html = readFileSync(resolve(process.cwd(), 'index.html'), 'utf8')
    const parsed = new DOMParser().parseFromString(html, 'text/html')
    expect(parsed.querySelector('link[rel="icon"]')?.getAttribute('href')).toBe('/saa-logo.svg')
    expect(parsed.querySelector('link[rel="alternate icon"]')?.getAttribute('href')).toBe('/favicon.ico')
    for (const asset of ['saa-logo.svg', 'saa-logo.png', 'favicon.ico']) {
      expect(statSync(resolve(process.cwd(), 'public', asset)).size).toBeGreaterThan(0)
    }
  })

  it('sidebar keeps overflow inside the menu instead of the sider shell', () => {
    const removeStyles = installApplicationStyles()
    renderLayout()
    const sider = document.querySelector<HTMLElement>('.layout-sider')!
    const children = sider.querySelector<HTMLElement>(':scope > .arco-layout-sider-children')!
    const menu = sider.querySelector<HTMLElement>('.arco-menu')!

    expect(getComputedStyle(children).overflow).toBe('hidden')
    expect(getComputedStyle(menu).flexGrow).toBe('1')
    expect(getComputedStyle(menu).height).toBe('auto')
    removeStyles()
  })

  it('collapsed sidebar tooltip renders the menu label instead of duplicating its icon', async () => {
    localStorage.setItem('yf:sider-collapsed', 'true')
    const user = userEvent.setup()
    renderLayout()
    const item = screen.getByLabelText('工作台')
    expect(within(item).queryByText('工作台')).not.toBeInTheDocument()
    await user.hover(item)
    expect(await screen.findByText('工作台')).toBeVisible()
  })

  it('table action slots preserve action order and mark unavailable actions', () => {
    render(actionSlots([
      <button key="enter">进入</button>,
      false,
      <button key="status">状态</button>,
    ], 'project'))
    const slots = document.querySelectorAll('.action-slots--project > .action-slot')
    expect(slots).toHaveLength(3)
    expect(slots[0]).toHaveTextContent('进入')
    expect(slots[1]).toHaveClass('action-slot--empty')
    expect(slots[1]).toHaveAttribute('aria-hidden', 'true')
    expect(slots[2]).toHaveTextContent('状态')
  })

  it('paginated list pages constrain table body scrolling to keep pagination visible', async () => {
    const removeStyles = installApplicationStyles()
    const pages = [
      <MemoryRouter key="projects"><ProjectList /></MemoryRouter>,
      <SupplierList key="suppliers" />,
      <UserList key="users" />,
      <RoleList key="roles" />,
      <AuditLog key="audit" />,
    ]

    for (const pageComponent of pages) {
      const view = render(pageComponent)
      const card = await waitFor(() => view.container.querySelector<HTMLElement>('.page-card--table')!)
      const table = await waitFor(() => view.container.querySelector<HTMLElement>('.page-table')!)
      expect(card).toBeTruthy()
      expect(table).toBeTruthy()
      expect(getComputedStyle(card).overflow).toBe('hidden')
      const body = table.querySelector<HTMLElement>('.arco-table-body')
      if (body) expect(getComputedStyle(body).minHeight).toMatch(/^0(?:px)?$/)
      view.unmount()
    }
    removeStyles()
  })
})
