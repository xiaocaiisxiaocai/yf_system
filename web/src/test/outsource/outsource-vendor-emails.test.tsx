import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import http from '../../api/client'
import OutsourceSettingsPage from '../../outsource/pages/OutsourceSettingsPage'
import { useAuth } from '../../store/auth'
import { createExcelWorkbook } from '../../utils/excelWorkbook'
import * as workbook from '../../utils/excelWorkbook'

const vendors = [
  { id: 1, name: '鼎亚', status: 'ACTIVE' },
  { id: 2, name: '元控', status: 'ACTIVE' },
  { id: 3, name: '停用厂', status: 'DISABLED' },
]
const emails = [
  { id: 11, vendorId: 1, vendorName: '鼎亚', email: 'a@vendor.test', updatedAt: '2026-11-20T00:00:00.000Z' },
  { id: 12, vendorId: 1, vendorName: '鼎亚', email: 'b@vendor.test', updatedAt: '2026-11-20T00:00:00.000Z' },
]

function setSession() {
  useAuth.setState({
    token: 'internal-token',
    user: { id: 7, employeeNo: 'buyer', realName: '采购', email: 'buyer@example.test', userType: 'INTERNAL', supplierId: null, isSystemAdmin: false },
    permissions: ['outsource:settings'],
    menus: [],
    mustChangePassword: false,
    booted: true,
    generation: 3,
  })
}

function mockApi() {
  return vi.spyOn(http, 'get').mockImplementation((url: string) => Promise.resolve({
    data: url.endsWith('/vendor-emails') ? emails : url.endsWith('/vendors') ? vendors : [],
  } as never))
}

async function openTab(user: ReturnType<typeof userEvent.setup>) {
  render(<MemoryRouter><OutsourceSettingsPage /></MemoryRouter>)
  await user.click(await screen.findByRole('tab', { name: '外协邮箱' }))
  await screen.findByText('a@vendor.test')
}

