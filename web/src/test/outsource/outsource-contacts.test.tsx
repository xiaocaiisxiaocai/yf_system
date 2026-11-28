import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import http from '../../api/client'
import OutsourceContactsPage from '../../outsource/pages/OutsourceContactsPage'
import * as contactsWorkbook from '../../outsource/contactsWorkbook'

const sampleColumns = [
  { id: 1, colKey: 'col_grp', title: '归属端', dataType: 'TEXT', personDisplay: 'COMBINED', phoneTitle: null, autoMerge: true, vendorVisible: true, isGroupColumn: true, sortNo: 10, widthHint: 16, status: 'ACTIVE' },
  { id: 2, colKey: 'col_unit', title: '对应课别', dataType: 'TEXT', personDisplay: 'COMBINED', phoneTitle: null, autoMerge: true, vendorVisible: true, isGroupColumn: false, sortNo: 20, widthHint: 18, status: 'ACTIVE' },
  { id: 3, colKey: 'col_mgr', title: '课别主管', dataType: 'PERSON', personDisplay: 'SPLIT', phoneTitle: '主管电话', autoMerge: true, vendorVisible: true, isGroupColumn: false, sortNo: 30, widthHint: 16, status: 'ACTIVE' },
]

const sampleVendors = [
  { id: 101, name: '外包厂商A', status: 'ACTIVE' },
  { id: 102, name: '外包厂商B', status: 'ACTIVE' },
]

const samplePeople = [
  { id: 11, scopeType: 'INTERNAL', vendorId: null, vendorName: null, userId: null, userEmployeeNo: null, userRealName: null, name: '主管甲', phone: '13800000001', email: null, titleHint: '一课主管', status: 'ACTIVE', updatedAt: '2026-11-28T00:00:00Z' },
]

const sampleRows = [
  {
    id: 51, sortNo: 10, visibility: 'SPECIFIC_VENDORS', version: 2, vendorIds: [101],
    values: { col_grp: '研发端', col_unit: '电控工令' },
    people: { col_mgr: [{ personId: 11, prefixLabel: null, name: '主管甲', phone: '13800000001', email: null, scopeType: 'INTERNAL', vendorId: null, vendorName: null, status: 'ACTIVE' }] },
    updatedAt: '2026-11-28T00:00:00Z',
  },
  {
    id: 52, sortNo: 20, visibility: 'ALL_VENDORS', version: 3, vendorIds: [],
    values: { col_grp: '研发端', col_unit: '电控程序' },
    people: { col_mgr: [{ personId: 11, prefixLabel: null, name: '主管甲', phone: '13800000001', email: null, scopeType: 'INTERNAL', vendorId: null, vendorName: null, status: 'ACTIVE' }] },
    updatedAt: '2026-11-28T00:00:00Z',
  },
]

function renderPage() {
  return render(
    <MemoryRouter>
      <OutsourceContactsPage />
    </MemoryRouter>,
  )
}

