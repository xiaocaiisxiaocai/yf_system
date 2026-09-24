import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, useLocation } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({
  get: vi.fn(),
  auth: {
    user: { id: 1, realName: '协作用户', userType: 'INTERNAL' } as { id: number; realName: string; userType: string },
    menus: ['dashboard'] as string[],
    permissions: ['project:list'] as string[],
    hasPerm: (permission: string) => mocks.auth.permissions.includes(permission),
  },
  collaboration: { revision: 'initial', status: 'ready' },
}))

vi.mock('../../../api/client', () => ({ default: { get: mocks.get } }))
vi.mock('../../../store/auth', () => ({
  useAuth: (selector?: (state: typeof mocks.auth) => unknown) => selector ? selector(mocks.auth) : mocks.auth,
}))
vi.mock('../../../store/collaboration', () => ({
  useCollaboration: (selector: (state: typeof mocks.collaboration) => unknown) => selector(mocks.collaboration),
}))

import Dashboard from '../../../pages/Dashboard'
import MessagePanel from '../../../components/MessagePanel'
import DeptManage from '../../../pages/org/DeptManage'
import SysConfig from '../../../pages/system/SysConfig'
import { queryClient } from '../../../api/queryClient'

const summary = {
  projectCount: 3,
  activeProjectCount: 2,
  pendingConfirmations: 1,
  unreadMessages: 12,
  recentMessages: [],
}
const emptyPage = { list: [], total: 0, page: 1, pageSize: 10 }

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((ok, fail) => { resolve = ok; reject = fail })
  return { promise, resolve, reject }
}

function httpError(status: number) {
  return Object.assign(new Error(`HTTP ${status}`), { response: { status } })
}

function CurrentLocation() {
  const location = useLocation()
  return <output aria-label="当前地址">{location.pathname}{location.search}</output>
}

function dashboardView() {
  return (
    <MemoryRouter initialEntries={['/dashboard']}>
      <Dashboard />
      <CurrentLocation />
    </MemoryRouter>
  )
}

function setDefaultGet() {
  mocks.get.mockImplementation(async (url: string) => ({
    data: url === '/dashboard/summary' ? summary : emptyPage,
  }))
}

