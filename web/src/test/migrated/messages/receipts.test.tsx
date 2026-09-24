import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { StrictMode } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('../../../api/client', async () => {
  const state = (await import('./mockState')).messageMocks
  return { default: { get: state.get, post: state.post, delete: state.delete } }
})
vi.mock('../../../store/auth', async () => {
  const state = await import('./mockState')
  return { useAuth: () => state.messageAuthState() }
})

import MessagePanel from '../../../components/MessagePanel'
import { ReceiptBody } from '../../../components/MessageReceipts'
import {
  deferred,
  messagePage,
  messageRow,
  renderMessagePanel,
  setMessagePermissions,
} from './harness'
import { messageMocks } from './mockState'

const visibleIds = new Set<number>()
type ReceiptPayload = { data: { readers: Array<{ userId: number; realName: string; userType: string }>; unread: never[] } }
type ReceiptPending = ReturnType<typeof deferred<ReceiptPayload>>
type CountPayload = { data: Array<{ id: number; readCount: number; totalCount: number }> }
type CountPending = ReturnType<typeof deferred<CountPayload>>

function installVisibleRects() {
  vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(function (this: HTMLElement) {
    if (this.classList.contains('message-scroll-area')) {
      return { x: 0, y: 0, width: 600, height: 800, top: 0, right: 600, bottom: 800, left: 0, toJSON: () => ({}) }
    }
    const id = Number(this.dataset.messageId)
    if (Number.isFinite(id)) {
      const visible = visibleIds.has(id)
      return {
        x: 0, y: visible ? 20 : 1100, width: 120, height: 32,
        top: visible ? 20 : 1100, right: 120, bottom: visible ? 52 : 1132, left: 0,
        toJSON: () => ({}),
      }
    }
    return { x: 0, y: 0, width: 0, height: 0, top: 0, right: 0, bottom: 0, left: 0, toJSON: () => ({}) }
  })
}

async function flush() {
  await act(async () => {
    for (let turn = 0; turn < 3; turn += 1) {
      await vi.advanceTimersByTimeAsync(1)
      await Promise.resolve()
    }
  })
}

async function advance(ms: number) {
  await act(async () => vi.advanceTimersByTimeAsync(ms))
  await flush()
}

function closeDrawer() {
  const close = document.querySelector<HTMLElement>('.arco-drawer-close-icon')
    ?? document.querySelector<HTMLElement>('.arco-drawer-close')
  expect(close).not.toBeNull()
  fireEvent.click(close!)
}

