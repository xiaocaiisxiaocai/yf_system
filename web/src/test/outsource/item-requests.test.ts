import { afterEach, describe, expect, it, vi } from 'vitest'
import http from '../../api/client'
import { exportOutsourceItems, listOutsourceItems } from '../../outsource/itemRequests'

describe('外发工令筛选的请求传输', () => {
  afterEach(() => vi.restoreAllMocks())

  it('短条件沿用 GET 列表和 Excel 导出', async () => {
    const get = vi.spyOn(http, 'get').mockResolvedValue({ data: [] })
    await listOutsourceItems({ page: 2, pageSize: 20, status: 'NORMAL' })
    await exportOutsourceItems({ status: 'NORMAL' })
    expect(get).toHaveBeenCalledWith('/outsource/items', { params: { page: 2, pageSize: 20, status: 'NORMAL' } })
    expect(get).toHaveBeenCalledWith('/outsource/items/export', {
      params: { status: 'NORMAL' }, responseType: 'blob', timeout: 0,
    })
  })

  it('多列中文百分号编码超过限额时使用同名 JSON 参数，保留分页、时区和 Excel 响应', async () => {
    const post = vi.spyOn(http, 'post').mockResolvedValue({ data: [] })
    const term = '研发'.repeat(30)
    const columnFilters = JSON.stringify({ remark: term, supervisor: term, machineModel: term, customer: term, importedAt: '2026-11-26' })
    const filters = { columnFilters, columnFilterTimeZone: 'Asia/Shanghai', vendorId: 8, workOrders: '甲\n乙', typeId: undefined }
    const body = { columnFilters, columnFilterTimeZone: 'Asia/Shanghai', vendorId: '8', workOrders: '甲\n乙' }
    await listOutsourceItems({ ...filters, page: 2, pageSize: 10 })
    await exportOutsourceItems(filters)
    expect(post).toHaveBeenCalledWith('/outsource/items/query', { ...body, page: '2', pageSize: '10' })
    expect(post).toHaveBeenCalledWith('/outsource/items/export', body, { responseType: 'blob', timeout: 0 })
  })
})
