import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import http from '../../../api/client'
import type { Project, ProjectCopyJob } from '../../../api/types'
import ProjectManageActions from '../../../pages/project/ProjectManageActions'
import { useAuth } from '../../../store/auth'

function deferred<T>() {
  let resolve!: (value: T | PromiseLike<T>) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((ok, fail) => { resolve = ok; reject = fail })
  return { promise, resolve, reject }
}

const sampleProject: Project = {
  id: 9,
  name: '仿真主项目',
  description: null,
  status: 'IN_PROGRESS',
  supplierId: 1,
  supplierName: '厂商甲',
  workOrderNos: ['WO-1'],
  machineModel: 'M1',
  robotPartId: 11,
  robotPartNumber: 'R-11',
  robotModelName: '型号A',
  responsibleUserId: 7,
  responsibleUserEmployeeNo: 'E007',
  responsibleUserName: '张工',
  sectionId: 1,
  sectionName: '仿真一课',
  priorityId: 1,
  priorityName: '普通',
  robotTypeId: 1,
  robotTypeName: '六轴',
  robotOwnerContactIds: [1],
  expectedCompletionDate: '2026-12-31',
  createdBy: 7,
  createdByName: '张工',
  createdAt: '2026-09-01T00:00:00Z',
  updatedAt: '2026-09-01T00:00:00Z',
  canManage: false,
  canTransfer: false,
  canDelete: false,
  hasCopyHistory: false,
  copySource: null,
} as Project

const queuedJob: ProjectCopyJob = {
  jobId: 12,
  sourceProjectId: 9,
  targetName: '仿真主项目 - 副本',
  status: 'pending',
  filesTotal: 2,
  filesCopied: 0,
  bytesTotal: 2048,
  bytesCopied: 0,
  error: null,
  result: null,
  createdAt: '2026-09-23T00:00:00Z',
  startedAt: null,
  completedAt: null,
}

const succeededJob: ProjectCopyJob = {
  ...queuedJob,
  status: 'succeeded',
  filesCopied: 2,
  bytesCopied: 2048,
  startedAt: '2026-09-23T00:00:01Z',
  completedAt: '2026-09-23T00:00:02Z',
  result: { projectId: 88, copyFileCount: 2 },
}

function LocationProbe() {
  const { pathname, search } = useLocation()
  return <output aria-label="当前路径">{`${pathname}${search}`}</output>
}

function setSession(generation = 1) {
  useAuth.setState({
    token: `token-${generation}`,
    user: {
      id: 7,
      employeeNo: 'E007',
      realName: '张工',
      email: 'eng@example.test',
      userType: 'INTERNAL',
      supplierId: null,
      isSystemAdmin: false,
    },
    permissions: ['project:create'],
    menus: ['project:list'],
    mustChangePassword: false,
    booted: true,
    generation,
  })
}

function renderActions() {
  return render(
    <MemoryRouter initialEntries={['/projects/9']}>
      <LocationProbe />
      <Routes>
        <Route path="/projects/:id" element={<ProjectManageActions project={sampleProject} onChanged={() => undefined} />} />
      </Routes>
    </MemoryRouter>,
  )
}

describe('ProjectManageActions copy jobs', () => {
  beforeEach(() => {
    setSession(1)
  })

  afterEach(() => {
    vi.restoreAllMocks()
    useAuth.setState({ token: null, user: null, permissions: [], menus: [], generation: 0 })
  })

  it('initial copy jobs failure exposes a retry button that unlocks copying on recovery', async () => {
    const user = userEvent.setup()
    const get = vi.spyOn(http, 'get')
      .mockRejectedValueOnce(new Error('temporary outage'))
      .mockResolvedValueOnce({ data: { jobs: [] } } as never)

    renderActions()

    const retryButton = await screen.findByRole('button', { name: '重试加载复制状态' })
    expect(screen.getByRole('button', { name: '复制' })).toBeDisabled()
    expect(screen.queryByRole('button', { name: '复制任务' })).not.toBeInTheDocument()

    await user.click(retryButton)
    await waitFor(() => expect(screen.getByRole('button', { name: '复制' })).toBeEnabled())
    expect(screen.queryByRole('button', { name: '重试加载复制状态' })).not.toBeInTheDocument()
    expect(get).toHaveBeenCalledTimes(2)
    expect(get).toHaveBeenCalledWith('/project-copy-jobs', expect.objectContaining({ quietNetworkError: true }))
  })

  it('passes real auth generation to copy jobs so session changes abort and reload', async () => {
    const first = deferred<{ data: { jobs: ProjectCopyJob[] } }>()
    const second = deferred<{ data: { jobs: ProjectCopyJob[] } }>()
    const get = vi.spyOn(http, 'get')
      .mockReturnValueOnce(first.promise as never)
      .mockReturnValueOnce(second.promise as never)

    renderActions()
    await waitFor(() => expect(get).toHaveBeenCalledTimes(1))
    const firstSignal = (get.mock.calls[0][1] as { signal?: AbortSignal })?.signal
    expect(firstSignal?.aborted).toBe(false)

    await act(async () => { setSession(2) })
    await waitFor(() => expect(get).toHaveBeenCalledTimes(2))
    expect(firstSignal?.aborted).toBe(true)

    await act(async () => { second.resolve({ data: { jobs: [] } }) })
    await waitFor(() => expect(screen.getByRole('button', { name: '复制' })).toBeEnabled())
  })

  it('shows queued copy job first and exposes entering the copy once completed', async () => {
    const user = userEvent.setup()
    const completionPoll = deferred<{ data: { jobs: ProjectCopyJob[] } }>()
    vi.spyOn(http, 'get')
      .mockResolvedValueOnce({ data: { jobs: [] } } as never)
      .mockReturnValueOnce(completionPoll.promise as never)
    vi.spyOn(http, 'post').mockResolvedValueOnce({ data: queuedJob } as never)

    renderActions()
    const copyButton = await screen.findByRole('button', { name: '复制' })
    await waitFor(() => expect(copyButton).toBeEnabled())

    await user.click(copyButton)
    const dialog = await screen.findByRole('dialog')
    await user.click(within(dialog).getByRole('button', { name: '创建复制任务' }))

    // Phase 1: check queued state before releasing completion response (HANDOFF.md).
    expect(await screen.findByText('排队中')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '进入副本' })).not.toBeInTheDocument()

    // Phase 2: release completion response and verify entering the copy works.
    await act(async () => { completionPoll.resolve({ data: { jobs: [succeededJob] } }) })
    const enterButton = await screen.findByRole('button', { name: '进入副本' })
    await user.click(enterButton)
    await waitFor(() => expect(screen.getByLabelText('当前路径')).toHaveTextContent('/projects/88?tab=files'))
  })
})
