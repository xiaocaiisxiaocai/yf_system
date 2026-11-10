import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import http from '../../api/client'
import { ItemHistoryDrawer, LatestDueCell } from '../../outsource/pages/ItemHistory'

const changes = [
  { id: 3, field: 'latestDueDate', label: '最新交期', oldValue: '2027-01-15', newValue: '2027-01-20', source: 'IPMS_SCHEDULED', actorName: null, changedAt: '2026-11-12T18:00:00Z' },
  { id: 2, field: 'status', label: '状态', oldValue: '正常', newValue: '暂停', source: 'EDIT', actorName: '采购乙', changedAt: '2026-11-11T02:00:00Z' },
  { id: 1, field: 'latestDueDate', label: '最新交期', oldValue: '2026-12-31', newValue: '2027-01-15', source: 'IPMS_MANUAL', actorName: '采购乙', changedAt: '2026-11-10T03:00:00Z' },
]

describe('外协清单变更履历', () => {
  afterEach(() => vi.restoreAllMocks())

  it('交期未变时只显示日期，不加载履历', () => {
    const get = vi.spyOn(http, 'get')
    render(<LatestDueCell item={{ id: 1, originalDueDate: '2026-12-31', latestDueDate: '2026-12-31' }} />)
    expect(screen.getByText('2026-12-31')).not.toHaveClass('outsource-due-changed')
    expect(get).not.toHaveBeenCalled()
  })

  it('交期变过时悬停显示从原始交期起的每次变化', async () => {
    const user = userEvent.setup()
    const get = vi.spyOn(http, 'get').mockResolvedValue({ data: changes } as never)
    render(<LatestDueCell item={{ id: 9, originalDueDate: '2026-12-31', latestDueDate: '2027-01-20' }} />)
    const cell = screen.getByText('2027-01-20')
    expect(cell).toHaveClass('outsource-due-changed')
    await user.hover(cell)
    const list = await screen.findByRole('list')
    expect(within(list).getAllByRole('listitem').map((item) => item.firstChild?.textContent))
      .toEqual(['2026-12-31', '2027-01-15', '2027-01-20'])
    expect(get).toHaveBeenCalledWith('/outsource/items/9/changes')
  })

  it('履历抽屉列出所有字段变动、来源与操作人', async () => {
    vi.spyOn(http, 'get').mockResolvedValue({ data: changes } as never)
    render(<ItemHistoryDrawer item={{ id: 9, workOrderNo: '21089901' }} onClose={() => undefined} />)
    expect(await screen.findByText('工令 21089901 · 变更履历')).toBeInTheDocument()
    const row = (await screen.findByText('暂停')).closest('tr')!
    expect(within(row).getByText('编辑')).toBeInTheDocument()
    expect(within(row).getByText('采购乙')).toBeInTheDocument()
    expect(screen.getByText('定时同步')).toBeInTheDocument()
    expect(screen.getByText('手动同步')).toBeInTheDocument()
  })
})
