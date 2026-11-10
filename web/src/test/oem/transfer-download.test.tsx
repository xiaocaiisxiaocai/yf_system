import { fireEvent, render, screen } from '@testing-library/react'
import { QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createAppQueryClient } from '../../api/queryClient'
import type { OemApi } from '../../oem/api/OemApi'
import type { TransferDetail } from '../../oem/api/types'
import { OemProvider } from '../../oem/OemContext'
import TransferDetailPage from '../../oem/pages/TransferDetailPage'

function detail(): TransferDetail {
  return {
    summary: {
      id: 90,
      direction: 'OEM_TO_INTERNAL',
      companyId: 9,
      companyName: '甲厂',
      title: '可下载的传递单',
      sender: { realm: 'oem', id: 3, employeeNo: 'V0003', realName: '厂商' },
      lifecycleStatus: 'RELEASED',
      approvalStatus: 'NOT_REQUIRED',
      approvalBlockedReason: null,
      validationSummary: 'VALID',
      fileCount: 1,
      totalBytes: 1024,
      availableCount: 1,
      purgePendingCount: 0,
      purgedCount: 0,
      missingCount: 0,
      downloadableCount: 0,
      createdAt: '2026-10-05T00:00:00Z',
      sentAt: '2026-10-05T00:05:00Z',
      releasedAt: '2026-10-05T00:06:00Z',
      version: 2,
    },
    description: null,
    retention: { templateId: 1, templateName: '保留七天', mode: 'AFTER_RELEASE', releaseTtlMinutes: 10080, receiptGraceMinutes: null, summary: '发布后七天删除' },
    manifestSha256: null,
    expiresAt: null,
    closedReason: null,
    closedAt: null,
    capabilities: { canEdit: false, canSend: false, canDelete: false, canReadContent: true, contentPurpose: 'DOWNLOAD' },
    files: [{
      id: 301,
      originalName: '图纸.zip',
      ext: 'zip',
      sizeBytes: 1024,
      sha256: 'a'.repeat(64),
      validationStatus: 'VALID',
      payloadStatus: 'AVAILABLE',
      validationAttempts: 1,
      validationMessage: null,
      createdAt: '2026-10-05T00:01:00Z',
      firstRecipientDownloadAt: null,
      purgeDueAt: null,
      purgedAt: null,
      downloadable: true,
    }],
    approval: null,
  } as unknown as TransferDetail
}

function renderDetail(startDownload: (id: number) => Promise<unknown>) {
  const api = { transfer: vi.fn().mockResolvedValue(detail()), startDownload } as unknown as OemApi
  return render(
    <QueryClientProvider client={createAppQueryClient()}>
      <MemoryRouter initialEntries={['/oem/transfers/90']}>
        <OemProvider value={{ api, realm: 'internal', base: '/oem', permissions: new Set(['oem:transfer_view']), userId: 7, queryScope: ['download'] }}>
          <Routes><Route path="/oem/transfers/:id" element={<TransferDetailPage />} /></Routes>
        </OemProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

const frames = () => Array.from(document.querySelectorAll<HTMLIFrameElement>('iframe.native-download-frame'))

describe('ODM/OEMA 传递单下载', () => {
  afterEach(() => {
    for (const frame of frames()) frame.remove()
  })

  it('下载经隐藏 iframe 发起，不整页导航', async () => {
    const before = window.location.href
    const startDownload = vi.fn().mockResolvedValue({ url: '/api/v1/oem/files/301/download', downloadSessionId: 's', expiresAt: '', purpose: 'DOWNLOAD' })
    renderDetail(startDownload)

    fireEvent.click(await screen.findByRole('button', { name: /下载/ }))

    await vi.waitFor(() => expect(frames()).toHaveLength(1))
    expect(startDownload).toHaveBeenCalledWith(301)
    expect(frames()[0].src).toBe(`${window.location.origin}/api/v1/oem/files/301/download`)
    expect(window.location.href).toBe(before)
  })

  it('授权指向非本文件的下载路径时提示并拒绝发起', async () => {
    const startDownload = vi.fn().mockResolvedValue({ url: '/api/v1/oem/files/999/download', downloadSessionId: 's', expiresAt: '', purpose: 'DOWNLOAD' })
    renderDetail(startDownload)

    fireEvent.click(await screen.findByRole('button', { name: /下载/ }))

    expect(await screen.findByText('下载地址无效，请刷新后重试')).toBeInTheDocument()
    expect(frames()).toHaveLength(0)
  })
})
