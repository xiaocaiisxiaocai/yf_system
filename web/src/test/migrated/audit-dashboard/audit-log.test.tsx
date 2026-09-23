import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Message } from '@arco-design/web-react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { AuditLogRow } from '../../../pages/system/auditLogDetails'

const mocks = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  delete: vi.fn(),
  permissions: ['log:view'] as string[],
}))

vi.mock('../../../api/client', () => ({
  default: { get: mocks.get, post: mocks.post, delete: mocks.delete },
}))

vi.mock('../../../store/auth', () => ({
  useAuth: (selector: (state: { hasPerm: (permission: string) => boolean }) => unknown) => selector({
    hasPerm: (permission: string) => mocks.permissions.includes(permission),
  }),
}))

import AuditLog from '../../../pages/system/AuditLog'

const page = (list: AuditLogRow[], current = 1, total = list.length) => ({
  list,
  total,
  page: current,
  pageSize: 20,
})

const loginRow = (overrides: Partial<AuditLogRow> = {}): AuditLogRow => ({
  id: 101,
  action: 'LOGIN',
  createdAt: '2026-09-09T00:00:00Z',
  canDelete: true,
  ...overrides,
})

async function renderLoaded() {
  const result = render(<AuditLog />)
  await screen.findByText(/共 \d+ 条记录/)
  await waitFor(() => expect(mocks.get).toHaveBeenCalled())
  return result
}

