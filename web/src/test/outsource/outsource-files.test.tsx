import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { AxiosError } from 'axios'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import http from '../../api/client'
import type { OutsourceItemFileResponse, OutsourceItemFilesResponse, OutsourceItemResponse } from '../../api/generated/api-types'
import { fmtSize, fmtTime } from '../../api/types'
import { OEM_PREVIEW_MAX_BYTES } from '../../oem/components/oemPreview'
import OutsourceListPage from '../../outsource/pages/OutsourceListPage'
import { useAuth } from '../../store/auth'

const available: OutsourceItemFileResponse = {
  fileId: 81, transferId: 19, name: '装配图.PNG', sizeBytes: 2048, direction: 'INTERNAL_TO_OEM',
  vendorName: '原外发厂商', sentAt: '2026-11-24T01:30:00Z', state: 'AVAILABLE',
}

function item(id: number, fileCount: number, purgedFileCount = 0): OutsourceItemResponse {
  return {
    id, serialNo: id, workOrderNo: `工令${id}`, outsourceDate: '2026-11-24', vendorId: 2, vendorName: '现外发厂商',
    customer: null, machineModel: null, supervisor: null, mechanicalOwner: null, electricalOwner: null,
    workOrderIssuedOn: null, bomOn: null, drawingReleasedOn: null, typeCode: 'OEMA', typeId: 5, typeLabel: 'OEMA',
    quantity: 1, originalDueDate: null, latestDueDate: null, remark: null, status: 'NORMAL', sendStatus: 'SENT',
    matchState: 'OK', closed: false, ipmsSyncedAt: null, updatedAt: available.sentAt, electricalHandoff: false,
    electricalHandoffAt: null, electricalHandoffSigner: null, changedFields: ['files'], importedBy: null, importedAt: null,
    modifiedBy: null, modifiedAt: null, customerFullName: null, customValues: null, fileCount, purgedFileCount,
  }
}

function renderList(items: OutsourceItemResponse[], loadFiles: () => Promise<OutsourceItemFilesResponse>, permissions: string[] = []) {
  useAuth.setState({
    token: 'token', user: { id: 7, employeeNo: 'buyer', realName: '采购', email: '', userType: 'INTERNAL', supplierId: null, isSystemAdmin: false },
    permissions: ['outsource:view', ...permissions], menus: [], generation: 1, mustChangePassword: false, booted: true,
  })
  const fields = [{ key: 'workOrderNo', label: '工令', pinned: true }, { key: 'files', label: '档案', pinned: false }]
    .map((field) => ({ ...field, category: 'FIXED', dataType: 'TEXT', options: [], vendorVisible: true, vendorEditable: false, canEdit: false }))
  const get = vi.spyOn(http, 'get').mockImplementation(async (url: string) => {
    if (url === '/outsource/items/1/files') return { data: await loadFiles() }
    if (url === `/oem/files/${available.fileId}/content`) return { data: new Blob(['image'], { type: 'image/png' }) }
    const data = url === '/outsource/items' ? { list: items, total: items.length, page: 1, pageSize: 20 }
      : url === '/outsource/fields' ? { fields } : url === '/outsource/sync-status' ? null : []
    return { data }
  })
  render(<MemoryRouter><OutsourceListPage /></MemoryRouter>)
  return get
}

const fileActions = () => screen.queryAllByRole('button', { name: /^(预览|下载)/ })

