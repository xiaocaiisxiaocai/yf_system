import { QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import { createAppQueryClient } from '../api/queryClient'
import AnnouncementOrderDialog from '../announcements/AnnouncementOrderDialog'
import { MAX_ORDER, moveOrderRow, previewOrder } from '../announcements/announcementOrder'
import { announcementOrderApi, type OrderRow } from '../announcements/announcementOrderApi'

function row(id: number, sortOrder: number | null = null): OrderRow {
  return { id, title: `公告 ${id}`, departmentName: '研发部', publishedAt: '2026-11-28T03:00:00Z', pinnedAt: null, sortOrder, version: 1 }
}
const click = (name: string) => fireEvent.click(screen.getByRole('button', { name }))
const mount = (onSaved = vi.fn()) => render(<QueryClientProvider client={createAppQueryClient()}>
  <AnnouncementOrderDialog onClose={vi.fn()} onSaved={onSaved} />
</QueryClientProvider>)

afterEach(() => vi.restoreAllMocks())

it('自动、重复、最大顺序号及跨位移动均可由读者实际排序重现', () => {
  for (const ranks of [[null, null, null, null], [0, 0, 1, null], [10, 20, 30, 40], [MAX_ORDER, MAX_ORDER, null, null]]) {
    const rows = previewOrder(ranks.map((rank, i) => row(i + 1, rank)))
    for (let from = 0; from < rows.length; from++) {
      for (let to = 0; to < rows.length; to++) {
        const expected = rows.map(r => r.id)
        expected.splice(to, 0, ...expected.splice(from, 1))
        const moved = moveOrderRow(rows, rows[from].id, rows[to].id)
        expect(previewOrder(moved).map(r => r.id)).toEqual(expected)
        expect(moved.every(r => r.sortOrder === null || (r.sortOrder >= 0 && r.sortOrder <= MAX_ORDER))).toBe(true)
      }
    }
  }
})

it('第十条可移动到下一页，预览后才提交，版本冲突可重新载入', async () => {
  const list = Array.from({ length: 11 }, (_, i) => row(11 - i))
  const load = vi.spyOn(announcementOrderApi, 'get').mockResolvedValue({ revision: 'one', list })
  const save = vi.spyOn(announcementOrderApi, 'save').mockRejectedValueOnce({ response: { status: 409,
    data: { message: '公告顺序已变更' } } })
  mount()
  await screen.findByText('公告 2')
  click('公告 2下移')
  // The moved row is now on page 2 even though the dialog shows ten entries per page.
  expect(screen.getByText('公告 2')).toBeVisible()
  expect(screen.queryByText('公告 11')).not.toBeInTheDocument()
  click('预览排序')
  expect(screen.getByText('公告排序预览')).toBeVisible()
  expect(screen.getByText('公告 1')).toBeVisible()
  expect(screen.queryByText('公告 2')).not.toBeInTheDocument()
  expect(save).not.toHaveBeenCalled()
  click('保存排序')
  expect(await screen.findByRole('alert')).toHaveTextContent('公告顺序已变更')
  expect(save).toHaveBeenCalledWith(false, 'one', expect.any(Array))
  load.mockResolvedValue({ revision: 'two', list })
  click('刷新并重新排列')
  expect(await screen.findByText('调整公告顺序')).toBeVisible()
  expect(screen.getByRole('button', { name: '预览排序' })).toBeDisabled()
})

it('置顶预览使用新顺序号并只保存本次改动', async () => {
  const load = vi.spyOn(announcementOrderApi, 'get').mockImplementation(async pinned => ({ revision: pinned ? 'pin' : 'normal',
    list: [row(2), row(1)].map(r => ({ ...r, pinnedAt: pinned ? r.publishedAt : null })) }))
  const save = vi.spyOn(announcementOrderApi, 'save').mockResolvedValue(undefined)
  const onSaved = vi.fn()
  mount(onSaved)
  await screen.findByText('公告 1')
  fireEvent.click(screen.getByText('置顶公告'))
  await waitFor(() => expect(load).toHaveBeenCalledWith(true, expect.any(AbortSignal)))
  const rank = await screen.findByLabelText('公告 1顺序号')
  fireEvent.change(rank, { target: { value: '0' } })
  fireEvent.blur(rank)
  click('预览排序')
  expect(save).not.toHaveBeenCalled()
  click('保存排序')
  await waitFor(() => expect(onSaved).toHaveBeenCalledOnce())
  expect(save).toHaveBeenCalledWith(true, 'pin', [{ id: 1, sortOrder: 0 }])
})

it('从拖动手柄移到目标行后可预览并保存新顺序', async () => {
  const list = [row(3, 10), row(2, 20), row(1, 30)]
  vi.spyOn(announcementOrderApi, 'get').mockResolvedValue({ revision: 'drag', list })
  const save = vi.spyOn(announcementOrderApi, 'save').mockResolvedValue(undefined)
  mount()
  const source = await screen.findByRole('button', { name: '拖动公告 1' })
  const target = screen.getByText('公告 3').closest('tr')!
  fireEvent.dragStart(source, { dataTransfer: { effectAllowed: 'all', setData: vi.fn() } })
  fireEvent.dragOver(target)
  fireEvent.drop(target)
  click('预览排序')
  expect(screen.getAllByText(/^公告 [123]$/).map(e => e.textContent)).toEqual(['公告 1', '公告 3', '公告 2'])
  click('保存排序')
  await waitFor(() => expect(save).toHaveBeenCalledWith(false, 'drag', [{ id: 1, sortOrder: expect.any(Number) }]))
})