describe('外协清单设定 → 外协邮箱', () => {
  afterEach(() => {
    vi.restoreAllMocks()
    useAuth.setState({ token: null, user: null, permissions: [], menus: [] })
  })

  it('与默认抄送并排，表格第一列是厂商、第二列是邮箱，并提示还没有邮箱的启用厂商', async () => {
    const user = userEvent.setup()
    setSession()
    mockApi()
    await openTab(user)

    const tabs = screen.getAllByRole('tab').map((tab) => tab.textContent)
    expect(tabs.indexOf('外协邮箱')).toBe(tabs.indexOf('默认抄送') + 1)
    const headers = screen.getAllByRole('columnheader').map((header) => header.textContent)
    expect(headers.slice(0, 2)).toEqual(['厂商', '邮箱'])
    const row = screen.getByText('b@vendor.test').closest('tr')!
    expect(within(row).getAllByRole('cell')[0]).toHaveTextContent('鼎亚')
    // 元控 has no address yet; 鼎亚 has two and 停用厂 is disabled, so neither is listed.
    const warning = screen.getByText(/还没有外协邮箱/)
    expect(warning).toHaveTextContent('元控')
    expect(warning).not.toHaveTextContent('鼎亚')
    expect(warning).not.toHaveTextContent('停用厂')
  })

  it('选择厂商并填写邮箱后加入；邮箱格式不对时不提交', async () => {
    const user = userEvent.setup()
    setSession()
    mockApi()
    const post = vi.spyOn(http, 'post').mockResolvedValue({ data: { id: 13 } } as never)
    await openTab(user)

    const add = screen.getByRole('button', { name: /加入/ })
    expect(add).toBeDisabled()
    await user.click(screen.getByRole('combobox', { name: '选择厂商' }))
    // 弹出层展开动画期间不接收指针事件，直接触发点击。
    fireEvent.click(await screen.findByRole('option', { name: '元控' }))
    await user.type(screen.getByRole('textbox', { name: '邮箱' }), 'not-an-address')
    await user.click(add)
    expect(post).not.toHaveBeenCalled()

    await user.clear(screen.getByRole('textbox', { name: '邮箱' }))
    await user.type(screen.getByRole('textbox', { name: '邮箱' }), 'yk@vendor.test')
    await user.click(add)
    await waitFor(() => expect(post).toHaveBeenCalledWith('/outsource/settings/vendor-emails', { vendorId: 2, email: 'yk@vendor.test' }))
    // 停用厂 cannot be picked for a new address.
    await user.click(screen.getByRole('combobox', { name: '选择厂商' }))
    expect(screen.queryByRole('option', { name: '停用厂' })).not.toBeInTheDocument()
  })

  it('编辑只改邮箱，删除需要确认', async () => {
    const user = userEvent.setup()
    setSession()
    mockApi()
    const put = vi.spyOn(http, 'put').mockResolvedValue({ data: {} } as never)
    const del = vi.spyOn(http, 'delete').mockResolvedValue({ data: {} } as never)
    await openTab(user)

    const row = screen.getByText('a@vendor.test').closest('tr')!
    await user.click(within(row).getByRole('button', { name: '编辑' }))
    const input = await screen.findByDisplayValue('a@vendor.test')
    await user.clear(input)
    await user.type(input, 'new@vendor.test')
    await user.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(put).toHaveBeenCalledWith('/outsource/settings/vendor-emails/11', { email: 'new@vendor.test' }))

    await user.click(within(screen.getByText('b@vendor.test').closest('tr')!).getByRole('button', { name: '删除' }))
    expect(del).not.toHaveBeenCalled()
    fireEvent.click(await screen.findByRole('button', { name: '确定' }))
    await waitFor(() => expect(del).toHaveBeenCalledWith('/outsource/settings/vendor-emails/12'))
  })

  it('导出只有厂商名称和邮箱两列', async () => {
    const user = userEvent.setup()
    setSession()
    mockApi()
    const download = vi.spyOn(workbook, 'downloadExcelWorkbook').mockResolvedValue(undefined)
    await openTab(user)

    await user.click(screen.getByRole('button', { name: '导出' }))
    await waitFor(() => expect(download).toHaveBeenCalledTimes(1))
    const [fileName, sheets] = download.mock.calls[0]
    expect(fileName).toBe('外协邮箱.xlsx')
    expect(sheets[0].headers).toEqual(['厂商名称', '邮箱'])
    expect(sheets[0].rows).toEqual([['鼎亚', 'a@vendor.test'], ['鼎亚', 'b@vendor.test']])
  })

  it('导入先预览：不存在或停用的厂商、已有和重复的邮箱、格式错误都不会待导入，确认后只提交待导入的行', async () => {
    const user = userEvent.setup()
    setSession()
    mockApi()
    const post = vi.spyOn(http, 'post').mockResolvedValue({ data: { created: 1, skipped: 0, failed: 0, rows: [{ rowNumber: 3, key: 'new@vendor.test', status: 'CREATED', message: null }] } } as never)
    const download = vi.spyOn(workbook, 'downloadExcelWorkbook').mockResolvedValue(undefined)
    await openTab(user)

    await user.click(screen.getByRole('button', { name: '导入' }))
    const dialog = await screen.findByRole('dialog', { name: '导入外协邮箱' })
    await user.click(within(dialog).getByRole('button', { name: '下载模板' }))
    await waitFor(() => expect(download).toHaveBeenCalled())
    const [fileName, [sheet]] = download.mock.calls[0]
    expect(fileName).toBe('外协邮箱导入模板.xlsx')
    expect(sheet).toEqual({ name: '数据', headers: ['厂商名称', '邮箱'], rows: [['示例厂商', 'example@example.com']] })
    const buffer = await createExcelWorkbook([{ ...sheet, rows: [
      ...sheet.rows,
      ['元控', 'new@vendor.test'],
      ['鼎亚', 'A@vendor.test'],
      ['鼎亚', 'dup@vendor.test'],
      ['鼎亚', 'dup@vendor.test'],
      ['停用厂', 'x@vendor.test'],
      ['没有的厂商', 'y@vendor.test'],
      ['元控', 'bad-address'],
    ] }])
    await user.upload(within(dialog).getByLabelText('选择 Excel 文件') as HTMLInputElement,
      new File([buffer], 'emails.xlsx', { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' }))
    expect(await screen.findByText('待导入 1')).toBeInTheDocument()
    expect(screen.getByText('跳过 3')).toBeInTheDocument()
    expect(screen.getByText('错误 3')).toBeInTheDocument()
    expect(post).not.toHaveBeenCalled()

    await user.click(screen.getByRole('button', { name: '确认导入' }))
    await waitFor(() => expect(post).toHaveBeenCalledWith('/outsource/settings/vendor-emails/import',
      { rows: [{ rowNumber: 3, vendorName: '元控', email: 'new@vendor.test' }] }))
  })
})
