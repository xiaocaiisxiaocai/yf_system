import { render, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  delete: vi.fn(),
  collaboration: { revision: '1', status: 'ready' },
}))

vi.mock('../../../api/client', () => ({
  getApiErrorCode: (error: unknown) => (error as { response?: { data?: { code?: number } } } | undefined)?.response?.data?.code,
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

vi.mock('../../../components/MessagePanel', () => ({ default: () => <div>messages</div> }))
vi.mock('../../../components/ProjectActivityPanel', () => ({ default: () => <div>activity</div> }))
vi.mock('../../../store/collaboration', () => ({
  useCollaboration: (selector: (state: { revision: string; status: string }) => unknown) => selector(mocks.collaboration),
}))

import ProjectList from '../../../pages/project/ProjectList'
import ProjectDetail from '../../../pages/project/ProjectDetail'
import FileTable from '../../../components/FileTable'

describe('项目读取契约', () => {
  beforeEach(() => {
    mocks.collaboration = { revision: '1', status: 'ready' }
    mocks.post.mockResolvedValue({ data: {} })
    mocks.put.mockResolvedValue({ data: {} })
    mocks.delete.mockResolvedValue({ data: {} })
  })

  it('collaboration summary failure never blocks core project HTTP reads', async () => {
    mocks.collaboration = { revision: 'error-revision', status: 'error' }
    mocks.get.mockImplementation(async (url: string) => {
      if (url === '/projects') return { data: { list: [], total: 0, page: 1, pageSize: 10 } }
      if (url === '/project-copy-jobs') return { data: { jobs: [] } }
      if (url === '/projects/9') return { data: { id: 9, name: '协作项目', status: 'IN_PROGRESS', canManage: true, files: [] } }
      if (url === '/projects/9/files') return { data: { list: [], total: 0, page: 1, pageSize: 10 } }
      if (url === '/supplier-options' || url === '/project-dictionaries' || url === '/robot-parts') return { data: [] }
      return { data: [] }
    })

    const views = [
      render(<MemoryRouter><ProjectList /></MemoryRouter>),
      render(<MemoryRouter initialEntries={['/projects/9']}><Routes><Route path="/projects/:id" element={<ProjectDetail />} /></Routes></MemoryRouter>),
      render(<FileTable projectId={9} projectStatus="IN_PROGRESS" />),
    ]

    await waitFor(() => {
      expect(mocks.get).toHaveBeenCalledWith('/projects', expect.anything())
      expect(mocks.get).toHaveBeenCalledWith('/projects/9', expect.anything())
      expect(mocks.get).toHaveBeenCalledWith('/projects/9/files', expect.anything())
    })
    views.forEach((view) => view.unmount())
  })
})
