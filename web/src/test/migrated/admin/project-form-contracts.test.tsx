import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useNavigate } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  delete: vi.fn(),
  collaboration: { revision: '1', status: 'ready' },
}))

vi.mock('../../../api/client', () => ({
  default: { get: mocks.get, post: mocks.post, put: mocks.put, delete: mocks.delete },
}))

vi.mock('../../../store/auth', () => ({
  useAuth: (selector?: (state: unknown) => unknown) => {
    const state = {
      user: { id: 1, employeeNo: 'admin', realName: '管理员', userType: 'INTERNAL', isSystemAdmin: true },
      hasPerm: (code: string) => ['project:create', 'project:update', 'project:delete', 'file:download'].includes(code),
    }
    return selector ? selector(state) : state
  },
}))

vi.mock('dockview-react', () => import('../../dockviewMock'))
vi.mock('../../../components/MessagePanel', () => ({ default: () => <div>messages</div> }))
vi.mock('../../../components/ProjectActivityPanel', () => ({ default: () => <div>activity</div> }))
vi.mock('../../../store/collaboration', () => ({
  useCollaboration: (selector: (state: { revision: string; status: string }) => unknown) => selector(mocks.collaboration),
}))

import ProjectGroupDetail from '../../../pages/project/ProjectGroupDetail'
import ProjectList from '../../../pages/project/ProjectList'
import ProjectDetail from '../../../pages/project/ProjectDetail'
import FileTable from '../../../components/FileTable'

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((ok, fail) => { resolve = ok; reject = fail })
  return { promise, resolve, reject }
}

function detail(id: number, name: string, projects: Array<Record<string, unknown>> = []) {
  return {
    group: {
      id,
      name,
      description: null,
      supplierName: 'Robot 厂商',
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
      subprojectCount: projects.length,
      completedCount: 0,
      pendingCount: 0,
      terminatedCount: 0,
    },
    projects,
  }
}

function SwitchProject() {
  const navigate = useNavigate()
  return <button onClick={() => navigate('/project-groups/4')}>切换主项目</button>
}

function renderGroup(path = '/project-groups/3') {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <SwitchProject />
      <Routes>
        <Route path="/project-groups/:id" element={<ProjectGroupDetail />} />
      </Routes>
    </MemoryRouter>,
  )
}

