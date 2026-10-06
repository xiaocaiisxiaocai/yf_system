import { render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { deferred } from './testUtils'

const mocks = vi.hoisted(() => ({
  get: vi.fn(),
  put: vi.fn(),
  post: vi.fn(),
}))

vi.mock('../../../api/client', () => ({
  default: mocks,
  getApiErrorCode: (error: unknown) => (error as { response?: { data?: { code?: number } } } | undefined)?.response?.data?.code,
}))
vi.mock('dockview-react', () => import('../../dockviewMock'))
vi.mock('../../../components/FileTable', () => ({ default: () => <div>文件列表</div> }))
vi.mock('../../../components/MessagePanel', () => ({ default: () => <div>留言列表</div> }))
vi.mock('../../../components/ProjectActivityPanel', () => ({ default: () => <div>动态列表</div> }))
vi.mock('../../../store/auth', () => ({
  useAuth: () => ({
    user: { id: 1, userType: 'INTERNAL' },
    hasPerm: (permission: string) => permission === 'project:status',
  }),
}))
vi.mock('../../../store/collaboration', () => ({
  useCollaboration: (selector: (state: { revision: string; status: string }) => unknown) => selector({ revision: '1', status: 'ready' }),
}))

import ProjectGroupDetail from '../../../pages/project/ProjectGroupDetail'

const project = {
  id: 9,
  projectGroupId: 3,
  projectGroupName: '主项目',
  name: '子项目',
  supplierName: '供应商',
  status: 'DRAFT',
  workOrderNos: [],
  updatedAt: '2026-09-09T00:00:00Z',
}
const group = {
  id: 3,
  name: '主项目',
  status: 'IN_PROGRESS',
  workOrderNos: [],
  subprojectCount: 1,
  completedCount: 0,
  pendingCount: 0,
  terminatedCount: 0,
}

describe('ProjectGroupDetail migrated behavior', () => {
  beforeEach(() => {
    mocks.get.mockImplementation((url: string) => {
      if (url === '/project-groups/3') return Promise.resolve({ data: { group, projects: [project] } })
      if (url === '/projects/9') return Promise.resolve({ data: project })
      if (url === '/projects/9/summary') return Promise.resolve({ data: { unreadMessages: 0 } })
      return Promise.reject(new Error(`unexpected GET ${url}`))
    })
    mocks.post.mockResolvedValue({ data: {} })
    mocks.put.mockResolvedValue({ data: {} })
  })

  it('subproject status update uses a per-project synchronous in-flight latch', async () => {
    const pending = deferred<{ data: unknown }>()
    mocks.put.mockImplementation(() => pending.promise)
    render(
      <MemoryRouter initialEntries={['/project-groups/3']}>
        <Routes><Route path="/project-groups/:id" element={<ProjectGroupDetail />} /></Routes>
      </MemoryRouter>,
    )

    const start = await screen.findByRole('button', { name: '开始' })
    start.click()
    start.click()
    await waitFor(() => expect(mocks.put).toHaveBeenCalledTimes(1))
    expect(mocks.put).toHaveBeenCalledWith('/projects/9/status', { status: 'IN_PROGRESS' })
    pending.resolve({ data: {} })
    // 面板内的流程操作完成后同时刷新子项目与主项目进度。
    const groupReads = () => mocks.get.mock.calls.filter(([url]) => url === '/project-groups/3').length
    await waitFor(() => expect(groupReads()).toBe(2))
    expect(mocks.get.mock.calls.filter(([url]) => url === '/projects/9').length).toBeGreaterThanOrEqual(2)
  })
})
