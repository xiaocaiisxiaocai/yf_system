import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  delete: vi.fn(),
}))

vi.mock('../api/client', () => ({ default: mocks }))
vi.mock('dockview-react', () => import('./dockviewMock'))
vi.mock('../components/FileTable', () => ({ default: () => <div>文件列表</div> }))
vi.mock('../components/MessagePanel', () => ({ default: () => <div>留言列表</div> }))
vi.mock('../components/ProjectActivityPanel', () => ({ default: () => <div>动态列表</div> }))

vi.mock('../store/auth', () => ({
  useAuth: () => ({
    user: { id: 1, userType: 'INTERNAL' },
    hasPerm: (permission: string) => permission === 'project:create',
  }),
}))

vi.mock('../store/collaboration', () => ({
  useCollaboration: (selector: (state: { revision: string; status: string }) => unknown) => selector({
    revision: 'revision-1',
    status: 'ready',
  }),
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

describe('子项目后台复制任务', () => {
  beforeEach(() => {
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
