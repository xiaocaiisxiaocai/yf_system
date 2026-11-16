import { useCallback, useEffect, useRef, useState } from 'react'
import { Card, Empty } from '@arco-design/web-react'
import type { TableColumnProps } from '@arco-design/web-react'
import http from '../../api/client'
import type { ApiResponses } from '../../api/types'
import LoadError from '../../components/LoadError'
import ResizableTable from '../../components/ResizableTable'
import { isGroup, MES_CODE_TEXT, MES_FIXED_COLUMNS, MES_LAYOUT, type MesColumn } from '../mesColumns'
import './MesOrdersPage.css'

type Order = ApiResponses['GET /outsource/mes-mirror/orders']['list'][number]

const cell = (column: MesColumn) => (_: unknown, order: Order) => {
  const value = order.values[column.prop]
  if (value === null || value === undefined) return ''
  return MES_CODE_TEXT[column.prop]?.[value] ?? value
}

const leaf = (column: MesColumn, fixed?: 'left'): TableColumnProps<Order> => ({
  title: column.label, key: column.prop, width: column.width, render: cell(column), ...(fixed ? { fixed } : {}),
})

/** The IPMS MES monitoring table, filled from the mirrored data: same columns, same header groups. */
export default function MesOrdersPage() {
  const [orders, setOrders] = useState<Order[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(false)
  const sequence = useRef(0)

  const load = useCallback(async () => {
    const seq = ++sequence.current
    setLoading(true); setError(false)
    try {
      const response = await http.get<ApiResponses['GET /outsource/mes-mirror/orders']>('/outsource/mes-mirror/orders', { params: { page, pageSize } })
      if (seq !== sequence.current) return
      setOrders(response.data.list); setTotal(Number(response.data.total))
    } catch {
      if (seq === sequence.current) setError(true)
    } finally {
      if (seq === sequence.current) setLoading(false)
    }
  }, [page, pageSize])

  useEffect(() => {
    let active = true
    void Promise.resolve().then(() => { if (active) void load() })
    return () => { active = false; sequence.current += 1 }
  }, [load])

  const columns: TableColumnProps<Order>[] = [
    { title: '序号', key: 'seq', width: 72, fixed: 'left', render: (_, __, index) => (page - 1) * pageSize + index + 1 },
    ...MES_FIXED_COLUMNS.map((column) => leaf(column, 'left')),
    ...MES_LAYOUT.map((item): TableColumnProps<Order> => isGroup(item)
      ? { title: item.label, key: item.label, children: item.columns.map((column) => leaf(column)) }
      : leaf(item)),
  ]

  return <Card className="page-card page-card--table outsource-ipms-data-page">
    <h1 className="visually-hidden">IPMS 资料</h1>
    {error ? <LoadError message="IPMS 资料加载失败" onRetry={() => void load()} />
      : <ResizableTable<Order> tableId="outsource-ipms-orders" className="page-table" rowKey="workOrderNo" loading={loading} data={orders}
        columns={columns} noDataElement={loading ? <span /> : <Empty description="暂无资料" />} scroll={{ y: 'var(--page-table-scroll-y)' }}
        border={{ wrapper: true, headerCell: true, bodyCell: true, cell: true }}
        pagination={{ current: page, pageSize, total, onChange: (next, size) => { setPage(size === pageSize ? next : 1); setPageSize(size) } }} />}
  </Card>
}