describe('留言回执刷新与详情', () => {
  beforeEach(() => {
    visibleIds.clear()
    setMessagePermissions()
    messageMocks.post.mockResolvedValue({ data: {} })
    messageMocks.delete.mockResolvedValue({ data: {} })
    vi.stubGlobal('IntersectionObserver', undefined)
    Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'visible' })
    Object.defineProperty(navigator, 'onLine', { configurable: true, value: true })
    installVisibleRects()
  })
  afterEach(() => vi.useRealTimers())

  it('message receipt requests ignore late responses and closing invalidates them', async () => {
    const requests = new Map<number, ReceiptPending[]>()
    const rows = [
      messageRow(1, { senderId: 9, senderName: '我', content: '一' }),
      messageRow(2, { senderId: 9, senderName: '我', content: '二' }),
    ]
    messageMocks.get.mockImplementation(async (url: string) => {
      if (url === '/projects/1/messages') return messagePage(rows)
      const id = Number(url.match(/messages\/(\d+)\/reads$/)?.[1])
      const request = deferred<ReceiptPayload>()
      requests.set(id, [...(requests.get(id) ?? []), request])
      return request.promise
    })
    const user = userEvent.setup()
    renderMessagePanel({ projectStatus: 'COMPLETED' })
    const buttons = await screen.findAllByRole('button', { name: '回执详情' })
    await user.click(buttons[0])
    await user.click(buttons[1])
    await act(async () => requests.get(2)![0].resolve({ data: { readers: [{ userId: 2, realName: '读者二', userType: 'INTERNAL' }], unread: [] } }))
    await act(async () => requests.get(1)![0].resolve({ data: { readers: [{ userId: 3, realName: '读者一', userType: 'INTERNAL' }], unread: [] } }))
    expect(await screen.findByText('读者二')).toBeVisible()
    expect(screen.queryByText('读者一')).not.toBeInTheDocument()

    await user.click(buttons[0])
    await waitFor(() => expect(requests.get(1)).toHaveLength(2))
    closeDrawer()
    await act(async () => requests.get(1)![1].resolve({ data: { readers: [{ userId: 4, realName: '关闭后返回', userType: 'INTERNAL' }], unread: [] } }))
    expect(screen.queryByText('关闭后返回')).not.toBeInTheDocument()
  })

  it('receipt sync updates visible counts without replacing loaded messages or the draft', async () => {
    vi.useFakeTimers()
    setMessagePermissions('message:create')
    const rows = [messageRow(1, { content: '第一条内容' }), messageRow(2, { content: '第二条内容' })]
    const requests: Array<{ url: string; ids?: string }> = []
    messageMocks.get.mockImplementation(async (url: string, config?: { params?: { ids?: string } }) => {
      requests.push({ url, ids: config?.params?.ids })
      if (url === '/projects/1/messages') return messagePage(rows)
      if (url === '/projects/1/message-receipts') return { data: [{ id: 1, readCount: 1, totalCount: 1 }] }
      throw new Error(`unexpected request ${url}`)
    })
    const view = renderMessagePanel()
    await flush()
    visibleIds.add(1)
    fireEvent.change(screen.getByPlaceholderText('输入留言，Ctrl+Enter 发送'), { target: { value: '正在编辑的草稿' } })
    await advance(5000)

    expect(requests.filter(request => request.url.endsWith('/message-receipts'))).toEqual([
      { url: '/projects/1/message-receipts', ids: '1' },
    ])
    expect(screen.getByRole('button', { name: '查看留言回执：1/1' })).toBeVisible()
    expect(view.container.querySelectorAll('[data-message-id]')).toHaveLength(2)
    expect(screen.getByPlaceholderText('输入留言，Ctrl+Enter 发送')).toHaveValue('正在编辑的草稿')
    expect(screen.getByText('第一条内容')).toBeVisible()
    expect(screen.getByText('第二条内容')).toBeVisible()
  })

  it('receipt sync pauses while hidden, resumes on focus, and cleans up when inactive', async () => {
    vi.useFakeTimers()
    let receiptCount = 0
    let receiptCalls = 0
    messageMocks.get.mockImplementation(async (url: string) => {
      if (url === '/projects/1/messages') return messagePage([messageRow(1, { content: '可见留言' })])
      receiptCalls += 1
      receiptCount += 1
      return { data: [{ id: 1, readCount: receiptCount, totalCount: receiptCount }] }
    })
    const view = renderMessagePanel({ active: true })
    await flush()
    visibleIds.add(1)
    await advance(5000)
    expect(screen.getByRole('button', { name: '查看留言回执：1/1' })).toBeVisible()

    Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'hidden' })
    fireEvent(document, new Event('visibilitychange'))
    const hiddenCalls = receiptCalls
    await advance(10000)
    expect(receiptCalls).toBe(hiddenCalls)
    Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'visible' })
    fireEvent.focus(window)
    await flush()
    expect(receiptCalls).toBe(hiddenCalls + 1)
    expect(screen.getByRole('button', { name: '查看留言回执：2/2' })).toBeVisible()

    view.rerender(<MessagePanel projectId={1} projectStatus="IN_PROGRESS" active={false} />)
    const inactiveCalls = receiptCalls
    fireEvent.focus(window)
    fireEvent.online(window)
    fireEvent(document, new Event('visibilitychange'))
    await advance(10000)
    expect(receiptCalls).toBe(inactiveCalls)
  })

  it('receipt sync ignores delayed responses after project switch and aborts on unmount', async () => {
    vi.useFakeTimers()
    const pending: Array<{ projectId: number; signal: AbortSignal; request: CountPending }> = []
    messageMocks.get.mockImplementation(async (url: string, config: { signal?: AbortSignal }) => {
      const projectId = Number(url.match(/projects\/(\d+)/)?.[1])
      if (url.endsWith('/messages')) return messagePage([messageRow(1, { projectId, content: `项目${projectId}留言` })])
      if (projectId === 2) return { data: [{ id: 1, readCount: 0, totalCount: 1 }] }
      const request = deferred<CountPayload>()
      pending.push({ projectId, signal: config.signal!, request })
      return request.promise
    })
    const view = renderMessagePanel({ projectId: 1, active: true })
    await flush()
    visibleIds.add(1)
    await advance(5000)
    expect(pending).toHaveLength(1)
    view.rerender(<MessagePanel projectId={2} projectStatus="IN_PROGRESS" active />)
    await flush()
    expect(pending[0].signal.aborted).toBe(true)
    await act(async () => pending[0].request.resolve({ data: [{ id: 1, readCount: 1, totalCount: 1 }] }))
    expect(screen.getByText('项目2留言')).toBeVisible()
    expect(screen.getByRole('button', { name: '查看留言回执：0/1' })).toBeVisible()
    view.unmount()

    const second = renderMessagePanel({ projectId: 3, active: true })
    await flush()
    await advance(5000)
    const late = pending.find(request => request.projectId === 3)!
    expect(late).toBeDefined()
    second.unmount()
    expect(late.signal.aborted).toBe(true)
    late.request.resolve({ data: [{ id: 1, readCount: 1, totalCount: 1 }] })
  })

  it('receipt sync keeps the last count after failure and recovers on the backoff poll', async () => {
    vi.useFakeTimers()
    let fail = true
    let receiptCalls = 0
    messageMocks.get.mockImplementation(async (url: string) => {
      if (url === '/projects/1/messages') return messagePage([messageRow(1, { content: '保留内容' })])
      receiptCalls += 1
      if (fail) throw new Error('receipts unavailable')
      return { data: [{ id: 1, readCount: 1, totalCount: 1 }] }
    })
    renderMessagePanel()
    await flush()
    visibleIds.add(1)
    await advance(5000)
    expect(receiptCalls).toBe(1)
    expect(screen.getByRole('button', { name: '查看留言回执：0/1' })).toBeVisible()
    fail = false
    await advance(10000)
    expect(receiptCalls).toBe(2)
    expect(screen.getByRole('button', { name: '查看留言回执：1/1' })).toBeVisible()
    await advance(5000)
    expect(receiptCalls).toBe(3)
  })

  it('receipt revision refreshes visible counts immediately without a realtime poll timer', async () => {
    vi.useFakeTimers()
    setMessagePermissions('message:create')
    visibleIds.add(1)
    let receiptCalls = 0
    messageMocks.get.mockImplementation(async (url: string) => {
      if (url === '/projects/1/messages') return messagePage([messageRow(1, { content: '实时回执留言' })])
      receiptCalls += 1
      return { data: [{ id: 1, readCount: 1, totalCount: 1 }] }
    })
    const view = renderMessagePanel({ receiptRevision: 'receipt-1', realtimeConnected: true })
    await flush()
    fireEvent.change(screen.getByPlaceholderText('输入留言，Ctrl+Enter 发送'), { target: { value: '保留中的实时草稿' } })
    const before = receiptCalls
    view.rerender(<MessagePanel projectId={1} projectStatus="IN_PROGRESS" active receiptRevision="receipt-2" realtimeConnected />)
    await flush()
    expect(receiptCalls).toBe(before + 1)
    expect(screen.getByRole('button', { name: '查看留言回执：1/1' })).toBeVisible()
    expect(screen.getByPlaceholderText('输入留言，Ctrl+Enter 发送')).toHaveValue('保留中的实时草稿')
    await advance(60000)
    expect(receiptCalls).toBe(before + 1)
  })

  it('disconnecting realtime receipt updates restores the five second poll interval', async () => {
    vi.useFakeTimers()
    visibleIds.add(1)
    let receiptCalls = 0
    messageMocks.get.mockImplementation(async (url: string) => {
      if (url === '/projects/1/messages') return messagePage([messageRow(1, { content: '断线回执留言' })])
      receiptCalls += 1
      return { data: [{ id: 1, readCount: 0, totalCount: 1 }] }
    })
    const view = renderMessagePanel({ receiptRevision: 'receipt-1', realtimeConnected: true })
    await flush()
    const before = receiptCalls
    view.rerender(<MessagePanel projectId={1} projectStatus="IN_PROGRESS" receiptRevision="receipt-1" realtimeConnected={false} />)
    await flush()
    expect(receiptCalls).toBe(before + 1)
    await advance(5000)
    expect(receiptCalls).toBe(before + 2)
    view.rerender(<MessagePanel projectId={1} projectStatus="IN_PROGRESS" receiptRevision="receipt-1" realtimeConnected />)
    await flush()
    const reconnected = receiptCalls
    await advance(10000)
    expect(receiptCalls).toBe(reconnected)
  })

  it('StrictMode focus wake refetches an unchanged visible receipt set once', async () => {
    vi.useFakeTimers()
    visibleIds.add(1)
    let receiptCalls = 0
    messageMocks.get.mockImplementation(async (url: string) => {
      if (url === '/projects/1/messages') return messagePage([messageRow(1)])
      receiptCalls += 1
      return { data: [{ id: 1, readCount: receiptCalls, totalCount: receiptCalls }] }
    })
    const view = render(
      <StrictMode>
        <MessagePanel projectId={1} projectStatus="IN_PROGRESS" active realtimeConnected />
      </StrictMode>,
    )
    await flush()
    await flush()
    expect(receiptCalls).toBeGreaterThan(0)
    const beforeFocus = receiptCalls

    fireEvent.focus(window)
    await flush()

    expect(receiptCalls).toBe(beforeFocus + 1)
    view.unmount()
  })

  it('message receipt popover distinguishes failure, retries, and ignores an older response', async () => {
    const pending: Array<ReturnType<typeof deferred<{ data: { readers: Array<{ realName: string }>; unread: never[] } }>>> = []
    messageMocks.get.mockImplementation(async () => {
      const request = deferred<{ data: { readers: Array<{ realName: string }>; unread: never[] } }>()
      pending.push(request)
      return request.promise
    })
    const view = render(<ReceiptBody id={7} />)
    await waitFor(() => expect(pending).toHaveLength(1))
    await act(async () => pending[0].reject(new Error('reads unavailable')))
    expect(await screen.findByText('回执加载失败')).toBeVisible()
    expect(screen.queryByText('暂无')).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: '重试' }))
    await waitFor(() => expect(pending).toHaveLength(2))
    view.rerender(<ReceiptBody id={8} />)
    await waitFor(() => expect(pending).toHaveLength(3))
    await act(async () => pending[1].resolve({ data: { readers: [{ realName: '旧回执' }], unread: [] } }))
    expect(screen.queryByText('旧回执')).not.toBeInTheDocument()
    await act(async () => pending[2].resolve({ data: { readers: [{ realName: '新回执' }], unread: [] } }))
    expect(await screen.findByText('新回执')).toBeVisible()
  })

  it('message detail replaces stale content with loading, failure and retry states for the requested message', async () => {
    const b = deferred<{ data: { readers: never[]; unread: never[] } }>()
    let bAttempts = 0
    const rows = [messageRow(1, { senderId: 9, senderName: '我', content: 'A' }), messageRow(2, { senderId: 9, senderName: '我', content: 'B' })]
    messageMocks.get.mockImplementation(async (url: string) => {
      if (url === '/projects/1/messages') return messagePage(rows)
      if (url === '/messages/1/reads') return { data: { readers: [{ userId: 11, realName: '读者 A', userType: 'INTERNAL' }], unread: [] } }
      bAttempts += 1
      if (bAttempts === 1) return b.promise
      return { data: { readers: [{ userId: 22, realName: '读者 B', userType: 'INTERNAL' }], unread: [] } }
    })
    const user = userEvent.setup()
    renderMessagePanel()
    const buttons = await screen.findAllByRole('button', { name: '回执详情' })
    await user.click(buttons[0])
    expect(await screen.findByText('读者 A')).toBeVisible()
    await user.click(buttons[1])
    expect(screen.queryByText('读者 A')).not.toBeInTheDocument()
    expect(screen.getByText('回执加载中…')).toBeVisible()
    await act(async () => b.reject(new Error('receipt unavailable')))
    expect(await screen.findByText('回执加载失败')).toBeVisible()
    await user.click(screen.getByRole('button', { name: '重试' }))
    expect(await screen.findByText('读者 B')).toBeVisible()
  })

  it('message detail ignores an older rejection and never reopens after close', async () => {
    const pending: ReceiptPending[] = []
    const rows = [messageRow(1, { senderId: 9, content: 'A' }), messageRow(2, { senderId: 9, content: 'B' })]
    messageMocks.get.mockImplementation(async (url: string) => {
      if (url === '/projects/1/messages') return messagePage(rows)
      const request = deferred<ReceiptPayload>()
      pending.push(request)
      return request.promise
    })
    const user = userEvent.setup()
    renderMessagePanel()
    const buttons = await screen.findAllByRole('button', { name: '回执详情' })
    await user.click(buttons[0])
    await user.click(buttons[1])
    await act(async () => pending[1].resolve({ data: { readers: [{ userId: 22, realName: '读者 B', userType: 'INTERNAL' }], unread: [] } }))
    await act(async () => pending[0].reject(new Error('late A failure')))
    expect(await screen.findByText('读者 B')).toBeVisible()

    await user.click(buttons[0])
    await waitFor(() => expect(pending).toHaveLength(3))
    closeDrawer()
    await act(async () => pending[2].reject(new Error('failure after close')))
    expect(screen.queryByText('回执加载失败')).not.toBeInTheDocument()
  })
})
