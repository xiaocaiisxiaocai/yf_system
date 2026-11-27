import { ConfigProvider } from '@arco-design/web-react'
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { AxiosInstance } from 'axios'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it, vi } from 'vitest'
import { TABLE_PAGINATION } from '../../components/tablePagination'
import { OemProvider } from '../../oem/OemContext'
import { OemApi } from '../../oem/api/OemApi'
import type { Company, CompanyContact, Page, VendorAccount } from '../../oem/api/types'
import CompaniesPage from '../../oem/pages/admin/CompaniesPage'

vi.mock('../../utils/excelWorkbook', () => ({
  downloadExcelWorkbook: vi.fn().mockResolvedValue(undefined),
  createExcelWorkbook: vi.fn(),
  readExcelTable: vi.fn(),
}))
import { downloadExcelWorkbook, readExcelTable } from '../../utils/excelWorkbook'


function company(overrides: Partial<Company> = {}): Company {
  return {
    id: 7,
    name: '精工制造有限公司',
    remark: null,
    status: 'ACTIVE',
    accountCount: 1,
    activeAccountCount: 1,
    contactCount: 1,
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

function contact(overrides: Partial<CompanyContact> = {}): CompanyContact {
  return {
    id: 41,
    companyId: 7,
    name: '陈志明',
    email: 'zm.chen@example.test',
    sortNo: 1,
    status: 'ACTIVE',
    createdAt: '2026-10-05T00:00:00Z',
    updatedAt: '2026-10-05T00:00:00Z',
    ...overrides,
  }
}

function page(list: Company[], current = 1, total = list.length): Page<Company> {
  return { list, total, page: current, pageSize: 100 }
}

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((ok, fail) => { resolve = ok; reject = fail })
  return { promise, resolve, reject }
}

function directoryElement(api: Partial<OemApi>, permissions: string[]) {
  return (
    <OemProvider value={{
      api: api as OemApi,
      realm: 'internal',
      base: '/oem',
      permissions: new Set(permissions),
      userId: 1,
      queryScope: ['test'],
    }}>
      <MemoryRouter><CompaniesPage /></MemoryRouter>
    </OemProvider>
  )
}

function renderDirectory(api: Partial<OemApi>, permissions: string[]) {
  return render(directoryElement(api, permissions))
}

function baseApi(overrides: Partial<OemApi> = {}): Partial<OemApi> {
  return {
    companies: vi.fn().mockResolvedValue(page([company()])),
    accounts: vi.fn().mockResolvedValue([account()]),
    contacts: vi.fn().mockResolvedValue([contact()]),
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
    allInternalRecipients: vi.fn().mockResolvedValue([]),
    setRecipientRemovedFromAll: vi.fn(),
    restoreRecipients: vi.fn(),
    ...overrides,
  }
}

function vendorPanel() {
  return screen.getByRole('complementary', { name: '厂商列表' })
}

function summarySection() {
  return screen.getByRole('region', { name: '厂商信息' })
}

function findAccountsPanel() {
  return screen.findByRole('region', { name: '账号管理' })
}

function findContactsPanel() {
  return screen.findByRole('region', { name: '联络人' })
}

describe('ODM/OEMA 厂商与账号目录', () => {
  it('仅厂商管理可维护厂商及联络人，不渲染或请求账号列表', async () => {
    const api = baseApi()
    renderDirectory(api, ['oem:company_manage'])
    expect(await within(vendorPanel()).findByText('精工制造有限公司')).toBeVisible()
    const contacts = await findContactsPanel()
    expect(await within(contacts).findByText('陈志明')).toBeVisible()
    expect(screen.queryByRole('region', { name: '账号管理' })).not.toBeInTheDocument()
    expect(api.accounts).not.toHaveBeenCalled()
  })

  it.each(['oem:account_manage', 'oem:directory_view'])('%s 单独可以平铺读取账号，不加载联络人，撤权即卸载账号面板', async (permission) => {
    const api = baseApi()
    const view = renderDirectory(api, [permission])
    const accounts = await findAccountsPanel()
    expect(await within(accounts).findByText('OEM_0031')).toBeVisible()
    expect(within(accounts).getByRole('button', { name: '新增账号' }).hasAttribute('disabled')).toBe(permission === 'oem:directory_view')
    expect(screen.queryByRole('region', { name: '联络人' })).not.toBeInTheDocument()
    expect(api.accounts).toHaveBeenCalledWith(7)
    expect(api.contacts).not.toHaveBeenCalled()

    view.rerender(directoryElement(api, ['oem:company_manage']))
    expect(screen.queryByText('OEM_0031')).not.toBeInTheDocument()
    expect(screen.queryByRole('region', { name: '账号管理' })).not.toBeInTheDocument()
    const contacts = await findContactsPanel()
    expect(await within(contacts).findByText('陈志明')).toBeVisible()

    view.rerender(directoryElement(api, [permission]))
    const restoredAccounts = await findAccountsPanel()
    expect(await within(restoredAccounts).findByText('OEM_0031')).toBeVisible()
    expect(screen.queryByRole('region', { name: '联络人' })).not.toBeInTheDocument()
  })

  it('撤回账号管理后立即关闭依赖账号列表的导入对话框', async () => {
    const api = baseApi()
    const view = renderDirectory(api, ['oem:account_manage'])
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: '导入' }))
    fireEvent.click(await screen.findByText('导入账号'))
    expect(await screen.findByRole('dialog', { name: '导入 ODM/OEMA 厂商账号' })).toBeVisible()
    view.rerender(directoryElement(api, ['oem:company_manage']))
    expect(screen.queryByRole('dialog', { name: '导入 ODM/OEMA 厂商账号' })).not.toBeInTheDocument()
    view.rerender(directoryElement(api, ['oem:account_manage']))
    expect(screen.queryByRole('dialog', { name: '导入 ODM/OEMA 厂商账号' })).not.toBeInTheDocument()
  })

  it('同页主从平铺展示厂商、账号管理与联络人，且编辑厂商仅维护名称与备注', async () => {
    const user = userEvent.setup()
    renderDirectory(baseApi(), ['oem:company_manage', 'oem:account_manage', 'oem:transfer_view'])
    expect(await within(vendorPanel()).findByText('精工制造有限公司')).toBeVisible()
    expect(within(summarySection()).getByRole('heading', { name: '精工制造有限公司' })).toBeVisible()
    expect(within(summarySection()).queryByRole('button', { name: '传递记录' })).not.toBeInTheDocument()
    const accounts = await findAccountsPanel()
    const contacts = await findContactsPanel()
    expect(await within(accounts).findByText('OEM_0031')).toBeVisible()
    expect(await within(contacts).findByText('陈志明')).toBeVisible()

    await user.click(within(summarySection()).getByRole('button', { name: '编辑' }))
    const dialog = await screen.findByRole('dialog', { name: '编辑厂商' })
    expect(within(dialog).getByLabelText('厂商名称')).toBeVisible()
    expect(within(dialog).getByLabelText('备注')).toBeVisible()
    expect(within(dialog).queryByLabelText('联系人')).not.toBeInTheDocument()
    expect(within(dialog).queryByLabelText('联系电话')).not.toBeInTheDocument()
    expect(within(dialog).queryByLabelText('联系邮箱')).not.toBeInTheDocument()
  })

  it('切换左侧厂商时右侧账号管理与联络人同步切换', async () => {
    const user = userEvent.setup()
    const vendorA = company({ id: 7, name: '甲厂商', accountCount: 1, contactCount: 1 })
    const vendorB = company({ id: 8, name: '乙厂商', accountCount: 1, contactCount: 1 })
    const accounts = vi.fn().mockImplementation(async (companyId: number) => companyId === 7
      ? [account({ id: 71, companyId: 7, employeeNo: 'A_ACC' })]
      : [account({ id: 81, companyId: 8, employeeNo: 'B_ACC' })])
    const contacts = vi.fn().mockImplementation(async (companyId: number) => companyId === 7
      ? [contact({ id: 72, companyId: 7, name: '甲联络人', email: 'a@example.test' })]
      : [contact({ id: 82, companyId: 8, name: '乙联络人', email: 'b@example.test' })])

    renderDirectory(baseApi({
      companies: vi.fn().mockResolvedValue(page([vendorA, vendorB])),
      accounts,
      contacts,
    }), ['oem:company_manage', 'oem:account_manage'])

    expect(await within(await findAccountsPanel()).findByText('A_ACC')).toBeVisible()
    expect(await within(await findContactsPanel()).findByText('甲联络人')).toBeVisible()

    await user.click(within(vendorPanel()).getByRole('button', { name: /乙厂商/ }))
    expect(await within(await findAccountsPanel()).findByText('B_ACC')).toBeVisible()
    expect(await within(await findContactsPanel()).findByText('乙联络人')).toBeVisible()
    expect(within(summarySection()).getByRole('heading', { name: '乙厂商' })).toBeVisible()
  })

  it('可通知人员是统一的一份名单：只在工具栏打开，可移除和恢复', async () => {
    const user = userEvent.setup()
    const person = { userId: 51, employeeNo: 'E051', realName: '王五', departmentName: '采购课', email: 'w@example.invalid', canReceive: true, unavailableReason: null, removed: false }
    const leader = { userId: 52, employeeNo: 'E052', realName: '赵六', departmentName: '采购部', email: 'z@example.invalid', canReceive: true, unavailableReason: null, removed: false }
    const setRecipientRemovedFromAll = vi.fn()
      .mockResolvedValueOnce([person, { ...leader, removed: true }])
      .mockResolvedValueOnce([person, leader])
    renderDirectory(baseApi({
      allInternalRecipients: vi.fn().mockResolvedValue([person, leader]),
      setRecipientRemovedFromAll,
    }), ['oem:company_manage'])

    await within(vendorPanel()).findByText('精工制造有限公司')
    expect(within(summarySection()).queryByRole('button', { name: '可通知人员' })).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: '可通知人员' }))
    await screen.findByText('赵六（E052）')
    const drawer = document.querySelector<HTMLElement>('.arco-drawer')!
    const leaderRow = (await within(drawer).findByText('赵六（E052）')).closest('tr')!
    await user.click(within(leaderRow).getByRole('button', { name: '移除' }))
    await waitFor(() => expect(setRecipientRemovedFromAll).toHaveBeenCalledWith(52, true))
    expect(await within(drawer).findByText('已移除')).toBeInTheDocument()
    await user.click(within(drawer).getByRole('button', { name: '恢复' }))
    await waitFor(() => expect(setRecipientRemovedFromAll).toHaveBeenLastCalledWith(52, false))
  })

  it('重新导入：弹窗勾选已移除的人，确认后恢复；没有已移除的人时按钮不可用', async () => {
    const user = userEvent.setup()
    const base = { departmentName: '采购课', email: 'w@example.invalid', canReceive: true, unavailableReason: null }
    const kept = { userId: 51, employeeNo: 'E051', realName: '王五', ...base, removed: false }
    const gone = { userId: 52, employeeNo: 'E052', realName: '赵六', ...base, removed: true }
    const other = { userId: 53, employeeNo: 'E053', realName: '钱七', ...base, removed: true }
    const restoreRecipients = vi.fn().mockResolvedValue([kept, { ...gone, removed: false }, other])
    renderDirectory(baseApi({
      allInternalRecipients: vi.fn().mockResolvedValue([kept, gone, other]),
      restoreRecipients,
    }), ['oem:company_manage'])

    await user.click(await screen.findByRole('button', { name: '可通知人员' }))
    await screen.findByText('王五（E051）')
    await user.click(screen.getByRole('button', { name: '重新导入' }))
    const dialog = await screen.findByRole('dialog', { name: '重新导入' })
    expect(within(dialog).queryByText('王五（E051）')).not.toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: '导入' })).toBeDisabled()
    const rowOf = (name: string) => within(dialog).getByText(name).closest('tr')!
    await user.click(within(rowOf('赵六（E052）')).getByRole('checkbox'))
    await user.click(within(dialog).getByRole('button', { name: '导入' }))
    await waitFor(() => expect(restoreRecipients).toHaveBeenCalledWith([52]))
  })

  it('可通知人员按页显示：每页 100 人，可翻页，搜索后回到第一页', async () => {
    const user = userEvent.setup()
    const people = Array.from({ length: 105 }, (_, index) => ({
      userId: 100 + index, employeeNo: `E${100 + index}`, realName: `人员${String(index + 1).padStart(2, '0')}`, departmentName: '采购课',
      email: `p${index}@example.invalid`, canReceive: true, unavailableReason: null, removed: false,
    }))
    renderDirectory(baseApi({ allInternalRecipients: vi.fn().mockResolvedValue(people) }), ['oem:company_manage'])

    await user.click(await screen.findByRole('button', { name: '可通知人员' }))
    const drawer = (await screen.findByText('人员01（E100）')).closest<HTMLElement>('.arco-drawer')!
    expect(within(drawer).queryByText('人员101（E200）')).not.toBeInTheDocument()
    expect(drawer.querySelector('.arco-pagination')).not.toBeNull()
    await user.click(within(drawer).getByText('2', { selector: '.arco-pagination-item' }))
    expect(await within(drawer).findByText('人员101（E200）')).toBeInTheDocument()
    await user.type(within(drawer).getByPlaceholderText('姓名、工号或组织'), '人员01')
    expect(await within(drawer).findByText('人员01（E100）')).toBeInTheDocument()
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

  it.each([
    ['导入厂商', '导入 ODM/OEMA 厂商资料', 'ODM-OEMA厂商导入模板.xlsx', ['厂商名称', '备注'], ['示例厂商', '示例备注']],
    ['导入账号', '导入 ODM/OEMA 厂商账号', 'ODM-OEMA账号导入模板.xlsx', ['厂商名称', '账号', '初始密码'], ['示例厂商', 'demo001', 'T7!kmQ4n']],
    ['导入联络人', '导入 ODM/OEMA 厂商联络人', 'ODM-OEMA联络人导入模板.xlsx', ['厂商名称', '联络人姓名', '邮箱', '排序'], ['示例厂商', '示例联络人', 'example@example.com', '1']],
  ])('%s模板的数据表包含一行范例', async (menu, title, filename, headers, example) => {
    const user = userEvent.setup()
    vi.mocked(downloadExcelWorkbook).mockClear()
    renderDirectory(baseApi(), ['oem:company_manage', 'oem:account_manage'])

    await user.click(await screen.findByRole('button', { name: '导入' }))
    fireEvent.click(await screen.findByRole('menuitem', { name: menu }))
    const dialog = await screen.findByRole('dialog', { name: title })
    await user.click(within(dialog).getByRole('button', { name: '下载模板' }))
    await waitFor(() => expect(downloadExcelWorkbook).toHaveBeenCalledTimes(1))
    const [name, sheets] = vi.mocked(downloadExcelWorkbook).mock.calls[0]
    expect(name).toBe(filename)
    expect(sheets[0]).toEqual({ name: '数据', headers, rows: [example] })
  })

  it('共用 ODM/OEMA 登录账号不显示或要求填写联系人姓名和邮箱', async () => {
    const user = userEvent.setup()
    renderDirectory(baseApi(), ['oem:account_manage'])
    const accounts = await findAccountsPanel()
    expect(await within(accounts).findByText('OEM_0031')).toBeVisible()

    expect(within(accounts).queryByText('姓名')).not.toBeInTheDocument()
    expect(within(accounts).queryByText('邮箱')).not.toBeInTheDocument()
    await user.click(within(accounts).getByRole('button', { name: '新增账号' }))
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
    const view = renderDirectory(baseApi(), ['oem:company_manage', 'oem:account_manage'])

    const accountsWithoutDelete = await findAccountsPanel()
    expect(await within(accountsWithoutDelete).findByText('OEM_0031')).toBeVisible()
    expect(within(summarySection()).getByRole('button', { name: '编辑' })).toBeVisible()
    expect(within(summarySection()).queryByRole('button', { name: '删除' })).not.toBeInTheDocument()
    expect(within(accountsWithoutDelete).queryByRole('button', { name: '删除' })).not.toBeInTheDocument()
    view.unmount()

    renderDirectory(baseApi(), ['oem:company_manage', 'oem:company_delete', 'oem:account_manage', 'oem:account_delete'])
    const accountsWithDelete = await findAccountsPanel()
    expect(await within(accountsWithDelete).findByText('OEM_0031')).toBeVisible()
    expect(within(summarySection()).getByRole('button', { name: '删除' })).toBeVisible()
    expect(within(accountsWithDelete).getByRole('button', { name: '删除' })).toBeVisible()
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
    const panel = await findAccountsPanel()
    expect(await within(panel).findByText('OEM_0031')).toBeVisible()
    let accountRow = within(panel).getByText('OEM_0031').closest('tr')!

    await user.click(within(accountRow).getByRole('button', { name: '删除' }))
    expect(await screen.findByText('删除账号“OEM_0031”')).toBeVisible()
    let confirmDialog = screen.getByText('删除账号“OEM_0031”').closest<HTMLElement>('[role="dialog"]')!
    await user.click(within(confirmDialog).getByRole('button', { name: '确认删除' }))
    expect(await within(panel).findByRole('alert')).toHaveTextContent(backendReason)
    expect(within(panel).getByText('OEM_0031')).toBeVisible()
    await waitFor(() => expect(screen.queryByText('删除账号“OEM_0031”')).not.toBeInTheDocument())

    shouldFail = false
    accountRow = within(panel).getByText('OEM_0031').closest('tr')!
    await user.click(within(accountRow).getByRole('button', { name: '删除' }))
    confirmDialog = (await screen.findAllByText('删除账号“OEM_0031”')).at(-1)!.closest<HTMLElement>('[role="dialog"]')!
    await user.click(within(confirmDialog).getByRole('button', { name: '确认删除' }))
    await waitFor(() => expect(within(panel).queryByText('OEM_0031')).not.toBeInTheDocument())
    expect(within(vendorPanel()).getByText(/账号 0/)).toBeVisible()
    expect(accounts).toHaveBeenCalledTimes(2)
    expect(companies).toHaveBeenCalledTimes(2)
  })

  it('账号停用被拒绝时在面板内显示服务端原因，不刷新列表', async () => {
    const user = userEvent.setup()
    const backendReason = '该厂商有含业务记录的草稿，草稿仅创建人可见。须保留一个启用账号，请先启用其他账号或停用厂商。'
    const accounts = vi.fn().mockResolvedValue([account()])
    const setAccountStatus = vi.fn().mockRejectedValue({ response: { status: 409, data: { code: 40901, message: backendReason } } })
    renderDirectory(baseApi({ accounts, setAccountStatus }), ['oem:company_manage', 'oem:account_manage'])
    const panel = await findAccountsPanel()
    expect(await within(panel).findByText('OEM_0031')).toBeVisible()
    const accountRow = within(panel).getByText('OEM_0031').closest('tr')!

    await user.click(within(accountRow).getByRole('button', { name: '停用' }))
    fireEvent.click(await screen.findByRole('button', { name: /确\s*定/ }))
    await waitFor(() => expect(setAccountStatus).toHaveBeenCalledWith(31, 'DISABLED'))
    expect(await within(panel).findByRole('alert')).toHaveTextContent(`停用失败：${backendReason}`)
    expect(accounts).toHaveBeenCalledTimes(1)
    expect(within(accountRow).getByRole('button', { name: '停用' })).toBeVisible()
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
    await within(vendorPanel()).findByText('精工制造有限公司')

    await user.click(within(summarySection()).getByRole('button', { name: '停用' }))
    let confirmDialog = (await screen.findByText('停用厂商“精工制造有限公司”')).closest<HTMLElement>('[role="dialog"]')!
    fireEvent.click(within(confirmDialog).getByRole('button', { name: /确\s*定/ }))
    expect(await screen.findByText(`停用失败：${backendReason}`)).toHaveAttribute('role', 'alert')
    expect(setCompanyStatus).toHaveBeenCalledWith(7, 'DISABLED')
    expect(companies).toHaveBeenCalledTimes(1)
    await waitFor(() => expect(screen.queryByText('停用厂商“精工制造有限公司”')).not.toBeInTheDocument())

    shouldFail = false
    await user.click(within(summarySection()).getByRole('button', { name: '停用' }))
    confirmDialog = (await screen.findAllByText('停用厂商“精工制造有限公司”')).at(-1)!.closest<HTMLElement>('[role="dialog"]')!
    fireEvent.click(within(confirmDialog).getByRole('button', { name: /确\s*定/ }))
    await waitFor(() => expect(companies).toHaveBeenCalledTimes(2))
    await waitFor(() => expect(screen.queryByText(`停用失败：${backendReason}`)).not.toBeInTheDocument())
  })

  it('厂商删除忽略同步重复确认，并在删掉末页唯一行后回退一页', async () => {
    const user = userEvent.setup()
    const pending = deferred<void>()
    let deleted = false
    const companies = vi.fn().mockImplementation(async (query: { page: number }) => {
      if (query.page === 2 && !deleted) return page([company({ id: 91, name: '末页厂商' })], 2, 101)
      return page([company({ id: 1, name: '首页厂商' })], 1, deleted ? 100 : 101)
    })
    const deleteCompany = vi.fn().mockImplementation(async () => {
      await pending.promise
      deleted = true
    })
    renderDirectory(baseApi({ companies, deleteCompany }), ['oem:company_manage', 'oem:company_delete'])

    fireEvent.click((await within(vendorPanel()).findByText('2')).closest('.arco-pagination-item')!)
    await within(vendorPanel()).findByText('末页厂商')
    await user.click(within(summarySection()).getByRole('button', { name: '删除' }))
    expect(await screen.findByText('删除厂商“末页厂商”')).toBeVisible()
    const confirmDialog = screen.getByText('删除厂商“末页厂商”').closest<HTMLElement>('[role="dialog"]')!
    const confirm = within(confirmDialog).getByRole('button', { name: '确认删除' })
    fireEvent.click(confirm)
    fireEvent.click(confirm)
    await waitFor(() => expect(deleteCompany).toHaveBeenCalledTimes(1))
    await act(async () => pending.resolve())

    expect(await within(vendorPanel()).findByText('首页厂商')).toBeVisible()
    expect(companies).toHaveBeenLastCalledWith(expect.objectContaining({ page: 1 }))
    expect(vendorPanel().querySelector('.arco-pagination')).toBeNull()
  })

  it('左侧厂商列表单页隐藏分页，多页时在全局分页配置下也不显示重复总数或条数选择', async () => {
    const view = render(
      <ConfigProvider componentConfig={{ Pagination: TABLE_PAGINATION }}>
        {directoryElement(baseApi({
          companies: vi.fn().mockResolvedValue(page([company()], 1, 2)),
        }), ['oem:company_manage'])}
      </ConfigProvider>,
    )
    await within(vendorPanel()).findByText('精工制造有限公司')
    expect(vendorPanel().querySelector('.arco-pagination')).toBeNull()
    view.unmount()

    render(
      <ConfigProvider componentConfig={{ Pagination: TABLE_PAGINATION }}>
        {directoryElement(baseApi({
          companies: vi.fn().mockResolvedValue(page([company()], 1, 120)),
        }), ['oem:company_manage'])}
      </ConfigProvider>,
    )
    await within(vendorPanel()).findByText('精工制造有限公司')
    expect(vendorPanel().querySelector('.arco-pagination')).not.toBeNull()
    expect(within(vendorPanel()).queryByText(/共 \d+ 条/)).not.toBeInTheDocument()
    expect(vendorPanel().querySelector('.arco-pagination-option')).toBeNull()
  })

  it('厂商保存忽略同步重复确认', async () => {
    const user = userEvent.setup()
    const pending = deferred<Company>()
    const createCompany = vi.fn().mockReturnValue(pending.promise)
    renderDirectory(baseApi({ createCompany }), ['oem:company_manage'])
    await within(vendorPanel()).findByText('精工制造有限公司')

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

    await within(vendorPanel()).findByText('骏创')
    await waitFor(() => expect(accounts).toHaveBeenCalledWith(junchuang.id))
    await user.click(within(vendorPanel()).getByRole('button', { name: /元控/ }))
    const panel = await findAccountsPanel()
    expect(await within(panel).findByText('YK001')).toBeVisible()

    await act(async () => firstAccounts.resolve([junchuangAccount]))
    expect(within(panel).getByText('YK001')).toBeVisible()
    expect(screen.queryByText('JC001')).not.toBeInTheDocument()

    const accountRow = within(panel).getByText('YK001').closest('tr')!
    await user.click(within(accountRow).getByRole('button', { name: '删除' }))
    const confirm = (await screen.findByText('删除账号“YK001”')).closest<HTMLElement>('[role="dialog"]')!
    await user.click(within(confirm).getByRole('button', { name: '确认删除' }))

    await waitFor(() => expect(deleteAccount).toHaveBeenCalledTimes(1))
    expect(deleteAccount).toHaveBeenCalledWith(yuankongAccount.id)
    expect(deleteAccount).not.toHaveBeenCalledWith(junchuangAccount.id)
  })

  it('导入 ODM/OEMA 账号时拦截数值格式的账号列，并在厂商账号全部停用时展示提醒', async () => {
    const user = userEvent.setup()
    vi.mocked(readExcelTable).mockResolvedValueOnce([{
      rowNumber: 2,
      values: { 厂商名称: '精工制造有限公司', 账号: '12345', 初始密码: 'First#2026' },
      numericCells: new Set(['账号']),
    }])
    const accounts = vi.fn().mockResolvedValue([account({ status: 'DISABLED' })])
    renderDirectory(baseApi({ accounts }), ['oem:company_manage', 'oem:account_manage'])

    const panel = await findAccountsPanel()
    expect(await within(panel).findByText('该厂商所有账号均已停用，暂时无法登录门户接收或回传文件。')).toBeVisible()

    await user.click(screen.getByRole('button', { name: '导入' }))
    fireEvent.click(await screen.findByText('导入账号'))
    const dialog = await screen.findByRole('dialog', { name: '导入 ODM/OEMA 厂商账号' })
    await user.upload(within(dialog).getByLabelText('选择 Excel 文件'), new File(['x'], 'accounts.xlsx'))
    expect(await within(dialog).findByText('账号列请设置为文本格式，以保留前导零')).toBeVisible()
  })
})
