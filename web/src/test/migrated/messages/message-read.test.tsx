import { act, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import {
  deferred,
  messagePage,
  messageRow,
  renderMessagePanel,
  setMessagePermissions,
} from './harness'
import { messageMocks } from './mockState'
import MessagePanel from '../../../components/MessagePanel'

class IntersectionObserverMock implements IntersectionObserver {
  static instances: IntersectionObserverMock[] = []
  readonly root = null
  readonly rootMargin = '0px'
  readonly scrollMargin = '0px'
  readonly thresholds = [0.6]
  nodes: Element[] = []
  disconnected = false
  private readonly callback: IntersectionObserverCallback
  constructor(callback: IntersectionObserverCallback) {
    this.callback = callback
    IntersectionObserverMock.instances.push(this)
  }
  observe(node: Element) { this.nodes.push(node) }
  unobserve(node: Element) { this.nodes = this.nodes.filter(item => item !== node) }
  disconnect() { this.disconnected = true }
  takeRecords() { return [] }
  fire() {
    this.callback(this.nodes.map(target => ({
      target,
      isIntersecting: true,
      intersectionRatio: 0.6,
    } as IntersectionObserverEntry)), this)
  }
}

function activeObserver() {
  const observer = [...IntersectionObserverMock.instances]
    .reverse()
    .find(instance => !instance.disconnected && instance.nodes.length > 0)
  expect(observer, 'an unread marker must be observed').toBeDefined()
  return observer!
}

async function intersectUnread() {
  await act(async () => {
    activeObserver().fire()
    await Promise.resolve()
  })
}

describe('留言可见后标记已读', () => {
  beforeEach(() => {
    IntersectionObserverMock.instances = []
    vi.stubGlobal('IntersectionObserver', IntersectionObserverMock)
    setMessagePermissions()
    messageMocks.delete.mockResolvedValue({ data: {} })
  })

  it('mark-read refreshes the participant count from the authoritative receipt', async () => {
    messageMocks.get.mockImplementation(async (url: string) => url === '/projects/1/messages'
      ? messagePage([messageRow(7, { readByMe: false })])
      : { data: { readers: [{ userId: 9 }], unread: [] } })
    messageMocks.post.mockResolvedValue({ data: {} })
    const { container } = renderMessagePanel()
    await screen.findByText('留言 7')

    await intersectUnread()

    await waitFor(() => expect(messageMocks.post).toHaveBeenCalledTimes(1))
    expect(await screen.findByRole('button', { name: '查看留言回执：1/1' })).toBeVisible()
    expect(container.querySelector('[data-message-id="7"]')).toHaveAttribute('data-unread', 'false')
  })

  it('mark-read does not count a view-all reader who is not a project participant', async () => {
    messageMocks.get.mockImplementation(async (url: string) => url === '/projects/1/messages'
      ? messagePage([messageRow(7, { readByMe: false })])
      : { data: { readers: [], unread: [{ userId: 3 }] } })
    messageMocks.post.mockResolvedValue({ data: {} })
    const { container } = renderMessagePanel()
    await screen.findByText('留言 7')
    await intersectUnread()
    expect(await screen.findByRole('button', { name: '查看留言回执：0/1' })).toBeVisible()
    expect(container.querySelector('[data-message-id="7"]')).toHaveAttribute('data-unread', 'false')
  })

  it('a failed receipt refresh marks the message read without inventing a count', async () => {
    messageMocks.get.mockImplementation(async (url: string) => {
      if (url === '/projects/1/messages') return messagePage([messageRow(7, { readCount: 2, totalCount: 3, readByMe: false })])
      throw new Error('receipt temporarily unavailable')
    })
    messageMocks.post.mockResolvedValue({ data: {} })
    const { container } = renderMessagePanel()
    await screen.findByText('留言 7')
    await intersectUnread()
    await waitFor(() => expect(container.querySelector('[data-message-id="7"]')).toHaveAttribute('data-unread', 'false'))
    expect(screen.getByRole('button', { name: '查看留言回执：2/3' })).toBeVisible()
  })

  it('repeated visibility callbacks submit the same message only once', async () => {
    messageMocks.get.mockImplementation(async (url: string) => url === '/projects/1/messages'
      ? messagePage([messageRow(7, { readByMe: false })])
      : { data: { readers: [{ userId: 9 }], unread: [] } })
    messageMocks.post.mockResolvedValue({ data: {} })
    renderMessagePanel()
    await screen.findByText('留言 7')
    const observer = activeObserver()
    await act(async () => {
      observer.fire()
      observer.fire()
      await Promise.resolve()
    })
    await waitFor(() => expect(messageMocks.post).toHaveBeenCalledTimes(1))
  })

  it('a late receipt from the previous project cannot overwrite the current project', async () => {
    const oldReceipt = deferred<{ data: { readers: Array<{ userId: number }>; unread: never[] } }>()
    messageMocks.get.mockImplementation(async (url: string) => {
      if (url === '/projects/1/messages') return messagePage([messageRow(7, { readByMe: false })])
      if (url === '/projects/2/messages') return messagePage([messageRow(8, { projectId: 2, readCount: 0, totalCount: 2 })])
      if (url === '/messages/7/reads') return oldReceipt.promise
      throw new Error(`unexpected GET ${url}`)
    })
    messageMocks.post.mockResolvedValue({ data: {} })
    const view = renderMessagePanel()
    await screen.findByText('留言 7')
    await intersectUnread()
    await waitFor(() => expect(messageMocks.get).toHaveBeenCalledWith('/messages/7/reads'))

    view.rerender(<MessagePanel projectId={2} projectStatus="IN_PROGRESS" />)
    await screen.findByText('留言 8')
    await act(async () => oldReceipt.resolve({ data: { readers: [{ userId: 9 }], unread: [] } }))

    expect(screen.getByRole('button', { name: '查看留言回执：0/2' })).toBeVisible()
    expect(screen.queryByText('留言 7')).not.toBeInTheDocument()
  })

  it('a late receipt cannot pass an A-B-A project switch', async () => {
    const oldReceipt = deferred<{ data: { readers: Array<{ userId: number }>; unread: never[] } }>()
    let projectARequests = 0
    messageMocks.get.mockImplementation(async (url: string) => {
      if (url === '/projects/1/messages') {
        projectARequests += 1
        return projectARequests === 1
          ? messagePage([messageRow(7, { readByMe: false })])
          : messagePage([messageRow(7, { readCount: 0, totalCount: 2 })])
      }
      if (url === '/projects/2/messages') return messagePage([messageRow(8, { projectId: 2 })])
      if (url === '/messages/7/reads') return oldReceipt.promise
      throw new Error(`unexpected GET ${url}`)
    })
    messageMocks.post.mockResolvedValue({ data: {} })
    const view = renderMessagePanel()
    await screen.findByText('留言 7')
    await intersectUnread()
    await waitFor(() => expect(messageMocks.get).toHaveBeenCalledWith('/messages/7/reads'))

    view.rerender(<MessagePanel projectId={2} projectStatus="IN_PROGRESS" />)
    await screen.findByText('留言 8')
    view.rerender(<MessagePanel projectId={1} projectStatus="IN_PROGRESS" />)
    await screen.findByRole('button', { name: '查看留言回执：0/2' })
    await act(async () => oldReceipt.resolve({ data: { readers: [{ userId: 9 }], unread: [] } }))

    expect(projectARequests).toBe(2)
    expect(screen.getByRole('button', { name: '查看留言回执：0/2' })).toBeVisible()
  })

  it('receipt synchronization uses at most four concurrent requests', async () => {
    const ids = [1, 2, 3, 4, 5]
    const receipts = new Map(ids.map(id => [id, deferred<{ data: { readers: never[]; unread: never[] } }>()]))
    let active = 0
    let maximum = 0
    messageMocks.get.mockImplementation(async (url: string) => {
      if (url === '/projects/1/messages') return messagePage(ids.map(id => messageRow(id, { readByMe: false })))
      const id = Number(url.match(/^\/messages\/(\d+)\/reads$/)?.[1])
      active += 1
      maximum = Math.max(maximum, active)
      const response = await receipts.get(id)!.promise
      active -= 1
      return response
    })
    messageMocks.post.mockResolvedValue({ data: {} })
    renderMessagePanel()
    await screen.findByText('留言 5')

    await intersectUnread()
    await waitFor(() => expect(maximum).toBe(4))
    expect(messageMocks.get).not.toHaveBeenCalledWith('/messages/5/reads')
    await act(async () => {
      for (const id of [1, 2, 3, 4]) receipts.get(id)!.resolve({ data: { readers: [], unread: [] } })
      await Promise.resolve()
    })
    await waitFor(() => expect(messageMocks.get).toHaveBeenCalledWith('/messages/5/reads'))
    await act(async () => {
      receipts.get(5)!.resolve({ data: { readers: [], unread: [] } })
      await Promise.resolve()
      await Promise.resolve()
    })
    expect(maximum).toBe(4)
  })
})
