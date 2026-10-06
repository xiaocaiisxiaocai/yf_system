import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  delete: vi.fn(),
  permissions: [] as string[],
  user: { id: 1, employeeNo: 'admin', realName: '管理员', userType: 'INTERNAL', isSystemAdmin: false },
  departments: [] as Array<Record<string, unknown>>,
  roles: [] as Array<Record<string, unknown>>,
  permissionCatalog: [] as Array<Record<string, unknown>>,
  users: [] as Array<Record<string, unknown>>,
  suppliers: [] as Array<Record<string, unknown>>,
}))

vi.mock('../../../api/client', () => ({
  default: { get: mocks.get, post: mocks.post, put: mocks.put, delete: mocks.delete },
  bootAuth: vi.fn(),
}))

vi.mock('../../../store/auth', () => ({
  useAuth: (selector?: (state: unknown) => unknown) => {
    const state = {
      token: 'test-token', mustChangePassword: false, menus: ['supplier:list'],
      permissions: mocks.permissions,
      user: mocks.user,
      hasPerm: (code: string) => mocks.permissions.includes(code),
      logout: vi.fn(),
      setUser: vi.fn(),
    }
    return selector ? selector(state) : state
  },
}))

import { Guard } from '../../../App'
import DeptManage from '../../../pages/org/DeptManage'
import UserList from '../../../pages/org/UserList'
import RoleList from '../../../pages/rbac/RoleList'
import SupplierList from '../../../pages/supplier/SupplierList'
import ProjectList from '../../../pages/project/ProjectList'

function page(list: unknown[] = [], pageSize = 10) {
  return { list, total: list.length, page: 1, pageSize }
}

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((res, rej) => {
    resolve = res
    reject = rej
  })
  return { promise, resolve, reject }
}

function treeNode(label: string) {
  return screen.getByText(label).closest<HTMLElement>('.arco-tree-node')!
}

function treeCheckbox(label: string) {
  return treeNode(label).querySelector<HTMLInputElement>('input[type="checkbox"]')!
}

function moreTrigger() {
  return document.querySelector<HTMLButtonElement>('.org-view-more-trigger')!
}

function installDefaultGet() {
  mocks.get.mockImplementation(async (url: string) => {
    if (url === '/departments') return { data: mocks.departments }
    if (url === '/admin/department-leader-options') return { data: [] }
    if (url === '/permissions') return { data: mocks.permissionCatalog }
    if (url === '/admin/roles') return { data: page(mocks.roles, 20) }
    if (url === '/admin/user-role-options') return { data: mocks.roles.filter((role) => role.status === 'ACTIVE').map((role) => ({ id: role.id, name: role.name })) }
    if (url === '/admin/users') return { data: page(mocks.users) }
    if (url === '/admin/suppliers') return { data: page(mocks.suppliers) }
    if (url === '/supplier-options' || url === '/project-dictionaries') return { data: [] }
    if (url.endsWith('/accounts')) return { data: [] }
    return { data: page() }
  })
}

async function openRolePermissions(user: ReturnType<typeof userEvent.setup>, roleName = '测试') {
  const row = (await screen.findByText(roleName)).closest('tr')!
  await user.click(within(row).getByRole('button', { name: /分配权限|查看权限/ }))
  await screen.findByText(new RegExp(`(?:分配权限|查看权限) · ${roleName}`))
}

