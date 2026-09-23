import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  delete: vi.fn(),
  logout: vi.fn(),
  setUser: vi.fn(),
  permissions: ['dept:manage', 'dept:delete', 'user:delete', 'role:delete', 'supplier:manage', 'supplier:account', 'supplier:delete', 'project:create', 'project:update'],
  user: { id: 1, employeeNo: 'admin', realName: '管理员', email: 'admin@example.invalid', userType: 'INTERNAL', isSystemAdmin: true },
}))

vi.mock('../../../api/client', () => ({
  default: { get: mocks.get, post: mocks.post, put: mocks.put, delete: mocks.delete },
  withAuthLock: async (operation: () => unknown) => operation(),
}))

vi.mock('../../../store/auth', () => ({
  useAuth: (selector?: (state: unknown) => unknown) => {
    const state = {
      token: 'test-token', mustChangePassword: false, menus: [],
      permissions: mocks.permissions,
      user: mocks.user,
      hasPerm: (code: string) => mocks.permissions.includes(code),
      logout: mocks.logout,
      setUser: mocks.setUser,
    }
    return selector ? selector(state) : state
  },
}))

vi.mock('../../../store/collaboration', () => ({
  useCollaboration: (selector: (state: { revision: string; status: string }) => unknown) => selector({ revision: '', status: 'ready' }),
}))

import ChangePassword from '../../../pages/ChangePassword'
import Profile from '../../../pages/Profile'
import DeptManage from '../../../pages/org/DeptManage'
import UserList from '../../../pages/org/UserList'
import RoleList from '../../../pages/rbac/RoleList'
import SupplierList from '../../../pages/supplier/SupplierList'
import ProjectList from '../../../pages/project/ProjectList'
import {
  PASSWORD_MAX_BYTES,
  PASSWORD_MAX_CHARS,
  PASSWORD_MIN_CHARS,
  PASSWORD_VALIDATION_MESSAGE,
  validatePassword,
} from '../../../utils/password'
import { normalizeList } from '../../../utils/listValues'
import { textLengthRule } from '../../../utils/textRules'

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((ok, fail) => { resolve = ok; reject = fail })
  return { promise, resolve, reject }
}

function page(list: unknown[] = [], pageSize = 10) {
  return { list, total: list.length, page: 1, pageSize }
}

function ruleError(label: string, maximum: number, value: string) {
  let result: string | undefined
  textLengthRule(label, maximum).validator!(value, (error) => { result = typeof error === 'string' ? error : undefined })
  return result
}

async function openArcoSelect(placeholder: string, option: string) {
  const input = screen.getByPlaceholderText(placeholder)
  const combobox = input.closest<HTMLElement>('[role="combobox"]') ?? input.closest<HTMLElement>('.arco-select') ?? input
  await waitFor(() => expect(combobox).not.toHaveAttribute('aria-disabled', 'true'))
  fireEvent.click(combobox)
  const options = await screen.findAllByText(option)
  fireEvent.click(options.at(-1)!.closest('li') ?? options.at(-1)!)
}

