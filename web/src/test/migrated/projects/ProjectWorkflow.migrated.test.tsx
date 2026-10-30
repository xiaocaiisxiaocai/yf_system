import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { deferred } from './testUtils'

const mocks = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  auth: {
    user: { id: 1, userType: 'INTERNAL' } as { id: number; userType: string },
    permissions: [] as string[],
  },
}))

vi.mock('../../../api/client', () => ({
  default: { get: mocks.get, post: mocks.post, put: mocks.put },
  getApiErrorCode: (error: { response?: { data?: { code?: number } } }) => error?.response?.data?.code,
}))

vi.mock('../../../store/auth', () => ({
  useAuth: () => ({
    user: mocks.auth.user,
    hasPerm: (permission: string) => mocks.auth.permissions.includes(permission),
  }),
}))

import ProjectWorkflowPanel from '../../../components/ProjectWorkflowPanel'

const pendingProject = (submission = 31) => ({
  id: 17,
  status: 'PENDING_CONFIRMATION',
  confirmSide: 'COMPANY',
  latestSubmitterId: 2,
  latestSubmissionId: submission,
}) as never

async function confirmPopover() {
  const popup = await screen.findByRole('tooltip')
  fireEvent.click(within(popup).getByRole('button', { name: '确定' }))
}

