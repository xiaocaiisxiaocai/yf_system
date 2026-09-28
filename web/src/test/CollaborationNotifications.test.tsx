import { act, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, useLocation } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => {
  const state = {
    revision: 'revision-1',
    unreadCount: 1,
    latestId: 7,
    status: 'ready' as const,
    refresh: vi.fn(async () => undefined),
  }
  return {
    state,
    initial: { revision: 'revision-1', unreadCount: 1, latestId: 7 },
    store: null as null | { setState: (partial: Partial<typeof state>) => void; getState: () => typeof state },
    authState: { generation: 1 },
    get: vi.fn(),
    post: vi.fn(),
    startPolling: vi.fn(() => vi.fn()),
  }
})

vi.mock('../api/client', () => ({
  default: {
    get: mocks.get,
    post: mocks.post,
  },
}))

vi.mock('../store/auth', () => ({
  useAuth: {
    getState: () => mocks.authState,
  },
}))

vi.mock('../store/collaboration', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../store/collaboration')>()
  const { create } = await import('zustand')
  const useCollaboration = create(() => mocks.state)
  mocks.store = useCollaboration
  return {
    ...actual,
    useCollaboration,
    startCollaborationPolling: mocks.startPolling,
  }
})

import CollaborationNotifications from '../components/CollaborationNotifications'
import {
  parseCollaborationNotificationPage,
  parseCollaborationSummary,
} from '../store/collaboration'

function CurrentLocation() {
  const location = useLocation()
  return <output aria-label="当前地址">{location.pathname}{location.search}</output>
}

function renderNotifications() {
  return render(
    <MemoryRouter initialEntries={['/home']}>
      <CollaborationNotifications />
      <CurrentLocation />
    </MemoryRouter>,
  )
}

function notification(overrides: Record<string, unknown> = {}) {
  return {
    id: 7,
    type: 'MESSAGE',
    action: 'CREATE',
    projectId: 11,
    projectName: '协作项目',
    projectGroupName: '主项目 A',
    actorName: '张三',
    title: '新增留言',
    summary: '请核对附件',
    occurredAt: '2026-09-13T08:00:00Z',
    targetId: 29,
    targetAvailable: true,
    read: false,
    ...overrides,
  }
}

function page(item = notification()) {
  return {
    list: [item],
    total: 1,
    page: 1,
    pageSize: 20,
    unreadCount: 1,
  }
}

describe('协作通知行为', () => {
  beforeEach(() => {
    mocks.get.mockResolvedValue({ data: page() })
    mocks.post.mockResolvedValue({ data: {} })
    mocks.state.refresh.mockReset()
    mocks.state.refresh.mockResolvedValue(undefined)
    mocks.store!.setState({ ...mocks.initial })
  })

  it('通过真实交互呈现留言语义，并完成已查看与目标导航', async () => {
    const user = userEvent.setup()
    renderNotifications()

    await user.click(screen.getByRole('button', { name: '协作动态通知，1 条未查看' }))

    expect(await screen.findByText('发送留言')).toBeVisible()
    expect(screen.getByText(/不等同于留言已读/)).toBeVisible()
    expect(screen.getByText('主项目 A / 协作项目')).toBeVisible()

    const title = screen.getByRole('button', { name: '查看通知：新增留言' })
    const article = title.closest('article')
    expect(article).toHaveAttribute('data-read', 'false')

    await user.click(screen.getByRole('button', { name: '标记为已查看：新增留言' }))
    await waitFor(() => expect(mocks.post).toHaveBeenCalledWith('/collaboration/reads', { ids: [7] }))
    await waitFor(() => expect(article).toHaveAttribute('data-read', 'true'))
    expect(mocks.state.refresh).toHaveBeenCalledOnce()

    await user.click(title)
    expect(screen.getByRole('status', { name: '当前地址' })).toHaveTextContent('/projects/11?tab=messages&target=29')
  })

  it('自己标记已查看导致的概览变化不会误报新动态，真正的新动态仍会提示', async () => {
    mocks.get.mockResolvedValue({
      data: { ...page(), list: [notification(), notification({ id: 6, title: '另一条留言' })], total: 2, unreadCount: 2 },
    })
    // 标记后概览刷新：未查看数变化 -> revision 变化，但没有更新的通知（latestId 不变）。
    mocks.state.refresh.mockImplementation(async () => {
      mocks.store!.setState({ revision: 'revision-2', unreadCount: 1, latestId: 7 })
    })
    const user = userEvent.setup()
    renderNotifications()
    await user.click(screen.getByRole('button', { name: '协作动态通知，1 条未查看' }))
    await screen.findByRole('button', { name: '查看通知：新增留言' })

    await user.click(screen.getByRole('button', { name: '标记为已查看：新增留言' }))
    await waitFor(() => expect(mocks.state.refresh).toHaveBeenCalledOnce())
    await waitFor(() => expect(screen.getByRole('button', { name: '查看通知：新增留言' }).closest('article')).toHaveAttribute('data-read', 'true'))
    expect(screen.queryByText('有新的协作动态')).not.toBeInTheDocument()

    // 之后真正到达的新通知仍然提示刷新。
    act(() => mocks.store!.setState({ revision: 'revision-3', unreadCount: 2, latestId: 8 }))
    expect(await screen.findByText('有新的协作动态')).toBeVisible()
  })

  it('标记期间到达的新通知不会被已查看同步吞掉', async () => {
    mocks.state.refresh.mockImplementation(async () => {
      mocks.store!.setState({ revision: 'revision-2', unreadCount: 1, latestId: 9 })
    })
    const user = userEvent.setup()
    renderNotifications()
    await user.click(screen.getByRole('button', { name: '协作动态通知，1 条未查看' }))
    await screen.findByRole('button', { name: '查看通知：新增留言' })

    await user.click(screen.getByRole('button', { name: '标记为已查看：新增留言' }))
    expect(await screen.findByText('有新的协作动态')).toBeVisible()
  })

  it('拒绝格式错误的网络条目，并向用户呈现可重试状态', async () => {
    mocks.get.mockResolvedValue({ data: page(notification({ projectGroupName: undefined })) })
    const user = userEvent.setup()
    renderNotifications()

    await user.click(screen.getByRole('button', { name: '协作动态通知，1 条未查看' }))

    const failure = await screen.findByText('通知列表加载失败')
    expect(failure.closest('[role="alert"]')).toHaveTextContent('通知列表加载失败')
    expect(screen.queryByRole('button', { name: '查看通知：新增留言' })).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: '重试' }))
    await waitFor(() => expect(mocks.get).toHaveBeenCalledTimes(2))
  })

  it('直接导入生产解析器校验概览与通知页边界', () => {
    expect(parseCollaborationSummary({ unreadCount: 0, latestId: 0, revision: '0' })).toEqual({
      unreadCount: 0,
      latestId: 0,
      revision: '0',
    })
    expect(() => parseCollaborationSummary({ unreadCount: -1, latestId: 0, revision: '0' }))
      .toThrow('协作通知概览响应格式错误')
    expect(parseCollaborationNotificationPage(page()).list[0].targetId).toBe(29)
    expect(() => parseCollaborationNotificationPage(page(notification({ read: 'false' }))))
      .toThrow('协作通知条目格式错误')
  })
})
