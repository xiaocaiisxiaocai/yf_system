import { act, fireEvent, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('../../../api/client', async () => {
  const state = (await import('./mockState')).messageMocks
  return { default: { get: state.get, post: state.post, delete: state.delete } }
})
vi.mock('../../../store/auth', async () => {
  const state = (await import('./mockState')).messageMocks
  return { useAuth: () => ({ hasPerm: (permission: string) => state.permissions.has(permission), user: state.user }) }
})

import MessagePanel from '../../../components/MessagePanel'
import {
  deferred,
  messagePage,
  messageRow,
  renderedMessageIds,
  renderMessagePanel,
  setMessagePermissions,
} from './harness'
import { messageMocks } from './mockState'

function textbox() {
  return screen.getByPlaceholderText('输入留言，Ctrl+Enter 发送') as HTMLTextAreaElement
}

async function runNotificationTarget(missing: boolean) {
  const rows = Array.from({ length: 65 }, (_, index) => messageRow(65 - index))
    .filter(row => !missing || row.id !== 22)
  const requests: Array<{ beforeId?: number; targetId?: number }> = []
  messageMocks.get.mockImplementation(async (_url: string, { params }: { params: { beforeId?: number; targetId?: number } }) => {
    requests.push({ ...params })
    const list = rows.filter(row => params.beforeId === undefined || row.id < params.beforeId).slice(0, 20)
    return messagePage(list, rows.length)
  })
  const user = userEvent.setup()
  const view = renderMessagePanel({ targetId: 22, revision: 'r1' })
  await waitFor(() => expect(requests).toHaveLength(3))
  expect(requests.map(request => request.beforeId)).toEqual([undefined, 46, 26])
  expect(requests.every(request => request.targetId === undefined)).toBe(true)
  expect(renderedMessageIds(view.container)).toEqual(rows.slice(0, 60).map(row => row.id))
  if (missing) {
    expect(screen.queryByLabelText('当前定位留言')).not.toBeInTheDocument()
  } else {
    expect(screen.getByLabelText('当前定位留言')).toHaveAttribute('data-message-id', '22')
  }
  await user.click(screen.getByRole('button', { name: /加载更多/ }))
  expect(renderedMessageIds(view.container)).toEqual(rows.map(row => row.id))
  if (!missing) expect(screen.getByLabelText('当前定位留言')).toHaveAttribute('data-message-id', '22')
}

describe('留言修订同步与竞争', () => {
  beforeEach(() => {
    setMessagePermissions()
    messageMocks.delete.mockResolvedValue({ data: {} })
    vi.stubGlobal('IntersectionObserver', undefined)
  })
  afterEach(() => vi.useRealTimers())

  it('message revision automatically shows the new message while preserving the draft', async () => {
    setMessagePermissions('message:create')
    let rows = [messageRow(3), messageRow(2), messageRow(1)]
    messageMocks.get.mockImplementation(async () => messagePage(rows))
    messageMocks.post.mockImplementation(async (_url: string, body: { content: string }) => ({ data: messageRow(5, { content: body.content }) }))
    const user = userEvent.setup()
    const view = renderMessagePanel({ revision: 'initial' })
    await screen.findByText('留言 3')
    await user.type(textbox(), '正在写的回复')
    rows = [messageRow(4, { content: '刚刚发出的新留言' }), ...rows]
    view.rerender(<MessagePanel projectId={1} projectStatus="IN_PROGRESS" revision="new-message" />)

    await screen.findByText('刚刚发出的新留言')
    expect(messageMocks.get).toHaveBeenCalledTimes(2)
    expect(messageMocks.get.mock.calls[1][1]).toMatchObject({ params: { pageSize: 20 }, quietNetworkError: true })
    expect(textbox()).toHaveValue('正在写的回复')
    expect(screen.queryByRole('button', { name: '查看最新留言' })).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: /发送/ }))
    expect(messageMocks.post.mock.calls[0][1]).toEqual({ content: '正在写的回复' })
    expect(textbox()).toHaveValue('')
  })

  it('a push arriving before the POST response is deduplicated by the confirmed message id', async () => {
    setMessagePermissions('message:create')
    let rows = [messageRow(2), messageRow(1)]
    const post = deferred<{ data: ReturnType<typeof messageRow> }>()
    const created = messageRow(3, { senderId: 9, senderName: '我', content: '推送先到' })
    messageMocks.get.mockImplementation(async () => messagePage(rows))
    messageMocks.post.mockReturnValue(post.promise)
    const view = renderMessagePanel({ revision: 'r1' })
    await screen.findByText('留言 2')
    fireEvent.change(textbox(), { target: { value: '推送先到' } })
    fireEvent.click(screen.getByRole('button', { name: /发送/ }))
    rows = [created, ...rows]
    view.rerender(<MessagePanel projectId={1} projectStatus="IN_PROGRESS" revision="r2" />)
    await screen.findByText('推送先到')
    await act(async () => post.resolve({ data: created }))

    expect(renderedMessageIds(view.container)).toEqual([3, 2, 1])
    expect(messageMocks.get).toHaveBeenCalledTimes(2)
    expect(screen.getByRole('heading', { name: '协作留言（3）' })).toBeVisible()
  })

  it('late message synchronization cannot replace a message confirmed by POST', async () => {
    setMessagePermissions('message:create')
    const rows = [messageRow(2), messageRow(1)]
    const syncs: Array<ReturnType<typeof deferred<ReturnType<typeof messagePage>>>> = []
    const created = messageRow(3, { senderId: 9, senderName: '我', content: '已确认的新留言' })
    messageMocks.get.mockImplementation(async (_url: string, config: { quietNetworkError?: boolean }) => {
      if (!config.quietNetworkError) return messagePage(rows)
      const request = deferred<ReturnType<typeof messagePage>>()
      syncs.push(request)
      return request.promise
    })
    messageMocks.post.mockResolvedValue({ data: created })
    const view = renderMessagePanel({ revision: 'r1' })
    await screen.findByText('留言 2')
    view.rerender(<MessagePanel projectId={1} projectStatus="IN_PROGRESS" revision="r2" />)
    await waitFor(() => expect(messageMocks.get).toHaveBeenCalledTimes(2))
    const syncSignal = messageMocks.get.mock.calls[1][1].signal as AbortSignal
    fireEvent.change(textbox(), { target: { value: '已确认的新留言' } })
    fireEvent.click(screen.getByRole('button', { name: /发送/ }))
    await screen.findByText('已确认的新留言')
    expect(syncSignal.aborted).toBe(true)
    await act(async () => syncs[0].resolve(messagePage(rows)))
    expect(renderedMessageIds(view.container)).toEqual([3, 2, 1])
  })

  it('late load-more response cannot overwrite a confirmed message', async () => {
    setMessagePermissions('message:create')
    const rows = Array.from({ length: 25 }, (_, index) => messageRow(25 - index))
    const append = deferred<ReturnType<typeof messagePage>>()
    messageMocks.get.mockImplementation(async (_url: string, { params }: { params: { beforeId?: number } }) =>
      params.beforeId ? append.promise : messagePage(rows.slice(0, 20), rows.length))
    messageMocks.post.mockResolvedValue({ data: messageRow(26, { senderId: 9, senderName: '我', content: '分页期间发送' }) })
    const view = renderMessagePanel({ revision: 'r1' })
    await screen.findByText('留言 25')
    fireEvent.click(screen.getByRole('button', { name: /加载更多/ }))
    fireEvent.change(textbox(), { target: { value: '分页期间发送' } })
    fireEvent.click(screen.getByRole('button', { name: /发送/ }))
    await screen.findByText('分页期间发送')
    await act(async () => append.resolve(messagePage(rows.slice(20), rows.length)))

    expect(renderedMessageIds(view.container)).toEqual([26, ...rows.slice(0, 20).map(row => row.id)])
    expect(screen.getByRole('heading', { name: '协作留言（26）' })).toBeVisible()
  })

  it('notification message views show the full list and continue receiving new messages', async () => {
    let target = messageRow(9, { content: '定位留言旧内容' })
    messageMocks.get.mockImplementation(async () => messagePage([messageRow(10), target, messageRow(8)]))
    const view = renderMessagePanel({ targetId: 9, revision: 'r1' })
    await screen.findByText('定位留言旧内容')
    target = messageRow(9, { content: '定位留言最新内容' })
    view.rerender(<MessagePanel projectId={1} projectStatus="IN_PROGRESS" targetId={9} revision="r2" />)

    await screen.findByText('定位留言最新内容')
    expect(renderedMessageIds(view.container)).toEqual([10, 9, 8])
    expect(messageMocks.get.mock.calls.every(call => call[1].params.targetId === undefined)).toBe(true)
    expect(messageMocks.get.mock.calls[1][1].params.beforeId).toBeUndefined()
  })

  it('notification target loads contiguous history without filtering, missing=false', async () => {
    await runNotificationTarget(false)
  })

  it('notification target loads contiguous history without filtering, missing=true', async () => {
    await runNotificationTarget(true)
  })

  it('message auto-sync covers every page through the loaded history and keeps the next cursor contiguous', async () => {
    let rows = Array.from({ length: 60 }, (_, index) => messageRow(60 - index))
    const requests: Array<{ phase: string; beforeId?: number }> = []
    let phase = 'initial'
    messageMocks.get.mockImplementation(async (_url: string, { params }: { params: { beforeId?: number } }) => {
      requests.push({ phase, beforeId: params.beforeId })
      const list = rows.filter(row => params.beforeId === undefined || row.id < params.beforeId).slice(0, 20)
      return messagePage(list, rows.length)
    })
    const user = userEvent.setup()
    const view = renderMessagePanel({ revision: 'r1' })
    await screen.findByText('留言 60')
    await user.click(screen.getByRole('button', { name: /加载更多/ }))
    expect(renderedMessageIds(view.container)).toEqual(Array.from({ length: 40 }, (_, index) => 60 - index))

    phase = 'sync'
    rows = Array.from({ length: 85 }, (_, index) => messageRow(85 - index))
    view.rerender(<MessagePanel projectId={1} projectStatus="IN_PROGRESS" revision="r2" />)
    await waitFor(() => expect(renderedMessageIds(view.container)).toHaveLength(80))
    expect(requests.filter(request => request.phase === 'sync').map(request => request.beforeId))
      .toEqual([undefined, 66, 46, 26])
    await user.click(screen.getByRole('button', { name: /加载更多/ }))
    expect(requests.at(-1)?.beforeId).toBe(6)
    expect(renderedMessageIds(view.container)).toEqual(Array.from({ length: 85 }, (_, index) => 85 - index))
  })

  it('loading older messages cannot acknowledge a newer revision before auto-sync runs', async () => {
    const initialRows = Array.from({ length: 40 }, (_, index) => messageRow(40 - index))
    let currentRows = initialRows
    const append = deferred<ReturnType<typeof messagePage>>()
    messageMocks.get.mockImplementation(async (_url: string, config: { quietNetworkError?: boolean; params: { beforeId?: number } }) => {
      if (!config.quietNetworkError && config.params.beforeId === 21) return append.promise
      const list = currentRows.filter(row => config.params.beforeId === undefined || row.id < config.params.beforeId).slice(0, 20)
      return messagePage(list, currentRows.length)
    })
    const view = renderMessagePanel({ revision: 'r1' })
    await screen.findByText('留言 40')
    fireEvent.click(screen.getByRole('button', { name: /加载更多/ }))
    await waitFor(() => expect(messageMocks.get).toHaveBeenCalledTimes(2))
    currentRows = [messageRow(41, { content: '追加历史期间到达' }), ...initialRows]
    view.rerender(<MessagePanel projectId={1} projectStatus="IN_PROGRESS" revision="r2" />)
    expect(messageMocks.get).toHaveBeenCalledTimes(2)
    await act(async () => append.resolve(messagePage(initialRows.slice(20), initialRows.length)))
    await screen.findByText('追加历史期间到达')
    expect(messageMocks.get.mock.calls.some(call => call[1].quietNetworkError)).toBe(true)
    expect(renderedMessageIds(view.container)).toEqual(Array.from({ length: 41 }, (_, index) => 41 - index))
  })

  it('failed message auto-sync preserves history and draft, then retries after five seconds', async () => {
    vi.useFakeTimers()
    setMessagePermissions('message:create')
    const oldRows = [messageRow(2), messageRow(1)]
    const newRows = [messageRow(3, { content: '重试后出现' }), ...oldRows]
    let syncAttempts = 0
    messageMocks.get.mockImplementation(async (_url: string, config: { quietNetworkError?: boolean }) => {
      if (!config.quietNetworkError) return messagePage(oldRows)
      syncAttempts += 1
      if (syncAttempts === 1) throw new Error('temporary message sync failure')
      return messagePage(newRows)
    })
    const view = renderMessagePanel({ revision: 'r1' })
    await act(async () => Promise.resolve())
    fireEvent.change(textbox(), { target: { value: '失败时也保留' } })
    view.rerender(<MessagePanel projectId={1} projectStatus="IN_PROGRESS" revision="r2" />)
    await act(async () => Promise.resolve())
    expect(renderedMessageIds(view.container)).toEqual([2, 1])
    expect(textbox()).toHaveValue('失败时也保留')
    expect(screen.getByText('留言同步失败，正在重试')).toBeVisible()
    await act(async () => vi.advanceTimersByTimeAsync(5000))
    expect(syncAttempts).toBe(2)
    expect(renderedMessageIds(view.container)).toEqual([3, 2, 1])
    expect(textbox()).toHaveValue('失败时也保留')
    expect(screen.queryByText('留言同步失败，正在重试')).not.toBeInTheDocument()
  })

  it('message auto-sync ignores late responses from older revisions and projects', async () => {
    const pending: Array<{ projectId: number; signal: AbortSignal; request: ReturnType<typeof deferred<ReturnType<typeof messagePage>>> }> = []
    messageMocks.get.mockImplementation(async (url: string, config: { quietNetworkError?: boolean; signal: AbortSignal }) => {
      const projectId = Number(url.match(/projects\/(\d+)/)?.[1])
      if (!config.quietNetworkError) return messagePage([messageRow(projectId * 10, { projectId })])
      const request = deferred<ReturnType<typeof messagePage>>()
      pending.push({ projectId, signal: config.signal, request })
      return request.promise
    })
    const view = renderMessagePanel({ revision: 'r1' })
    await screen.findByText('留言 10')
    view.rerender(<MessagePanel projectId={1} projectStatus="IN_PROGRESS" revision="r2" />)
    await waitFor(() => expect(pending).toHaveLength(1))
    view.rerender(<MessagePanel projectId={1} projectStatus="IN_PROGRESS" revision="r3" />)
    await waitFor(() => expect(pending).toHaveLength(2))
    expect(pending[0].signal.aborted).toBe(true)
    await act(async () => pending[1].request.resolve(messagePage([messageRow(12, { content: '当前 revision' })])))
    await act(async () => pending[0].request.resolve(messagePage([messageRow(11, { content: '旧 revision' })])))
    expect(renderedMessageIds(view.container)).toEqual([12])

    view.rerender(<MessagePanel projectId={1} projectStatus="IN_PROGRESS" revision="r4" />)
    await waitFor(() => expect(pending).toHaveLength(3))
    const oldProject = pending[2]
    view.rerender(<MessagePanel projectId={2} projectStatus="IN_PROGRESS" revision="project-2" />)
    await screen.findByText('留言 20')
    expect(oldProject.signal.aborted).toBe(true)
    await act(async () => oldProject.request.resolve(messagePage([messageRow(13, { content: '旧项目' })])))
    expect(renderedMessageIds(view.container)).toEqual([20])
  })

  it('inactive message panels do not auto-sync until activated', async () => {
    let rows = [messageRow(1)]
    messageMocks.get.mockImplementation(async () => messagePage(rows))
    const view = renderMessagePanel({ revision: 'r1', active: false })
    await screen.findByText('留言 1')
    rows = [messageRow(2), ...rows]
    view.rerender(<MessagePanel projectId={1} projectStatus="IN_PROGRESS" revision="r2" active={false} />)
    expect(messageMocks.get).toHaveBeenCalledTimes(1)
    expect(renderedMessageIds(view.container)).toEqual([1])
    view.rerender(<MessagePanel projectId={1} projectStatus="IN_PROGRESS" revision="r2" active />)
    await screen.findByText('留言 2')
    expect(messageMocks.get).toHaveBeenCalledTimes(2)
    expect(messageMocks.get.mock.calls[1][1].quietNetworkError).toBe(true)
  })
})