describe('管理员表单契约', () => {
  beforeEach(() => {
    mocks.permissions = ['dept:manage', 'dept:delete', 'user:delete', 'role:delete', 'supplier:manage', 'supplier:account', 'supplier:delete', 'project:create', 'project:update']
    mocks.user = { id: 1, employeeNo: 'admin', realName: '管理员', email: 'admin@example.invalid', userType: 'INTERNAL', isSystemAdmin: true }
    mocks.get.mockImplementation(async (url: string) => {
      if (url === '/departments') return { data: [{ id: 4, name: '研发部', kind: 'DEPARTMENT', status: 'ACTIVE', sortNo: 1, children: [] }] }
      if (url === '/admin/user-role-options') return { data: [{ id: 7, name: '内部成员' }] }
      if (url === '/permissions' || url === '/supplier-options' || url === '/project-dictionaries') return { data: [] }
      if (url === '/admin/roles') return { data: page([], 20) }
      return { data: page() }
    })
    mocks.post.mockResolvedValue({ data: {} })
    mocks.put.mockResolvedValue({ data: {} })
    mocks.delete.mockResolvedValue({ data: {} })
  })

  it('password contract counts Unicode scalars and is shared by every password-writing form', async () => {
    expect(PASSWORD_MIN_CHARS).toBe(6)
    expect(PASSWORD_MAX_CHARS).toBe(20)
    expect(PASSWORD_MAX_BYTES).toBe(256)
    const sixCharacters = 'A中🙂b2#'
    const twentyCharacters = sixCharacters.repeat(3) + 'Z9'
    expect(Array.from(sixCharacters)).toHaveLength(6)
    expect(Array.from(twentyCharacters)).toHaveLength(20)
    expect(validatePassword('A中🙂2#')).toMatch(/6-20/)
    expect(validatePassword(sixCharacters)).toBeUndefined()
    expect(validatePassword(twentyCharacters)).toBeUndefined()
    expect(validatePassword(`${twentyCharacters}x`)).toMatch(/6-20/)
    for (const weak of ['短密码', 'abcdef', '中'.repeat(6), 'Password123456!', 'P@ssw0rd1234!', 'passwordpassword1!', 'qwerty123456', '123456789012', 'abcdabcdabcd']) {
      expect(validatePassword(weak)).toBe(PASSWORD_VALIDATION_MESSAGE)
    }

    const changePassword = render(<MemoryRouter><ChangePassword /></MemoryRouter>)
    expect(screen.getByPlaceholderText(/新密码（6–20 位）/)).toBeVisible()
    changePassword.unmount()

    const profile = render(<MemoryRouter><Profile /></MemoryRouter>)
    expect(screen.getByPlaceholderText('6-20 位')).toBeVisible()
    profile.unmount()

    const user = userEvent.setup()
    const users = render(<UserList />)
    await user.click(await screen.findByRole('button', { name: '新增用户' }))
    expect(screen.getByPlaceholderText('6-20 位')).toBeVisible()
    users.unmount()

    mocks.get.mockImplementation(async (url: string) => {
      if (url === '/admin/suppliers') return { data: page([{ id: 8, name: '供应商', status: 'ACTIVE', createdAt: '' }]) }
      if (url === '/admin/suppliers/8/accounts' || url === '/admin/supplier-role-options') return { data: [] }
      return { data: [] }
    })
    const suppliers = render(<SupplierList />)
    await user.click(await screen.findByRole('button', { name: '账号管理' }))
    await user.click(await screen.findByRole('button', { name: '新增账号' }))
    expect(screen.getByPlaceholderText('6-20 位')).toBeVisible()
    suppliers.unmount()
  })

  it('form limits match the backend character contracts', async () => {
    const user = userEvent.setup()

    const dept = render(<DeptManage />)
    await user.click(await screen.findByRole('button', { name: '新增事业部' }))
    expect(screen.getByPlaceholderText('请输入事业部名称')).toHaveAttribute('maxlength', '64')
    dept.unmount()

    const role = render(<RoleList />)
    await user.click(await screen.findByRole('button', { name: '新增角色' }))
    expect(screen.getByPlaceholderText('选填')).toHaveAttribute('maxlength', '255')
    role.unmount()

    const supplier = render(<SupplierList />)
    await user.click(await screen.findByRole('button', { name: '新增供应商' }))
    expect(screen.getByPlaceholderText('选填')).toHaveAttribute('maxlength', '500')
    supplier.unmount()
  })

  it('required business names and emails validate scalar lengths before submitting', async () => {
    const contracts: Array<[string, number]> = [
      ['名称', 128], ['机器型号', 128], ['名称', 64], ['供应商名称', 64],
      ['姓名', 32], ['邮箱', 128], ['联系邮箱', 128],
    ]
    for (const [label, maximum] of contracts) {
      expect(ruleError(label, maximum, '😀'.repeat(maximum))).toBeUndefined()
      expect(ruleError(label, maximum, '😀'.repeat(maximum + 1))).toContain(String(maximum))
      expect(ruleError(label, maximum, '   ')).toContain(String(maximum))
      expect(ruleError(label, maximum, '  正常名称  ')).toBeUndefined()
    }

    const views = [
      render(<MemoryRouter><ProjectList /></MemoryRouter>),
      render(<RoleList />),
      render(<SupplierList />),
      render(<UserList />),
      render(<MemoryRouter><Profile /></MemoryRouter>),
    ]
    expect(await screen.findByPlaceholderText('请输入联系邮箱')).toBeVisible()
    expect(screen.getByRole('button', { name: '新增角色' })).toBeVisible()
    expect(screen.getByRole('button', { name: '新增供应商' })).toBeVisible()
    expect(screen.getByRole('button', { name: '新增用户' })).toBeVisible()
    expect(screen.getByRole('button', { name: '新建主项目' })).toBeVisible()
    views.forEach((view) => view.unmount())
  })

  it('project list normalizes work-order and subproject values like the backend', () => {
    expect(normalizeList([' WO-1 ', 'wo-1', 'WO-2', '', '  ', 'Wo-2'])).toEqual(['WO-1', 'WO-2'])
  })

  it('internal user form requires an organization', async () => {
    const user = userEvent.setup()
    render(<UserList />)
    await user.click(await screen.findByRole('button', { name: '新增用户' }))
    await user.type(screen.getByPlaceholderText('3-32 位字母、数字或下划线'), 'employee_1')
    await user.type(screen.getByPlaceholderText('6-20 位'), 'Strong#2026')
    await user.type(screen.getByPlaceholderText('姓名'), '测试用户')
    await user.type(screen.getByPlaceholderText('name@example.com'), 'employee@example.invalid')
    await openArcoSelect('选择角色', '内部成员')
    await user.click(screen.getByRole('button', { name: '创建用户' }))

    expect(await screen.findByText('请选择所属组织')).toBeVisible()
    expect(mocks.post).not.toHaveBeenCalled()
    expect(screen.getByPlaceholderText('选择组织')).not.toHaveAttribute('allowclear')
  })

  it('password change rejects same-render duplicate submissions and unlocks after completion', async () => {
    const first = deferred<{ data: Record<string, never> }>()
    mocks.put.mockImplementationOnce(() => first.promise).mockResolvedValue({ data: {} })
    const user = userEvent.setup()
    render(<MemoryRouter><ChangePassword /></MemoryRouter>)
    await user.type(screen.getByLabelText('原密码'), 'Old#2026')
    await user.type(screen.getByLabelText('新密码'), 'New#2026')
    await user.type(screen.getByLabelText('确认新密码'), 'New#2026')
    const form = screen.getByRole('button', { name: '确认修改' }).closest('form')!

    fireEvent.submit(form)
    fireEvent.submit(form)
    await waitFor(() => expect(mocks.put).toHaveBeenCalledTimes(1))
    first.resolve({ data: {} })
    await waitFor(() => expect(screen.getByRole('button', { name: '确认修改' })).toBeEnabled())

    fireEvent.submit(form)
    await waitFor(() => expect(mocks.put).toHaveBeenCalledTimes(2))
  })

  it('user creation rejects same-render duplicate confirmations', async () => {
    const pending = deferred<{ data: Record<string, never> }>()
    mocks.post.mockReturnValue(pending.promise)
    const user = userEvent.setup()
    render(<UserList />)
    await user.click(await screen.findByRole('button', { name: '新增用户' }))
    await user.type(screen.getByPlaceholderText('3-32 位字母、数字或下划线'), 'employee_1')
    await user.type(screen.getByPlaceholderText('6-20 位'), 'Strong#2026')
    await user.type(screen.getByPlaceholderText('姓名'), '测试用户')
    await user.type(screen.getByPlaceholderText('name@example.com'), 'employee@example.invalid')
    await openArcoSelect('选择组织', '研发部（部门）')
    await openArcoSelect('选择角色', '内部成员')
    const confirm = screen.getByRole('button', { name: '创建用户' })

    fireEvent.click(confirm)
    fireEvent.click(confirm)
    await waitFor(() => expect(mocks.post).toHaveBeenCalledTimes(1))
    pending.resolve({ data: {} })
  })

  it.each([
    ['ChangePassword', () => render(<MemoryRouter><ChangePassword /></MemoryRouter>)],
    ['Profile', () => render(<MemoryRouter><Profile /></MemoryRouter>)],
  ])('%s rejects the current password without submitting or ending the session', async (pageName, renderPage) => {
    const user = userEvent.setup()
    renderPage()
    const oldPassword = pageName === 'ChangePassword'
      ? screen.getByLabelText('原密码')
      : screen.getByPlaceholderText('请输入当前密码')
    const newPassword = pageName === 'ChangePassword'
      ? screen.getByLabelText('新密码')
      : screen.getByPlaceholderText('6-20 位')
    const confirmPassword = pageName === 'ChangePassword'
      ? screen.getByLabelText('确认新密码')
      : screen.getByPlaceholderText('请再次输入新密码')
    await user.type(oldPassword, 'Unchanged#2026')
    await user.type(newPassword, 'Unchanged#2026')
    await user.type(confirmPassword, 'Unchanged#2026')
    await user.click(screen.getByRole('button', { name: pageName === 'ChangePassword' ? '确认修改' : '修改密码' }))

    expect((await screen.findAllByText('新密码不能与当前密码相同')).at(-1)).toBeVisible()
    expect(mocks.put).not.toHaveBeenCalled()
    expect(mocks.logout).not.toHaveBeenCalled()
  })
})
