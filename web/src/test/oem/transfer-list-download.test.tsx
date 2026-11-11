import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createAppQueryClient } from '../../api/queryClient'
import type { OemApi } from '../../oem/api/OemApi'
import type { TransferDetail, TransferFile, TransferSummary } from '../../oem/api/types'
import { OemProvider } from '../../oem/OemContext'
import TransferListPage from '../../oem/pages/TransferListPage'

vi.mock('../../oem/components/OemFilePreview', () => ({
  default: ({ file }: { file: TransferFile | null }) => file ? <div role="region" aria-label="文件预览">{file.originalName}</div> : null,
}))

// previewableCount 默认等于可下载数（夹具里的文件默认都是可预览格式）；不可预览的行显式传 0。
function summary(id: number, title: string, downloadableCount: number, previewableCount = downloadableCount): TransferSummary {
  return {
    id, direction: 'INTERNAL_TO_OEM', companyId: 9, companyName: '公司', title,
    sender: { realm: 'internal', id: 7, employeeNo: '', realName: '发送人' },
    lifecycleStatus: 'RELEASED', approvalStatus: 'APPROVED', approvalBlockedReason: null, validationSummary: 'VALID',
    fileCount: 2, totalBytes: 2048, availableCount: 2, purgePendingCount: 0, purgedCount: 0, missingCount: 0,
    downloadableCount, previewableCount, createdAt: '2026-11-10T00:00:00Z', sentAt: '2026-11-10T00:01:00Z', releasedAt: '2026-11-10T00:02:00Z', purgeDueAt: null, version: 2,
  }
}

function file(id: number, originalName: string, downloadable = true): TransferFile {
  return {
    id, originalName, ext: originalName.split('.').pop()!, sizeBytes: 1024, sha256: 'a'.repeat(64),
    validationStatus: 'VALID', payloadStatus: 'AVAILABLE', validationAttempts: 0, validationMessage: null,
    createdAt: '2026-11-10T00:00:00Z', firstRecipientDownloadAt: null, purgeDueAt: null, purgedAt: null, downloadable,
  } as TransferFile
}

function detail(row: TransferSummary, files: TransferFile[]) {
  return { summary: row, files } as unknown as TransferDetail
}

function LocationProbe() {
  return <span data-testid="location">{useLocation().pathname}</span>
}

function renderList(rows: TransferSummary[], details: Record<number, TransferDetail>) {
  const startDownload = vi.fn((id: number) => Promise.resolve({
    url: `/api/v1/oem/files/${id}/download`, downloadSessionId: 's', expiresAt: '', purpose: 'DOWNLOAD',
  }))
  const api = {
    listTransfers: vi.fn().mockResolvedValue({ list: rows, total: rows.length, page: 1, pageSize: 20 }),
    transfer: vi.fn((id: number) => Promise.resolve(details[id])),
    startDownload,
  } as unknown as OemApi
  render(
    <QueryClientProvider client={createAppQueryClient()}>
      <MemoryRouter initialEntries={['/oem-portal/transfers']}>
        <OemProvider value={{ api, realm: 'oem', base: '/oem-portal', permissions: new Set(), userId: 3, queryScope: ['list-download'] }}>
          <Routes>
            <Route path="/oem-portal/transfers" element={<><TransferListPage /><LocationProbe /></>} />
            <Route path="/oem-portal/transfers/:id" element={<LocationProbe />} />
          </Routes>
        </OemProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  )
  return { api, startDownload }
}

const frames = () => Array.from(document.querySelectorAll<HTMLIFrameElement>('iframe.native-download-frame'))

