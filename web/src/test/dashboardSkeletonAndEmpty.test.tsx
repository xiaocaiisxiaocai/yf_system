import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({
  get: vi.fn(),
  auth: {
    user: { id: 1, realName: '测试用户', userType: 'INTERNAL' },
    menus: ['dashboard'],
    permissions: ['project:list'],
    hasPerm: (p: string) => mocks.auth.permissions.includes(p),
  },
  collaboration: { revision: 'initial', status: 'ready' },
}))

vi.mock('../api/client', () => ({ default: { get: mocks.get } }))
vi.mock('../store/auth', () => ({
  useAuth: (selector?: (state: typeof mocks.auth) => unknown) => selector ? selector(mocks.auth) : mocks.auth,
}))
vi.mock('../store/collaboration', () => ({
  useCollaboration: (selector: (state: typeof mocks.collaboration) => unknown) => selector(mocks.collaboration),
}))

import Dashboard from '../pages/Dashboard'

describe('Dashboard Skeleton and Empty State Actions', () => {
  beforeEach(() => {
    mocks.auth.user = { id: 1, realName: '测试用户', userType: 'INTERNAL' }
    mocks.auth.menus = ['dashboard']
    mocks.auth.permissions = ['project:list']
  })

  it('renders skeleton cards while loading', async () => {
    // Keep requests pending
    mocks.get.mockReturnValue(new Promise(() => {}))
    render(
      <MemoryRouter>
        <Dashboard />
      </MemoryRouter>
    )

    expect(screen.getByLabelText('正在加载业务概览')).toBeInTheDocument()
    expect(screen.getByLabelText('正在加载待处理项目')).toBeInTheDocument()
    expect(screen.getByLabelText('正在加载留言')).toBeInTheDocument()
  })

  it('renders action guidance when pending tasks and messages are empty', async () => {
    mocks.auth.user = { id: 1, realName: '测试用户', userType: 'INTERNAL' }
    mocks.get.mockImplementation(async (url: string) => {
      if (url === '/dashboard/summary') {
        return { data: { projectCount: 5, activeProjectCount: 2, pendingTasks: 0, unreadMessages: 0, recentMessages: [] } }
      }
      return { data: { list: [], total: 0, page: 1, pageSize: 10 } }
    })

    render(
      <MemoryRouter>
        <Dashboard />
      </MemoryRouter>
    )

    // For internal user, pending empty should guide to "查看我的项目"
    expect(await screen.findByRole('button', { name: '查看我的项目' })).toBeVisible()
    // For unread messages empty, should offer "查看全部留言"
    expect(screen.getByRole('button', { name: '查看全部留言' })).toBeVisible()
  })

  it('renders supplier-specific guidance when user is supplier', async () => {
    mocks.auth.user = { id: 2, realName: '供应商人员', userType: 'SUPPLIER' }
    mocks.get.mockImplementation(async (url: string) => {
      if (url === '/dashboard/summary') {
        return { data: { projectCount: 3, activeProjectCount: 1, pendingTasks: 0, unreadMessages: 0, recentMessages: [] } }
      }
      return { data: { list: [], total: 0, page: 1, pageSize: 10 } }
    })

    render(
      <MemoryRouter>
        <Dashboard />
      </MemoryRouter>
    )

    // For supplier, pending empty should guide to "查看全部项目"
    expect(await screen.findByRole('button', { name: '查看全部项目' })).toBeVisible()
  })
})
