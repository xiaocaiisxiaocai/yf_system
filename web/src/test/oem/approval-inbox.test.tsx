import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
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
    const allTasks = Array.from({ length: 101 }, (_, index) => task(index + 1))
    const pendingApprovals = vi.fn().mockImplementation(({ page, pageSize }: { page: number; pageSize: number }) =>
      Promise.resolve({ list: allTasks.slice((page - 1) * pageSize, page * pageSize), total: allTasks.length, page, pageSize }))
    renderInbox({ pendingApprovals } as unknown as OemApi)

    expect(await screen.findByRole('link', { name: '图纸-1' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '显示列' })).toBeInTheDocument()
    expect(screen.getByRole('separator', { name: '调整传递单列宽' })).toBeInTheDocument()
    expect(pendingApprovals).toHaveBeenLastCalledWith({ page: 1, pageSize: 100 })
    expect(screen.queryByRole('link', { name: '图纸-101' })).not.toBeInTheDocument()

    const pagination = document.querySelector('.arco-pagination') as HTMLElement
    const user = userEvent.setup()
    await user.click(within(pagination).getByText('2'))

    expect(await screen.findByRole('link', { name: '图纸-101' })).toHaveAttribute('href', '/oem/transfers/201')
    await waitFor(() => expect(screen.queryByRole('link', { name: '图纸-1' })).not.toBeInTheDocument())
    expect(pendingApprovals).toHaveBeenLastCalledWith({ page: 2, pageSize: 100 })

    await user.click(screen.getByRole('combobox'))
    const options = await screen.findAllByRole('option')
    expect(options.map(option => option.textContent?.match(/\d+/)?.[0])).toEqual(['50', '100', '200', '500'])
    fireEvent.click(options[3])
    await waitFor(() => expect(pendingApprovals).toHaveBeenLastCalledWith({ page: 1, pageSize: 500 }))
  })
})
