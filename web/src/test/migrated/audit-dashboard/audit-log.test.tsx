import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
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
  })

  it('a capped audit count is shown as a lower bound with a filter hint', async () => {
    mocks.get.mockResolvedValue({ data: page([loginRow()], 1, 10_001) })
    render(<AuditLog />)
    expect(await screen.findByText('10000+ 条记录，请缩小筛选范围查看完整结果')).toBeVisible()
    expect(screen.queryByText('共 10001 条记录')).not.toBeInTheDocument()
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

  it('audit logs are read-only: no selection, delete buttons or delete requests', async () => {
    mocks.permissions = ['log:view', 'log:delete']
    mocks.get.mockResolvedValue({ data: page([
      loginRow(),
      loginRow({ id: 102, action: 'AUDIT_LOG_DELETE' }),
      loginRow({ id: 103, action: 'AUDIT_LOG_RETENTION' }),
    ]) })
    await renderLoaded()
    await screen.findAllByText('登录成功')

    expect(screen.queryByRole('button', { name: '删除所选' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '删除' })).not.toBeInTheDocument()
    expect(screen.queryAllByRole('checkbox')).toHaveLength(0)
    expect(screen.getAllByRole('button', { name: '查看' })).toHaveLength(3)
    expect(mocks.post).not.toHaveBeenCalled()
    expect(mocks.delete).not.toHaveBeenCalled()
  })

  it('refresh after retention shrinks the last page corrects the controlled page', async () => {
    const row = loginRow({ id: 21 })
    let purged = false
    const pages: number[] = []
    mocks.get.mockImplementation(async (_url, { params }) => {
      pages.push(params.page)
      return { data: page(params.page === 2 && !purged ? [row] : [], params.page, purged ? 20 : 21) }
    })
    const user = userEvent.setup()
    await renderLoaded()

    await user.click(screen.getByText('2'))
    await screen.findByText('登录成功')
    purged = true
    await user.click(screen.getByRole('button', { name: '刷新' }))

    await waitFor(() => expect(pages.slice(-2)).toEqual([2, 1]))
    expect(screen.queryByText('2')).not.toBeInTheDocument()
  })
})
