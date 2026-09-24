import { act, fireEvent, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('../../../api/client', async () => {
  const state = (await import('./mockState')).messageMocks
  return { default: { get: state.get, post: state.post, delete: state.delete } }
})

vi.mock('../../../store/auth', async () => {
  const state = await import('./mockState')
  return { useAuth: state.selectMessageAuthState }
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

function sendButton() {
  return screen.getByRole('button', { name: /发送/ })
}

function loadMoreButton() {
  return screen.queryByRole('button', { name: /加载更多/ })
}

async function runDeletingLoadedMessages(deleteCount: number) {
  setMessagePermissions('message:delete_any')
  let rows = Array.from({ length: 25 }, (_, index) => messageRow(25 - index, { content: `message ${25 - index}` }))
  messageMocks.get.mockImplementation(async (_url: string, { params }: { params: { beforeId?: number; page: number } }) => {
    const beforeId = params.beforeId
    const list = beforeId
      ? rows.filter(row => row.id < beforeId).slice(0, 20)
      : rows.slice((params.page - 1) * 20, params.page * 20)
    return messagePage(list, rows.length)
  })
  messageMocks.delete.mockImplementation(async (url: string) => {
    rows = rows.filter(row => row.id !== Number(url.split('/').pop()))
    return { data: {} }
  })
  const user = userEvent.setup()
  const view = renderMessagePanel()
  await screen.findByText('message 25')
  for (let index = 0; index < deleteCount; index += 1) {
    await user.click(screen.getAllByRole('button', { name: /删除/ })[0])
    fireEvent.click(screen.getByRole('button', { name: '确定' }))
    await waitFor(() => expect(messageMocks.delete).toHaveBeenCalledTimes(index + 1))
  }
  expect(loadMoreButton()).toBeVisible()
  await user.click(loadMoreButton()!)
  expect(renderedMessageIds(view.container)).toEqual(rows.map(row => row.id))
}

describe('留言撰写、发送与分页', () => {
  beforeEach(() => {
    setMessagePermissions()
    messageMocks.delete.mockResolvedValue({ data: {} })
    vi.stubGlobal('IntersectionObserver', undefined)
    vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:message-image')
    vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined)
  })

  it('message composer keeps input typed during first mount but clears it when the project changes', async () => {
    setMessagePermissions('message:create')
    messageMocks.get.mockResolvedValue(messagePage([]))
    messageMocks.post.mockResolvedValue({ data: messageRow(1) })
    const user = userEvent.setup()
    const view = renderMessagePanel()
    await screen.findByText('暂无留言')
    await user.type(textbox(), '挂载时立即输入的草稿')
    expect(textbox()).toHaveValue('挂载时立即输入的草稿')

    view.rerender(<MessagePanel projectId={2} projectStatus="IN_PROGRESS" />)
    await waitFor(() => expect(textbox()).toHaveValue(''))
  })

  it('confirmed message response appears immediately without refetching or dropping loaded history', async () => {
    setMessagePermissions('message:create')
    const rows = [messageRow(3), messageRow(2), messageRow(1)]
    messageMocks.get.mockResolvedValue(messagePage(rows))
    messageMocks.post.mockResolvedValue({ data: messageRow(4, { senderId: 9, senderName: '我', content: '服务端已确认' }) })
    const user = userEvent.setup()
    const { container } = renderMessagePanel({ revision: 'r1' })
    await screen.findByText('留言 3')
    await user.type(textbox(), '服务端已确认')
    await user.click(sendButton())

    await waitFor(() => expect(renderedMessageIds(container)).toEqual([4, 3, 2, 1]))
    expect(messageMocks.get).toHaveBeenCalledTimes(1)
    expect(screen.getByRole('heading', { name: '协作留言（4）' })).toBeVisible()
  })

  it('sending from a notification keeps the full list and inserts the confirmed message', async () => {
    setMessagePermissions('message:create')
    messageMocks.get.mockResolvedValue(messagePage([messageRow(9)]))
    messageMocks.post.mockImplementation(async (_url: string, body: { content: string }) => ({
      data: messageRow(10, { content: body.content }),
    }))
    const user = userEvent.setup()
    const { container } = renderMessagePanel({ targetId: 9, revision: 'r1' })
    await screen.findByText('留言 9')
    await user.type(textbox(), '离开定位后查看')
    await user.click(sendButton())

    expect(messageMocks.post).toHaveBeenCalledTimes(1)
    expect(messageMocks.get).toHaveBeenCalledTimes(1)
    expect(messageMocks.get.mock.calls[0][1].params.targetId).toBeUndefined()
    expect(renderedMessageIds(container)).toEqual([10, 9])
  })

  it('sending preserves later edits and rejects rapid duplicate clicks before rerender', async () => {
    setMessagePermissions('message:create')
    const post = deferred<{ data: ReturnType<typeof messageRow> }>()
    messageMocks.get.mockResolvedValue(messagePage([messageRow(1)]))
    messageMocks.post.mockReturnValue(post.promise)
    const user = userEvent.setup()
    const { container } = renderMessagePanel({ revision: 'r1' })
    await screen.findByText('留言 1')
    await user.type(textbox(), '第一版草稿')
    fireEvent.click(sendButton())
    fireEvent.click(sendButton())
    expect(messageMocks.post).toHaveBeenCalledTimes(1)

    fireEvent.change(textbox(), { target: { value: '发送期间继续编辑' } })
    await act(async () => post.resolve({ data: messageRow(2, { senderId: 9, senderName: '我', content: '第一版草稿' }) }))
    expect(textbox()).toHaveValue('发送期间继续编辑')
    expect(renderedMessageIds(container)).toEqual([2, 1])
  })

  it('completed and terminated projects hide message delete controls', async () => {
    setMessagePermissions('message:delete_any')
    messageMocks.get.mockResolvedValue(messagePage([messageRow(1, { content: '历史留言' })]))
    const view = renderMessagePanel({ projectStatus: 'COMPLETED' })
    await screen.findByText('历史留言')
    expect(screen.queryByRole('button', { name: /删除/ })).not.toBeInTheDocument()

    view.rerender(<MessagePanel projectId={1} projectStatus="TERMINATED" />)
    expect(screen.queryByRole('button', { name: /删除/ })).not.toBeInTheDocument()
    view.rerender(<MessagePanel projectId={1} projectStatus="IN_PROGRESS" />)
    expect(await screen.findByRole('button', { name: /删除/ })).toBeVisible()
  })

  it('handles a failed message deletion and permits a later retry', async () => {
    setMessagePermissions('message:delete_any')
    messageMocks.get.mockResolvedValue(messagePage([messageRow(1, { content: '保留的留言' })]))
    messageMocks.delete.mockRejectedValueOnce(new Error('delete failed')).mockResolvedValueOnce({ data: {} })
    const user = userEvent.setup()
    renderMessagePanel()
    await screen.findByText('保留的留言')

    await user.click(screen.getByRole('button', { name: /删除/ }))
    fireEvent.click(screen.getByRole('button', { name: '确定' }))
    await waitFor(() => expect(messageMocks.delete).toHaveBeenCalledTimes(1))
    expect(screen.getByText('保留的留言')).toBeVisible()

    await user.click(screen.getByRole('button', { name: /删除/ }))
    fireEvent.click(screen.getByRole('button', { name: '确定' }))
    await waitFor(() => expect(screen.queryByText('保留的留言')).not.toBeInTheDocument())
  })

  it('deleting 1 loaded messages keeps older messages reachable', async () => {
    await runDeletingLoadedMessages(1)
  })

  it('deleting 20 loaded messages keeps older messages reachable', async () => {
    await runDeletingLoadedMessages(20)
  })

  it('a failed second message page can be retried without losing or duplicating messages', async () => {
    const rows = Array.from({ length: 41 }, (_, index) => messageRow(41 - index, { content: `message ${41 - index}` }))
    let appendFailures = 2
    messageMocks.get.mockImplementation(async (_url: string, { params }: { params: { beforeId?: number } }) => {
      const beforeId = params.beforeId
      if (beforeId && appendFailures > 0) {
        appendFailures -= 1
        throw new Error('transient page failure')
      }
      const list = beforeId ? rows.filter(row => row.id < beforeId).slice(0, 20) : rows.slice(0, 20)
      return messagePage(list, rows.length)
    })
    const user = userEvent.setup()
    const { container } = renderMessagePanel()
    await screen.findByText('message 41')
    await user.click(loadMoreButton()!)
    expect(renderedMessageIds(container)).toEqual(rows.slice(0, 20).map(row => row.id))
    await user.click(screen.getByRole('button', { name: '重试' }))
    expect(renderedMessageIds(container)).toEqual(rows.slice(0, 20).map(row => row.id))
    await user.click(screen.getByRole('button', { name: '重试' }))
    expect(screen.queryByRole('button', { name: '重试' })).not.toBeInTheDocument()
    await user.click(loadMoreButton()!)
    expect(new Set(renderedMessageIds(container)).size).toBe(41)
    expect(renderedMessageIds(container)).toEqual(rows.map(row => row.id))
  })

  it('message pagination stops at the response total for exact 20 and 40 item totals', async () => {
    for (const total of [20, 40]) {
      let calls = 0
      messageMocks.get.mockImplementation(async () => {
        calls += 1
        return messagePage(
          Array.from({ length: 20 }, (_, offset) => messageRow((calls - 1) * 20 + offset + 1)),
          total,
        )
      })
      const user = userEvent.setup()
      const view = renderMessagePanel({ projectId: total })
      await screen.findByText('留言 1')
      if (total === 20) {
        expect(loadMoreButton()).not.toBeInTheDocument()
      } else {
        await user.click(loadMoreButton()!)
        await waitFor(() => expect(loadMoreButton()).not.toBeInTheDocument())
      }
      view.unmount()
      messageMocks.get.mockReset()
    }
  })

  it('message cursor pagination keeps loading after concurrent deletion shrinks total', async () => {
    const pages = [
      { list: Array.from({ length: 20 }, (_, offset) => messageRow(offset + 1)), total: 41 },
      { list: Array.from({ length: 20 }, (_, offset) => messageRow(offset + 21)), total: 31 },
      { list: [messageRow(41)], total: 31 },
    ]
    let call = 0
    messageMocks.get.mockImplementation(async () => messagePage(pages[call].list, pages[call++].total))
    const user = userEvent.setup()
    const { container } = renderMessagePanel()
    await screen.findByText('留言 1')
    await user.click(loadMoreButton()!)
    expect(loadMoreButton()).toBeVisible()
    await user.click(loadMoreButton()!)
    expect(renderedMessageIds(container)).toHaveLength(41)
    expect(loadMoreButton()).not.toBeInTheDocument()
  })

  it('message composition counts Unicode code points and sends the complete 4000 character boundary', async () => {
    setMessagePermissions('message:create')
    messageMocks.get.mockResolvedValue(messagePage([]))
    messageMocks.post.mockImplementation(async (_url: string, body: { content: string }) => ({ data: messageRow(1, { content: body.content }) }))
    renderMessagePanel()
    await screen.findByText('暂无留言')
    const text = '中'.repeat(3998) + '🙂🙂'
    fireEvent.change(textbox(), { target: { value: `${text}超` } })
    expect(textbox()).toHaveValue(text)
    fireEvent.click(sendButton())
    await waitFor(() => expect(messageMocks.post).toHaveBeenCalledTimes(1))
    expect(messageMocks.post.mock.calls[0][1]).toEqual({ content: text })
    await waitFor(() => expect(textbox()).toHaveValue(''))
  })

  it('message send failure is handled, preserves the draft, and allows a successful retry', async () => {
    setMessagePermissions('message:create')
    let fail = true
    messageMocks.get.mockResolvedValue(messagePage([]))
    messageMocks.post.mockImplementation(async (_url: string, body: { content: string }) => {
      if (fail) throw new Error('service unavailable')
      return { data: messageRow(1, { content: body.content }) }
    })
    const user = userEvent.setup()
    renderMessagePanel()
    await screen.findByText('暂无留言')
    fireEvent.change(textbox(), { target: { value: '  保留重试的留言  ' } })
    await user.click(sendButton())
    expect(textbox()).toHaveValue('  保留重试的留言  ')
    expect(sendButton()).not.toBeDisabled()
    fail = false
    await user.click(sendButton())
    expect(messageMocks.post.mock.calls[1][1]).toEqual({ content: '保留重试的留言' })
    expect(textbox()).toHaveValue('')
  })

  it('message images submit atomically, retain failed drafts and suppress duplicate sends', async () => {
    setMessagePermissions('message:create')
    const first = deferred<{ data: ReturnType<typeof messageRow> }>()
    const second = deferred<{ data: ReturnType<typeof messageRow> }>()
    messageMocks.get.mockImplementation(async (url: string) => {
      if (url.includes('/images/')) return { data: new Blob(['image']) }
      return messagePage([])
    })
    messageMocks.post.mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise)
    const view = renderMessagePanel()
    await screen.findByText('暂无留言')
    const image = new File(['image fixture'], 'screenshot.png', { type: 'image/png' })
    const fileInput = view.container.querySelector<HTMLInputElement>('input[type="file"]')!
    fireEvent.change(fileInput, { target: { files: [image] } })
    expect(sendButton()).not.toBeDisabled()
    fireEvent.click(sendButton())
    fireEvent.click(sendButton())
    expect(messageMocks.post).toHaveBeenCalledTimes(1)
    const body = messageMocks.post.mock.calls[0][1] as FormData
    expect(body).toBeInstanceOf(FormData)
    expect(body.get('content')).toBe('')
    expect((body.get('images') as File).name).toBe('screenshot.png')
    expect(fileInput).toBeDisabled()

    await act(async () => first.reject(new Error('upload failed')))
    expect(screen.getByLabelText('已添加 1 张图片')).toBeVisible()
    fireEvent.click(sendButton())
    await act(async () => second.resolve({
      data: messageRow(10, { content: '', images: [{ id: 2, name: 'screenshot.png', sizeBytes: 13, mimeType: 'image/png' }] }),
    }))
    await waitFor(() => expect(screen.queryByLabelText('已添加 1 张图片')).not.toBeInTheDocument())
    expect(screen.getByLabelText('留言图片，共 1 张')).toBeVisible()

    fireEvent.change(fileInput, { target: { files: [image] } })
    view.rerender(<MessagePanel projectId={2} projectStatus="IN_PROGRESS" />)
    await waitFor(() => expect(screen.queryByLabelText('已添加 1 张图片')).not.toBeInTheDocument())
  })
})
