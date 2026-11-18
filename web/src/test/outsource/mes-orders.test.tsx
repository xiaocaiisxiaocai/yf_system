import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import http from '../../api/client'
import MesOrdersPage from '../../outsource/pages/MesOrdersPage'

describe('IPMS 资料', () => {
  afterEach(() => vi.restoreAllMocks())

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
