import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({
  get: vi.fn(), post: vi.fn(), put: vi.fn(), delete: vi.fn(),
  getImpl: async (_url: string, _config?: unknown): Promise<{ data: unknown }> => ({ data: [] }),
  permissions: [] as string[],
  user: { id: 1, employeeNo: 'admin', realName: '管理员', userType: 'INTERNAL', isSystemAdmin: true },
}))

vi.mock('../../../api/client', () => ({
  default: {
    get: (...args: [string, unknown?]) => mocks.get(...args),
    post: mocks.post, put: mocks.put, delete: mocks.delete,
  },
  withAuthLock: async (operation: () => unknown) => operation(),
}))

vi.mock('../../../store/auth', () => ({
  useAuth: (selector?: (state: unknown) => unknown) => {
    const state = {
      token: 'test-token', menus: ['project:list'], permissions: mocks.permissions,
      user: mocks.user,
      hasPerm: (code: string) => mocks.permissions.includes(code),
      logout: vi.fn(), setUser: vi.fn(),
    }
    return selector ? selector(state) : state
  },
}))

vi.mock('../../../store/collaboration', () => ({
  useCollaboration: (selector: (state: { revision: string; status: string }) => unknown) => selector({ revision: '', status: 'ready' }),
}))

import SupplierList from '../../../pages/supplier/SupplierList'
import UserList from '../../../pages/org/UserList'
import RoleList from '../../../pages/rbac/RoleList'
import ProjectList from '../../../pages/project/ProjectList'
import ProjectGroupDetail from '../../../pages/project/ProjectGroupDetail'
import FileTable from '../../../components/FileTable'
import AuditLog from '../../../pages/system/AuditLog'

function page(list: unknown[] = [], pageSize = 10, current = 1, total = list.length) {
  return { list, total, page: current, pageSize }
}

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((ok, fail) => { resolve = ok; reject = fail })
  return { promise, resolve, reject }
}

function installGet(handler: typeof mocks.getImpl) {
  mocks.getImpl = handler
  mocks.get.mockImplementation((url: string, config?: unknown) => mocks.getImpl(url, config))
}

function supplierRow(overrides: Record<string, unknown> = {}) {
  return { id: 8, name: 'fixture', status: 'ACTIVE', createdAt: '', ...overrides }
}

function account(id: number, employeeNo: string) {
  return { id, employeeNo, realName: `账号${id}`, email: `${employeeNo}@example.invalid`, roleName: '供应商人员', status: 'ACTIVE', createdAt: '', lastLoginAt: null }
}

async function openAccounts(user: ReturnType<typeof userEvent.setup>, supplierName = 'fixture') {
  const row = (await screen.findByText(supplierName)).closest('tr')!
  await user.click(within(row).getByRole('button', { name: '账号管理' }))
  await screen.findByText(new RegExp(`账号管理 · ${supplierName}`))
}

