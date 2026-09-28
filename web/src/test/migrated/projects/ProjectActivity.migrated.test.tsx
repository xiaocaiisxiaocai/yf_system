import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { deferred } from './testUtils'

const mocks = vi.hoisted(() => ({ get: vi.fn() }))

vi.mock('../../../api/client', () => ({ default: { get: mocks.get } }))

import ProjectActivityPanel from '../../../components/ProjectActivityPanel'

function row(id: number, type: 'PROJECT' | 'FILE' | 'MESSAGE' = 'FILE', overrides = {}) {
  return {
    id,
    type,
    action: type === 'PROJECT' ? 'UPDATE' : 'CREATE',
    actorName: '测试用户',
    occurredAt: `2026-09-09T00:00:0${id}Z`,
    title: type === 'PROJECT' ? '更新项目' : `${type}对象`,
    summary: type === 'PROJECT' ? '项目摘要' : `${type}对象`,
    targetId: id,
    targetAvailable: type !== 'PROJECT',
    ...overrides,
  }
}

function response(list: unknown[], nextCursor: string | null = null, status = 'IN_PROGRESS') {
  return {
    data: {
      list,
      nextCursor,
      summary: {
        status,
        pendingConfirmation: status === 'IN_PROGRESS',
        confirmSide: status === 'IN_PROGRESS' ? 'COMPANY' : null,
        lastActivityAt: '2026-09-09T00:00:01Z',
      },
    },
  }
}

