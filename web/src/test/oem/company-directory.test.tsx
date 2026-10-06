import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { AxiosInstance } from 'axios'
import { Modal } from '@arco-design/web-react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { OemProvider } from '../../oem/OemContext'
import { OemApi } from '../../oem/api/OemApi'
import type { Company, Page, VendorAccount } from '../../oem/api/types'
import CompaniesPage from '../../oem/pages/admin/CompaniesPage'

afterEach(() => Modal.destroyAll())

function company(overrides: Partial<Company> = {}): Company {
  return {
    id: 7,
    name: '精工制造有限公司',
    contactName: '联系人甲',
    contactPhone: '13800000000',
    contactEmail: 'contact@example.invalid',
    remark: null,
    status: 'ACTIVE',
    accountCount: 1,
    activeAccountCount: 1,
    createdAt: '2026-10-05T00:00:00Z',
    ...overrides,
  }
}

function account(overrides: Partial<VendorAccount> = {}): VendorAccount {
  return {
    id: 31,
    employeeNo: 'OEM_0031',
    realName: '厂商账号甲',
    email: 'oem-0031@example.invalid',
    companyId: 7,
    status: 'ACTIVE',
    mustChangePassword: false,
    locked: false,
    lastLoginAt: null,
    ...overrides,
  }
}

function page(list: Company[], current = 1, total = list.length): Page<Company> {
  return { list, total, page: current, pageSize: 20 }
}

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((ok, fail) => { resolve = ok; reject = fail })
  return { promise, resolve, reject }
}

function renderDirectory(api: Partial<OemApi>, permissions: string[]) {
  return render(
    <OemProvider value={{
      api: api as OemApi,
      realm: 'internal',
      base: '/oem',
      permissions: new Set(permissions),
      userId: 1,
      queryScope: ['test'],
    }}>
      <CompaniesPage />
    </OemProvider>,
  )
}

function baseApi(overrides: Partial<OemApi> = {}): Partial<OemApi> {
  return {
    companies: vi.fn().mockResolvedValue(page([company()])),
    accounts: vi.fn().mockResolvedValue([account()]),
    createCompany: vi.fn(),
    updateCompany: vi.fn(),
    setCompanyStatus: vi.fn(),
    deleteCompany: vi.fn(),
    createAccount: vi.fn(),
    updateAccount: vi.fn(),
    setAccountStatus: vi.fn(),
    deleteAccount: vi.fn(),
    resetAccountPassword: vi.fn(),
    ...overrides,
  }
}

async function openAccounts(user: ReturnType<typeof userEvent.setup>) {
  const row = (await screen.findByText('精工制造有限公司')).closest('tr')!
  await user.click(within(row).getByRole('button', { name: '账号' }))
  await screen.findByText('OEM_0031')
  return document.querySelector<HTMLElement>('.oem-accounts-drawer')!
}

