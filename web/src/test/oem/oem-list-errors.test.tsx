import type { ReactNode } from 'react'
import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClientProvider } from '@tanstack/react-query'
import { AxiosError } from 'axios'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it, vi } from 'vitest'
import { createAppQueryClient } from '../../api/queryClient'
import type { OemApi } from '../../oem/api/OemApi'
import type { Company, Page, PendingTask, TransferSummary, VendorAccount } from '../../oem/api/types'
import { OemProvider, type OemContextValue } from '../../oem/OemContext'
import CompaniesPage from '../../oem/pages/admin/CompaniesPage'
import ApprovalInboxPage from '../../oem/pages/ApprovalInboxPage'
import BlockedRecoveryPage from '../../oem/pages/BlockedRecoveryPage'
import TransferListPage from '../../oem/pages/TransferListPage'

function serverDown() {
  return new AxiosError('Request failed with status code 500', 'ERR_BAD_RESPONSE')
}

function summary(title: string): TransferSummary {
  return {
    id: 41,
    direction: 'INTERNAL_TO_OEM',
    companyId: 9,
    companyName: '甲厂',
    title,
    sender: { realm: 'internal', id: 7, employeeNo: 'A0007', realName: '发送人' },
    lifecycleStatus: 'RELEASED',
    approvalStatus: 'APPROVED',
    approvalBlockedReason: null,
    validationSummary: 'VALID',
    fileCount: 1,
    totalBytes: 3,
    availableCount: 1,
    purgePendingCount: 0,
    purgedCount: 0,
    missingCount: 0,
    createdAt: '2026-10-05T00:00:00Z',
    sentAt: '2026-10-05T00:01:00Z',
    releasedAt: '2026-10-05T00:02:00Z',
    version: 2,
  }
}

function task(): PendingTask {
  return {
    taskId: 5,
    version: 1,
    transferId: 41,
    title: '待审图纸',
    companyName: '甲厂',
    senderName: '发送人',
    senderEmployeeNo: 'A0007',
    nodeName: '课别主管',
    approvalMode: 'SINGLE',
    sentAt: '2026-10-05T00:00:00Z',
    activatedAt: '2026-10-05T00:01:00Z',
  }
}

function company(): Company {
  return {
    id: 7,
    name: '精工制造有限公司',
    contactName: '联系人甲',
    contactPhone: null,
    contactEmail: null,
    remark: null,
    status: 'ACTIVE',
    accountCount: 1,
    activeAccountCount: 1,
    createdAt: '2026-10-05T00:00:00Z',
    updatedAt: '2026-10-05T00:00:00Z',
  }
}

function account(): VendorAccount {
  return {
    id: 31,
    employeeNo: 'OEM_0031',
    realName: '厂商账号甲',
    email: 'oem@example.invalid',
    companyId: 7,
    status: 'ACTIVE',
    mustChangePassword: false,
    locked: false,
    lastLoginAt: null,
    createdAt: '2026-10-05T00:00:00Z',
    updatedAt: '2026-10-05T00:00:00Z',
  }
}

function page<T>(list: T[]): Page<T> {
  return { list, total: list.length, page: 1, pageSize: 20 }
}

function renderOem(element: ReactNode, api: Partial<OemApi>, permissions: string[] = []) {
  const client = createAppQueryClient()
  const value: OemContextValue = {
    api: api as OemApi,
    realm: 'internal',
    base: '/oem',
    permissions: new Set(permissions),
    userId: 7,
    queryScope: ['list-error'],
  }
  const view = render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <OemProvider value={value}>{element}</OemProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  )
  return { client, ...view }
}

describe('OEM 列表加载失败', () => {
  it('传递单列表失败可重试，背景失败保留已有行', async () => {
    const user = userEvent.setup()
    const kept = summary('保留的传递单')
    const listTransfers = vi.fn()
      .mockRejectedValueOnce(serverDown())
      .mockResolvedValueOnce(page([summary('重试后的传递单')]))
      .mockRejectedValueOnce(serverDown())
    const first = renderOem(<TransferListPage />, { listTransfers }, ['oem:transfer_view'])

    expect(await screen.findByRole('button', { name: '重试' })).toBeVisible()
    expect(screen.getByText('传递单列表加载失败，请重试')).toBeVisible()
    expect(screen.queryByText('Request failed with status code 500')).not.toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: '重试' }))
    expect(await screen.findByText('重试后的传递单')).toBeVisible()
    first.unmount()

    const again = vi.fn()
      .mockResolvedValueOnce(page([kept]))
      .mockRejectedValueOnce(serverDown())
    const refreshed = renderOem(<TransferListPage />, { listTransfers: again }, ['oem:transfer_view'])
    expect(await screen.findByText('保留的传递单')).toBeVisible()
    await refreshed.client.refetchQueries()
    expect(screen.getByText('保留的传递单')).toBeVisible()
    expect(await screen.findByText('传递单列表加载失败，请重试')).toBeVisible()
  })

  it('待审批与审批异常列表失败可重试', async () => {
    const user = userEvent.setup()
    const pendingApprovals = vi.fn()
      .mockRejectedValueOnce(serverDown())
      .mockResolvedValueOnce(page([task()]))
    const { unmount } = renderOem(<ApprovalInboxPage />, { pendingApprovals }, ['oem:flow_approve'])
    expect(await screen.findByRole('alert')).toHaveTextContent('待审批列表加载失败，请重试')
    await user.click(screen.getByRole('button', { name: '重试' }))
    expect(await screen.findByRole('link', { name: '待审图纸' })).toBeVisible()
    unmount()

    const blocked = summary('阻断的传递单')
    blocked.lifecycleStatus = 'SEALED'
    blocked.approvalStatus = 'APPROVAL_BLOCKED'
    blocked.approvalBlockedReason = '原审批人已停用'
    const listTransfers = vi.fn()
      .mockRejectedValueOnce(serverDown())
      .mockResolvedValueOnce(page([blocked]))
    renderOem(<BlockedRecoveryPage />, { listTransfers }, ['oem:approval_recover'])
    expect(await screen.findByText('审批异常列表加载失败，请重试')).toBeVisible()
    await user.click(screen.getByRole('button', { name: '重试' }))
    expect(await screen.findByText('阻断的传递单')).toBeVisible()
  })

  it('账号抽屉失败可重试，再次失败时保留已有账号', async () => {
    const user = userEvent.setup()
    const accounts = vi.fn()
      .mockRejectedValueOnce(serverDown())
      .mockResolvedValueOnce([account()])
      .mockRejectedValueOnce(serverDown())
    renderOem(<CompaniesPage />, {
      companies: vi.fn().mockResolvedValue(page([company()])),
      accounts,
      setAccountStatus: vi.fn().mockResolvedValue(undefined),
    }, ['oem:company_manage', 'oem:account_manage'])

    await user.click(await screen.findByRole('button', { name: '账号' }))
    const drawer = document.querySelector('.oem-accounts-drawer') as HTMLElement
    expect(await within(drawer).findByRole('button', { name: '重试' })).toBeVisible()
    expect(within(drawer).getByRole('alert')).toHaveTextContent('账号列表加载失败，请重试')
    expect(within(drawer).queryByText('OEM_0031')).not.toBeInTheDocument()

    await user.click(within(drawer).getByRole('button', { name: '重试' }))
    expect(await within(drawer).findByText('OEM_0031')).toBeVisible()

    await user.click(within(drawer).getByRole('button', { name: '停用' }))
    expect(await within(drawer).findByRole('alert')).toHaveTextContent('账号列表加载失败，请重试')
    expect(within(drawer).getByText('OEM_0031')).toBeVisible()
  })
})