describe('ProjectActivityPanel migrated behavior', () => {
  beforeEach(() => { mocks.get.mockReset() })

  it('project activity waits for its tab, filters, refreshes and ignores late responses', async () => {
    const requests: Array<{ params: Record<string, unknown>; request: ReturnType<typeof deferred<ReturnType<typeof response>>> }> = []
    mocks.get.mockImplementation((_url: string, config: { params: Record<string, unknown> } = { params: {} }) => {
      const request = deferred<ReturnType<typeof response>>()
      requests.push({ params: config.params, request })
      return request.promise
    })
    const user = userEvent.setup()
    const { rerender } = render(<ProjectActivityPanel projectId={1} active={false} />)
    expect(mocks.get).not.toHaveBeenCalled()

    rerender(<ProjectActivityPanel projectId={1} active />)
    await waitFor(() => expect(requests).toHaveLength(1))
    expect(requests[0].params).toEqual({ pageSize: 20 })

    const filter = screen.getByRole('combobox', { name: '动态类型' })
    await user.click(filter)
    fireEvent.click(await screen.findByRole('option', { name: '项目' }))
    await waitFor(() => expect(requests).toHaveLength(2))
    expect(requests[1].params).toEqual({ pageSize: 20, type: 'PROJECT' })
    requests[1].request.resolve(response([row(2, 'PROJECT', { summary: '<驳回原因>', action: 'REJECT' })]))
    requests[0].request.resolve(response([row(1)], 'late-cursor'))

    expect(await screen.findByText('<驳回原因>')).toBeInTheDocument()
    expect(screen.queryByText('FILE对象')).not.toBeInTheDocument()
    expect(document.querySelector('[data-activity-id="2"] time')).toHaveAttribute('datetime', '2026-09-09T00:00:02Z')

    const refresh = screen.getByRole('button', { name: '刷新' })
    fireEvent.click(refresh)
    fireEvent.click(refresh)
    await waitFor(() => expect(requests).toHaveLength(3))
    requests[2].request.resolve(response([row(3, 'PROJECT', { summary: null })]))
    await waitFor(() => expect(document.querySelector('[data-activity-id="3"]')).toBeInTheDocument())
    expect(document.querySelector('[data-activity-id="3"] .project-activity-target')).toBeNull()

    rerender(<ProjectActivityPanel projectId={2} active />)
    await waitFor(() => expect(requests).toHaveLength(4))
    requests[3].request.resolve(response([row(4)]))
    await waitFor(() => expect(document.querySelector('[data-activity-id="4"]')).toBeInTheDocument())
  })

  it('completed legacy supplier acceptance remains readable without appearing as pending internal acceptance', async () => {
    mocks.get.mockResolvedValue({
      data: {
        list: [row(6, 'PROJECT', { action: 'CONFIRM' })],
        nextCursor: null,
        summary: { status: 'COMPLETED', pendingConfirmation: false, confirmSide: 'SUPPLIER', lastActivityAt: null },
      },
    })
    render(<ProjectActivityPanel projectId={1} />)

    expect(await screen.findByText('验收通过')).toBeInTheDocument()
    expect(screen.getByText('无待验收申请')).toBeInTheDocument()
    expect(screen.queryByText('项目动态加载失败')).not.toBeInTheDocument()
  })

  it('project activity exposes malformed initial responses as a retryable failure', async () => {
    mocks.get
      .mockResolvedValueOnce({ data: { list: [] } })
      .mockResolvedValueOnce(response([row(6)]))
    const user = userEvent.setup()
    render(<ProjectActivityPanel projectId={1} />)

    expect(await screen.findByText('项目动态加载失败')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: '重试' }))
    await waitFor(() => expect(document.querySelector('[data-activity-id="6"]')).toBeInTheDocument())
    expect(mocks.get).toHaveBeenCalledTimes(2)
  })

  it('project activity keeps loaded rows when an append fails, retries the cursor and exposes target navigation', async () => {
    let appendFailures = 1
    mocks.get.mockImplementation((_url: string, config: { params: Record<string, unknown> } = { params: {} }) => {
      if (config.params.cursor && appendFailures-- > 0) return Promise.reject(new Error('temporary failure'))
      return Promise.resolve(response(config.params.cursor
        ? [
          row(2, 'FILE', { targetAvailable: false, summary: '图纸.pdf' }),
          row(3, 'PROJECT', { summary: null }),
          row(4, 'MESSAGE', { targetAvailable: true, summary: '留言内容' }),
        ]
        : [row(1, 'PROJECT', { summary: '驳回原因：需要补充资料', action: 'REJECT' })], config.params.cursor ? null : 'cursor-1'))
    })
    const navigated = vi.fn()
    const user = userEvent.setup()
    render(<ProjectActivityPanel projectId={1} onNavigate={navigated} />)

    await user.click(await screen.findByRole('button', { name: '加载更多' }))
    expect(await screen.findByText('加载失败')).toBeInTheDocument()
    expect(document.querySelector('[data-activity-id="1"]')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: '重试' }))

    expect(await screen.findByRole('button', { name: '查看留言内容' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '查看图纸.pdf' })).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: '查看留言内容' }))
    expect(navigated).toHaveBeenCalledWith('MESSAGE', 4)
    expect(mocks.get.mock.calls[1][1].params).toEqual({ pageSize: 20, cursor: 'cursor-1' })
    expect(mocks.get.mock.calls[2][1].params).toEqual({ pageSize: 20, cursor: 'cursor-1' })
  })

  it('re-entering the tab keeps the loaded rows, aborts hidden requests and keeps rows on a quiet refresh failure', async () => {
    const configs: Array<{ signal?: AbortSignal; quietNetworkError?: boolean }> = []
    let call = 0
    const second = deferred<ReturnType<typeof response>>()
    mocks.get.mockImplementation((_url: string, config: { signal?: AbortSignal; quietNetworkError?: boolean }) => {
      configs.push(config)
      call += 1
      if (call === 1) return Promise.resolve(response([row(1)]))
      if (call === 2) return second.promise
      return Promise.reject(Object.assign(new Error('offline'), { isAxiosError: true, code: 'ERR_NETWORK' }))
    })
    const { rerender } = render(<ProjectActivityPanel projectId={1} active />)
    await waitFor(() => expect(document.querySelector('[data-activity-id="1"]')).toBeInTheDocument())
    expect(configs[0].quietNetworkError).toBe(true)

    rerender(<ProjectActivityPanel projectId={1} active={false} />)
    rerender(<ProjectActivityPanel projectId={1} active />)
    await waitFor(() => expect(configs).toHaveLength(2))
    // 重新进入时旧列表保持可见，不闪成空白。
    expect(document.querySelector('[data-activity-id="1"]')).toBeInTheDocument()
    rerender(<ProjectActivityPanel projectId={1} active={false} />)
    expect(configs[1].signal?.aborted).toBe(true)

    rerender(<ProjectActivityPanel projectId={1} active />)
    expect(await screen.findByText('项目动态刷新失败，当前显示上次数据。')).toBeInTheDocument()
    expect(document.querySelector('[data-activity-id="1"]')).toBeInTheDocument()
    expect(screen.queryByText('项目动态加载失败')).not.toBeInTheDocument()
  })
})