describe('工作台协作生产界面', () => {
  beforeEach(() => {
    mocks.auth.user = { id: 1, realName: '协作用户', userType: 'INTERNAL' }
    mocks.auth.menus = ['dashboard']
    mocks.auth.permissions = ['project:list']
    mocks.collaboration.revision = 'initial'
    mocks.collaboration.status = 'ready'
    setDefaultGet()
  })

  it('dashboard menu alone does not request project data, including after project access is revoked', async () => {
    mocks.auth.menus = ['dashboard', 'supplier:list']
    mocks.auth.permissions = ['supplier:account']
    const view = render(dashboardView())
    expect(await screen.findByText('工作台不可用')).toBeVisible()
    expect(screen.getByText(/未分配项目查看权限/)).toBeVisible()
    expect(mocks.get).not.toHaveBeenCalled()

    mocks.collaboration.revision = 'changed-without-access'
    view.rerender(dashboardView())
    expect(mocks.get).not.toHaveBeenCalled()

    mocks.auth.permissions = ['supplier:account', 'project:list']
    view.rerender(dashboardView())
    await waitFor(() => expect(mocks.get).toHaveBeenCalledTimes(3))

    mocks.auth.permissions = ['supplier:account']
    mocks.collaboration.revision = 'changed-after-revocation'
    view.rerender(dashboardView())
    await screen.findByText('工作台不可用')
    expect(mocks.get).toHaveBeenCalledTimes(3)
  })

  it('network recovery reloads dashboard resources even when server revision is unchanged', async () => {
    mocks.collaboration.revision = 'same-server-revision'
    const view = render(dashboardView())
    await waitFor(() => expect(mocks.get).toHaveBeenCalledTimes(3))

    mocks.collaboration.status = 'error'
    view.rerender(dashboardView())
    expect(mocks.get).toHaveBeenCalledTimes(3)

    mocks.collaboration.status = 'ready'
    view.rerender(dashboardView())
    await waitFor(() => expect(mocks.get).toHaveBeenCalledTimes(6))
    expect(mocks.get.mock.calls.slice(3).map(([url]) => url).sort()).toEqual(['/dashboard/messages', '/dashboard/pending-projects', '/dashboard/summary'])
  })

  it('dashboard defaults to the complete paginated unread feed and deep-links a selected message', async () => {
    const unread = {
      id: 37,
      projectId: 8,
      projectName: '设备验收',
      content: '请核对全部验收记录',
      senderName: '供应商成员',
      createdAt: '2026-09-13T01:00:00Z',
      unread: true,
    }
    mocks.get.mockImplementation(async (url: string, config: { params?: Record<string, unknown> } = {}) => {
      if (url === '/dashboard/summary') return { data: summary }
      if (url === '/dashboard/pending-projects') return { data: emptyPage }
      if (url === '/dashboard/messages') return {
        data: config.params?.unreadOnly
          ? { list: [unread], total: 12, page: config.params.page, pageSize: 10 }
          : emptyPage,
      }
      throw new Error(`unexpected request ${url}`)
    })
    const user = userEvent.setup()
    render(dashboardView())

    expect(await screen.findByText('请核对全部验收记录')).toBeVisible()
    const initialMessages = mocks.get.mock.calls.find(([url]) => url === '/dashboard/messages')
    expect(initialMessages?.[1]).toMatchObject({
      params: { page: 1, pageSize: 10, unreadOnly: true },
      signal: expect.any(AbortSignal),
      quietNetworkError: true,
    })

    await user.click(screen.getByRole('link', { name: '查看设备验收的留言' }))
    expect(screen.getByRole('status', { name: '当前地址' })).toHaveTextContent('/projects/8?tab=messages&target=37')

    await user.click(screen.getByRole('button', { name: '全部' }))
    await waitFor(() => {
      const last = mocks.get.mock.calls.filter(([url]) => url === '/dashboard/messages').at(-1)
      expect(last?.[1]).toMatchObject({ params: { page: 1, pageSize: 10, unreadOnly: false }, quietNetworkError: true })
    })
  })

  it('collaboration revisions refresh quietly and older replies cannot replace the latest dashboard snapshot', async () => {
    const refreshes: Array<{ url: string; config: Record<string, unknown>; request: ReturnType<typeof deferred<{ data: unknown }>> }> = []
    mocks.get.mockImplementation(async (url: string, config: Record<string, unknown> = {}) => {
      if (mocks.collaboration.revision === 'initial') return { data: url === '/dashboard/summary' ? summary : emptyPage }
      const request = deferred<{ data: unknown }>()
      refreshes.push({ url, config, request })
      return request.promise
    })
    const view = render(dashboardView())
    await screen.findByText('近30天未读留言')

    mocks.collaboration.revision = 'revision-1'
    view.rerender(dashboardView())
    await waitFor(() => expect(refreshes).toHaveLength(3))
    expect(screen.getByText('近30天未读留言')).toBeVisible()

    mocks.collaboration.revision = 'revision-2'
    view.rerender(dashboardView())
    await waitFor(() => expect(refreshes).toHaveLength(6))
    for (const refresh of refreshes) expect(refresh.config).toMatchObject({ signal: expect.any(AbortSignal), quietNetworkError: true })
    expect(refreshes.slice(0, 3).every(({ config }) => (config.signal as AbortSignal).aborted)).toBe(true)

    await act(async () => {
      for (const refresh of refreshes.slice(3)) {
        refresh.request.resolve({ data: refresh.url === '/dashboard/summary'
          ? { ...summary, unreadMessages: 1 }
          : refresh.url === '/dashboard/messages'
            ? { list: [{ id: 202, projectId: 2, content: '新留言', createdAt: '', unread: true }], total: 1, page: 1, pageSize: 10 }
            : emptyPage })
      }
    })
    expect(await screen.findByText('新留言')).toBeVisible()

    await act(async () => {
      for (const refresh of refreshes.slice(0, 3)) {
        refresh.request.resolve({ data: refresh.url === '/dashboard/summary'
          ? { ...summary, unreadMessages: 9 }
          : refresh.url === '/dashboard/messages'
            ? { list: [{ id: 101, projectId: 1, content: '旧留言', createdAt: '', unread: true }], total: 1, page: 1, pageSize: 10 }
            : emptyPage })
      }
    })
    expect(screen.getByText('新留言')).toBeVisible()
    expect(screen.queryByText('旧留言')).not.toBeInTheDocument()
  })

  it('a failed quiet refresh keeps the last data visible and marks it stale', async () => {
    mocks.get.mockImplementation(async (url: string) => {
      if (mocks.collaboration.revision !== 'initial') throw new Error('temporary refresh failure')
      if (url === '/dashboard/summary') return { data: summary }
      if (url === '/dashboard/pending-projects') return { data: emptyPage }
      return { data: { list: [{ id: 7, projectId: 3, content: '保留内容', createdAt: '', unread: true }], total: 1, page: 1, pageSize: 10 } }
    })
    const view = render(dashboardView())
    expect(await screen.findByText('保留内容')).toBeVisible()

    mocks.collaboration.revision = 'revision-1'
    view.rerender(dashboardView())
    await waitFor(() => expect(screen.getAllByText('更新暂时失败，显示上次数据')).toHaveLength(3))
    expect(screen.getByText('保留内容')).toBeVisible()
    expect(screen.getAllByText('更新暂时失败，显示上次数据')).toHaveLength(3)
  })

  it.each([401, 403])('an HTTP %s refresh hides protected snapshots and clears the dashboard cache', async (status) => {
    let denied = false
    mocks.get.mockImplementation(async (url: string) => {
      if (denied) throw httpError(status)
      if (url === '/dashboard/summary') return { data: summary }
      if (url === '/dashboard/pending-projects') return { data: emptyPage }
      return { data: { list: [{ id: 7, projectId: 3, content: '受保护留言', createdAt: '', unread: true }], total: 1, page: 1, pageSize: 10 } }
    })
    const view = render(dashboardView())
    expect(await screen.findByText('受保护留言')).toBeVisible()
    expect(queryClient.getQueriesData({ queryKey: ['dashboard'] }).some(([, data]) => data !== undefined)).toBe(true)

    denied = true
    mocks.collaboration.revision = `denied-${status}`
    view.rerender(dashboardView())

    expect(await screen.findByText('当前会话无权读取工作台数据，请重新登录或联系管理员确认权限。')).toBeVisible()
    expect(screen.queryByText('受保护留言')).not.toBeInTheDocument()
    await waitFor(() => expect(queryClient.getQueriesData({ queryKey: ['dashboard'] })).toHaveLength(0))
  })

  it('dashboard and project detail panels expose retry instead of a false empty state', async () => {
    const cases: Array<{
      api: string
      element: React.ReactElement
      recovered: () => Promise<unknown>
      fallback: (url: string) => unknown
    }> = [
      {
        api: '/projects/1/messages',
        element: <MessagePanel projectId={1} projectStatus="IN_PROGRESS" />,
        recovered: async () => ({ list: [{ id: 1, senderId: 2, senderName: '成员', senderType: 'INTERNAL', content: '消息', readByMe: true, readCount: 1, totalCount: 1, createdAt: '' }], total: 1 }),
        fallback: () => ({ list: [], total: 0 }),
      },
      {
        api: '/departments',
        element: <DeptManage />,
        recovered: async () => [{ id: 1, name: '事业部', kind: 'DIVISION', sortNo: 1, status: 'ACTIVE', children: [] }],
        fallback: () => [],
      },
      {
        api: '/admin/system/configs',
        element: <MemoryRouter><SysConfig /></MemoryRouter>,
        recovered: async () => [],
        fallback: (url) => url === '/admin/system/mail-settings'
          ? { host: '', port: 465, username: '', from: '', security: 'Auto', hasPassword: false, configured: false, passwordNeedsUpdate: false }
          : { configured: false, notificationsEnabled: true, queue: {}, missingEmailAccounts: [], recent: [] },
      },
      {
        api: '/dashboard/summary',
        element: dashboardView(),
        recovered: async () => ({ ...summary, unreadMessages: 0 }),
        fallback: () => emptyPage,
      },
    ]

    for (const item of cases) {
      let fail = true
      mocks.get.mockImplementation(async (url: string) => {
        if (url === item.api) {
          if (fail) throw new Error('simulated panel failure')
          return { data: await item.recovered() }
        }
        return { data: item.fallback(url) }
      })
      const user = userEvent.setup()
      const view = render(item.element)
      expect(await screen.findByText('加载失败')).toBeVisible()
      fail = false
      await user.click(screen.getAllByRole('button', { name: '重试' })[0])
      await waitFor(() => expect(screen.queryByText('加载失败')).not.toBeInTheDocument())
      view.unmount()
      mocks.get.mockClear()
    }
  })

  it('dashboard pending projects can retry, navigate, refresh, and recover from an expired last page', async () => {
    const pendingRequests: Array<{ params: Record<string, unknown>; request: ReturnType<typeof deferred<{ data: unknown }>> }> = []
    mocks.get.mockImplementation(async (url: string, config: { params?: Record<string, unknown> } = {}) => {
      if (url === '/dashboard/summary') return { data: { ...summary, pendingConfirmations: 11 } }
      if (url === '/dashboard/messages') return { data: emptyPage }
      if (url === '/dashboard/pending-projects') {
        const request = deferred<{ data: unknown }>()
        pendingRequests.push({ params: config.params ?? {}, request })
        return request.promise
      }
      throw new Error(`unexpected request ${url}`)
    })
    const user = userEvent.setup()
    render(dashboardView())
    await waitFor(() => expect(pendingRequests).toHaveLength(1))
    expect(pendingRequests[0].params).toEqual({ page: 1, pageSize: 10 })

    await act(async () => pendingRequests[0].request.reject(new Error('pending projects unavailable')))
    expect(await screen.findByText('内部待验收子项目加载失败')).toBeVisible()
    await user.click(screen.getByRole('button', { name: '重试' }))
    await waitFor(() => expect(pendingRequests).toHaveLength(2))

    const firstPage = [{ id: 11, name: '待确认项目 A', projectGroupId: 2, projectGroupName: '主项目 A', status: 'PENDING_CONFIRMATION', confirmSide: 'COMPANY', updatedAt: '2026-09-10T01:00:00Z' }]
    await act(async () => pendingRequests[1].request.resolve({ data: { list: firstPage, total: 11, page: 1, pageSize: 10 } }))
    expect(await screen.findByRole('link', { name: '主项目 A / 待确认项目 A' })).toHaveAttribute('href', '/projects/11')

    await user.click(screen.getByLabelText('第 2 页'))
    await waitFor(() => expect(pendingRequests).toHaveLength(3))
    expect(pendingRequests[2].params).toEqual({ page: 2, pageSize: 10 })
    await act(async () => pendingRequests[2].request.resolve({ data: { list: [], total: 10, page: 2, pageSize: 10 } }))
    await waitFor(() => expect(pendingRequests).toHaveLength(4))
    expect(pendingRequests[3].params).toEqual({ page: 1, pageSize: 10 })
    await act(async () => pendingRequests[3].request.resolve({ data: { list: firstPage, total: 10, page: 1, pageSize: 10 } }))
    await waitFor(() => expect(screen.queryByLabelText('第 2 页')).not.toBeInTheDocument())

    const pendingCard = screen.getByRole('heading', { name: '公司内部待验收子项目' }).closest('.arco-card')
    expect(pendingCard).not.toBeNull()
    await user.click(within(pendingCard as HTMLElement).getByRole('button', { name: '刷新' }))
    await waitFor(() => expect(pendingRequests).toHaveLength(5))
    await act(async () => pendingRequests[4].request.resolve({ data: emptyPage }))
    expect(await screen.findByText('暂无内部待验收子项目')).toBeVisible()
  })

  it('supplier dashboard uses company-acceptance wording while internal wording stays role-specific', async () => {
    mocks.auth.user = { id: 2, realName: '供应商用户', userType: 'SUPPLIER' }
    render(dashboardView())
    expect(await screen.findByText('跟进待公司验收与未读留言')).toBeVisible()
    expect(screen.getByRole('heading', { name: '待公司验收子项目' })).toBeVisible()
    expect(await screen.findByText('暂无待公司验收子项目')).toBeVisible()
    expect(screen.queryByText('优先处理公司内部验收与未读留言')).not.toBeInTheDocument()
  })

  it('dashboard without its menu permission stays local and does not call dashboard APIs', async () => {
    mocks.auth.menus = []
    mocks.auth.permissions = []
    render(dashboardView())
    expect(await screen.findByText('工作台不可用')).toBeVisible()
    expect(mocks.get).not.toHaveBeenCalled()
  })
})
