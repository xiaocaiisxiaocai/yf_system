import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Message } from '@arco-design/web-react'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  delete: vi.fn(),
  perms: ['project:create'] as string[],
  collaboration: {
    revision: 'revision-1',
    status: 'ready',
    realtimeStatus: 'connected',
    activityRevisions: {} as Record<number, number>,
    reconnectRevision: 0,
  },
}))

vi.mock('../api/client', () => ({
  default: { get: mocks.get, post: mocks.post, put: mocks.put, delete: mocks.delete },
  getApiErrorCode: (error: unknown) => (error as { response?: { data?: { code?: number } } } | undefined)?.response?.data?.code,
}))
vi.mock('dockview-react', () => import('./dockviewMock'))
vi.mock('../components/FileTable', () => ({ default: () => <div>文件列表</div> }))
vi.mock('../components/MessagePanel', () => ({ default: () => <div>留言列表</div> }))
vi.mock('../components/ProjectActivityPanel', () => ({ default: () => <div>动态列表</div> }))

vi.mock('../store/auth', () => ({
  useAuth: () => ({
    user: { id: 1, userType: 'INTERNAL' },
    hasPerm: (permission: string) => mocks.perms.includes(permission),
  }),
}))

vi.mock('../store/collaboration', () => ({
  useCollaboration: (selector: (state: typeof mocks.collaboration) => unknown) => selector(mocks.collaboration),
}))

import ProjectGroupDetail from '../pages/project/ProjectGroupDetail'

const detail = {
  group: {
    id: 3,
    name: '主项目 A',
    description: null,
    supplierName: 'Robot 厂商 A',
    status: 'IN_PROGRESS',
    workOrderNos: [],
    machineModel: null,
    robotPartNumber: null,
    robotModelName: null,
    responsibleUserEmployeeNo: null,
    responsibleUserName: null,
    sectionName: null,
    priorityName: null,
    expectedCompletionDate: null,
    completedAt: null,
    subprojectCount: 1,
    completedCount: 0,
    pendingCount: 0,
    terminatedCount: 0,
  },
  projects: [{
    id: 9,
    projectGroupId: 3,
    projectGroupName: '主项目 A',
    name: '装配线升级',
    description: null,
    status: 'IN_PROGRESS',
    unreadMessages: 0,
  }],
}

const subproject = { ...detail.projects[0], supplierName: 'Robot 厂商 A', workOrderNos: [] }

function subprojectGet(url: string) {
  if (url === '/projects/9') return Promise.resolve({ data: subproject })
  if (url === '/projects/9/summary') return Promise.resolve({ data: { unreadMessages: 0 } })
  return null
}

function copyJob(overrides: Record<string, unknown> = {}) {
  return {
    jobId: 71,
    sourceProjectId: 9,
    projectGroupId: 3,
    targetName: '装配线升级 - 副本',
    status: 'pending',
    filesTotal: 4,
    filesCopied: 0,
    bytesTotal: 4096,
    bytesCopied: 0,
    error: null,
    result: null,
    createdAt: '2026-09-23T01:00:00Z',
    startedAt: null,
    completedAt: null,
    ...overrides,
  }
}

function renderPage() {
  return render(
    <MemoryRouter initialEntries={['/project-groups/3']}>
      <Routes>
        <Route path="/project-groups/:id" element={<ProjectGroupDetail />} />
      </Routes>
    </MemoryRouter>,
  )
}

function resetSharedMocks() {
  mocks.perms = ['project:create']
  mocks.collaboration = { revision: 'revision-1', status: 'ready', realtimeStatus: 'connected', activityRevisions: {}, reconnectRevision: 0 }
}