describe('供应商与分页列表行为', () => {
  beforeEach(() => {
    mocks.permissions = ['project:create', 'project:update', 'project:delete', 'supplier:manage', 'supplier:account', 'supplier:delete', 'supplier:account_delete', 'user:manage', 'user:delete', 'role:manage', 'role:delete', 'file:download', 'file:delete']
    mocks.user = { id: 1, employeeNo: 'admin', realName: '管理员', userType: 'INTERNAL', isSystemAdmin: true }
    installGet(async (url: string) => {
      if (url === '/departments' || url === '/admin/user-role-options' || url === '/permissions' || url === '/supplier-options' || url === '/project-dictionaries') return { data: [] }
      if (url === '/admin/roles' || url === '/admin/audit-logs') return { data: page([], 20) }
      return { data: page() }
    })
    mocks.post.mockResolvedValue({ data: {} })
    mocks.put.mockResolvedValue({ data: {} })
    mocks.delete.mockResolvedValue({ data: {} })
  })

  it('project interaction wording and navigation match the current supplier access model', async () => {
    installGet(async (url: string) => {
      if (url === '/project-groups') return { data: page() }
      if (url === '/project-groups/3') return { data: { group: { id: 3, name: '主项目', status: 'IN_PROGRESS', subprojectCount: 0, completedCount: 0, pendingCount: 0, terminatedCount: 0 }, projects: [] } }
      if (url === '/projects/9/files') return { data: page([{ id: 4, originalName: '复制件.pdf', ext: 'pdf', sizeBytes: 1, direction: 'C2S', createdAt: '', isCopiedReference: true }]) }
      return { data: [] }
    })
    const user = userEvent.setup()
    let view = render(<MemoryRouter><ProjectList /></MemoryRouter>)
    await user.click(await screen.findByRole('button', { name: '新建主项目' }))
    expect(screen.getByText(/全部启用账号均可访问此主项目及其子项目/)).toBeVisible()
    view.unmount()

    view = render(<MemoryRouter initialEntries={['/project-groups/3']}><Routes><Route path="/project-groups/:id" element={<ProjectGroupDetail />} /></Routes></MemoryRouter>)
    await user.click(await screen.findByRole('button', { name: '查看资料' }))
    expect(screen.getByText(/该 Robot 厂商的全部启用账号均可访问/)).toBeVisible()
    view.unmount()

    view = render(<FileTable projectId={9} projectStatus="IN_PROGRESS" />)
    expect(await screen.findByText('复制件')).toBeVisible()
    expect(screen.getByText(/文件当前不统计已读状态/)).toBeVisible()
    view.unmount()
  })

  it('supplier account permission revocation removes the open account drawer', async () => {
    mocks.permissions = []
    let accountRequests = 0
    installGet(async (url: string) => {
      if (url.endsWith('/accounts')) { accountRequests += 1; return { data: [] } }
      return { data: page([supplierRow()]) }
    })
    const user = userEvent.setup()
    const view = render(<SupplierList />)
    expect(await screen.findByText('fixture')).toBeVisible()
    expect(screen.queryByRole('button', { name: '账号管理' })).not.toBeInTheDocument()

    mocks.permissions = ['supplier:account']
    view.rerender(<SupplierList />)
    await openAccounts(user)
    expect(document.querySelector('.account-drawer')).toBeInTheDocument()
    expect(accountRequests).toBe(1)

    mocks.permissions = []
    view.rerender(<SupplierList />)
    expect(document.querySelector('.account-drawer')).not.toBeInTheDocument()
    expect(accountRequests).toBe(1)
  })

  it('supplier account loading failures stop spinning and can retry', async () => {
    let fail = true
    installGet(async (url: string) => {
      if (url.endsWith('/accounts')) {
        if (fail) throw new Error('simulated failure')
        return { data: [account(16, 'fixture-account')] }
      }
      return { data: page([supplierRow()]) }
    })
    const user = userEvent.setup()
    render(<SupplierList />)
    await openAccounts(user)
    expect(await screen.findByText('账号列表暂不可用')).toBeVisible()
    expect(document.querySelector('.account-drawer .account-table')).not.toBeInTheDocument()
    fail = false
    await user.click(screen.getByRole('button', { name: '重试' }))
    expect(await screen.findByText('fixture-account')).toBeVisible()
  })

  it('password reset dialogs discard a cancelled password before another account opens', async () => {
    const rows = [
      { id: 11, employeeNo: 'user-a', realName: '甲', email: 'a@example.invalid', status: 'ACTIVE', createdAt: '' },
      { id: 12, employeeNo: 'user-b', realName: '乙', email: 'b@example.invalid', status: 'ACTIVE', createdAt: '' },
    ]
    installGet(async (url: string) => {
      if (url === '/admin/users') return { data: page(rows) }
      if (url === '/departments' || url === '/admin/user-role-options') return { data: [] }
      return { data: [] }
    })
    const user = userEvent.setup()
    let view = render(<UserList />)
    let row = (await screen.findByText('user-a')).closest('tr')!
    await user.click(within(row).getByRole('button', { name: '重置密码' }))
    await user.type(screen.getByPlaceholderText('6-20 位'), 'Secret#2026')
    await user.click(screen.getByRole('button', { name: '取消' }))
    row = screen.getByText('user-b').closest('tr')!
    await user.click(within(row).getByRole('button', { name: '重置密码' }))
    expect(screen.getByPlaceholderText('6-20 位')).toHaveValue('')
    view.unmount()

    const accounts = [account(21, 'supplier-a'), account(22, 'supplier-b')]
    installGet(async (url: string) => url.endsWith('/accounts') ? { data: accounts } : { data: page([supplierRow()]) })
    view = render(<SupplierList />)
    await openAccounts(user)
    row = (await screen.findByText('supplier-a')).closest('tr')!
    await user.click(within(row).getByRole('button', { name: '重置密码' }))
    await user.type(screen.getByPlaceholderText('6-20 位'), 'Secret#2026')
    expect(document.querySelector('.account-drawer')).toBeInTheDocument()
    expect(screen.getAllByText(/重置密码 · supplier-a/)[0]).toBeVisible()
    await user.click(screen.getByRole('button', { name: '取消' }))
    row = screen.getByText('supplier-b').closest('tr')!
    await user.click(within(row).getByRole('button', { name: '重置密码' }))
    expect(screen.getByPlaceholderText('6-20 位')).toHaveValue('')
  }, 10_000)

  it('disabled supplier account drawer disables new account creation and rejects stale submit', async () => {
    const supplier = supplierRow({ name: 'FULL_PAGE_SUP00', status: 'DISABLED' })
    installGet(async (url: string) => url.endsWith('/accounts') || url.endsWith('/supplier-role-options') ? { data: [] } : { data: page([supplier]) })
    const user = userEvent.setup()
    render(<SupplierList />)
    await openAccounts(user, 'FULL_PAGE_SUP00')
    const add = screen.getByRole('button', { name: '新增账号' })
    expect(add).toBeDisabled()
    fireEvent.click(add)
    expect(mocks.post).not.toHaveBeenCalled()
  })

  it('paginated lists and file table expose a retry state after the main GET fails', async () => {
    const cases = [
      { api: '/project-groups', render: () => render(<MemoryRouter><ProjectList /></MemoryRouter>) },
      { api: '/admin/suppliers', render: () => render(<SupplierList />) },
      { api: '/admin/users', render: () => render(<UserList />) },
      { api: '/admin/roles', render: () => render(<RoleList />) },
      { api: '/admin/audit-logs', render: () => render(<AuditLog />) },
      { api: '/projects/1/files', render: () => render(<FileTable projectId={1} projectStatus="IN_PROGRESS" />) },
    ]
    const user = userEvent.setup()
    for (const item of cases) {
      let fail = true
      installGet(async (url: string) => {
        if (url === item.api) {
          if (fail) throw new Error('simulated list failure')
          return { data: page() }
        }
        if (url === '/permissions' || url === '/departments' || url === '/admin/user-role-options' || url === '/supplier-options' || url === '/project-dictionaries') return { data: [] }
        return { data: page() }
      })
      const view = item.render()
      expect(await screen.findByText('加载失败')).toBeVisible()
      fail = false
      await user.click(screen.getByRole('button', { name: '重试' }))
      await waitFor(() => expect(screen.queryByText('加载失败')).not.toBeInTheDocument())
      view.unmount()
    }
  })

  it('list actions cannot let an old refresh overwrite a newer filter result', async () => {
    const requests: Array<{ keyword?: string; resolve: (value: { data: ReturnType<typeof page> }) => void }> = []
    installGet(async (url: string, config?: unknown) => {
      if (url !== '/admin/suppliers') return { data: [] }
      return new Promise((resolve) => requests.push({
        keyword: (config as { params?: { keyword?: string } })?.params?.keyword,
        resolve: resolve as (value: { data: ReturnType<typeof page> }) => void,
      }))
    })
    const user = userEvent.setup()
    render(<SupplierList />)
    await waitFor(() => expect(requests).toHaveLength(1))
    requests.shift()!.resolve({ data: page([supplierRow({ id: 1, name: '旧供应商' })]) })
    const oldRow = (await screen.findByText('旧供应商')).closest('tr')!
    await user.click(within(oldRow).getByRole('button', { name: '禁用' }))
    fireEvent.click((await screen.findAllByRole('button', { name: '确定' })).at(-1)!)
    await waitFor(() => expect(requests).toHaveLength(1))
    const stale = requests.shift()!

    const search = screen.getByPlaceholderText('供应商名称')
    await user.type(search, 'new-filter')
    fireEvent.click(search.closest('.arco-input-search')!.querySelector('.arco-icon-search')!)
    await waitFor(() => expect(requests.some((request) => request.keyword === 'new-filter')).toBe(true))
    const fresh = requests.find((request) => request.keyword === 'new-filter')!
    fresh.resolve({ data: page([supplierRow({ id: 2, name: '新筛选结果' })]) })
    expect(await screen.findByText('新筛选结果')).toBeVisible()
    stale.resolve({ data: page([supplierRow({ id: 1, name: '旧供应商' })]) })
    await waitFor(() => expect(screen.queryByText('旧供应商')).not.toBeInTheDocument())
  })

  it('supplier account drawer fits on desktop and keeps columns reachable on narrow screens without phone fields', async () => {
    installGet(async (url: string) => url.endsWith('/accounts') ? { data: [account(11, 'supplier-11')] } : { data: page([supplierRow()]) })
    const user = userEvent.setup()
    render(<SupplierList />)
    await openAccounts(user)
    const drawer = document.querySelector<HTMLElement>('.account-drawer')!
    expect(drawer).toBeInTheDocument()
    expect(drawer.querySelector('.account-table')).toBeInTheDocument()
    expect(within(drawer).getByText('角色')).toBeVisible()
    expect(within(drawer).queryByText('电话')).not.toBeInTheDocument()
    expect(drawer.querySelector('table')).toHaveStyle({ width: '840px' })
    await user.click(screen.getByRole('button', { name: '新增账号' }))
    expect(screen.queryByLabelText('电话')).not.toBeInTheDocument()
  })

  it('supplier form and table drop contact address and email fields', async () => {
    installGet(async () => ({ data: page([supplierRow()]) }))
    const user = userEvent.setup()
    render(<SupplierList />)
    expect(await screen.findByText('fixture')).toBeVisible()
    for (const removed of ['联系人', '联系电话', '联系邮箱', '地址']) expect(screen.queryByText(removed)).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: '新增供应商' }))
    expect(screen.getByPlaceholderText('公司全称')).toBeVisible()
    expect(screen.getByPlaceholderText('选填')).toBeVisible()
    for (const removed of ['联系人', '联系电话', '联系邮箱', '地址']) expect(screen.queryByLabelText(removed)).not.toBeInTheDocument()
  })

  it('supplier and supplier-account status writes ignore synchronous duplicate clicks', async () => {
    const supplierPending = deferred<{ data: Record<string, never> }>()
    const accountPending = deferred<{ data: Record<string, never> }>()
    installGet(async (url: string) => url.endsWith('/accounts') ? { data: [account(12, 'supplier-12')] } : { data: page([supplierRow()]) })
    mocks.put.mockImplementation((url: string) => url.includes('supplier-accounts') ? accountPending.promise : supplierPending.promise)
    const user = userEvent.setup()
    render(<SupplierList />)
    const row = (await screen.findByText('fixture')).closest('tr')!
    const disableSupplier = within(row).getByRole('button', { name: '禁用' })
    await user.click(disableSupplier)
    const confirm = await screen.findByRole('button', { name: '确定' })
    fireEvent.click(confirm); fireEvent.click(confirm)
    await waitFor(() => expect(mocks.put).toHaveBeenCalledTimes(1))
    supplierPending.resolve({ data: {} })

    await openAccounts(user)
    const accountRow = (await screen.findByText('supplier-12')).closest('tr')!
    const disableAccount = within(accountRow).getByRole('button', { name: '禁用' })
    fireEvent.click(disableAccount); fireEvent.click(disableAccount)
    await waitFor(() => expect(mocks.put).toHaveBeenCalledTimes(2))
    accountPending.resolve({ data: {} })
  })

  it('deleting the only row on a controlled last page reloads the last page allowed by the response total', async () => {
    let deleted = false
    installGet(async (url: string, config?: unknown) => {
      if (url !== '/admin/suppliers') return { data: [] }
      const requested = Number((config as { params?: { page?: number } })?.params?.page || 1)
      return { data: page(requested === 2 && !deleted ? [supplierRow({ id: 91, name: '末页供应商' })] : [supplierRow({ id: 1, name: '首页供应商' })], 10, requested, deleted ? 10 : 11) }
    })
    mocks.delete.mockImplementation(async () => { deleted = true; return { data: {} } })
    const user = userEvent.setup()
    render(<SupplierList />)
    fireEvent.click((await screen.findByText('2')).closest('.arco-pagination-item')!)
    const lastRow = (await screen.findByText('末页供应商')).closest('tr')!
    await user.click(within(lastRow).getByRole('button', { name: '删除' }))
    fireEvent.click((await screen.findAllByRole('button', { name: '确定' })).at(-1)!)
    expect(await screen.findByText('首页供应商')).toBeVisible()
    expect(screen.getByText('1').closest('.arco-pagination-item')).toHaveClass('arco-pagination-item-active')
  })
})