describe('组织、角色与权限行为', () => {
  beforeEach(() => {
    mocks.permissions = []
    mocks.user = { id: 1, employeeNo: 'admin', realName: '管理员', userType: 'INTERNAL', isSystemAdmin: false }
    mocks.departments = []
    mocks.roles = []
    mocks.permissionCatalog = []
    mocks.users = []
    mocks.suppliers = []
    installDefaultGet()
    mocks.post.mockResolvedValue({ data: {} })
    mocks.put.mockResolvedValue({ data: {} })
    mocks.delete.mockResolvedValue({ data: {} })
  })

  it('organization structure uses division, department and section levels', async () => {
    mocks.permissions = ['dept:manage', 'dept:delete']
    mocks.departments = [{
      id: 1, name: '事业一部', kind: 'DIVISION', sortNo: 1, status: 'ACTIVE',
      children: [{
        id: 2, name: '研发部', kind: 'DEPARTMENT', parentId: 1, sortNo: 3, status: 'DISABLED',
        children: [{ id: 3, name: '开发课', kind: 'SECTION', parentId: 2, sortNo: 1, status: 'ACTIVE', children: [] }],
      }],
    }]
    const user = userEvent.setup()
    render(<DeptManage />)

    expect(await screen.findByRole('heading', { name: '组织架构' })).toBeVisible()
    expect(screen.getByLabelText('组织统计')).toHaveTextContent('1 个事业部')
    expect(screen.getByLabelText('组织统计')).toHaveTextContent('1 个部门')
    expect(screen.getByLabelText('组织统计')).toHaveTextContent('1 个课别')
    await user.click(screen.getByText('研发部'))
    expect(screen.getByRole('navigation', { name: '层级路径' })).toHaveTextContent('事业一部')
    expect(screen.getByRole('navigation', { name: '层级路径' })).toHaveTextContent('研发部')
    expect(screen.getByRole('button', { name: '新增课别' })).toBeVisible()
    expect(screen.getByRole('button', { name: '编辑部门' })).toBeVisible()

    const search = screen.getByPlaceholderText('搜索组织名称')
    await user.type(search, '开发课')
    expect(screen.getAllByText('事业一部')[0]).toBeVisible()
    expect(screen.getAllByText('研发部')[0]).toBeVisible()
    expect(screen.getAllByText('开发课')[0]).toBeVisible()
    await user.clear(search)
    await user.type(search, '不存在的组织')
    expect(screen.getByText('未找到匹配的组织')).toBeVisible()
  })

  it('sets a department leader from the latest candidate query and updates the current tree data once', async () => {
    mocks.permissions = ['dept:leader_manage']
    mocks.departments = [{ id: 1, name: '事业一部', kind: 'DIVISION', sortNo: 1, status: 'ACTIVE', leader: null, children: [] }]
    const stale = deferred<{ data: Array<Record<string, unknown>> }>()
    const latest = deferred<{ data: Array<Record<string, unknown>> }>()
    const update = deferred<{ data: Record<string, unknown> }>()
    mocks.get.mockImplementation(async (url: string, config?: { params?: { keyword?: string } }) => {
      if (url === '/departments') return { data: mocks.departments }
      if (url === '/admin/department-leader-options') {
        if (config?.params?.keyword === '旧') return stale.promise
        if (config?.params?.keyword === '新') return latest.promise
        return { data: [] }
      }
      return { data: page() }
    })
    mocks.put.mockReturnValueOnce(update.promise)
    const user = userEvent.setup()
    render(<DeptManage />)

    await user.click((await screen.findAllByText('事业一部'))[0])
    expect(screen.queryByRole('button', { name: '新增事业部' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '编辑事业部' })).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: '设置主管' }))

    const search = screen.getByRole('textbox', { name: '搜索主管候选' })
    fireEvent.change(search, { target: { value: '旧' } })
    fireEvent.change(search, { target: { value: '新' } })
    latest.resolve({ data: [{ id: 22, employeeNo: 'E022', realName: '新主管', departmentName: '制造部' }] })
    expect(await screen.findByRole('radio', { name: /新主管.*E022.*制造部/ })).toBeVisible()
    stale.resolve({ data: [{ id: 21, employeeNo: 'E021', realName: '旧主管', departmentName: '旧部门' }] })
    await waitFor(() => expect(screen.queryByText('旧主管')).not.toBeInTheDocument())

    await user.click(screen.getByRole('radio', { name: /新主管.*E022.*制造部/ }))
    expect(screen.getByText('当前选择').parentElement).toHaveTextContent('新主管E022')
    const save = screen.getByRole('button', { name: '保存主管' })
    fireEvent.click(save)
    fireEvent.click(save)
    await waitFor(() => expect(mocks.put).toHaveBeenCalledTimes(1))
    expect(mocks.put).toHaveBeenCalledWith('/admin/departments/1/leader', { leaderUserId: 22 })
    update.resolve({ data: { id: 1, name: '事业一部', kind: 'DIVISION', leader: { id: 22, employeeNo: 'E022', realName: '新主管', active: true } } })

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(screen.getByLabelText('事业一部主管配置')).toHaveTextContent('新主管')
    await user.click(screen.getByRole('button', { name: '全部组织' }))
    expect(screen.getByText('主管：新主管（E022）')).toBeVisible()
  })

  it('clears the configured leader and keeps the detail and child summary in sync', async () => {
    mocks.permissions = ['dept:leader_manage']
    mocks.departments = [{
      id: 1, name: '事业一部', kind: 'DIVISION', sortNo: 1, status: 'ACTIVE',
      leader: { id: 20, employeeNo: 'E020', realName: '原主管', active: true }, children: [],
    }]
    mocks.put.mockResolvedValueOnce({ data: { id: 1, name: '事业一部', kind: 'DIVISION', leader: null } })
    const user = userEvent.setup()
    render(<DeptManage />)

    await user.click((await screen.findAllByText('事业一部'))[0])
    await user.click(screen.getByRole('button', { name: '清空主管' }))
    await user.click(await screen.findByRole('button', { name: '确认清空' }))

    await waitFor(() => expect(mocks.put).toHaveBeenCalledWith('/admin/departments/1/leader', { leaderUserId: null }))
    expect(screen.getByLabelText('事业一部主管配置')).toHaveTextContent('暂未配置主管')
    await user.click(screen.getByRole('button', { name: '全部组织' }))
    expect(screen.queryByText('主管：原主管（E020）')).not.toBeInTheDocument()
  })

  it('becomes read-only when leader permission is removed and discards the pending candidate response', async () => {
    mocks.permissions = ['dept:leader_manage']
    mocks.departments = [{
      id: 1, name: '事业一部', kind: 'DIVISION', sortNo: 1, status: 'ACTIVE',
      leader: { id: 20, employeeNo: 'E020', realName: '现任主管', active: true }, children: [],
    }]
    const pending = deferred<{ data: Array<Record<string, unknown>> }>()
    mocks.get.mockImplementation(async (url: string) => {
      if (url === '/departments') return { data: mocks.departments }
      if (url === '/admin/department-leader-options') return pending.promise
      return { data: page() }
    })
    const user = userEvent.setup()
    const view = render(<DeptManage />)

    await user.click((await screen.findAllByText('事业一部'))[0])
    await user.click(screen.getByRole('button', { name: '更换主管' }))
    expect(screen.getByRole('dialog', { name: /设置主管/ })).toBeVisible()
    expect(screen.getByText('当前选择').parentElement).toHaveTextContent('现任主管E020')
    expect(screen.getByRole('button', { name: '保存主管' })).toBeDisabled()
    mocks.permissions = []
    view.rerender(<DeptManage />)
    await waitFor(() => expect(screen.queryByRole('dialog', { name: /设置主管/ })).not.toBeInTheDocument())
    pending.resolve({ data: [{ id: 25, employeeNo: 'E025', realName: '迟到候选', departmentName: null }] })

    await waitFor(() => expect(screen.queryByText('迟到候选')).not.toBeInTheDocument())
    expect(screen.getByLabelText('事业一部主管配置')).toHaveTextContent('现任主管')
    expect(screen.queryByRole('button', { name: '更换主管' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '清空主管' })).not.toBeInTheDocument()
  })

  it('keeps the selected leader after a save error and allows a retry', async () => {
    mocks.permissions = ['dept:leader_manage']
    mocks.departments = [{ id: 1, name: '事业一部', kind: 'DIVISION', sortNo: 1, status: 'ACTIVE', leader: null, children: [] }]
    mocks.get.mockImplementation(async (url: string) => {
      if (url === '/departments') return { data: mocks.departments }
      if (url === '/admin/department-leader-options') {
        return { data: [{ id: 22, employeeNo: 'E022', realName: '候选主管', departmentName: '制造部' }] }
      }
      return { data: page() }
    })
    mocks.put
      .mockRejectedValueOnce(new Error('save failed'))
      .mockResolvedValueOnce({ data: { id: 1, name: '事业一部', kind: 'DIVISION', leader: { id: 22, employeeNo: 'E022', realName: '候选主管', active: true } } })
    const user = userEvent.setup()
    render(<DeptManage />)

    await user.click((await screen.findAllByText('事业一部'))[0])
    await user.click(screen.getByRole('button', { name: '设置主管' }))
    const option = await screen.findByRole('radio', { name: /候选主管.*E022.*制造部/ })
    await user.click(option)
    await user.click(screen.getByRole('button', { name: '保存主管' }))

    expect(await screen.findByText('主管配置保存失败，请重试')).toBeVisible()
    expect(screen.getByRole('dialog', { name: /设置主管/ })).toBeVisible()
    expect(option).toHaveAttribute('aria-checked', 'true')
    await user.click(screen.getByRole('button', { name: '保存主管' }))
    await waitFor(() => expect(mocks.put).toHaveBeenCalledTimes(2))
    await waitFor(() => expect(screen.queryByRole('dialog', { name: /设置主管/ })).not.toBeInTheDocument())
  })

  it('hard delete actions require their dedicated delete permission', async () => {
    const cases = [
      {
        render: () => render(<MemoryRouter><ProjectList /></MemoryRouter>),
        pagePermission: 'project:update', deletePermission: 'project:delete', rowText: '可删主项目',
        setup: () => { mocks.get.mockImplementation(async (url: string) => ({ data: url === '/project-groups' ? page([{ id: 1, name: '可删主项目', status: 'DRAFT', subprojectCount: 0 }]) : [] })) },
      },
      {
        render: () => render(<SupplierList />),
        pagePermission: 'supplier:manage', deletePermission: 'supplier:delete', rowText: '可删供应商',
        setup: () => { mocks.suppliers = [{ id: 8, name: '可删供应商', status: 'ACTIVE', createdAt: '' }] },
      },
      {
        render: () => render(<UserList />),
        pagePermission: 'user:manage', deletePermission: 'user:delete', rowText: '可删用户',
        setup: () => { mocks.users = [{ id: 2, employeeNo: 'staff', realName: '可删用户', email: 'staff@example.invalid', status: 'ACTIVE', createdAt: '' }] },
      },
      {
        render: () => render(<RoleList />),
        pagePermission: 'role:manage', deletePermission: 'role:delete', rowText: '可删角色',
        setup: () => { mocks.roles = [{ id: 9, name: '可删角色', isBuiltIn: false, permissionIds: [], assignedUserCount: 0, status: 'ACTIVE' }] },
      },
    ]

    for (const item of cases) {
      installDefaultGet()
      item.setup()
      mocks.permissions = [item.pagePermission]
      let view = item.render()
      let row = (await screen.findByText(item.rowText)).closest('tr')!
      expect(within(row).queryByRole('button', { name: '删除' })).not.toBeInTheDocument()
      view.unmount()

      installDefaultGet()
      item.setup()
      mocks.permissions = [item.pagePermission, item.deletePermission]
      view = item.render()
      row = (await screen.findByText(item.rowText)).closest('tr')!
      expect(within(row).getByRole('button', { name: '删除' })).toBeVisible()
      view.unmount()
    }
  })

  it('department hard delete requires the dedicated delete permission', async () => {
    mocks.departments = [{ id: 3, name: '开发课', kind: 'SECTION', parentId: 2, sortNo: 1, status: 'ACTIVE', children: [] }]
    const user = userEvent.setup()

    mocks.permissions = ['dept:manage']
    let view = render(<DeptManage />)
    await user.click(await screen.findByText('开发课'))
    fireEvent.click(moreTrigger())
    expect(screen.queryByText('删除课别')).not.toBeInTheDocument()
    view.unmount()

    mocks.permissions = ['dept:manage', 'dept:delete']
    view = render(<DeptManage />)
    await user.click(await screen.findByText('开发课'))
    fireEvent.click(moreTrigger())
    expect(await screen.findByText('删除课别')).toBeVisible()
  })

  it('organization more actions require confirmation and retain the selected target', async () => {
    mocks.permissions = ['dept:manage', 'dept:delete']
    mocks.departments = [
      { id: 1, name: '事业一部', kind: 'DIVISION', sortNo: 1, status: 'ACTIVE', children: [] },
      { id: 2, name: '事业二部', kind: 'DIVISION', sortNo: 2, status: 'ACTIVE', children: [] },
    ]
    const user = userEvent.setup()
    render(<DeptManage />)
    await user.click((await screen.findAllByText('事业一部'))[0])
    fireEvent.click(moreTrigger())
    const deleteItems = await screen.findAllByText('删除事业部')
    fireEvent.click(deleteItems[0].closest('.arco-dropdown-menu-item') ?? deleteItems[0])
    expect(await screen.findByText('删除事业部')).toBeVisible()
    expect(screen.getByText(/有下级或关联用户时无法删除/)).toBeVisible()

    fireEvent.click(screen.getByRole('button', { name: '确认删除' }))
    await waitFor(() => expect(mocks.delete).toHaveBeenCalledWith('/admin/departments/1'))
  })

  it('permission tree keeps menu-only grants, adds a parent for actions and removes actions with an unchecked parent', async () => {
    mocks.permissions = ['role:manage']
    mocks.roles = [{ id: 7, name: '测试', isBuiltIn: false, permissionIds: [5], assignedUserCount: 0, status: 'ACTIVE' }]
    mocks.permissionCatalog = [
      { id: 5, code: 'org:dept', name: '部门', type: 'MENU', parentId: null, grantable: true, supplierAssignable: true },
      { id: 24, code: 'dept:manage', name: '管理部门', type: 'ACTION', parentId: 5, grantable: true, supplierAssignable: true },
    ]
    const user = userEvent.setup()
    render(<RoleList />)
    await openRolePermissions(user)

    expect(treeCheckbox('部门').parentElement).toHaveClass('arco-checkbox-indeterminate')
    await user.click(treeCheckbox('管理部门'))
    expect(treeCheckbox('部门').checked).toBe(true)
    await user.click(treeCheckbox('部门'))
    expect(treeCheckbox('管理部门').checked).toBe(false)
  })

  it('supplier route accepts either supplier capability while account-only UI keeps supplier writes hidden', async () => {
    mocks.permissions = ['supplier:account']
    mocks.suppliers = [{ id: 8, name: '供应商', status: 'ACTIVE', createdAt: '' }]
    const allowed = render(<MemoryRouter><Guard menu="supplier:list" anyPermission={['supplier:manage', 'supplier:account']}><div>已允许进入</div></Guard></MemoryRouter>)
    expect(screen.getByText('已允许进入')).toBeVisible()
    allowed.unmount()

    render(<SupplierList />)
    const row = (await screen.findByText('供应商')).closest('tr')!
    expect(within(row).getByRole('button', { name: '账号管理' })).toBeVisible()
    expect(within(row).queryByRole('button', { name: '编辑' })).not.toBeInTheDocument()
    expect(within(row).queryByRole('button', { name: '禁用' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '新增供应商' })).not.toBeInTheDocument()
  })

  it('role capabilities make protected roles read-only and exclude unsafe supplier permissions from cascades', async () => {
    mocks.permissions = ['role:manage']
    mocks.roles = [
      { id: 7, name: '供应商人员', isBuiltIn: true, permissionIds: [2], assignedUserCount: 0, status: 'ACTIVE', canManage: true, supplierRestricted: true },
      { id: 1, name: '系统管理员', isBuiltIn: true, permissionIds: [6, 32], assignedUserCount: 1, status: 'ACTIVE', canManage: false, supplierRestricted: false },
    ]
    mocks.permissionCatalog = [
      { id: 2, code: 'project:list', name: '项目协作', type: 'MENU', parentId: null, grantable: true, supplierAssignable: true },
      { id: 9, code: 'file:upload', name: '上传文件', type: 'ACTION', parentId: 2, grantable: true, supplierAssignable: true },
      { id: 39, code: 'project:confirm', name: '确认验收', type: 'ACTION', parentId: 2, grantable: true, supplierAssignable: false },
    ]
    const user = userEvent.setup()
    render(<RoleList />)
    const protectedRow = (await screen.findByText('系统管理员')).closest('tr')!
    expect(within(protectedRow).getByRole('button', { name: '查看权限' })).toBeVisible()
    expect(within(protectedRow).queryByRole('button', { name: '编辑' })).not.toBeInTheDocument()

    await openRolePermissions(user, '供应商人员')
    expect(treeCheckbox('确认验收')).toBeDisabled()
    await user.click(treeCheckbox('项目协作'))
    expect(treeCheckbox('上传文件').checked).toBe(true)
    expect(treeCheckbox('确认验收').checked).toBe(false)
  })

  it('permission groups cascade on clicks while preserving existing menu-only grants', async () => {
    mocks.permissions = ['role:manage']
    mocks.roles = [{ id: 7, name: '测试', isBuiltIn: false, permissionIds: [5], assignedUserCount: 0, status: 'ACTIVE' }]
    mocks.permissionCatalog = [
      { id: 1, code: 'dashboard', name: '工作台', type: 'MENU', parentId: null, grantable: true },
      { id: 5, code: 'org:dept', name: '部门', type: 'MENU', parentId: null, grantable: true },
      { id: 24, code: 'dept:manage', name: '管理部门', type: 'ACTION', parentId: 5, grantable: true },
      { id: 25, code: 'dept:delete', name: '删除部门', type: 'ACTION', parentId: 5, grantable: true },
    ]
    const user = userEvent.setup()
    render(<RoleList />)
    await openRolePermissions(user)
    expect(treeCheckbox('部门').parentElement).toHaveClass('arco-checkbox-indeterminate')

    await user.click(treeCheckbox('工作台'))
    expect(treeCheckbox('部门').parentElement).toHaveClass('arco-checkbox-indeterminate')
    await user.click(treeCheckbox('部门'))
    expect(treeCheckbox('管理部门').checked).toBe(true)
    expect(treeCheckbox('删除部门').checked).toBe(true)
    await user.click(treeCheckbox('管理部门'))
    expect(treeCheckbox('部门').parentElement).toHaveClass('arco-checkbox-indeterminate')

    await user.click(screen.getByRole('button', { name: '保存权限' }))
    await waitFor(() => expect(mocks.put).toHaveBeenCalledWith('/admin/roles/7/permissions', {
      permissionIds: expect.arrayContaining([1, 5, 25]),
    }))
  })

  it('disabled users can keep their unavailable role while editing profile, but cannot choose another unavailable role', async () => {
    mocks.permissions = ['user:manage']
    mocks.departments = [{ id: 4, name: '研发部', kind: 'DEPARTMENT', status: 'ACTIVE', sortNo: 1, children: [] }]
    mocks.roles = [{ id: 7, name: '内部成员', status: 'ACTIVE' }]
    mocks.users = [{
      id: 9, employeeNo: 'disabled-user', realName: '停用用户', email: 'disabled@example.invalid',
      departmentId: 4, departmentName: '研发部', roleId: 41, roleName: '历史角色', status: 'DISABLED', createdAt: '',
    }]
    const user = userEvent.setup()
    render(<UserList />)
    const row = (await screen.findByText('停用用户')).closest('tr')!
    await user.click(within(row).getByRole('button', { name: '编辑' }))

    const roleLabel = screen.getAllByText('角色').find((element) => element.tagName === 'LABEL')!
    const roleSelect = roleLabel.closest('.arco-form-item')!.querySelector('[role="combobox"]')!
    fireEvent.click(roleSelect)
    const retired = (await screen.findAllByText(/历史角色（已禁用）/)).find((element) => element.closest('li'))!
    expect(retired.closest('li')).toHaveClass('arco-select-option-disabled')
    expect(screen.queryByText(/另一个已禁用角色/)).not.toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: '保存用户' }))
    await waitFor(() => expect(mocks.put).toHaveBeenCalledWith('/admin/users/9', expect.objectContaining({ roleId: 41 })))
  })

  it('user status toggle confirms success and a failed background refresh keeps the loaded table', async () => {
    mocks.permissions = ['user:manage']
    mocks.users = [{
      id: 9, employeeNo: 'e9', realName: '在职用户', email: 'e9@example.invalid',
      departmentId: 4, departmentName: '研发部', roleId: 7, roleName: '内部成员', status: 'ACTIVE', createdAt: '',
    }]
    render(<UserList />)
    const row = (await screen.findByText('在职用户')).closest('tr')!
    let userListCalls = 0
    mocks.get.mockImplementation(async (url: string) => {
      if (url === '/admin/users') {
        userListCalls += 1
        throw new Error('refresh failed')
      }
      if (url === '/departments' || url === '/admin/user-role-options') return { data: [] }
      return { data: page() }
    })
    fireEvent.click(within(row).getByRole('button', { name: '禁用' }))
    fireEvent.click(await screen.findByRole('button', { name: '确定' }))

    await waitFor(() => expect(mocks.put).toHaveBeenCalledWith('/admin/users/9/status', { status: 'DISABLED' }))
    expect(await screen.findByText('用户已禁用')).toBeInTheDocument()
    await waitFor(() => expect(userListCalls).toBeGreaterThan(0))
    expect(await screen.findByText('当前显示上次加载的数据')).toBeVisible()
    expect(screen.getByText('在职用户')).toBeInTheDocument()
  })

  it('built-in roles never expose hard delete while custom roles still can', async () => {
    mocks.permissions = ['role:manage', 'role:delete']
    mocks.roles = [
      { id: 1, name: '供应商人员', isBuiltIn: true, status: 'ACTIVE', permissionIds: [], assignedUserCount: 0 },
      { id: 2, name: '自定义角色', isBuiltIn: false, status: 'ACTIVE', permissionIds: [], assignedUserCount: 0 },
    ]
    render(<RoleList />)
    const builtIn = (await screen.findByText('供应商人员')).closest('tr')!
    const custom = screen.getByText('自定义角色').closest('tr')!
    expect(within(builtIn).queryByRole('button', { name: '删除' })).not.toBeInTheDocument()
    expect(within(custom).getByRole('button', { name: '删除' })).toBeVisible()
  })
})