describe('外发工令档案', () => {
  afterEach(() => {
    document.querySelectorAll('iframe.native-download-frame').forEach((frame) => frame.remove())
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
    useAuth.setState({ token: null, user: null, permissions: [], menus: [] })
  })

  it('light is gray without files and green with available files', async () => {
    const loadFiles = vi.fn()
    renderList([item(1, 0), item(2, 3, 1), item(3, 0, 2)], loadFiles)

    const header = await screen.findByRole('columnheader', { name: /^档案/ })
    expect(header.previousElementSibling).toHaveTextContent('工令')
    expect(header).not.toHaveClass('arco-table-col-fixed-left')
    const empty = screen.getByRole('button', { name: '档案：无文件' })
    const ready = screen.getByRole('button', { name: '档案：3 个文件' })
    const purged = screen.getByRole('button', { name: '档案：已清除' })
    expect(empty).toHaveTextContent('0')
    expect(ready).toHaveTextContent('3')
    expect(purged).toHaveTextContent('已清除')
    expect(empty.querySelector('.outsource-files-light')).not.toHaveClass('outsource-files-light--available')
    expect(ready.querySelector('.outsource-files-light')).toHaveClass('outsource-files-light--available')
    expect(purged.querySelector('.outsource-files-light')).not.toHaveClass('outsource-files-light--available')
    expect(ready.closest('td')).not.toHaveClass('outsource-cell-changed')
    expect(loadFiles).not.toHaveBeenCalled()
  })

  it('clicking loads the list lazily and shows name, direction, vendor and state', async () => {
    const user = userEvent.setup()
    let resolveFiles!: (response: OutsourceItemFilesResponse) => void
    const loadFiles = vi.fn(() => new Promise<OutsourceItemFilesResponse>((resolve) => { resolveFiles = resolve }))
    const get = renderList([item(1, 1)], loadFiles)

    const trigger = await screen.findByRole('button', { name: '档案：1 个文件' })
    expect(loadFiles).not.toHaveBeenCalled()
    trigger.focus()
    await user.keyboard('{Enter}')
    expect(await screen.findByRole('status', { name: '正在加载档案' })).toBeInTheDocument()
    expect(loadFiles).toHaveBeenCalledTimes(1)
    expect(get).toHaveBeenCalledWith('/outsource/items/1/files')
    await act(async () => resolveFiles({ canOpen: false, files: [available] }))
    const row = within((await screen.findByText(available.name)).closest('li')!)
    for (const value of ['发给厂商', available.vendorName, fmtSize(available.sizeBytes), fmtTime(available.sentAt), '可用']) {
      expect(row.getByText(value)).toBeInTheDocument()
    }
  })

  it('purged files are listed with 已清除 and have no buttons', async () => {
    const user = userEvent.setup()
    const purged = { ...available, name: '已归档图纸.pdf', state: 'PURGED', direction: 'OEM_TO_INTERNAL' }
    renderList([item(1, 0, 1)], async () => ({ canOpen: true, files: [purged] }))

    await user.click(await screen.findByRole('button', { name: '档案：已清除' }))
    const name = await screen.findByText(purged.name)
    const row = within(name.closest('li')!)
    expect(name).toHaveClass('outsource-files-name--purged')
    expect(row.getByText('已清除')).toBeInTheDocument()
    expect(row.getByText('厂商发来')).toBeInTheDocument()
    expect(row.queryAllByRole('button')).toHaveLength(0)
  })

  it('viewer without canOpen sees names but no preview or download buttons', async () => {
    const user = userEvent.setup()
    renderList([item(1, 1)], async () => ({ canOpen: false, files: [available] }), ['outsource:file_view'])

    await user.click(await screen.findByRole('button', { name: '档案：1 个文件' }))
    expect(await screen.findByText(available.name)).toBeInTheDocument()
    expect(fileActions()).toHaveLength(0)
  })

  it('viewer with canOpen can preview and download an available file', async () => {
    const user = userEvent.setup()
    const oversized = { ...available, fileId: 82, name: '超大.pdf', sizeBytes: OEM_PREVIEW_MAX_BYTES + 1 }
    const unsupported = { ...available, fileId: 83, name: '图纸.step' }
    const get = renderList([item(1, 3)], async () => ({ canOpen: true, files: [available, oversized, unsupported] }), ['outsource:file_view'])
    const downloadPath = `/api/v1/oem/files/${available.fileId}/download`
    const post = vi.spyOn(http, 'post').mockResolvedValue({ data: { url: downloadPath } })
    vi.stubGlobal('URL', class extends URL {
      static createObjectURL = vi.fn(() => 'blob:preview')
      static revokeObjectURL = vi.fn()
    })

    await user.click(await screen.findByRole('button', { name: '档案：3 个文件' }))
    for (const file of [oversized, unsupported]) {
      const row = within((await screen.findByText(file.name)).closest('li')!)
      expect(row.queryByRole('button', { name: /^预览/ })).toBeDisabled()
      expect(row.getByRole('button', { name: `下载 ${file.name}` })).toBeEnabled()
    }
    const download = screen.getByRole('button', { name: `下载 ${available.name}` })
    // Arco prevents pointer interaction until the opening animation ends.
    await waitFor(() => expect(getComputedStyle(download).pointerEvents).not.toBe('none'))
    await user.click(download)
    await waitFor(() => expect(post).toHaveBeenCalledExactlyOnceWith(`/oem/files/${available.fileId}/download-sessions`, {}))
    expect(document.querySelector('iframe.native-download-frame')).toHaveAttribute('src', new URL(downloadPath, window.location.origin).href)

    await user.click(screen.getByRole('button', { name: `预览 ${available.name}` }))
    const dialog = await screen.findByRole('dialog', { name: `预览：${available.name}` })
    expect(within(dialog).getByRole('button', { name: '关闭文件预览' })).toBeInTheDocument()
    await waitFor(() => expect(get).toHaveBeenCalledWith(`/oem/files/${available.fileId}/content`, expect.objectContaining({ responseType: 'blob' })))
  })

  it('processing files have no buttons even with canOpen', async () => {
    const user = userEvent.setup()
    renderList([item(1, 0)], async () => ({ canOpen: true, files: [{ ...available, state: 'PROCESSING' }] }))

    await user.click(await screen.findByRole('button', { name: '档案：无文件' }))
    const row = within((await screen.findByText(available.name)).closest('li')!)
    expect(row.getByText('处理中')).toBeInTheDocument()
    expect(row.queryAllByRole('button')).toHaveLength(0)
  })

  it('load failure shows an error with retry', async () => {
    const user = userEvent.setup()
    const loadFiles = vi.fn().mockRejectedValueOnce(new AxiosError()).mockResolvedValueOnce({ canOpen: false, files: [available] })
    renderList([item(1, 1)], loadFiles)

    await user.click(await screen.findByRole('button', { name: '档案：1 个文件' }))
    const error = await screen.findByRole('alert')
    expect(error).toHaveTextContent('档案清单加载失败')
    const retry = within(error).getByRole('button', { name: '重试' })
    await waitFor(() => expect(getComputedStyle(retry).pointerEvents).not.toBe('none'))
    await user.click(retry)
    expect(await screen.findByText(available.name)).toBeVisible()
    expect(loadFiles).toHaveBeenCalledTimes(2)
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
})