describe('重复搜索运行时注册用例', () => {
  beforeEach(() => {
    mocks.permissions = ['project:update', 'supplier:account', 'user:manage', 'file:download']
    mocks.user = { id: 1, employeeNo: 'admin', realName: '管理员', userType: 'INTERNAL', isSystemAdmin: true }
  })

  const cases = [
    { title: 'pages/project/ProjectList.tsx: repeated identical searches finish and reload', api: '/project-groups', placeholder: '主项目名称', render: () => render(<MemoryRouter><ProjectList /></MemoryRouter>) },
    { title: 'pages/supplier/SupplierList.tsx: repeated identical searches finish and reload', api: '/admin/suppliers', placeholder: '供应商名称', render: () => render(<SupplierList />) },
    { title: 'pages/org/UserList.tsx: repeated identical searches finish and reload', api: '/admin/users', placeholder: '工号 / 姓名 / 邮箱', render: () => render(<UserList />) },
    { title: 'components/FileTable.tsx: repeated identical searches finish and reload', api: '/projects/1/files', placeholder: '文件名', render: () => render(<FileTable projectId={1} projectStatus="IN_PROGRESS" />) },
  ]

  for (const item of cases) {
    it(item.title, async () => {
      let requests = 0
      installGet(async (url: string) => {
        if (url === item.api) { requests += 1; return { data: page() } }
        return { data: [] }
      })
      const user = userEvent.setup()
      item.render()
      await waitFor(() => expect(requests).toBe(1))
      const search = screen.getByPlaceholderText(item.placeholder)
      await user.type(search, 'steel')
      const searchAction = search.closest('.arco-input-search')!.querySelector<HTMLElement>('.arco-icon-search')!
      fireEvent.click(searchAction)
      await waitFor(() => expect(requests).toBe(2))
      fireEvent.click(searchAction)
      await waitFor(() => expect(requests).toBe(3))
      expect(document.querySelector('.arco-spin-loading')).not.toBeInTheDocument()
    })
  }
})
