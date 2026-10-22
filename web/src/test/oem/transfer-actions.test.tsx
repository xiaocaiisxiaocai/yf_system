import { act, fireEvent, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { describe, expect, it, vi } from 'vitest'
import { createAppQueryClient } from '../../api/queryClient'
import type { OemApi } from '../../oem/api/OemApi'
import type { TransferDetail } from '../../oem/api/types'
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
      sender: { realm: 'internal', id: 7, employeeNo: 'A0007', realName: '发送人' },
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
      createdAt: '2026-10-05T00:00:00Z',
      sentAt: '2026-10-05T00:05:00Z',
      releasedAt: null,
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
        approverSource: 'SECTION_LEADER',
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
          replacesTaskId: null,
          reassignReason: null,
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

describe('OEM 终止与改派', () => {
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

  it('同步连点改派只提交一次，失败后可再次提交', async () => {
    const user = userEvent.setup()
    const consoleError = vi.spyOn(console, 'error').mockImplementation(() => undefined)
    let resolveReassign!: (value: TransferDetail) => void
    const reassign = vi.fn()
      .mockRejectedValueOnce(new Error('改派失败'))
      .mockImplementationOnce(() => new Promise<TransferDetail>((resolve) => { resolveReassign = resolve }))
    renderDetail({
      transfer: vi.fn().mockResolvedValue(detail()),
      approverOptions: vi.fn().mockResolvedValue([{ id: 12, employeeNo: 'A0012', realName: '新审批人' }]),
      reassign,
    })

    await user.click(await screen.findByRole('button', { name: '改派' }))
    const dialog = await screen.findByRole('dialog', { name: '改派审批人' })
    await user.click(withinDialog(dialog))
    // Arco 选项在下拉动画期间是 pointer-events: none，userEvent 点不到。
    fireEvent.click(await screen.findByRole('option', { name: '新审批人（A0012）' }))
    fireEvent.change(screen.getByPlaceholderText('改派原因（必填）'), { target: { value: '原审批人离职' } })
    // 只在改派对话框内查找：上一用例的 Modal.confirm 在关闭动画结束前可能仍留在 DOM 里。
    const ok = within(dialog).getByRole('button', { name: '确定' })
    await act(async () => {
      fireEvent.click(ok)
      fireEvent.click(ok)
    })
    expect(reassign).toHaveBeenCalledTimes(1)
    expect(reassign).toHaveBeenCalledWith(51, 12, '原审批人离职', 3)
    expect(consoleError).not.toHaveBeenCalled()
    expect(screen.getByRole('dialog', { name: '改派审批人' })).toBeInTheDocument()

    await user.click(within(dialog).getByRole('button', { name: '确定' }))
    expect(reassign).toHaveBeenCalledTimes(2)
    resolveReassign(detail())
    expect(await screen.findByText('已改派')).toBeInTheDocument()
    consoleError.mockRestore()
  })
})

function withinDialog(dialog: HTMLElement) {
  const combo = dialog.querySelector('[role="combobox"]')
  if (!combo) throw new Error('改派对话框里没有审批人选择框')
  return combo
}