describe('操作日志真实界面', () => {
  beforeEach(() => {
    mocks.permissions = ['log:view']
    mocks.get.mockResolvedValue({ data: page([]) })
    mocks.post.mockResolvedValue({ data: { deleted: 1 } })
    mocks.delete.mockResolvedValue({ data: { deleted: 1 } })
  })

  it('audit time filtering preserves an explicitly selected midnight endpoint', async () => {
    let query: Record<string, unknown> = {}
    mocks.get.mockImplementation(async (_url, config) => {
      query = config.params
      return { data: page([]) }
    })
    const user = userEvent.setup()
    const { container } = await renderLoaded()
    const inputs = Array.from(container.querySelectorAll<HTMLInputElement>('.arco-picker input'))
    expect(inputs).toHaveLength(2)

    await user.click(inputs[0])
    await user.clear(inputs[0])
    await user.type(inputs[0], '2026-09-04 12:00:00{Enter}')
    await user.click(inputs[1])
    await user.clear(inputs[1])
    await user.type(inputs[1], '2026-09-05 00:00:00')
    const confirmRange = document.querySelector<HTMLButtonElement>('.arco-picker-btn-confirm')
    expect(confirmRange).not.toBeNull()
    fireEvent.click(confirmRange as HTMLButtonElement)
    await user.click(screen.getByRole('button', { name: '查询' }))

    await waitFor(() => expect(mocks.get).toHaveBeenCalledTimes(2))
    expect(query.end).toBe(new Date('2026-09-05T00:00:00').toISOString())
  })

  it('project workflow audit actions expose precise labels and state or rejection summaries', async () => {
    const row: AuditLogRow = {
      id: 201,
      action: 'PROJECT_REJECT',
      targetType: 'project',
      targetId: '7',
      detail: { from: 'PENDING_CONFIRMATION', to: 'IN_PROGRESS', reason: '需要补充资料' },
      createdAt: '2026-09-09T00:00:00Z',
    }
    mocks.get.mockResolvedValue({ data: page([row]) })
    const user = userEvent.setup()
    await renderLoaded()

    expect(screen.getByText('验收驳回')).toBeVisible()
    expect(screen.getByText(/项目状态：待验收 → 进行中；驳回原因：需要补充资料/)).toBeVisible()
    await user.click(screen.getByRole('button', { name: '查看' }))
    expect(await screen.findByRole('heading', { name: '变化明细' })).toBeVisible()
    expect(screen.getByText('驳回原因')).toBeVisible()
    expect(screen.getByText('需要补充资料')).toBeVisible()
  })

  it('profile update audit exposes the auth filter label and changed field summary', async () => {
    const row: AuditLogRow = {
      id: 202,
      userId: 1,
      employeeNo: 'admin',
      action: 'PROFILE_UPDATE',
      targetType: 'user',
      targetId: '1',
      detail: { changedFields: ['email'] },
      createdAt: '2026-09-11T00:00:00Z',
    }
    mocks.get.mockResolvedValue({ data: page([row]) })
    await renderLoaded()
    expect(screen.getByText('更新个人资料')).toBeVisible()
    expect(screen.getByText('修改了邮箱')).toBeVisible()
  })

  it('audit hard-delete controls require the dedicated delete permission', async () => {
    const protectedRows = [
      loginRow({ id: 102, canDelete: false }),
      loginRow({ id: 103, action: 'AUDIT_LOG_DELETE', canDelete: true }),
      loginRow({ id: 104, action: 'AUDIT_LOG_RETENTION', canDelete: true }),
    ]
    mocks.get.mockResolvedValue({ data: page([loginRow(), ...protectedRows]) })

    const withoutDelete = render(<AuditLog />)
    await screen.findAllByText('登录成功')
    expect(screen.queryByRole('button', { name: '删除所选' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '删除' })).not.toBeInTheDocument()
    withoutDelete.unmount()

    mocks.permissions = ['log:view', 'log:delete']
    const withDelete = render(<AuditLog />)
    await screen.findAllByText('登录成功')
    expect(screen.getByRole('button', { name: '删除所选' })).toBeDisabled()
    expect(screen.getAllByRole('button', { name: '删除' })).toHaveLength(1)
    const checkboxes = screen.getAllByRole('checkbox')
    expect(checkboxes).toHaveLength(5)
    expect(checkboxes[1]).toBeEnabled()
    for (const checkbox of checkboxes.slice(2)) expect(checkbox).toBeDisabled()
    withDelete.unmount()
  })

  it('audit selection is cleared across filtering, paging and refresh, and reports the returned delete count', async () => {
    mocks.permissions = ['log:view', 'log:delete']
    const initial = loginRow({ id: 101 })
    const filtered = loginRow({ id: 202, action: 'LOGIN_FAILED' })
    const page2 = [loginRow({ id: 301 }), loginRow({ id: 302, action: 'LOGIN_FAILED' }), loginRow({ id: 303, action: 'AUDIT_LOG_DELETE', canDelete: false })]
    let holdNext = false
    let releaseFetch: (() => void) | undefined
    mocks.get.mockImplementation(async (_url, { params }) => {
      const list = params.page === 2 ? page2 : params.keyword === 'next' ? [filtered] : [initial]
      const response = { data: page(list, params.page, params.page === 2 || params.keyword === 'next' ? 23 : list.length) }
      if (!holdNext) return response
      holdNext = false
      await new Promise<void>((resolve) => { releaseFetch = resolve })
      return response
    })
    mocks.post.mockResolvedValue({ data: { deleted: 1 } })
    const success = vi.spyOn(Message, 'success')
    const user = userEvent.setup()
    await renderLoaded()

    await user.click(screen.getAllByRole('checkbox')[1])
    expect(screen.getByRole('button', { name: '删除所选' })).toBeEnabled()
    await user.click(screen.getByRole('button', { name: '删除所选' }))
    const staleConfirm = await screen.findByRole('button', { name: '确定' })

    await user.clear(screen.getByPlaceholderText('搜索姓名、对象或详情'))
    await user.type(screen.getByPlaceholderText('搜索姓名、对象或详情'), 'next')
    await user.click(screen.getByRole('button', { name: '查询' }))
    await screen.findByText('登录失败')
    expect(screen.getAllByRole('checkbox')[1]).not.toBeChecked()
    fireEvent.click(staleConfirm)
    expect(mocks.post).not.toHaveBeenCalled()

    await user.click(screen.getByLabelText('第 2 页'))
    await screen.findByText('删除操作日志')
    expect(screen.getAllByRole('checkbox').slice(1).every((node) => !node.hasAttribute('checked'))).toBe(true)

    await user.click(screen.getAllByRole('checkbox')[1])
    await user.click(screen.getAllByRole('checkbox')[2])
    expect(screen.getAllByRole('checkbox')[3]).toBeDisabled()
    holdNext = true
    await user.click(screen.getByRole('button', { name: '刷新' }))
    expect(screen.getByRole('button', { name: '删除所选' })).toBeDisabled()
    releaseFetch?.()
    await waitFor(() => expect(mocks.get).toHaveBeenCalled())

    await user.click(screen.getAllByRole('checkbox')[1])
    await user.click(screen.getAllByRole('checkbox')[2])
    await user.click(screen.getByRole('button', { name: '删除所选' }))
    fireEvent.click(await screen.findByRole('button', { name: '确定' }))
    await waitFor(() => expect(mocks.post).toHaveBeenCalledWith('/admin/audit-logs/batch-delete', { ids: [301, 302] }))
    expect(success).toHaveBeenCalledWith('已删除 1 条')
  })

  it('audit deletion from the only row on the last page corrects the controlled page', async () => {
    mocks.permissions = ['log:view', 'log:delete']
    const row = loginRow({ id: 21 })
    let deleted = false
    const pages: number[] = []
    mocks.get.mockImplementation(async (_url, { params }) => {
      pages.push(params.page)
      return { data: page(params.page === 2 && !deleted ? [row] : [], params.page, deleted ? 20 : 21) }
    })
    mocks.delete.mockImplementation(async () => {
      deleted = true
      return { data: { deleted: 1 } }
    })
    const user = userEvent.setup()
    await renderLoaded()

    await user.click(screen.getByText('2'))
    await screen.findByText('登录成功')
    await user.click(screen.getByRole('button', { name: '删除' }))
    fireEvent.click(await screen.findByRole('button', { name: '确定' }))

    await waitFor(() => expect(pages.slice(-2)).toEqual([2, 1]))
    expect(screen.queryByText('2')).not.toBeInTheDocument()
  })
})
