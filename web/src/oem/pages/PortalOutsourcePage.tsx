import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Button, Card, Checkbox, Empty, Input } from '@arco-design/web-react'
import type { TableColumnProps } from '@arco-design/web-react'
import { IconDownload, IconSearch } from '@arco-design/web-react/icon'
import type { ApiResponses } from '../../api/types'
import LoadError from '../../components/LoadError'
import ResizableTable from '../../components/ResizableTable'
import { TABLE_PAGINATION } from '../../components/tablePagination'
import { saveBlob } from '../../utils/saveBlob'
import { useOem } from '../OemContext'
import '../../outsource/pages/OutsourceListPage.css'

type Page = ApiResponses['GET /oem/outsource-items']
type Row = Page['list'][number]

const WIDTHS: Record<string, number> = {
  serialNo: 70, outsourceDate: 110, status: 80, workOrderNo: 110, customer: 150, machineModel: 120, type: 200, vendor: 120,
  remark: 180, quantity: 70,
}

/** Row colours as on the internal list and in the mail: 正常 green, 暂停 red, 取消 yellow. */
const rowClass = (row: Row) => [
  row.status === 'PAUSED' ? 'outsource-row--paused' : row.status === 'CANCELLED' ? 'outsource-row--cancelled' : 'outsource-row--normal',
  row.closed ? 'outsource-row--closed' : '',
].filter(Boolean).join(' ')

/** The vendor's own outsourcing records, read only, with the columns of the vendor Excel attachment (外协清单契约 → 厂商查看). */
export default function PortalOutsourcePage() {
  const { api } = useOem()
  const [data, setData] = useState<Page | null>(null)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [keyword, setKeyword] = useState('')
  const [applied, setApplied] = useState('')
  const [includeClosed, setIncludeClosed] = useState(false)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(false)
  const [exporting, setExporting] = useState(false)
  const sequence = useRef(0)

  const load = useCallback(async () => {
    const seq = ++sequence.current
    setLoading(true); setError(false)
    try {
      const response = await api.outsourceItems({ page, pageSize, keyword: applied, includeClosed })
      if (seq === sequence.current) setData(response)
    } catch {
      if (seq === sequence.current) setError(true)
    } finally {
      if (seq === sequence.current) setLoading(false)
    }
  }, [api, page, pageSize, applied, includeClosed])

  useEffect(() => {
    let active = true
    void Promise.resolve().then(() => { if (active) void load() })
    return () => { active = false; sequence.current += 1 }
  }, [load])

  const exportItems = async () => {
    if (exporting) return
    setExporting(true)
    try {
      const day = new Date().toLocaleDateString('sv-SE', { timeZone: 'Asia/Shanghai' }).replaceAll('-', '')
      saveBlob(await api.outsourceExport({ keyword: applied, includeClosed }), `外协清单_${day}.xlsx`)
    } catch {
      // 统一请求层提示
    } finally {
      setExporting(false)
    }
  }

  const columns = useMemo<TableColumnProps<Row>[]>(() => (data?.columns ?? []).map((column, index) => ({
    title: column.title, key: column.key, width: WIDTHS[column.key] ?? 110, ellipsis: true,
    fixed: index < 4 ? 'left' as const : undefined,
    align: column.key === 'serialNo' || column.key === 'status' || column.key === 'quantity' ? 'center' as const : undefined,
    render: (_: unknown, row: Row) => row.values[column.key] ?? '',
  })), [data?.columns])

  return <Card className="page-card page-card--table outsource-list-page">
    <h1 className="visually-hidden">外协清单</h1>
    <div className="page-toolbar responsive-toolbar">
      <Input allowClear prefix={<IconSearch />} placeholder="工令、客户或机型" style={{ width: 260 }} value={keyword}
        onChange={(value) => { setKeyword(value); if (!value) { setPage(1); setApplied('') } }}
        onPressEnter={() => { setPage(1); setApplied(keyword.trim()) }} />
      <Checkbox checked={includeClosed} onChange={(value) => { setPage(1); setIncludeClosed(value) }}>显示已完成</Checkbox>
      <Button icon={<IconDownload />} loading={exporting} onClick={() => void exportItems()}>导出</Button>
    </div>
    {error ? <LoadError message="外协清单加载失败" onRetry={load} />
      : <ResizableTable<Row> tableId="oem-portal-outsource" className="page-table outsource-table" rowKey="id" loading={loading}
          data={data?.list ?? []} columns={columns} rowClassName={rowClass}
          noDataElement={loading ? <span /> : <Empty description="暂无外协记录" />}
          scroll={{ x: 1800, y: 'var(--page-table-scroll-y)' }}
          pagination={{
            ...TABLE_PAGINATION, current: page, pageSize, total: Number(data?.total ?? 0),
            onChange: (next, size) => { setPage(size !== pageSize ? 1 : next); setPageSize(size) },
          }} />}
  </Card>
}