describe('ODM/OEMA 传递单列表直接下载', () => {
  afterEach(() => {
    for (const frame of frames()) frame.remove()
  })

  it('只有可下载的行在最后一列显示下载；单个文件直接下载且不进入详情', async () => {
    const user = userEvent.setup()
    const single = summary(41, '测试文件.xlsx', 1)
    const locked = summary(42, '未发布.xlsx', 0)
    const { api, startDownload } = renderList([single, locked], { 41: detail(single, [file(301, '测试文件.xlsx'), file(302, '已清理.xlsx', false)]) })

    const button = await screen.findByRole('button', { name: '下载 测试文件.xlsx' })
    expect(screen.queryByRole('button', { name: '下载 未发布.xlsx' })).not.toBeInTheDocument()
    expect(screen.getByRole('columnheader', { name: /操作/ })).toBeVisible()

    await user.click(button)

    await vi.waitFor(() => expect(frames()).toHaveLength(1))
    expect(api.transfer).toHaveBeenCalledWith(41)
    expect(startDownload).toHaveBeenCalledExactlyOnceWith(301)
    expect(new URL(frames()[0].src).pathname).toBe('/api/v1/oem/files/301/download')
    expect(screen.getByTestId('location')).toHaveTextContent('/oem-portal/transfers')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('最后一列有预览：单个可预览文件直接打开预览，不进入详情', async () => {
    const user = userEvent.setup()
    const row = summary(45, '图纸.pdf', 1)
    const { startDownload } = renderList([row], { 45: detail(row, [file(331, '图纸.pdf')]) })

    await user.click(await screen.findByRole('button', { name: '预览 图纸.pdf' }))

    expect(await screen.findByRole('region', { name: '文件预览' })).toHaveTextContent('图纸.pdf')
    expect(startDownload).not.toHaveBeenCalled()
    expect(screen.getByTestId('location')).toHaveTextContent('/oem-portal/transfers')
  })

  it('不支持在线预览的格式不显示预览按钮，只保留下载', async () => {
    const row = summary(46, '底座.step', 1, 0)
    renderList([row], { 46: detail(row, [file(341, '底座.step')]) })

    expect(await screen.findByRole('button', { name: '下载 底座.step' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '预览 底座.step' })).not.toBeInTheDocument()
  })

  it('多个文件时弹窗逐个预览或下载', async () => {
    const user = userEvent.setup()
    const multi = summary(47, '图纸 等2个文件', 2)
    renderList([multi], { 47: detail(multi, [file(351, '底座.step'), file(352, '控制柜.pdf')]) })

    await user.click(await screen.findByRole('button', { name: '预览 图纸 等2个文件' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).queryByRole('button', { name: '预览 底座.step' })).not.toBeInTheDocument()
    await user.click(within(dialog).getByRole('button', { name: '预览 控制柜.pdf' }))
    expect(await screen.findByRole('region', { name: '文件预览' })).toHaveTextContent('控制柜.pdf')
  })

  it('多个文件时列出可下载文件，逐个发起下载', async () => {
    const user = userEvent.setup()
    const multi = summary(43, '图纸 等2个文件', 2)
    const { startDownload } = renderList([multi], { 43: detail(multi, [file(311, '底座.step'), file(312, '控制柜.pdf')]) })

    await user.click(await screen.findByRole('button', { name: '下载 图纸 等2个文件' }))

    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByText('底座.step')).toBeVisible()
    expect(within(dialog).getByText('控制柜.pdf')).toBeVisible()
    expect(startDownload).not.toHaveBeenCalled()
    const buttons = within(dialog).getAllByRole('button', { name: /下载/ })
    await user.click(buttons[1])
    await vi.waitFor(() => expect(startDownload).toHaveBeenCalledExactlyOnceWith(312))
    expect(screen.getByTestId('location')).toHaveTextContent('/oem-portal/transfers')
  })

  it('列表数量过期时提示不可下载并刷新列表', async () => {
    const user = userEvent.setup()
    const stale = summary(44, '已过期.xlsx', 1)
    const { api, startDownload } = renderList([stale], { 44: detail(stale, [file(321, '已过期.xlsx', false)]) })

    await user.click(await screen.findByRole('button', { name: '下载 已过期.xlsx' }))

    expect(await screen.findByText('文件已不可下载')).toBeInTheDocument()
    expect(startDownload).not.toHaveBeenCalled()
    await vi.waitFor(() => expect(api.listTransfers).toHaveBeenCalledTimes(2))
  })
})
