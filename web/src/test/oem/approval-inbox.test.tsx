import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { describe, expect, it, vi } from 'vitest'

import { createAppQueryClient } from '../../api/queryClient'
import type { OemApi } from '../../oem/api/OemApi'
import type { PendingTask } from '../../oem/api/types'
import { OemProvider } from '../../oem/OemContext'
import ApprovalInboxPage from '../../oem/pages/ApprovalInboxPage'

function task(taskId: number): PendingTask {
  return {
    taskId,
    version: 1,
    transferId: 100 + taskId,
    title: `图纸-${taskId}`,
    companyName: '甲厂',
    senderName: '发送人',
    senderEmployeeNo: 'A0007',
    nodeName: '课别主管',
    approvalMode: 'SINGLE',
    sentAt: '2026-10-05T00:00:00Z',
    activatedAt: '2026-10-05T00:01:00Z',
  }
}

function renderInbox(api: OemApi) {
  return render(
    <QueryClientProvider client={createAppQueryClient()}>
      <MemoryRouter initialEntries={['/oem/approvals']}>
        <OemProvider value={{
          api, realm: 'internal', base: '/oem', permissions: new Set(['oem:flow_approve']), userId: 7, queryScope: ['internal', 1],
        }}>
          <Routes>
            <Route path="/oem/approvals" element={<ApprovalInboxPage />} />
          </Routes>
        </OemProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

describe('OEM approval inbox', () => {
  it('pages through pending approvals on the server', async () => {
    const pendingApprovals = vi.fn().mockImplementation(({ page }: { page: number }) => Promise.resolve(page === 1
      ? { list: Array.from({ length: 20 }, (_, index) => task(index + 1)), total: 21, page: 1, pageSize: 20 }
      : { list: [task(21)], total: 21, page: 2, pageSize: 20 }))
    renderInbox({ pendingApprovals } as unknown as OemApi)

    expect(await screen.findByRole('link', { name: '图纸-1' })).toBeInTheDocument()
    expect(pendingApprovals).toHaveBeenLastCalledWith({ page: 1, pageSize: 20 })
    expect(screen.queryByRole('link', { name: '图纸-21' })).not.toBeInTheDocument()

    const pagination = document.querySelector('.arco-pagination') as HTMLElement
    await userEvent.setup().click(within(pagination).getByText('2'))

    expect(await screen.findByRole('link', { name: '图纸-21' })).toHaveAttribute('href', '/oem/transfers/121')
    await waitFor(() => expect(screen.queryByRole('link', { name: '图纸-1' })).not.toBeInTheDocument())
    expect(pendingApprovals).toHaveBeenLastCalledWith({ page: 2, pageSize: 20 })
  })
})
