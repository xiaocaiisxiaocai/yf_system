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