describe('ProjectWorkflowPanel migrated behavior', () => {
  beforeEach(() => {
    mocks.auth.user = { id: 1, userType: 'INTERNAL' }
    mocks.auth.permissions = []
    mocks.get.mockResolvedValue({ data: { total: 0, items: [] } })
    mocks.post.mockResolvedValue({ data: {} })
    mocks.put.mockResolvedValue({ data: {} })
  })

  afterEach(() => {
    document.querySelectorAll('.arco-trigger-popup').forEach((node) => node.remove())
  })

  it('project workflow keeps acceptance and management internal while supplier submitters may withdraw', async () => {
    mocks.auth.permissions = ['project:confirm', 'project:withdraw']
    const { rerender } = render(<ProjectWorkflowPanel project={pendingProject()} onChanged={() => undefined} />)
    expect(screen.getByRole('button', { name: '验收通过' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '验收驳回' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '撤回' })).not.toBeInTheDocument()

    mocks.auth.user = { id: 2, userType: 'SUPPLIER' }
    rerender(<ProjectWorkflowPanel project={pendingProject(32)} onChanged={() => undefined} />)
    expect(screen.queryByRole('button', { name: '验收通过' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '验收驳回' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: '撤回' })).toBeInTheDocument()

    mocks.auth.user = { id: 1, userType: 'INTERNAL' }
    mocks.auth.permissions = ['project:withdraw', 'project:view_all']
    rerender(<ProjectWorkflowPanel project={pendingProject(33)} onChanged={() => undefined} />)
    expect(screen.queryByRole('button', { name: '撤回' })).not.toBeInTheDocument()
  })

  it('project workflow status commands are limited to start, terminate and restart', async () => {
    mocks.auth.permissions = ['project:status']
    const changed = vi.fn()
    const user = userEvent.setup()
    const { rerender } = render(<ProjectWorkflowPanel project={{ id: 1, status: 'DRAFT' } as never} onChanged={changed} />)

    await user.click(screen.getByRole('button', { name: '开始' }))
    expect(mocks.put).toHaveBeenLastCalledWith('/projects/1/status', { status: 'IN_PROGRESS' })

    rerender(<ProjectWorkflowPanel project={{ id: 1, status: 'IN_PROGRESS' } as never} onChanged={changed} />)
    await user.click(screen.getByRole('button', { name: '终止' }))
    await confirmPopover()
    expect(mocks.put).toHaveBeenLastCalledWith('/projects/1/status', { status: 'TERMINATED' })

    rerender(<ProjectWorkflowPanel project={{ id: 1, status: 'TERMINATED' } as never} onChanged={changed} />)
    await user.click(screen.getByRole('button', { name: '重新开始' }))
    expect(mocks.put).toHaveBeenLastCalledWith('/projects/1/status', { status: 'IN_PROGRESS' })

    rerender(<ProjectWorkflowPanel project={{ id: 1, status: 'COMPLETED' } as never} onChanged={changed} />)
    expect(screen.queryByRole('button', { name: /开始|终止/ })).not.toBeInTheDocument()
  })

  it.each([
    ['DRAFT', '开始'],
    ['TERMINATED', '重新开始'],
  ])('keeps %s unchanged when the supplier has no active account and allows retry', async (status, label) => {
    mocks.auth.permissions = ['project:status']
    mocks.put.mockRejectedValueOnce({ response: { data: {
      code: 40901,
      message: '项目所属厂商尚未添加启用的用户，请先添加或启用厂商用户后再开始项目',
    } } })
    const changed = vi.fn()
    render(<ProjectWorkflowPanel project={{ id: 17, status } as never} onChanged={changed} />)

    await userEvent.click(screen.getByRole('button', { name: label }))
    await waitFor(() => expect(screen.getByRole('button', { name: label })).not.toHaveClass('arco-btn-loading'))
    expect(mocks.put).toHaveBeenCalledTimes(1)
    expect(changed).not.toHaveBeenCalled()

    await userEvent.click(screen.getByRole('button', { name: label }))
    await waitFor(() => expect(changed).toHaveBeenCalledTimes(1))
    expect(mocks.put).toHaveBeenCalledTimes(2)
  })

  it('only suppliers can submit a project for company acceptance', async () => {
    mocks.auth.permissions = ['project:submit']
    const approved = { id: 1, status: 'IN_PROGRESS', materialReviewStatus: 'APPROVED', latestSupplierFileId: 5 } as never
    const { rerender } = render(<ProjectWorkflowPanel project={approved} onChanged={() => undefined} />)
    expect(screen.queryByRole('button', { name: '提交公司验收' })).not.toBeInTheDocument()

    mocks.auth.user = { id: 2, userType: 'SUPPLIER' }
    rerender(<ProjectWorkflowPanel project={approved} onChanged={() => undefined} />)
    await userEvent.click(screen.getByRole('button', { name: '提交公司验收' }))
    await confirmPopover()
    expect(mocks.post).toHaveBeenLastCalledWith('/projects/1/submit', { confirmSide: 'COMPANY' })
  })

  it.each([
    ['NO_MATERIAL', '待回传资料'],
    ['PENDING', '资料待确认'],
    ['RETURNED', '资料已退回'],
  ])('supplier submission stays disabled while the material review is %s', (materialReviewStatus, tag) => {
    mocks.auth.user = { id: 2, userType: 'SUPPLIER' }
    mocks.auth.permissions = ['project:submit']
    render(<ProjectWorkflowPanel
      project={{ id: 1, status: 'IN_PROGRESS', materialReviewStatus, materialReviewReason: materialReviewStatus === 'RETURNED' ? '缺少动作流程表' : null } as never}
      onChanged={() => undefined}
    />)
    expect(screen.getByRole('button', { name: '提交公司验收' })).toBeDisabled()
    expect(screen.getByText(tag)).toBeInTheDocument()
    if (materialReviewStatus === 'RETURNED') expect(screen.getByText('退回原因：缺少动作流程表')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '资料确认 OK' })).not.toBeInTheDocument()
  })

  it('only the responsible user reviews the returned material, against the file version shown', async () => {
    const changed = vi.fn()
    const user = userEvent.setup()
    const project = (latestSupplierFileId: number, materialReviewStatus = 'PENDING', responsibleUserId = 1) => ({
      id: 17, status: 'IN_PROGRESS', responsibleUserId, materialReviewStatus, latestSupplierFileId,
    }) as never
    const { rerender } = render(<ProjectWorkflowPanel project={project(5, 'PENDING', 9)} onChanged={changed} />)
    expect(screen.queryByRole('button', { name: '资料确认 OK' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '退回资料' })).not.toBeInTheDocument()

    rerender(<ProjectWorkflowPanel project={project(5)} onChanged={changed} />)
    await user.click(screen.getByRole('button', { name: '资料确认 OK' }))
    rerender(<ProjectWorkflowPanel project={project(6)} onChanged={changed} />)
    await confirmPopover()
    expect(mocks.post).toHaveBeenLastCalledWith('/projects/17/materials/approve', { expectedSupplierFileId: 5 })

    await user.click(screen.getByRole('button', { name: '退回资料' }))
    const dialog = await screen.findByRole('dialog')
    await user.click(within(dialog).getByRole('button', { name: '确认退回' }))
    expect(await within(dialog).findByText('请填写退回原因')).toBeInTheDocument()
    await user.type(within(dialog).getByPlaceholderText('请说明需要厂商补充或修改的资料'), '缺少 STEP')
    await user.click(within(dialog).getByRole('button', { name: '确认退回' }))
    await waitFor(() => expect(mocks.post).toHaveBeenLastCalledWith('/projects/17/materials/return', {
      reason: '缺少 STEP',
      expectedSupplierFileId: 6,
    }))

    rerender(<ProjectWorkflowPanel project={project(6, 'APPROVED')} onChanged={changed} />)
    expect(screen.queryByRole('button', { name: '资料确认 OK' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: '退回资料' })).toBeInTheDocument()
    rerender(<ProjectWorkflowPanel project={project(6, 'RETURNED')} onChanged={changed} />)
    expect(screen.getByRole('button', { name: '资料确认 OK' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '退回资料' })).not.toBeInTheDocument()
  })

  it('project rejection discards a cancelled reason but retains it after a failed submit', async () => {
    mocks.auth.permissions = ['project:confirm']
    const failed = deferred<{ data: unknown }>()
    mocks.post.mockImplementation(() => failed.promise)
    const user = userEvent.setup()
    render(<ProjectWorkflowPanel project={pendingProject(41)} onChanged={() => undefined} />)

    await user.click(screen.getByRole('button', { name: '验收驳回' }))
    let dialog = await screen.findByRole('dialog')
    let reason = within(dialog).getByPlaceholderText('请填写驳回原因')
    await user.type(reason, 'cancelled draft')
    await user.click(within(dialog).getByRole('button', { name: '取消' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())

    await user.click(screen.getByRole('button', { name: '验收驳回' }))
    dialog = await screen.findByRole('dialog')
    reason = within(dialog).getByPlaceholderText('请填写驳回原因')
    expect(reason).toHaveValue('')
    await user.type(reason, 'keep this reason while retrying')
    fireEvent.click(within(dialog).getByRole('button', { name: '确认驳回' }))
    await waitFor(() => expect(mocks.post).toHaveBeenCalledWith('/projects/17/reject', {
      reason: 'keep this reason while retrying',
      expectedSubmissionId: 41,
    }))
    failed.reject(new Error('offline'))
    await waitFor(() => expect(within(screen.getByRole('dialog')).getByPlaceholderText('请填写驳回原因')).toHaveValue('keep this reason while retrying'))
  })

  it('workflow actions ignore duplicate invocations while validation or the request is pending', async () => {
    mocks.auth.permissions = ['project:status']
    const request = deferred<{ data: unknown }>()
    mocks.put.mockImplementation(() => request.promise)
    const changed = vi.fn()
    render(<ProjectWorkflowPanel project={{ id: 17, status: 'DRAFT' } as never} onChanged={changed} />)
    const start = screen.getByRole('button', { name: '开始' })
    fireEvent.click(start)
    fireEvent.click(start)
    await waitFor(() => expect(mocks.put).toHaveBeenCalledTimes(1))
    request.resolve({ data: {} })
    await waitFor(() => expect(changed).toHaveBeenCalledTimes(1))
  })

  it('project workflow actions keep the submission version shown when confirmation opened', async () => {
    mocks.auth.permissions = ['project:confirm']
    const user = userEvent.setup()
    const { rerender } = render(<ProjectWorkflowPanel project={pendingProject(61)} onChanged={() => undefined} />)

    await user.click(screen.getByRole('button', { name: '验收通过' }))
    rerender(<ProjectWorkflowPanel project={pendingProject(62)} onChanged={() => undefined} />)
    await confirmPopover()
    expect(mocks.post).toHaveBeenLastCalledWith('/projects/17/confirm', { expectedSubmissionId: 61 })

    await user.click(screen.getByRole('button', { name: '验收驳回' }))
    rerender(<ProjectWorkflowPanel project={pendingProject(63)} onChanged={() => undefined} />)
    const dialog = await screen.findByRole('dialog')
    await user.type(within(dialog).getByPlaceholderText('请填写驳回原因'), '需要修改')
    await user.click(within(dialog).getByRole('button', { name: '确认驳回' }))
    expect(mocks.post).toHaveBeenLastCalledWith('/projects/17/reject', {
      reason: '需要修改',
      expectedSubmissionId: 62,
    })
  })

  it('a business conflict keeps the rejection dialog and does not reload the project', async () => {
    mocks.auth.permissions = ['project:confirm']
    mocks.post.mockRejectedValue({ isAxiosError: true, response: { data: { code: 40901 } } })
    const changed = vi.fn()
    const user = userEvent.setup()
    render(<ProjectWorkflowPanel project={pendingProject(71)} onChanged={changed} />)

    await user.click(screen.getByRole('button', { name: '验收驳回' }))
    const dialog = await screen.findByRole('dialog')
    await user.type(within(dialog).getByPlaceholderText('请填写驳回原因'), '版本已变化')
    fireEvent.click(within(dialog).getByRole('button', { name: '确认驳回' }))

    await waitFor(() => expect(mocks.post).toHaveBeenCalledOnce())
    expect(changed).not.toHaveBeenCalled()
    expect(screen.getByRole('dialog')).toBeInTheDocument()
    expect(within(dialog).getByPlaceholderText('请填写驳回原因')).toHaveValue('版本已变化')
  })

  it('a submission version conflict reloads once, keeps the reason, and never retries the write', async () => {
    mocks.auth.permissions = ['project:confirm']
    mocks.post.mockRejectedValue({ isAxiosError: true, response: { data: { code: 40903 } } })
    mocks.get.mockImplementation((url: string) => url === '/projects/17'
      ? Promise.resolve({ data: { id: 17, status: 'PENDING_CONFIRMATION', latestSubmissionId: 72 } })
      : Promise.resolve({ data: { total: 0, items: [] } }))
    const changed = vi.fn()
    const user = userEvent.setup()
    render(<ProjectWorkflowPanel project={pendingProject(71)} onChanged={changed} />)

    await user.click(screen.getByRole('button', { name: '验收驳回' }))
    const dialog = await screen.findByRole('dialog')
    await user.type(within(dialog).getByPlaceholderText('请填写驳回原因'), '版本已变化')
    fireEvent.click(within(dialog).getByRole('button', { name: '确认驳回' }))

    await waitFor(() => expect(changed).toHaveBeenCalledOnce())
    expect(mocks.post).toHaveBeenCalledOnce()
    expect(screen.getByRole('dialog')).toBeInTheDocument()
    expect(within(dialog).getByPlaceholderText('请填写驳回原因')).toHaveValue('版本已变化')

    expect(await screen.findByText('验收申请已更新，请确认原因后再次提交')).toBeInTheDocument()
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: '确认驳回' }))
    await waitFor(() => expect(mocks.post).toHaveBeenCalledTimes(2))
    expect(mocks.post).toHaveBeenLastCalledWith('/projects/17/reject', {
      reason: '版本已变化',
      expectedSubmissionId: 72,
    })
  })

  it('a submission version conflict closes the reject dialog once the project is no longer pending', async () => {
    mocks.auth.permissions = ['project:confirm']
    mocks.post.mockRejectedValue({ isAxiosError: true, response: { data: { code: 40903 } } })
    mocks.get.mockImplementation((url: string) => url === '/projects/17'
      ? Promise.resolve({ data: { id: 17, status: 'COMPLETED', latestSubmissionId: 71 } })
      : Promise.resolve({ data: { total: 0, items: [] } }))
    const changed = vi.fn()
    const user = userEvent.setup()
    render(<ProjectWorkflowPanel project={pendingProject(71)} onChanged={changed} />)

    await user.click(screen.getByRole('button', { name: '验收驳回' }))
    const dialog = await screen.findByRole('dialog')
    await user.type(within(dialog).getByPlaceholderText('请填写驳回原因'), '版本已变化')
    fireEvent.click(within(dialog).getByRole('button', { name: '确认驳回' }))

    await waitFor(() => expect(changed).toHaveBeenCalledOnce())
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(mocks.post).toHaveBeenCalledOnce()
  })
})
