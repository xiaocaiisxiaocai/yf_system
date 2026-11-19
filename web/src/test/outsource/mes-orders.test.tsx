import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import http from '../../api/client'
import MesOrdersPage from '../../outsource/pages/MesOrdersPage'

const mirrorIdle = {
  phase: 'IDLE', runMode: null, page: null, pageSize: 100, fetched: 0, pages: 0, total: null, pendingReason: null, startedAt: null,
  lastFinishedAt: null, lastSuccessAt: null, lastError: null, lastTrigger: 'SCHEDULED', lastCount: 1794, mirrorCount: 1794, preview: [],
}
const mirrorRunning = { ...mirrorIdle, phase: 'RUNNING', runMode: 'ALL', fetched: 200, pages: 2, total: 1794, lastTrigger: 'MANUAL' }

describe('IPMS 资料', () => {
  afterEach(() => { vi.useRealTimers(); vi.restoreAllMocks() })

  it('同步按钮发起全部抓取，进行中显示进度，完成后重新加载列表', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    let mirror: unknown = mirrorIdle
    vi.spyOn(http, 'get').mockImplementation(() => Promise.resolve({ data: mirror } as never))
    const post = vi.spyOn(http, 'post').mockImplementation((url: string) => Promise.resolve({
      data: url.endsWith('/run') ? mirrorRunning : { list: [], total: 0, page: 1, pageSize: 20 },
    } as never))
    render(<MemoryRouter><MesOrdersPage /></MemoryRouter>)
    await waitFor(() => expect(screen.getByRole('button', { name: /^同步$/ })).toBeEnabled())

    fireEvent.click(screen.getByRole('button', { name: /^同步$/ }))
    await waitFor(() => expect(post).toHaveBeenCalledWith('/outsource/mes-mirror/run', { mode: 'ALL' }))
    expect(await screen.findByRole('button', { name: /同步中 200 \/ 1794/ })).toBeInTheDocument()

    const queries = () => post.mock.calls.filter(([url]) => url === '/outsource/mes-mirror/orders/query').length
    const before = queries()
    mirror = { ...mirrorIdle, lastTrigger: 'MANUAL', mirrorCount: 1800 }
    await vi.advanceTimersByTimeAsync(3000)
    await waitFor(() => expect(queries()).toBe(before + 1))
    expect(screen.getByRole('button', { name: /^同步$/ })).toBeEnabled()
  })

  it('定时抓取进行中打开页面时显示进度，不能再发起同步', async () => {
    vi.spyOn(http, 'get').mockResolvedValue({ data: { ...mirrorRunning, lastTrigger: 'SCHEDULED' } } as never)
    const post = vi.spyOn(http, 'post').mockResolvedValue({ data: { list: [], total: 0, page: 1, pageSize: 20 } } as never)
    render(<MemoryRouter><MesOrdersPage /></MemoryRouter>)
    const button = await screen.findByRole('button', { name: /同步中 200 \/ 1794/ })
    fireEvent.click(button)
    expect(post).not.toHaveBeenCalledWith('/outsource/mes-mirror/run', expect.anything())
  })

  it('分页与筛选放在请求体里发送，筛选条件再多也不会让地址过长，且顶部显示已启用筛选并可一键清空', async () => {
    const post = vi.spyOn(http, 'post').mockResolvedValue({ data: { list: [], total: 0, page: 1, pageSize: 20 } } as never)
    const get = vi.spyOn(http, 'get')
    const view = render(<MemoryRouter><MesOrdersPage /></MemoryRouter>)
    await waitFor(() => expect(post).toHaveBeenCalledWith('/outsource/mes-mirror/orders/query', { page: 1, pageSize: 20, filters: {} }))
    expect(get).not.toHaveBeenCalledWith(expect.stringContaining('/outsource/mes-mirror/orders'), expect.anything())

    const filterTriggers = view.container.querySelectorAll<HTMLElement>('.arco-table-filters')
    expect(filterTriggers.length).toBeGreaterThan(0)
    fireEvent.click(filterTriggers[0])
    const input = await screen.findByPlaceholderText('筛选客户名称，可粘贴多个')
    fireEvent.change(input, { target: { value: '超穎' } })
    fireEvent.click(screen.getByRole('button', { name: '筛选' }))

    await waitFor(() => expect(post).toHaveBeenCalledWith('/outsource/mes-mirror/orders/query', {
      page: 1, pageSize: 20, filters: { customerName: '超穎' },
    }))
    expect(screen.getByRole('region', { name: '已启用筛选' })).toHaveTextContent('客户名称：超穎')

    fireEvent.click(screen.getByRole('button', { name: '清空筛选' }))
    await waitFor(() => expect(post).toHaveBeenLastCalledWith('/outsource/mes-mirror/orders/query', {
      page: 1, pageSize: 20, filters: {},
    }))
    expect(screen.queryByRole('region', { name: '已启用筛选' })).not.toBeInTheDocument()
  })

  it('粘贴多行筛选时每一行都是一个完整词，标签按服务端同样的切词规则展示', async () => {
    const post = vi.spyOn(http, 'post').mockResolvedValue({ data: { list: [], total: 0, page: 1, pageSize: 20 } } as never)
    const view = render(<MemoryRouter><MesOrdersPage /></MemoryRouter>)
    await waitFor(() => expect(post).toHaveBeenCalled())

    fireEvent.click(view.container.querySelectorAll<HTMLElement>('.arco-table-filters')[0])
    const input = await screen.findByPlaceholderText('筛选客户名称，可粘贴多个')
    fireEvent.change(input, { target: { value: 'Peng Shen Technology (Thailand)\n友達光電' } })
    fireEvent.click(screen.getByRole('button', { name: '筛选' }))

    await waitFor(() => expect(post).toHaveBeenCalledWith('/outsource/mes-mirror/orders/query', {
      page: 1, pageSize: 20, filters: { customerName: 'Peng Shen Technology (Thailand)\n友達光電' },
    }))
    // 名字里的空格不会被拆成多个条件。
    expect(screen.getByRole('region', { name: '已启用筛选' }))
      .toHaveTextContent('客户名称：Peng Shen Technology (Thailand)、友達光電')
  })
})
