import { useCallback, useEffect, useRef, useState } from 'react'
import { Card, Drawer, Empty, Input, Space } from '@arco-design/web-react'
import type { TableColumnProps } from '@arco-design/web-react'
import http from '../../api/client'
import { fmtTime, type ApiResponses } from '../../api/types'
import LoadError from '../../components/LoadError'
import ResizableTable from '../../components/ResizableTable'
import './MesOrdersPage.css'

type Order = ApiResponses['GET /outsource/mes-mirror/orders']['list'][number]
type Detail = ApiResponses['GET /outsource/mes-mirror/orders/{workOrderNo}']
type Field = Detail['fields'][number]

const FIELD_COLUMNS: TableColumnProps<Field>[] = [
  { title: '字段', dataIndex: 'key', width: 220, ellipsis: true },
  { title: '值', dataIndex: 'value', render: (value: string | null) => value ?? '' },
]

/** Every work order copied from IPMS, with all fields MES returned; a partial fetch only updates the rows it fetched. */
export default function MesOrdersPage() {
  const [orders, setOrders] = useState<Order[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [keyword, setKeyword] = useState('')
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(false)
  const [detail, setDetail] = useState<Detail | null>(null)
  const [detailLoading, setDetailLoading] = useState(false)
  const sequence = useRef(0)

  const load = useCallback(async () => {
    const seq = ++sequence.current
    setLoading(true); setError(false)
    try {
      const response = await http.get<ApiResponses['GET /outsource/mes-mirror/orders']>('/outsource/mes-mirror/orders', {
        params: { page, pageSize, keyword: keyword.trim() || undefined },
      })
      if (seq !== sequence.current) return
      setOrders(response.data.list); setTotal(Number(response.data.total))
    } catch {
      if (seq === sequence.current) setError(true)
    } finally {
      if (seq === sequence.current) setLoading(false)
    }
  }, [page, pageSize, keyword])

  useEffect(() => {
    let active = true
    void Promise.resolve().then(() => { if (active) void load() })
    return () => { active = false; sequence.current += 1 }
  }, [load])

  const open = async (order: Order) => {
    setDetailLoading(true); setDetail(null)
    try {
      const response = await http.get<ApiResponses['GET /outsource/mes-mirror/orders/{workOrderNo}']>(
        `/outsource/mes-mirror/orders/${encodeURIComponent(order.workOrderNo)}`)
      setDetail(response.data)
    } catch { /* 统一请求层提示 */ } finally { setDetailLoading(false) }
  }

  const columns: TableColumnProps<Order>[] = [
    {
      title: '工令', dataIndex: 'workOrderNo', width: 120,
      render: (value: string, order) => <a href="#" onClick={(event) => { event.preventDefault(); void open(order) }}>{value}</a>,
    },
    { title: '客户', dataIndex: 'customerName', width: 260, ellipsis: true },
    { title: '机型', dataIndex: 'deviceModels', width: 200, ellipsis: true },
    { title: '制程', dataIndex: 'processName', width: 160, ellipsis: true },
    { title: '交货日', dataIndex: 'deliveryDate', width: 110 },
    { title: '完工日', dataIndex: 'completeDate', width: 110 },
    { title: '台数', dataIndex: 'deviceNum', width: 80, align: 'right' },
    { title: '抓取时间', dataIndex: 'fetchedAt', width: 160, render: (value: string) => fmtTime(value) },
  ]

  return <Card className="page-card page-card--table outsource-ipms-data-page">
    <h1 className="visually-hidden">IPMS 资料</h1>
    <div className="page-toolbar responsive-toolbar">
      <Space wrap>
        <Input.Search aria-label="搜索工令、客户或机型" placeholder="工令、客户或机型" allowClear style={{ width: 280 }}
          onSearch={(value) => { setKeyword(value); setPage(1) }} />
      </Space>
    </div>
    {error ? <LoadError message="IPMS 资料加载失败" onRetry={() => void load()} />
      : <ResizableTable<Order> tableId="outsource-ipms-orders" className="page-table" rowKey="workOrderNo" loading={loading} data={orders}
        columns={columns} noDataElement={loading ? <span /> : <Empty description="暂无资料" />} scroll={{ x: 1200, y: 'var(--page-table-scroll-y)' }}
        pagination={{ current: page, pageSize, total, onChange: (next, size) => { setPage(size === pageSize ? next : 1); setPageSize(size) } }} />}
    <Drawer title={detail ? `工令 ${detail.workOrderNo}` : '工令资料'} width={560} visible={detailLoading || detail !== null} unmountOnExit
      footer={null} onCancel={() => { setDetail(null); setDetailLoading(false) }}>
      {detail && <>
        <p>抓取时间：{fmtTime(detail.fetchedAt)}</p>
        <ResizableTable<Field> tableId="outsource-ipms-fields" className="outsource-ipms-fields" size="small" rowKey="key" data={detail.fields}
          columns={FIELD_COLUMNS} pagination={false} />
      </>}
    </Drawer>
  </Card>
}
