import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { AxiosInstance } from 'axios'
import { describe, expect, it, vi } from 'vitest'
import { OemProvider } from '../../oem/OemContext'
import { OemApi } from '../../oem/api/OemApi'
import type { Company, Page, VendorAccount } from '../../oem/api/types'
import CompaniesPage from '../../oem/pages/admin/CompaniesPage'

vi.mock('../../utils/excelWorkbook', () => ({
  downloadExcelWorkbook: vi.fn().mockResolvedValue(undefined),
  createExcelWorkbook: vi.fn(),
  readExcelTable: vi.fn(),
}))
import { downloadExcelWorkbook } from '../../utils/excelWorkbook'
import { MemoryRouter } from 'react-router-dom'


function company(overrides: Partial<Company> = {}): Company {
  return {
    id: 7,
    name: '精工制造有限公司',
    remark: null,
    status: 'ACTIVE',
    accountCount: 1,
    activeAccountCount: 1,
    createdAt: '2026-10-05T00:00:00Z',
    updatedAt: '2026-10-05T00:00:00Z',
    ...overrides,
  }
}

function account(overrides: Partial<VendorAccount> = {}): VendorAccount {
  return {
    id: 31,
    employeeNo: 'OEM_0031',
    companyId: 7,
    status: 'ACTIVE',
    mustChangePassword: false,
    locked: false,
    lastLoginAt: null,
    createdAt: '2026-10-05T00:00:00Z',
    updatedAt: '2026-10-05T00:00:00Z',
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
      <MemoryRouter><CompaniesPage /></MemoryRouter>
    </OemProvider>,
  )
}

function baseApi(overrides: Partial<OemApi> = {}): Partial<OemApi> {
  return {
    companies: vi.fn().mockResolvedValue(page([company()])),
    accounts: vi.fn().mockResolvedValue([account()]),
    contacts: vi.fn().mockResolvedValue([]),
    createContact: vi.fn(),
    updateContact: vi.fn(),
    setContactStatus: vi.fn(),
    createCompany: vi.fn(),
    updateCompany: vi.fn(),
    setCompanyStatus: vi.fn(),
    deleteCompany: vi.fn(),
    createAccount: vi.fn(),
    setAccountStatus: vi.fn(),
    deleteAccount: vi.fn(),
    resetAccountPassword: vi.fn(),
    internalRecipients: vi.fn().mockResolvedValue([]),
    setCompanyRecipientRemoved: vi.fn(),
    allInternalRecipients: vi.fn().mockResolvedValue([]),
    setRecipientRemovedFromAll: vi.fn(),
    ...overrides,
  }
}

async function openAccounts(user: ReturnType<typeof userEvent.setup>) {
  const row = (await screen.findByText('精工制造有限公司')).closest('tr')!
  await user.click(within(row).getByRole('button', { name: '账号管理' }))
  await screen.findByText('OEM_0031')
  return document.querySelector<HTMLElement>('.oem-accounts-drawer')!
}