describe('OutsourceContactsPage', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('blocks confirmation during a mode change and requires a fresh preview after a conflict', async () => {
    const user = userEvent.setup()
    const rows = [{ rowNumber: 2, visibilityText: '仅内部',
      cells: [{ colKey: 'col_grp', textValue: '研发端', phonePairText: null }] }]
    vi.spyOn(contactsWorkbook, 'parseContactsWorkbook').mockResolvedValue(rows)
    vi.spyOn(http, 'get').mockImplementation(async (url: string) => (url === '/outsource/contacts'
      ? { data: { columns: sampleColumns, rows: sampleRows, vendors: sampleVendors, canManage: true, canConfigureColumns: false } }
      : { data: { people: samplePeople, vendors: sampleVendors } }) as never)

    function response(mode: string, previewVersion: string, replaceCount = 0) {
      return { data: { mode, previewVersion, replaceCount, ready: 1, skipped: 0, invalid: 0,
        rows: [], phoneChangeCount: 1, peoplePlans: [] } }
    }
    let resolvePreview!: (value: ReturnType<typeof response>) => void
    const delayed = new Promise<ReturnType<typeof response>>((resolve) => { resolvePreview = resolve })
    const post = vi.spyOn(http, 'post')
      .mockResolvedValueOnce(response('APPEND', 'append-version'))
      .mockImplementationOnce(() => delayed)
      .mockRejectedValueOnce({ response: { status: 409, data: { message: '导入预览已失效，请重新预览并确认' } } })
      .mockResolvedValueOnce(response('REPLACE_ALL', 'fresh-version', 3))
      .mockResolvedValueOnce({ data: { inserted: 1, replaced: 3, createdPeople: 0, updatedPhones: 1 } })

    renderPage()
    await user.click(await screen.findByRole('button', { name: 'Excel 导入' }))
    const dialog = await screen.findByRole('dialog', { name: 'Excel 导入对接联络表' })
    const confirm = within(dialog).getByRole('button', { name: '确认导入' })
    fireEvent.change(dialog.querySelector('input[type="file"]')!, {
      target: { files: [new File(['联络表'], '联络表.xlsx')] },
    })
    await user.click(await within(dialog).findByRole('checkbox', { name: /检测到 1 位/ }))
    expect(confirm).toBeEnabled()
    await user.click(within(dialog).getByText('整表覆盖'))
    expect(confirm).toBeDisabled()
    expect(within(dialog).queryByText(/待写入/)).not.toBeInTheDocument()
    resolvePreview(response('REPLACE_ALL', 'replace-version', 2))
    expect(await within(dialog).findByText('待覆盖：2')).toBeVisible()
    expect(confirm).toBeDisabled()
    await user.click(within(dialog).getByRole('checkbox', { name: /检测到 1 位/ }))
    await user.click(confirm)
    expect(post).toHaveBeenNthCalledWith(3, '/outsource/contacts/import', expect.objectContaining({
      mode: 'REPLACE_ALL', previewVersion: 'replace-version', confirmPhoneUpdates: true,
    }))
    await user.click(await within(dialog).findByRole('button', { name: '重新预览' }))
    expect(await within(dialog).findByText('待覆盖：3')).toBeVisible()
    expect(confirm).toBeDisabled()
    await user.click(within(dialog).getByRole('checkbox', { name: /检测到 1 位/ }))
    await user.click(confirm)
    expect(post).toHaveBeenNthCalledWith(5, '/outsource/contacts/import', expect.objectContaining({
      rows, previewVersion: 'fresh-version', confirmPhoneUpdates: true,
    }))
  })

  it('shows empty-state onboarding for configurator and notice for read-only users', async () => {
    vi.spyOn(http, 'get').mockImplementation(async (url: string) => {
      if (url === '/outsource/contacts') {
        return { data: { columns: [], rows: [], vendors: sampleVendors, canManage: false, canConfigureColumns: false } } as never
      }
      return { data: { people: [], vendors: sampleVendors } } as never
    })
    const { unmount } = renderPage()
    expect(await screen.findByText('管理员尚未配置联络表结构')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /管理列|新增一行/ })).not.toBeInTheDocument()
    unmount()

    vi.restoreAllMocks()
    vi.spyOn(http, 'get').mockImplementation(async (url: string) => {
      if (url === '/outsource/contacts') {
        return { data: { columns: [], rows: [], vendors: sampleVendors, canManage: false, canConfigureColumns: true } } as never
      }
      return { data: { people: [], vendors: sampleVendors } } as never
    })
    renderPage()
    expect(await screen.findByRole('button', { name: '立即配置列' })).toBeInTheDocument()
  })

  it('enforces read-only UI when canManage=false and allows merged-cell edit & batch visibility when canManage=true', async () => {
    const user = userEvent.setup()
    vi.spyOn(http, 'get').mockImplementation(async (url: string) => {
      if (url === '/outsource/contacts') {
        return { data: { columns: sampleColumns, rows: sampleRows, vendors: sampleVendors, canManage: false, canConfigureColumns: false } } as never
      }
      return { data: { people: samplePeople, vendors: sampleVendors } } as never
    })
    const { unmount } = renderPage()
    expect(await screen.findByText('电控工令')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '导出 Excel' })).toBeEnabled()
    expect(screen.queryByRole('button', { name: /新增一行|管理列|Excel 导入|下载模板|复制/ })).not.toBeInTheDocument()
    await user.click(screen.getByText('电控工令'))
    expect(screen.queryByText(/同步更新当前合并块/)).not.toBeInTheDocument()
    unmount()

    vi.restoreAllMocks()
    vi.spyOn(http, 'get').mockImplementation(async (url: string) => {
      if (url === '/outsource/contacts') {
        return { data: { columns: sampleColumns, rows: sampleRows, vendors: sampleVendors, canManage: true, canConfigureColumns: false } } as never
      }
      return { data: { people: samplePeople, vendors: sampleVendors } } as never
    })
    const put = vi.spyOn(http, 'put').mockResolvedValue({ data: [] } as never)

    renderPage()
    const mergedGroupCell = await screen.findByText('研发端', { selector: 'td span' })
    expect(mergedGroupCell.closest('td')).toHaveAttribute('rowspan', '2')
    expect(screen.queryByRole('button', { name: '管理列' })).not.toBeInTheDocument()

    // Click merged 研发端 cell -> saves both rows 51 and 52
    await user.click(mergedGroupCell)
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByText(/同步更新当前合并块的全部 2 行/)).toBeInTheDocument()
    const input = within(dialog).getByDisplayValue('研发端')
    await user.clear(input)
    await user.type(input, '研发中心')
    await user.click(within(dialog).getByRole('button', { name: '确定' }))

    await waitFor(() => expect(put).toHaveBeenCalledWith('/outsource/contacts/cells', {
      columnId: 1,
      rows: [{ rowId: 51, expectedVersion: 2 }, { rowId: 52, expectedVersion: 3 }],
      textValue: '研发中心',
      people: null,
    }))

    // Select both rows -> open batch visibility modal
    const checkboxes = screen.getAllByRole('checkbox')
    await user.click(checkboxes[0]) // header select all
    await user.click(await screen.findByRole('button', { name: '批量设置给谁看 (2)' }))
    const visDialog = await screen.findByRole('dialog')
    await user.click(within(visDialog).getByText('仅公司内部可见（对所有外包隐藏）'))
    await user.click(within(visDialog).getByRole('button', { name: '确定' }))

    await waitFor(() => expect(put).toHaveBeenCalledWith('/outsource/contacts/rows/visibility', {
      rows: [{ rowId: 51, expectedVersion: 2 }, { rowId: 52, expectedVersion: 3 }],
      visibility: 'INTERNAL_ONLY',
      vendorIds: [],
    }))
  })
})