describe('主项目表单与刷新契约', () => {
  beforeEach(() => {
    mocks.collaboration = { revision: '1', status: 'ready' }
    mocks.get.mockResolvedValue({ data: detail(3, '默认主项目') })
    mocks.post.mockResolvedValue({ data: {} })
    mocks.put.mockResolvedValue({ data: {} })
    mocks.delete.mockResolvedValue({ data: {} })
  })

  it('main-project route changes discard the previous project view before the next response', async () => {
    const second = deferred<{ data: ReturnType<typeof detail> }>()
    mocks.get.mockImplementation((url: string) => {
      if (url.endsWith('/copy-jobs')) return Promise.resolve({ data: { jobs: [] } })
      return url.endsWith('/3')
        ? Promise.resolve({ data: detail(3, '旧主项目') })
        : second.promise
    })
    const user = userEvent.setup()
    renderGroup()

    expect(await screen.findByRole('heading', { name: '旧主项目' })).toBeVisible()
    await user.click(screen.getByRole('button', { name: '切换主项目' }))
    expect(screen.queryByRole('heading', { name: '旧主项目' })).not.toBeInTheDocument()
    expect(document.querySelector('.arco-spin')).toBeInTheDocument()

    second.resolve({ data: detail(4, '新主项目') })
    expect(await screen.findByRole('heading', { name: '新主项目' })).toBeVisible()
  })

  it('main-project authorization loss clears the stale project and its open write dialogs', async () => {
    for (const status of [403, 404]) {
      const project = { id: 31, name: '旧子项目', description: '旧说明', status: 'DRAFT', unreadMessages: 0 }
      let requests = 0
      mocks.get.mockImplementation(async (url: string) => {
        if (url.endsWith('/copy-jobs')) return { data: { jobs: [] } }
        if (url === '/projects/31') return { data: { ...project, workOrderNos: [] } }
        if (url === '/projects/31/summary') return { data: { unreadMessages: 0 } }
        if (url.startsWith('/projects/31/')) return { data: { list: [], total: 0, page: 1, pageSize: 20 } }
        requests += 1
        if (requests === 1) return { data: detail(3, '旧主项目', [project]) }
        throw Object.assign(new Error('access revoked'), { isAxiosError: true, response: { status } })
      })
      const user = userEvent.setup()
      const view = renderGroup()
      expect(await screen.findByRole('heading', { name: '旧主项目' })).toBeVisible()
      await user.click(screen.getByRole('button', { name: '编辑' }))
      expect(await screen.findByText('编辑子项目')).toBeVisible()
      await user.click(screen.getByRole('button', { name: '复制' }))
      expect((await screen.findAllByText('复制子项目')).at(0)).toBeVisible()

      mocks.collaboration = { revision: `revoked-${status}`, status: 'ready' }
      view.rerender(
        <MemoryRouter initialEntries={['/project-groups/3']}>
          <SwitchProject />
          <Routes><Route path="/project-groups/:id" element={<ProjectGroupDetail />} /></Routes>
        </MemoryRouter>,
      )

      expect(await screen.findByText('主项目加载失败或没有访问权限')).toBeVisible()
      expect(screen.queryByRole('heading', { name: '旧主项目' })).not.toBeInTheDocument()
      expect(screen.queryByText('编辑子项目')).not.toBeInTheDocument()
      expect(screen.queryAllByText('复制子项目')).toHaveLength(0)
      view.unmount()
    }
  })

  it('main-project transient refresh failure preserves the last snapshot with a retry state', async () => {
    let requests = 0
    mocks.get.mockImplementation(async (url: string) => {
      if (url.endsWith('/copy-jobs')) return { data: { jobs: [] } }
      requests += 1
      if (requests === 1) return { data: detail(3, '可重试主项目') }
      throw Object.assign(new Error('service unavailable'), { isAxiosError: true, response: { status: 503 } })
    })
    const view = renderGroup()
    expect(await screen.findByRole('heading', { name: '可重试主项目' })).toBeVisible()

    mocks.collaboration = { revision: '2', status: 'ready' }
    view.rerender(
      <MemoryRouter initialEntries={['/project-groups/3']}>
        <SwitchProject />
        <Routes><Route path="/project-groups/:id" element={<ProjectGroupDetail />} /></Routes>
      </MemoryRouter>,
    )

    expect(await screen.findByText('刷新失败，当前显示上次数据。')).toBeVisible()
    expect(screen.getByRole('heading', { name: '可重试主项目' })).toBeVisible()
    expect(screen.getAllByRole('button', { name: '重试' })[0]).toBeEnabled()
  })

  it('collaboration summary failure never blocks core project HTTP reads', async () => {
    mocks.collaboration = { revision: 'error-revision', status: 'error' }
    mocks.get.mockImplementation(async (url: string) => {
      if (url === '/project-groups') return { data: { list: [], total: 0, page: 1, pageSize: 10 } }
      if (url === '/project-groups/3') return { data: detail(3, '错误协作状态下仍可读取') }
      if (url === '/project-groups/3/copy-jobs') return { data: { jobs: [] } }
      if (url === '/projects/9') return { data: { id: 9, name: '协作项目', status: 'IN_PROGRESS', projectGroupId: 3, projectGroupName: '主项目', files: [] } }
      if (url === '/projects/9/files') return { data: { list: [], total: 0, page: 1, pageSize: 10 } }
      if (url === '/supplier-options' || url === '/project-dictionaries' || url === '/robot-parts') return { data: [] }
      return { data: [] }
    })

    const views = [
      render(<MemoryRouter><ProjectList /></MemoryRouter>),
      renderGroup(),
      render(<MemoryRouter initialEntries={['/projects/9']}><Routes><Route path="/projects/:id" element={<ProjectDetail />} /></Routes></MemoryRouter>),
      render(<FileTable projectId={9} projectStatus="IN_PROGRESS" />),
    ]

    await waitFor(() => {
      expect(mocks.get).toHaveBeenCalledWith('/project-groups', expect.anything())
      expect(mocks.get).toHaveBeenCalledWith('/project-groups/3', expect.anything())
      expect(mocks.get).toHaveBeenCalledWith('/projects/9', expect.anything())
      expect(mocks.get).toHaveBeenCalledWith('/projects/9/files', expect.anything())
    })
    views.forEach((view) => view.unmount())
  })
})