describe('OEM 厂商与账号目录', () => {
  it('调用约定的厂商和账号删除端点', async () => {
    const http = { delete: vi.fn().mockResolvedValue({ data: {} }) }
    const api = new OemApi(http as unknown as AxiosInstance)

    await api.deleteCompany(17)
    await api.deleteAccount(29)

    expect(http.delete).toHaveBeenNthCalledWith(1, '/oem/companies/17')
    expect(http.delete).toHaveBeenNthCalledWith(2, '/oem/accounts/29')
  })

  it('删除按钮要求 manage 与 delete 权限同时具备', async () => {
    const user = userEvent.setup()
    const view = renderDirectory(baseApi(), ['oem:company_manage', 'oem:account_manage'])

    expect(await screen.findByText('精工制造有限公司')).toBeVisible()
    expect(screen.queryByRole('button', { name: '删除' })).not.toBeInTheDocument()
    const drawer = await openAccounts(user)
    expect(within(drawer).queryByRole('button', { name: '删除' })).not.toBeInTheDocument()
    view.unmount()

    renderDirectory(baseApi(), ['oem:company_manage', 'oem:company_delete', 'oem:account_manage', 'oem:account_delete'])
    const companyRow = (await screen.findByText('精工制造有限公司')).closest('tr')!
    expect(within(companyRow).getByRole('button', { name: '删除' })).toBeVisible()
    const permittedDrawer = await openAccounts(user)
    expect(within(permittedDrawer).getByRole('button', { name: '删除' })).toBeVisible()
  })

  it('账号删除失败保留行并显示服务端原因，成功后刷新账号与厂商计数', async () => {
    const user = userEvent.setup()
    const backendReason = '该账号已有图纸传输历史，请停用账号'
    let shouldFail = true
    let removed = false
    const companies = vi.fn().mockImplementation(async () => page([
      company({ accountCount: removed ? 0 : 1, activeAccountCount: removed ? 0 : 1 }),
    ]))
    const accounts = vi.fn().mockImplementation(async () => removed ? [] : [account()])
    const deleteAccount = vi.fn().mockImplementation(async () => {
      if (shouldFail) throw { response: { data: { message: backendReason } } }
      removed = true
    })
    renderDirectory(baseApi({ companies, accounts, deleteAccount }), [
      'oem:company_manage', 'oem:account_manage', 'oem:account_delete',
    ])
    const drawer = await openAccounts(user)
    let accountRow = within(drawer).getByText('OEM_0031').closest('tr')!

    await user.click(within(accountRow).getByRole('button', { name: '删除' }))
    expect(await screen.findByText('删除账号“OEM_0031”')).toBeVisible()
    let confirmDialog = screen.getByText('删除账号“OEM_0031”').closest<HTMLElement>('[role="dialog"]')!
    await user.click(within(confirmDialog).getByRole('button', { name: '确认删除' }))
    expect(await within(drawer).findByRole('alert')).toHaveTextContent(backendReason)
    expect(within(drawer).getByText('OEM_0031')).toBeVisible()
    await waitFor(() => expect(screen.queryByText('删除账号“OEM_0031”')).not.toBeInTheDocument())

    shouldFail = false
    accountRow = within(drawer).getByText('OEM_0031').closest('tr')!
    await user.click(within(accountRow).getByRole('button', { name: '删除' }))
    confirmDialog = (await screen.findAllByText('删除账号“OEM_0031”')).at(-1)!.closest<HTMLElement>('[role="dialog"]')!
    await user.click(within(confirmDialog).getByRole('button', { name: '确认删除' }))
    await waitFor(() => expect(within(drawer).queryByText('OEM_0031')).not.toBeInTheDocument())
    expect(await screen.findByText('0 / 0')).toBeVisible()
    expect(accounts).toHaveBeenCalledTimes(2)
    expect(companies).toHaveBeenCalledTimes(2)
  })

  it('厂商删除忽略同步重复确认，并在删掉末页唯一行后回退一页', async () => {
    const user = userEvent.setup()
    const pending = deferred<void>()
    let deleted = false
    const companies = vi.fn().mockImplementation(async (query: { page: number }) => {
      if (query.page === 2 && !deleted) return page([company({ id: 91, name: '末页厂商' })], 2, 21)
      return page([company({ id: 1, name: '首页厂商' })], 1, deleted ? 20 : 21)
    })
    const deleteCompany = vi.fn().mockImplementation(async () => {
      await pending.promise
      deleted = true
    })
    renderDirectory(baseApi({ companies, deleteCompany }), ['oem:company_manage', 'oem:company_delete'])

    fireEvent.click((await screen.findByText('2')).closest('.arco-pagination-item')!)
    const lastRow = (await screen.findByText('末页厂商')).closest('tr')!
    await user.click(within(lastRow).getByRole('button', { name: '删除' }))
    expect(await screen.findByText('删除厂商“末页厂商”')).toBeVisible()
    const confirmDialog = screen.getByText('删除厂商“末页厂商”').closest<HTMLElement>('[role="dialog"]')!
    const confirm = within(confirmDialog).getByRole('button', { name: '确认删除' })
    fireEvent.click(confirm)
    fireEvent.click(confirm)
    await waitFor(() => expect(deleteCompany).toHaveBeenCalledTimes(1))
    await act(async () => pending.resolve())

    expect(await screen.findByText('首页厂商')).toBeVisible()
    expect(screen.getByText('1').closest('.arco-pagination-item')).toHaveClass('arco-pagination-item-active')
  })

  it('厂商保存忽略同步重复确认', async () => {
    const user = userEvent.setup()
    const pending = deferred<Company>()
    const createCompany = vi.fn().mockReturnValue(pending.promise)
    renderDirectory(baseApi({ createCompany }), ['oem:company_manage'])
    await screen.findByText('精工制造有限公司')

    await user.click(screen.getByRole('button', { name: '新增厂商' }))
    await user.type(screen.getByLabelText('厂商名称'), '新厂商')
    const confirm = screen.getByRole('button', { name: '确定' })
    fireEvent.click(confirm)
    fireEvent.click(confirm)
    await waitFor(() => expect(createCompany).toHaveBeenCalledTimes(1))
    await act(async () => pending.resolve(company({ id: 99, name: '新厂商' })))
  })

  it('切换厂商时忽略前一厂商迟到的账号响应', async () => {
    const firstAccounts = deferred<VendorAccount[]>()
    const first = company({ id: 7, name: '厂商 A' })
    const second = company({ id: 8, name: '厂商 B' })
    const companies = vi.fn().mockResolvedValue(page([first, second]))
    const accounts = vi.fn().mockImplementation((companyId: number) => companyId === first.id
      ? firstAccounts.promise
      : Promise.resolve([account({ id: 82, companyId: second.id, employeeNo: 'OEM_B' })]))
    renderDirectory(baseApi({ companies, accounts }), ['oem:company_manage', 'oem:account_manage', 'oem:account_delete'])

    const firstRow = (await screen.findByText('厂商 A')).closest('tr')!
    fireEvent.click(within(firstRow).getByRole('button', { name: '账号' }))
    await waitFor(() => expect(accounts).toHaveBeenCalledWith(first.id))
    const secondRow = screen.getByText('厂商 B').closest('tr')!
    fireEvent.click(within(secondRow).getByRole('button', { name: '账号' }))
    expect(await screen.findByText('OEM_B')).toBeVisible()

    await act(async () => firstAccounts.resolve([account({ employeeNo: 'OEM_A' })]))
    expect(screen.getByText('OEM_B')).toBeVisible()
    expect(screen.queryByText('OEM_A')).not.toBeInTheDocument()
  })
})
