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

const reviewProject = (latestSupplierFileId = 5, materialReviewStatus = 'PENDING', responsibleUserId = 1) => ({
  id: 17, status: 'IN_PROGRESS', responsibleUserId, materialReviewStatus, latestSupplierFileId,
}) as never

const completedProject = (updatedAt = '2026-10-31T08:00:00.000Z', responsibleUserId = 1) => ({
  id: 17, status: 'COMPLETED', responsibleUserId, updatedAt, materialReviewStatus: null,
}) as never

async function confirmPopover() {
  const popup = await screen.findByRole('tooltip')
  fireEvent.click(within(popup).getByRole('button', { name: '确定' }))
}

describe('ProjectWorkflowPanel migrated behavior', () => {
  beforeEach(() => {
    mocks.auth.user = { id: 1, userType: 'INTERNAL' }
    mocks.auth.permissions = []
    mocks.get.mockResolvedValue({ data: { total: 2, list: [{ originalName: 'cycle-motion.mp4' }] } })
    mocks.post.mockResolvedValue({ data: {} })
    mocks.put.mockResolvedValue({ data: {} })
  })

  afterEach(() => {
    document.querySelectorAll('.arco-trigger-popup').forEach((node) => node.remove())
  })

  it('suppliers only see the material state; there is no submit, withdraw or acceptance command', () => {
    mocks.auth.user = { id: 2, userType: 'SUPPLIER' }
    mocks.auth.permissions = ['file:upload', 'project:list']
    const { rerender } = render(<ProjectWorkflowPanel project={reviewProject(5, 'PENDING', 1)} onChanged={() => undefined} />)
    expect(screen.getByText('资料待确认')).toBeInTheDocument()
    expect(screen.queryByRole('button')).not.toBeInTheDocument()

    rerender(<ProjectWorkflowPanel project={completedProject()} onChanged={() => undefined} />)
    expect(screen.queryByRole('button', { name: '重开' })).not.toBeInTheDocument()
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

  it.each([
    ['NO_MATERIAL', '待回传资料'],
    ['PENDING', '资料待确认'],
    ['RETURNED', '资料已退回'],
  ])('the %s material state shows its tag and return reason to the supplier', (materialReviewStatus, tag) => {
    mocks.auth.user = { id: 2, userType: 'SUPPLIER' }
    render(<ProjectWorkflowPanel
      project={{ id: 1, status: 'IN_PROGRESS', materialReviewStatus, materialReviewReason: materialReviewStatus === 'RETURNED' ? '缺少动作流程表' : null } as never}
      onChanged={() => undefined}
    />)
    expect(screen.getByText(tag)).toBeInTheDocument()
    if (materialReviewStatus === 'RETURNED') expect(screen.getByText('退回原因：缺少动作流程表')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '资料确认 OK' })).not.toBeInTheDocument()
  })

  it('only the responsible user approves or returns the material, against the file version shown', async () => {
    const changed = vi.fn()
    const user = userEvent.setup()
    const { rerender } = render(<ProjectWorkflowPanel project={reviewProject(5, 'PENDING', 9)} onChanged={changed} />)
    expect(screen.queryByRole('button', { name: '资料确认 OK' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '退回资料' })).not.toBeInTheDocument()

    rerender(<ProjectWorkflowPanel project={reviewProject(5)} onChanged={changed} />)
    await user.click(screen.getByRole('button', { name: '资料确认 OK' }))
    let dialog = await screen.findByRole('dialog')
    expect(await within(dialog).findByText('厂商回传文件 2 个，最新：cycle-motion.mp4')).toBeInTheDocument()
    expect(mocks.get).toHaveBeenLastCalledWith('/projects/17/files', expect.objectContaining({
      params: { page: 1, pageSize: 1, direction: 'S2C' },
    }))
    // A newer supplier file arriving while the dialog is open does not change the version being approved.
    rerender(<ProjectWorkflowPanel project={reviewProject(6)} onChanged={changed} />)
    await user.click(within(dialog).getByRole('button', { name: '确认并完成' }))
    await waitFor(() => expect(mocks.post).toHaveBeenLastCalledWith('/projects/17/materials/approve', { expectedSupplierFileId: 5 }))
    await waitFor(() => expect(changed).toHaveBeenCalledOnce())

    await user.click(screen.getByRole('button', { name: '退回资料' }))
    dialog = await screen.findByRole('dialog')
    await user.click(within(dialog).getByRole('button', { name: '确认退回' }))
    expect(await within(dialog).findByText('请填写退回原因')).toBeInTheDocument()
    await user.type(within(dialog).getByPlaceholderText('请说明需要厂商补充或修改的资料'), '缺少 STEP')
    await user.click(within(dialog).getByRole('button', { name: '确认退回' }))
    await waitFor(() => expect(mocks.post).toHaveBeenLastCalledWith('/projects/17/materials/return', {
      reason: '缺少 STEP',
      expectedSupplierFileId: 6,
    }))

    rerender(<ProjectWorkflowPanel project={reviewProject(6, 'RETURNED')} onChanged={changed} />)
    expect(screen.getByRole('button', { name: '资料确认 OK' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '退回资料' })).not.toBeInTheDocument()
    rerender(<ProjectWorkflowPanel project={reviewProject(6, 'NO_MATERIAL')} onChanged={changed} />)
    expect(screen.queryByRole('button', { name: /资料确认 OK|退回资料/ })).not.toBeInTheDocument()
  })

  it('a stale material version closes the approval dialog and reloads once without retrying', async () => {
    mocks.post.mockRejectedValue({ isAxiosError: true, response: { data: { code: 40903 } } })
    const changed = vi.fn()
    const user = userEvent.setup()
    render(<ProjectWorkflowPanel project={reviewProject(5)} onChanged={changed} />)

    await user.click(screen.getByRole('button', { name: '资料确认 OK' }))
    const dialog = await screen.findByRole('dialog')
    await within(dialog).findByText(/厂商回传文件/)
    await user.click(within(dialog).getByRole('button', { name: '确认并完成' }))
    await waitFor(() => expect(changed).toHaveBeenCalledOnce())
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(mocks.post).toHaveBeenCalledOnce()
  })

  it('only the responsible user reopens a completed subproject, with a reason and the version shown', async () => {
    const changed = vi.fn()
    const user = userEvent.setup()
    const { rerender } = render(<ProjectWorkflowPanel project={completedProject(undefined, 9)} onChanged={changed} />)
    expect(screen.queryByRole('button', { name: '重开' })).not.toBeInTheDocument()

    rerender(<ProjectWorkflowPanel project={completedProject('2026-10-31T08:00:00.000Z')} onChanged={changed} />)
    await user.click(screen.getByRole('button', { name: '重开' }))
    rerender(<ProjectWorkflowPanel project={completedProject('2026-10-31T09:00:00.000Z')} onChanged={changed} />)
    const dialog = await screen.findByRole('dialog')
    await user.click(within(dialog).getByRole('button', { name: '确认重开' }))
    expect(await within(dialog).findByText('请填写重开原因')).toBeInTheDocument()
    await user.type(within(dialog).getByPlaceholderText('请说明需要厂商返工的内容'), '动作节拍不对')
    await user.click(within(dialog).getByRole('button', { name: '确认重开' }))
    await waitFor(() => expect(mocks.post).toHaveBeenLastCalledWith('/projects/17/reopen', {
      reason: '动作节拍不对',
      expectedUpdatedAt: '2026-10-31T08:00:00.000Z',
    }))
    await waitFor(() => expect(changed).toHaveBeenCalledOnce())
  })

  it('reopening discards a cancelled reason but keeps it after a failed request', async () => {
    const failed = deferred<{ data: unknown }>()
    mocks.post.mockImplementation(() => failed.promise)
    const user = userEvent.setup()
    render(<ProjectWorkflowPanel project={completedProject()} onChanged={() => undefined} />)

    await user.click(screen.getByRole('button', { name: '重开' }))
    let dialog = await screen.findByRole('dialog')
    let reason = within(dialog).getByPlaceholderText('请说明需要厂商返工的内容')
    await user.type(reason, 'cancelled draft')
    await user.click(within(dialog).getByRole('button', { name: '取消' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())

    await user.click(screen.getByRole('button', { name: '重开' }))
    dialog = await screen.findByRole('dialog')
    reason = within(dialog).getByPlaceholderText('请说明需要厂商返工的内容')
    expect(reason).toHaveValue('')
    await user.type(reason, 'keep this reason while retrying')
    fireEvent.click(within(dialog).getByRole('button', { name: '确认重开' }))
    await waitFor(() => expect(mocks.post).toHaveBeenCalledWith('/projects/17/reopen', {
      reason: 'keep this reason while retrying',
      expectedUpdatedAt: '2026-10-31T08:00:00.000Z',
    }))
    failed.reject(new Error('offline'))
    await waitFor(() => expect(within(screen.getByRole('dialog')).getByPlaceholderText('请说明需要厂商返工的内容')).toHaveValue('keep this reason while retrying'))
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

  it('a business conflict keeps the reopen dialog and does not reload the project', async () => {
    mocks.post.mockRejectedValue({ isAxiosError: true, response: { data: { code: 40901 } } })
    const changed = vi.fn()
    const user = userEvent.setup()
    render(<ProjectWorkflowPanel project={completedProject()} onChanged={changed} />)

    await user.click(screen.getByRole('button', { name: '重开' }))
    const dialog = await screen.findByRole('dialog')
    await user.type(within(dialog).getByPlaceholderText('请说明需要厂商返工的内容'), '需要返工')
    fireEvent.click(within(dialog).getByRole('button', { name: '确认重开' }))

    await waitFor(() => expect(mocks.post).toHaveBeenCalledOnce())
    expect(changed).not.toHaveBeenCalled()
    expect(screen.getByRole('dialog')).toBeInTheDocument()
    expect(within(dialog).getByPlaceholderText('请说明需要厂商返工的内容')).toHaveValue('需要返工')
  })

  it('a version conflict closes the reopen dialog and reloads once without retrying', async () => {
    mocks.post.mockRejectedValue({ isAxiosError: true, response: { data: { code: 40903 } } })
    const changed = vi.fn()
    const user = userEvent.setup()
    render(<ProjectWorkflowPanel project={completedProject()} onChanged={changed} />)

    await user.click(screen.getByRole('button', { name: '重开' }))
    const dialog = await screen.findByRole('dialog')
    await user.type(within(dialog).getByPlaceholderText('请说明需要厂商返工的内容'), '需要返工')
    fireEvent.click(within(dialog).getByRole('button', { name: '确认重开' }))

    await waitFor(() => expect(changed).toHaveBeenCalledOnce())
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(mocks.post).toHaveBeenCalledOnce()
  })
})
