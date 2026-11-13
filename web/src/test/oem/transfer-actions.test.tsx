import { act, fireEvent, render, screen } from '@testing-library/react'
import { QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { describe, expect, it, vi } from 'vitest'
import { createAppQueryClient } from '../../api/queryClient'
import type { OemApi } from '../../oem/api/OemApi'
import type { TransferDetail, TransferFile } from '../../oem/api/types'
import { OemProvider } from '../../oem/OemContext'
import TransferDetailPage from '../../oem/pages/TransferDetailPage'


function detail(): TransferDetail {
  return {
    summary: {
      id: 88,
      direction: 'INTERNAL_TO_OEM',
      companyId: 9,
      companyName: '甲厂',
      title: '等待处置的传递单',
      sender: { realm: 'internal', id: 7, employeeNo: 'A0007', realName: '发送人' }, signerName: null,
      lifecycleStatus: 'SEALED',
      approvalStatus: 'APPROVAL_BLOCKED',
      approvalBlockedReason: '原审批人已停用',
      validationSummary: 'VALID',
      fileCount: 0,
      totalBytes: 0,
      availableCount: 0,
      purgePendingCount: 0,
      purgedCount: 0,
      missingCount: 0,
      downloadableCount: 0,
      previewableCount: 0,
      createdAt: '2026-10-05T00:00:00Z',
      sentAt: '2026-10-05T00:05:00Z',
      releasedAt: null,
      purgeDueAt: null,
      version: 4,
    },
    description: null,
    retention: { templateId: 1, templateName: '保留七天', mode: 'AFTER_RELEASE', releaseTtlMinutes: 10080, receiptGraceMinutes: null, summary: '发布后七天删除' },
    manifestSha256: null,
    expiresAt: null,
    closedReason: null,
    closedAt: null,
    capabilities: { canEdit: false, canSend: false, canDelete: false, canReadContent: true, contentPurpose: 'NONE' },
    files: [],
    approval: {
      instanceId: 5,
      status: 'APPROVAL_BLOCKED',
      blockedReason: '原审批人已停用',
      currentSortNo: 1,
      version: 6,
      templateName: '默认审批',
      nodes: [{
        sortNo: 1,
        name: '课别主管',
        approverSource: 'DIRECT_LEADER',
        approvalMode: 'SINGLE',
        status: 'PENDING',
        skipReason: null,
        usedFallback: false,
        completedAt: null,
        tasks: [{
          id: 51,
          status: 'PENDING',
          approverUserId: 21,
          approverName: '已停用主管',
          approverEmployeeNo: 'A0021',
          reason: null,
          decidedAt: null,
          version: 3,
        }],
      }],
    },
  }
}

function renderDetail(api: Partial<OemApi>) {
  return render(
    <QueryClientProvider client={createAppQueryClient()}>
      <MemoryRouter initialEntries={['/oem/transfers/88']}>
        <OemProvider value={{
          api: api as OemApi,
          realm: 'internal',
          base: '/oem',
          permissions: new Set(['oem:approval_recover', 'oem:transfer_view']),
          userId: 7,
          queryScope: ['actions'],
        }}>
          <Routes>
            <Route path="/oem/transfers/:id" element={<TransferDetailPage />} />
          </Routes>
        </OemProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

describe('ODM/OEMA 终止传递', () => {
  it('同步连点终止只提交一次', async () => {
    let resolveCancel!: (value: TransferDetail) => void
    const cancel = vi.fn().mockImplementation(() => new Promise<TransferDetail>((resolve) => { resolveCancel = resolve }))
    renderDetail({ transfer: vi.fn().mockResolvedValue(detail()), cancel })

    const terminate = await screen.findByRole('button', { name: '终止传递' })
    await act(async () => {
      fireEvent.click(terminate)
      fireEvent.click(terminate)
    })
    expect(await screen.findAllByRole('dialog')).toHaveLength(1)

    fireEvent.change(screen.getByPlaceholderText('请输入终止原因（发送人将收到通知）'), { target: { value: '审批人离职' } })
    const ok = screen.getByRole('button', { name: '确定' })
    await act(async () => {
      fireEvent.click(ok)
      fireEvent.click(ok)
    })
    expect(cancel).toHaveBeenCalledTimes(1)
    expect(cancel).toHaveBeenCalledWith(88, '审批人离职', 4)

    resolveCancel({ ...detail(), summary: { ...detail().summary, lifecycleStatus: 'CANCELLED' } })
    expect(await screen.findByText('传递已终止')).toBeInTheDocument()
  })
})

describe('ODM/OEMA 审批进度', () => {
  it('审批进度放在附件表的最后一列，下方没有审批流程卡片，也没有改派入口', async () => {
    const base = detail()
    const file = {
      id: 1, originalName: '图纸.pdf', ext: 'pdf', sizeBytes: 1024, sha256: 'a'.repeat(64), validationStatus: 'VALID', payloadStatus: 'AVAILABLE',
      validationAttempts: 0, validationMessage: null, createdAt: '2026-10-05T00:00:00Z', firstRecipientDownloadAt: null, purgeDueAt: null, purgedAt: null, downloadable: false,
    } as TransferFile
    renderDetail({ transfer: vi.fn().mockResolvedValue({ ...base, files: [file, { ...file, id: 2, originalName: '清单.xlsx' }] }) })

    await screen.findByText('图纸.pdf')
    const headers = screen.getAllByRole('columnheader').map((header) => header.textContent)
    expect(headers[headers.length - 1]).toBe('审批')
    expect(screen.getAllByText('课别主管').length).toBeGreaterThan(0)
    expect(screen.getAllByText('已停用主管（A0021）').length).toBeGreaterThan(0)
    expect(screen.getAllByText(/审批异常：原审批人已停用/).length).toBeGreaterThan(0)
    expect(screen.queryByText(/审批流程/)).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '改派' })).not.toBeInTheDocument()
  })
})