describe('子项目后台复制任务', () => {
  beforeEach(() => {
    resetSharedMocks()
    mocks.get.mockReset()
    mocks.post.mockReset()
    mocks.put.mockReset()
    mocks.delete.mockReset()
    mocks.get.mockImplementation((url: string) => {
      if (url === '/project-groups/3') return Promise.resolve({ data: detail })
      if (url === '/project-groups/3/copy-jobs') return Promise.resolve({ data: { jobs: [] } })
      const pane = subprojectGet(url)
      if (pane) return pane
      throw new Error(`unexpected GET ${url}`)
    })
  })

  it('accepts a short idempotent task, closes the modal, and exposes progress plus the result entry', async () => {
    const pending = copyJob()
    const complete = copyJob({
      status: 'succeeded',
      filesCopied: 4,
      bytesCopied: 4096,
      result: { projectId: 88, copyFileCount: 4 },
      completedAt: '2026-09-23T01:01:00Z',
    })
    mocks.post.mockResolvedValueOnce({ data: pending })
    let jobReads = 0
    mocks.get.mockImplementation((url: string) => {
      if (url === '/project-groups/3') return Promise.resolve({ data: detail })
      if (url === '/project-groups/3/copy-jobs') {
        jobReads += 1
        return Promise.resolve({ data: { jobs: [jobReads > 1 ? complete : pending] } })
      }
      const pane = subprojectGet(url)
      if (pane) return pane
      throw new Error(`unexpected GET ${url}`)
    })
    const user = userEvent.setup()
    renderPage()

    const copyButton = await screen.findByRole('button', { name: '复制' })
    await waitFor(() => expect(copyButton).toBeEnabled())
    await user.click(copyButton)
    await user.click(await screen.findByRole('button', { name: '创建复制任务' }))

    await waitFor(() => expect(mocks.post).toHaveBeenCalledTimes(1))
    const [path, body] = mocks.post.mock.calls[0]
    expect(path).toBe('/projects/9/copy')
    expect(body.name).toBe('装配线升级 - 副本')
    expect(body.idempotencyKey).toMatch(/^[0-9a-z-]{1,64}$/i)
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(await screen.findByText('装配线升级 - 副本')).toBeInTheDocument()
    expect(screen.getByText('排队中')).toBeInTheDocument()

    await waitFor(() => expect(mocks.get.mock.calls.filter(([url]) => url === '/project-groups/3/copy-jobs').length).toBeGreaterThan(1))
    await waitFor(() => {
      expect(screen.getByText('已完成')).toBeInTheDocument()
      expect(screen.getByRole('button', { name: '进入副本' })).toBeInTheDocument()
    })
  })

  it('reports a newly failed copy job once, but not failures that existed before the page opened', async () => {
    const error = vi.spyOn(Message, 'error').mockImplementation(() => () => {})
    const oldFailure = copyJob({ jobId: 70, targetName: '旧副本', status: 'failed', error: '旧错误', completedAt: '2026-09-22T01:00:00Z' })
    const running = copyJob({ jobId: 72, status: 'running' })
    const failed = copyJob({ jobId: 72, status: 'failed', error: '源文件内容校验失败', completedAt: '2026-09-23T01:01:00Z' })
    let jobReads = 0
    mocks.get.mockImplementation((url: string) => {
      if (url === '/project-groups/3') return Promise.resolve({ data: detail })
      if (url === '/project-groups/3/copy-jobs') {
        jobReads += 1
        return Promise.resolve({ data: { jobs: [jobReads > 1 ? failed : running, oldFailure] } })
      }
      const pane = subprojectGet(url)
      if (pane) return pane
      throw new Error(`unexpected GET ${url}`)
    })
    renderPage()

    await waitFor(() => expect(error).toHaveBeenCalledWith('子项目“装配线升级 - 副本”复制失败：源文件内容校验失败'))
    expect(error).toHaveBeenCalledTimes(1)
    expect(screen.queryByRole('button', { name: '复制任务' })).not.toBeInTheDocument()
    error.mockRestore()
  })

  it('subproject copy requires a renamed project, blocks duplicate submits and keeps the dialog after failure', async () => {
    let rejectFirst: (reason?: unknown) => void = () => undefined
    mocks.post
      .mockImplementationOnce(() => new Promise((_resolve, reject) => { rejectFirst = reject }))
      .mockResolvedValueOnce({ data: copyJob() })
    let jobReads = 0
    mocks.get.mockImplementation((url: string) => {
      if (url === '/project-groups/3') return Promise.resolve({ data: detail })
      if (url === '/project-groups/3/copy-jobs') {
        jobReads += 1
        return jobReads === 1
          ? Promise.resolve({ data: { jobs: [] } })
          : Promise.reject(new Error('copy job list temporarily unavailable'))
      }
      const pane = subprojectGet(url)
      if (pane) return pane
      throw new Error(`unexpected GET ${url}`)
    })
    const user = userEvent.setup()
    renderPage()

    const copyButton = await screen.findByRole('button', { name: '复制' })
    await waitFor(() => expect(copyButton).toBeEnabled())
    await user.click(copyButton)
    const submit = await screen.findByRole('button', { name: '创建复制任务' })
    await user.click(submit)
    await user.click(submit)
    await waitFor(() => expect(mocks.post).toHaveBeenCalledTimes(1))
    const firstBody = mocks.post.mock.calls[0][1]
    expect(firstBody.name).toBe('装配线升级 - 副本')
    expect(firstBody.name).not.toBe('装配线升级')
    rejectFirst(Object.assign(new Error('server timeout'), {
      isAxiosError: true,
      code: 'ERR_BAD_RESPONSE',
      response: { status: 500, data: { message: 'temporary failure' } },
    }))

    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByRole('alert')).toHaveTextContent('重试不会重复创建副本')
    expect(screen.getByRole('button', { name: '确认并重试' })).toBeEnabled()
    await user.click(screen.getByRole('button', { name: '暂时关闭' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    await user.click(copyButton)
    const retry = await screen.findByRole('button', { name: '确认并重试' })
    expect(retry).toBeEnabled()
    await user.click(retry)
    await waitFor(() => expect(mocks.post).toHaveBeenCalledTimes(2))
    expect(mocks.post.mock.calls[1][1]).toEqual(firstBody)
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  })

  it('fails closed while the copy-job contract is loading or unavailable', async () => {
    let rejectJobs: (reason?: unknown) => void = () => undefined
    mocks.get.mockImplementation((url: string) => {
      if (url === '/project-groups/3') return Promise.resolve({ data: detail })
      if (url === '/project-groups/3/copy-jobs') return new Promise((_resolve, reject) => { rejectJobs = reject })
      const pane = subprojectGet(url)
      if (pane) return pane
      throw new Error(`unexpected GET ${url}`)
    })
    const user = userEvent.setup()
    renderPage()

    const copyButton = await screen.findByRole('button', { name: '复制' })
    expect(copyButton).toBeDisabled()
    expect(mocks.post).not.toHaveBeenCalled()
    rejectJobs({ response: { status: 404 } })
    await waitFor(() => expect(copyButton).toBeDisabled())
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(mocks.post).not.toHaveBeenCalled()
    await user.click(copyButton)
    expect(mocks.post).not.toHaveBeenCalled()
  })
})

describe('主项目刷新范围', () => {
  const draftDetail = { ...detail, projects: [{ ...detail.projects[0], status: 'DRAFT' }] }
  const calls = (target: string) => mocks.get.mock.calls.filter(([url]) => url === target).length

  beforeEach(() => {
    resetSharedMocks()
    mocks.get.mockReset()
    mocks.delete.mockReset()
    mocks.get.mockImplementation((url: string) => {
      if (url === '/project-groups/3') return Promise.resolve({ data: draftDetail })
      if (url === '/project-groups/3/copy-jobs') return Promise.resolve({ data: { jobs: [copyJob({ status: 'succeeded', result: { projectId: 88, copyFileCount: 4 } })] } })
      const pane = subprojectGet(url)
      if (pane) return pane
      throw new Error(`unexpected GET ${url}`)
    })
  })

  it('deleting a subproject refreshes the group without resetting the copy-job list', async () => {
    mocks.perms = ['project:create', 'project:delete']
    mocks.delete.mockResolvedValue({ data: {} })
    const user = userEvent.setup()
    renderPage()
    const copyButton = await screen.findByRole('button', { name: '复制' })
    await waitFor(() => expect(copyButton).toBeEnabled())
    const groupReads = calls('/project-groups/3')
    const jobReads = calls('/project-groups/3/copy-jobs')

    await user.click(screen.getByRole('button', { name: '删除' }))
    fireEvent.click(await screen.findByRole('button', { name: '确定' }))
    await waitFor(() => expect(mocks.delete).toHaveBeenCalledWith('/projects/9'))
    await waitFor(() => expect(calls('/project-groups/3')).toBe(groupReads + 1))
    expect(calls('/project-groups/3/copy-jobs')).toBe(jobReads)
    // 复制任务快照未被重置，复制入口保持可用。
    expect(screen.getByRole('button', { name: '复制' })).toBeEnabled()
  })

  it('reacts only to realtime signals for projects of this group', async () => {
    const view = renderPage()
    await screen.findByRole('heading', { name: '主项目 A' })
    await waitFor(() => expect(calls('/project-groups/3')).toBe(1))
    const rerender = () => view.rerender(
      <MemoryRouter initialEntries={['/project-groups/3']}><Routes><Route path="/project-groups/:id" element={<ProjectGroupDetail />} /></Routes></MemoryRouter>,
    )

    // 已知的其他项目变更和连接状态下的全局指纹变化都不应重拉本主项目。
    mocks.collaboration = { ...mocks.collaboration, activityRevisions: { 50: 1 } }
    rerender()
    await waitFor(() => expect(calls('/project-groups/3')).toBe(2))
    mocks.collaboration = { ...mocks.collaboration, activityRevisions: { 50: 2 }, revision: 'revision-2', status: 'loading' }
    rerender()
    await new Promise((resolve) => setTimeout(resolve, 0))
    expect(calls('/project-groups/3')).toBe(2)

    mocks.collaboration = { ...mocks.collaboration, activityRevisions: { 50: 2, 9: 1 }, status: 'ready' }
    rerender()
    await waitFor(() => expect(calls('/project-groups/3')).toBe(3))

    // 实时断开时退回全局轮询指纹。
    mocks.collaboration = { ...mocks.collaboration, realtimeStatus: 'disconnected' }
    rerender()
    await waitFor(() => expect(calls('/project-groups/3')).toBe(4))
    mocks.collaboration = { ...mocks.collaboration, revision: 'revision-3' }
    rerender()
    await waitFor(() => expect(calls('/project-groups/3')).toBe(5))
  })

  it('coalesces a burst of realtime signals without aborting the in-flight request', async () => {
    const view = renderPage()
    await screen.findByRole('heading', { name: '主项目 A' })
    await waitFor(() => expect(calls('/project-groups/3')).toBe(1))
    const rerender = () => view.rerender(
      <MemoryRouter initialEntries={['/project-groups/3']}><Routes><Route path="/project-groups/:id" element={<ProjectGroupDetail />} /></Routes></MemoryRouter>,
    )
    const base = mocks.get.getMockImplementation()!
    const pending: Array<{ resolve: () => void; signal?: AbortSignal }> = []
    mocks.get.mockImplementation((url: string, config?: { signal?: AbortSignal }) => {
      if (url !== '/project-groups/3') return base(url)
      return new Promise((resolve) => { pending.push({ resolve: () => resolve({ data: draftDetail }), signal: config?.signal }) })
    })

    // 一批信号合并为一次重拉。
    for (let revision = 1; revision <= 5; revision += 1) {
      mocks.collaboration = { ...mocks.collaboration, activityRevisions: { 9: revision } }
      rerender()
    }
    await waitFor(() => expect(pending).toHaveLength(1))
    // 请求进行中继续到达的信号不中止它，只在完成后再拉一次。
    for (let revision = 6; revision <= 10; revision += 1) {
      mocks.collaboration = { ...mocks.collaboration, activityRevisions: { 9: revision } }
      rerender()
      await new Promise((resolve) => setTimeout(resolve, 100))
    }
    expect(pending).toHaveLength(1)
    expect(pending[0].signal?.aborted).toBe(false)
    pending[0].resolve()
    await waitFor(() => expect(pending).toHaveLength(2))
    pending[1].resolve()
    await new Promise((resolve) => setTimeout(resolve, 400))
    expect(pending).toHaveLength(2)
    expect(calls('/project-groups/3')).toBe(3)
  })

  it('ignores a stale owner-options response after the transfer dialog is reopened', async () => {
    mocks.perms = ['project:transfer']
    const requests: Array<{ resolve: (value: unknown) => void }> = []
    const base = mocks.get.getMockImplementation()!
    mocks.get.mockImplementation((url: string) => {
      if (url === '/project-owner-options') return new Promise((resolve) => { requests.push({ resolve }) })
      return base(url)
    })
    const user = userEvent.setup()
    renderPage()
    await user.click(await screen.findByRole('button', { name: '变更负责人' }))
    await user.click(await screen.findByRole('button', { name: '取消' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    await user.click(screen.getByRole('button', { name: '变更负责人' }))
    await waitFor(() => expect(requests).toHaveLength(2))

    requests[1].resolve({ data: [{ id: 21, realName: '新负责人', employeeNo: 'E21', sectionName: null }] })
    requests[0].resolve({ data: [{ id: 20, realName: '过期候选人', employeeNo: 'E20', sectionName: null }] })
    const dialog = await screen.findByRole('dialog')
    await user.click(within(dialog).getByRole('combobox'))
    expect(await screen.findByText('新负责人（E21）')).toBeInTheDocument()
    expect(screen.queryByText('过期候选人（E20）')).not.toBeInTheDocument()
  })
})

describe('子项目编辑并发', () => {
  const T1 = '2026-09-09T00:00:00.123456Z'
  const T2 = '2026-09-09T00:05:00.654321Z'
  let row = { ...detail.projects[0], updatedAt: T1 }

  beforeEach(() => {
    resetSharedMocks()
    mocks.perms = ['project:update']
    row = { ...detail.projects[0], updatedAt: T1 }
    mocks.get.mockReset()
    mocks.put.mockReset()
    mocks.get.mockImplementation((url: string) => {
      if (url === '/project-groups/3') return Promise.resolve({ data: { ...detail, projects: [row] } })
      const pane = subprojectGet(url)
      if (pane) return pane
      throw new Error(`unexpected GET ${url}`)
    })
  })

  it('sends the loaded version and, on a 409 conflict, warns and retries with the refreshed version', async () => {
    mocks.put.mockImplementationOnce(() => {
      // 他人已在此期间修改了子项目。
      row = { ...row, name: '他人改名', updatedAt: T2 }
      return Promise.reject({ isAxiosError: true, response: { status: 409, data: { code: 40901, message: '子项目已被他人修改，请刷新后重试' } } })
    })
    mocks.put.mockResolvedValueOnce({ data: { ...row, name: '我的名称' } })
    const user = userEvent.setup()
    renderPage()
    await user.click(await screen.findByRole('button', { name: '编辑' }))
    const dialog = await screen.findByRole('dialog')
    const name = within(dialog).getByPlaceholderText('子项目名称')
    await user.clear(name)
    await user.type(name, '我的名称')
    await user.click(within(dialog).getByRole('button', { name: '保存子项目' }))

    await waitFor(() => expect(mocks.put).toHaveBeenCalledTimes(1))
    expect(mocks.put.mock.calls[0]).toEqual(['/projects/9', expect.objectContaining({ name: '我的名称', expectedUpdatedAt: T1 })])
    expect(await within(dialog).findByText(/子项目已被他人修改，已刷新为最新数据（当前名称：他人改名）/)).toBeInTheDocument()
    expect(within(dialog).getByPlaceholderText('子项目名称')).toHaveValue('我的名称')

    await user.click(within(dialog).getByRole('button', { name: '保存子项目' }))
    await waitFor(() => expect(mocks.put).toHaveBeenCalledTimes(2))
    expect(mocks.put.mock.calls[1]).toEqual(['/projects/9', expect.objectContaining({ name: '我的名称', expectedUpdatedAt: T2 })])
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  })
})

describe('已加载主项目失去访问', () => {
  const calls = (target: string) => mocks.get.mock.calls.filter(([url]) => url === target).length
  let groupStatus: number | null = null

  function PathProbe() {
    return <output aria-label="当前路径">{useLocation().pathname}</output>
  }
  function tree() {
    return (
      <MemoryRouter initialEntries={['/project-groups/3']}>
        <PathProbe />
        <Routes>
          <Route path="/project-groups/:id" element={<ProjectGroupDetail />} />
          <Route path="/projects" element={<div>项目列表页</div>} />
        </Routes>
      </MemoryRouter>
    )
  }

  beforeEach(() => {
    resetSharedMocks()
    groupStatus = null
    mocks.get.mockReset()
    mocks.get.mockImplementation((url: string) => {
      if (url === '/project-groups/3') {
        return groupStatus
          ? Promise.reject({ isAxiosError: true, response: { status: groupStatus } })
          : Promise.resolve({ data: detail })
      }
      if (url === '/project-groups/3/copy-jobs') return Promise.resolve({ data: { jobs: [] } })
      const pane = subprojectGet(url)
      if (pane) return pane
      throw new Error(`unexpected GET ${url}`)
    })
  })
  afterEach(() => { vi.restoreAllMocks() })

  it('a realtime refetch returning 403 warns once and replaces the route with the project list', async () => {
    const warning = vi.spyOn(Message, 'warning').mockImplementation(() => () => {})
    const view = render(tree())
    await screen.findByRole('heading', { name: '主项目 A' })
    await waitFor(() => expect(calls('/project-groups/3')).toBe(1))

    // 他人把负责人转走后，实时信号触发的重拉返回 403。
    groupStatus = 403
    mocks.collaboration = { ...mocks.collaboration, reconnectRevision: 1 }
    view.rerender(tree())
    expect(await screen.findByText('项目列表页')).toBeInTheDocument()
    expect(screen.getByLabelText('当前路径')).toHaveTextContent(/^\/projects$/)
    expect(warning).toHaveBeenCalledTimes(1)
    expect(warning).toHaveBeenCalledWith('该主项目负责人已变更或权限已调整，您已无权访问')
    expect(screen.queryByText('主项目加载失败或没有访问权限')).not.toBeInTheDocument()
  })

  it('a refetch returning 404 reports the deletion', async () => {
    const warning = vi.spyOn(Message, 'warning').mockImplementation(() => () => {})
    const view = render(tree())
    await screen.findByRole('heading', { name: '主项目 A' })
    groupStatus = 404
    mocks.collaboration = { ...mocks.collaboration, reconnectRevision: 1 }
    view.rerender(tree())
    expect(await screen.findByText('项目列表页')).toBeInTheDocument()
    expect(warning).toHaveBeenCalledTimes(1)
    expect(warning).toHaveBeenCalledWith('该主项目已被删除')
  })

  it('an initial 403 keeps the no-access state without redirecting', async () => {
    const warning = vi.spyOn(Message, 'warning').mockImplementation(() => () => {})
    groupStatus = 403
    render(tree())
    expect(await screen.findByText('主项目加载失败或没有访问权限')).toBeInTheDocument()
    expect(screen.getByLabelText('当前路径')).toHaveTextContent('/project-groups/3')
    expect(warning).not.toHaveBeenCalled()
  })
})