describe('ODM/OEMA 厂商与账号目录', () => {
  it('厂商列表和编辑资料与 Robot 一致，仅显示名称、状态、创建时间和操作', async () => {
    const user = userEvent.setup()
    renderDirectory(baseApi(), ['oem:company_manage', 'oem:account_manage'])
    const row = (await screen.findByText('精工制造有限公司')).closest('tr')!
    expect(screen.getAllByRole('columnheader').map(cell => cell.textContent)).toEqual(['名称', '状态', '创建时间', '操作'])
    expect(screen.queryByRole('button', { name: '显示列' })).not.toBeInTheDocument()
    await user.click(within(row).getByRole('button', { name: '编辑' }))
    const dialog = await screen.findByRole('dialog', { name: '编辑厂商' })
    expect(within(dialog).getByLabelText('厂商名称')).toBeVisible()
    expect(within(dialog).getByLabelText('备注')).toBeVisible()
    expect(within(dialog).queryByLabelText('联系人')).not.toBeInTheDocument()
    expect(within(dialog).queryByLabelText('联系电话')).not.toBeInTheDocument()
    expect(within(dialog).queryByLabelText('联系邮箱')).not.toBeInTheDocument()
  })
  it('可通知人员默认全员：可在单个厂商中移除或恢复，也可一次从全部厂商移除', async () => {
    const user = userEvent.setup()
    const person = { userId: 51, employeeNo: 'E051', realName: '王五', departmentName: '采购课', email: 'w@example.invalid', canReceive: true, unavailableReason: null, removed: false }
    const leader = { userId: 52, employeeNo: 'E052', realName: '赵六', departmentName: '采购部', email: 'z@example.invalid', canReceive: true, unavailableReason: null, removed: false }
    const setCompanyRecipientRemoved = vi.fn().mockResolvedValue([person, { ...leader, removed: true }])
    const setRecipientRemovedFromAll = vi.fn().mockResolvedValue([person, { ...leader, removed: true }])
    renderDirectory(baseApi({
      internalRecipients: vi.fn().mockResolvedValue([person, leader]),
      setCompanyRecipientRemoved,
      allInternalRecipients: vi.fn().mockResolvedValue([person, leader]),
      setRecipientRemovedFromAll,
    }), ['oem:company_manage'])

    const row = (await screen.findByText('精工制造有限公司')).closest('tr')!
    const open = within(row).getByRole('button', { name: '可通知人员' })
    expect(open).toHaveAttribute('title', '无人可通知')
    await user.click(open)
    let drawer = document.querySelector<HTMLElement>('.arco-drawer')!
    const leaderRow = (await within(drawer).findByText('赵六（E052）')).closest('tr')!
    await user.click(within(leaderRow).getByRole('button', { name: '移除' }))
    await waitFor(() => expect(setCompanyRecipientRemoved).toHaveBeenCalledWith(7, 52, true))
    expect(await within(drawer).findByText('已移除')).toBeInTheDocument()
    expect(within(drawer).getByRole('button', { name: '恢复' })).toBeInTheDocument()
    await user.click(within(drawer).getByRole('button', { name: '关闭' }))

    await user.click(screen.getAllByRole('button', { name: '可通知人员' }).find(button => !button.closest('tr'))!)
    drawer = (await screen.findByText('可通知人员 · 全部厂商')).closest<HTMLElement>('.arco-drawer')!
    const allRow = (await within(drawer).findByText('赵六（E052）')).closest('tr')!
    await user.click(within(allRow).getByRole('button', { name: '移除' }))
    await waitFor(() => expect(setRecipientRemovedFromAll).toHaveBeenCalledWith(52, true))
  })

  it('账号和联络人 Excel 批量入口位于 ODM/OEMA 厂商总表页', async () => {
    renderDirectory(baseApi(), ['oem:company_manage', 'oem:account_manage'])

    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: '导入' }))
    expect(await screen.findByRole('menuitem', { name: '导入账号' })).toBeVisible()
    expect(screen.getByRole('menuitem', { name: '导入联络人' })).toBeVisible()
    expect(await screen.findByRole('menuitem', { name: '导入厂商' })).toBeVisible()
    await user.click(screen.getByRole('button', { name: '导出' }))
    expect(await screen.findByRole('menuitem', { name: '导出账号' })).toBeVisible()
    expect(screen.getByRole('menuitem', { name: '导出联络人' })).toBeVisible()
  })

  it('ODM/OEMA账号导出不包含姓名、邮箱或任何密码列', async () => {
    vi.mocked(downloadExcelWorkbook).mockClear()
    const user = userEvent.setup()
    renderDirectory(baseApi(), ['oem:company_manage', 'oem:account_manage'])

    await user.click(await screen.findByRole('button', { name: '导出' }))
    const exportAccounts = await screen.findByRole('menuitem', { name: '导出账号' })
    // 下拉菜单展开动画期间菜单不接收指针事件，等动画结束再点击。
    await waitFor(() => expect(getComputedStyle(exportAccounts.closest('.arco-dropdown') ?? exportAccounts).pointerEvents).not.toBe('none'))
    await user.click(exportAccounts)
    await waitFor(() => expect(downloadExcelWorkbook).toHaveBeenCalledTimes(1))

    const sheets = vi.mocked(downloadExcelWorkbook).mock.calls[0][1]
    expect(sheets).toHaveLength(1)
    expect(sheets[0].headers).toEqual(['厂商名称', '账号', '状态', '最近登录'])
    expect(sheets[0].headers).not.toContain('姓名')
    expect(sheets[0].headers).not.toContain('邮箱')
    expect(sheets[0].headers).not.toContain('初始密码')
  })

  it('共用 ODM/OEMA 登录账号不显示或要求填写联系人姓名和邮箱', async () => {
    const user = userEvent.setup()
    renderDirectory(baseApi(), ['oem:account_manage'])
    const drawer = await openAccounts(user)

    expect(within(drawer).queryByText('姓名')).not.toBeInTheDocument()
    expect(within(drawer).queryByText('邮箱')).not.toBeInTheDocument()
    await user.click(within(drawer).getByRole('button', { name: '新增账号' }))
    const dialog = await screen.findByRole('dialog', { name: '新增账号' })
    expect(within(dialog).getByLabelText('账号')).toBeVisible()
    expect(within(dialog).getByLabelText('初始密码')).toBeVisible()
    expect(within(dialog).queryByLabelText('姓名')).not.toBeInTheDocument()
    expect(within(dialog).queryByLabelText('邮箱')).not.toBeInTheDocument()
  })

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
    expect(screen.queryByText('0 / 0')).not.toBeInTheDocument()
    expect(accounts).toHaveBeenCalledTimes(2)
    expect(companies).toHaveBeenCalledTimes(2)
  })

  it('账号停用被拒绝时在抽屉内显示服务端原因，不刷新列表', async () => {
    const user = userEvent.setup()
    const backendReason = '该厂商有含业务记录的草稿，草稿仅创建人可见。须保留一个启用账号，请先启用其他账号或停用厂商。'
    const accounts = vi.fn().mockResolvedValue([account()])
    const setAccountStatus = vi.fn().mockRejectedValue({ response: { status: 409, data: { code: 40901, message: backendReason } } })
    renderDirectory(baseApi({ accounts, setAccountStatus }), ['oem:company_manage', 'oem:account_manage'])
    const drawer = await openAccounts(user)
    const accountRow = within(drawer).getByText('OEM_0031').closest('tr')!

    await user.click(within(accountRow).getByRole('button', { name: '禁用' }))
    expect(setAccountStatus).toHaveBeenCalledWith(31, 'DISABLED')
    expect(await within(drawer).findByRole('alert')).toHaveTextContent(`停用失败：${backendReason}`)
    expect(accounts).toHaveBeenCalledTimes(1)
    expect(within(accountRow).getByRole('button', { name: '禁用' })).toBeVisible()
  })

  it('厂商停用被拒绝时显示进行中传递单的原因，成功后刷新列表', async () => {
    const user = userEvent.setup()
    const backendReason = '该厂商仍有 2 个进行中的传递单（已发送、尚未发布或关闭），请先终止或等待其完成后再停用厂商'
    let shouldFail = true
    const companies = vi.fn().mockImplementation(async () => page([company({ status: shouldFail ? 'ACTIVE' : 'DISABLED' })]))
    const setCompanyStatus = vi.fn().mockImplementation(async () => {
      if (shouldFail) throw { response: { status: 409, data: { code: 40901, message: backendReason } } }
      return company({ status: 'DISABLED' })
    })
    renderDirectory(baseApi({ companies, setCompanyStatus }), ['oem:company_manage', 'oem:account_manage'])
    const row = (await screen.findByText('精工制造有限公司')).closest('tr')!

    await user.click(within(row).getByRole('button', { name: '禁用' }))
    // The popconfirm is still animating in (pointer-events: none), so click it directly.
    fireEvent.click(await screen.findByRole('button', { name: /确\s*定/ }))
    expect(await screen.findByText(`停用失败：${backendReason}`)).toHaveAttribute('role', 'alert')
    expect(setCompanyStatus).toHaveBeenCalledWith(7, 'DISABLED')
    expect(companies).toHaveBeenCalledTimes(1)

    shouldFail = false
    await user.click(within(row).getByRole('button', { name: '禁用' }))
    const confirms = await screen.findAllByRole('button', { name: /确\s*定/ })
    fireEvent.click(confirms.at(-1)!)
    await waitFor(() => expect(companies).toHaveBeenCalledTimes(2))
    await waitFor(() => expect(screen.queryByText(`停用失败：${backendReason}`)).not.toBeInTheDocument())
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

  it('切换厂商后忽略骏创迟到响应并只删除元控账号', async () => {
    const user = userEvent.setup()
    const firstAccounts = deferred<VendorAccount[]>()
    const junchuang = company({ id: 7, name: '骏创' })
    const yuankong = company({ id: 8, name: '元控' })
    const junchuangAccount = account({ id: 71, companyId: junchuang.id, employeeNo: 'JC001' })
    const yuankongAccount = account({ id: 82, companyId: yuankong.id, employeeNo: 'YK001' })
    let yuankongDeleted = false
    const companies = vi.fn().mockResolvedValue(page([junchuang, yuankong]))
    const accounts = vi.fn().mockImplementation((companyId: number) => companyId === junchuang.id
      ? firstAccounts.promise
      : Promise.resolve(yuankongDeleted ? [] : [yuankongAccount]))
    const deleteAccount = vi.fn().mockImplementation(async (accountId: number) => {
      if (accountId === yuankongAccount.id) yuankongDeleted = true
    })
    renderDirectory(baseApi({ companies, accounts, deleteAccount }), ['oem:company_manage', 'oem:account_manage', 'oem:account_delete'])

    const firstRow = (await screen.findByText('骏创')).closest('tr')!
    fireEvent.click(within(firstRow).getByRole('button', { name: '账号管理' }))
    await waitFor(() => expect(accounts).toHaveBeenCalledWith(junchuang.id))
    const secondRow = screen.getByText('元控').closest('tr')!
    fireEvent.click(within(secondRow).getByRole('button', { name: '账号管理' }))
    expect(await screen.findByText('YK001')).toBeVisible()

    await act(async () => firstAccounts.resolve([junchuangAccount]))
    expect(screen.getByText('YK001')).toBeVisible()
    expect(screen.queryByText('JC001')).not.toBeInTheDocument()

    const drawer = document.querySelector<HTMLElement>('.oem-accounts-drawer')!
    const accountRow = within(drawer).getByText('YK001').closest('tr')!
    await user.click(within(accountRow).getByRole('button', { name: '删除' }))
    const confirm = (await screen.findByText('删除账号“YK001”')).closest<HTMLElement>('[role="dialog"]')!
    await user.click(within(confirm).getByRole('button', { name: '确认删除' }))

    await waitFor(() => expect(deleteAccount).toHaveBeenCalledTimes(1))
    expect(deleteAccount).toHaveBeenCalledWith(yuankongAccount.id)
    expect(deleteAccount).not.toHaveBeenCalledWith(junchuangAccount.id)
  })
})
