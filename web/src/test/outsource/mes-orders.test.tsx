import { render, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import http from '../../api/client'
import MesOrdersPage from '../../outsource/pages/MesOrdersPage'

describe('IPMS 资料', () => {
  afterEach(() => vi.restoreAllMocks())

  it('分页与筛选放在请求体里发送，筛选条件再多也不会让地址过长', async () => {
    const post = vi.spyOn(http, 'post').mockResolvedValue({ data: { list: [], total: 0, page: 1, pageSize: 20 } } as never)
    const get = vi.spyOn(http, 'get')
    render(<MemoryRouter><MesOrdersPage /></MemoryRouter>)
    await waitFor(() => expect(post).toHaveBeenCalledWith('/outsource/mes-mirror/orders/query', { page: 1, pageSize: 20, filters: {} }))
    expect(get).not.toHaveBeenCalledWith(expect.stringContaining('/outsource/mes-mirror/orders'), expect.anything())
  })
})
