import { useCallback, useEffect, useRef, useState } from 'react'
import { Button, Card, Empty, Input, Message, Select, Space, Tag } from '@arco-design/web-react'
import type { TableColumnProps } from '@arco-design/web-react'
import { IconSync } from '@arco-design/web-react/icon'
import http from '../../api/client'
import type { ApiResponses } from '../../api/types'
import LoadError from '../../components/LoadError'
import ResizableTable from '../../components/ResizableTable'
import { useAuth } from '../../store/auth'
import { isGroup, MES_CODE_TEXT, MES_FIXED_COLUMNS, MES_LAYOUT, MES_PINNED_PROPS, type MesColumn } from '../mesColumns'
import { OUTSOURCE_PERMISSIONS } from '../outsourceNavigation'
import MesExcelImportControl from './MesExcelImportControl'
import './MesOrdersPage.css'

type Order = ApiResponses['POST /outsource/mes-mirror/orders/query']['list'][number]
type MirrorStatus = ApiResponses['GET /outsource/mes-mirror/status']

const FIRST_ROWS = 12
const ROWS_PER_STEP = 12
/** 工具栏（同步按钮与已启用筛选标签）占掉的高度，表格高度要相应缩短。 */
const TOOLBAR_HEIGHT = 40
const POLL_MS = 3000
const RUNNING = new Set(['RUNNING', 'STOPPING'])

const MES_COLUMN_LABELS: Record<string, string> = Object.fromEntries([
  ...MES_FIXED_COLUMNS.map((column) => [column.prop, column.label] as const),
  ...MES_LAYOUT.flatMap((item) => isGroup(item)
    ? item.columns.map((column) => [column.prop, `${item.label} · ${column.label}`] as const)
    : [[item.prop, item.label] as const]),
])

/** 与服务端同一个切词规则：粘贴的多行每行是一个完整词，单行才按空格、逗号、分号拆开。 */
function filterTerms(text: string): string[] {
  return text.includes('\n')
    ? text.split('\n').map((line) => line.trim()).filter(Boolean)
    : text.split(/[\s,，;；、]+/).filter(Boolean)
}

function formatFilterValue(prop: string, rawValue: string): string {
  const words = MES_CODE_TEXT[prop]
  const terms = filterTerms(rawValue)
  return words ? terms.map((token) => words[token] ?? token).join('、') : terms.join('、')
}

const cell = (column: MesColumn) => (_: unknown, order: Order) => {
  const value = order.values[column.prop]
  if (value === null || value === undefined) return ''
  return MES_CODE_TEXT[column.prop]?.[value] ?? value
}

/** Room the header text needs: its characters (14px each), the cell padding and the filter icon. */
const headerWidth = (label: string) => [...label].length * 14 + 32 + 28

/** The header filter of one column: a word list for coded fields, otherwise "contains" text. */
function FilterPanel({ column, value, onApply }: { column: MesColumn; value: string; onApply: (text: string) => void }) {
  const [text, setText] = useState(value)
  const words = MES_CODE_TEXT[column.prop]
  return <div className="outsource-ipms-filter">
    {words
      ? <Select mode="multiple" allowClear placeholder="全部" value={text.split(/\s+/).filter(Boolean)} onChange={(next: string[]) => setText(next.join(' '))}
        options={Object.entries(words).map(([code, label]) => ({ value: code, label }))} />
      : <Input.TextArea autoFocus autoSize={{ minRows: 1, maxRows: 6 }} placeholder={`筛选${column.label}，可粘贴多个`} value={text} onChange={setText}
        onPressEnter={(event) => { if (!event.shiftKey) { event.preventDefault(); onApply(text) } }} />}
    <div className="outsource-ipms-filter-actions">
      <Button size="mini" onClick={() => { setText(''); onApply('') }}>重置</Button>
      <Button size="mini" type="primary" onClick={() => onApply(text)}>筛选</Button>
    </div>
  </div>
}

/** The IPMS MES monitoring table, filled from the mirrored data: same columns, same header groups. */
export default function MesOrdersPage() {
  const canImport = useAuth((state) => state.permissions.includes(OUTSOURCE_PERMISSIONS.settings))
  const [orders, setOrders] = useState<Order[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [filters, setFilters] = useState<Record<string, string>>({})
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(false)
  const sequence = useRef(0)
  // 110 columns make a row heavy: the first rows appear at once and the rest of the page is added in small steps,
  // so a 100-row page shows data straight away instead of after one long render.
  const [grown, setGrown] = useState<{ source: Order[]; count: number }>({ source: [], count: FIRST_ROWS })
  const tableHeightFor = useCallback(() => Math.max(280, window.innerHeight - 240 - TOOLBAR_HEIGHT), [])
  const [tableHeight, setTableHeight] = useState(tableHeightFor)
  useEffect(() => {
    const resize = () => setTableHeight(tableHeightFor())
    resize()
    window.addEventListener('resize', resize)
    return () => window.removeEventListener('resize', resize)
  }, [tableHeightFor])

  const load = useCallback(async () => {
    const seq = ++sequence.current
    setLoading(true); setError(false)
    try {
      // Filters go in the body: many pasted names would make the address too long (HTTP 414).
      const response = await http.post<ApiResponses['POST /outsource/mes-mirror/orders/query']>('/outsource/mes-mirror/orders/query', { page, pageSize, filters })
      if (seq !== sequence.current) return
      setOrders(response.data.list); setTotal(Number(response.data.total))
    } catch {
      if (seq === sequence.current) setError(true)
    } finally {
      if (seq === sequence.current) setLoading(false)
    }
  }, [page, pageSize, filters])

  useEffect(() => {
    let active = true
    void Promise.resolve().then(() => { if (active) void load() })
    return () => { active = false; sequence.current += 1 }
  }, [load])

  // Sync = the full fetch from the settings page; a fetch already under way (also the daily one) is shown, not started again.
  const [mirror, setMirror] = useState<MirrorStatus | null>(null)
  const [starting, setStarting] = useState(false)
  const watching = useRef(false)
  const reloadRef = useRef(load)
  useEffect(() => { reloadRef.current = load }, [load])
  const readMirror = useCallback(async () => {
    const response = await http.get<ApiResponses['GET /outsource/mes-mirror/status']>('/outsource/mes-mirror/status')
    const next = response.data
    if (RUNNING.has(next.phase)) watching.current = true
    else if (watching.current) {
      watching.current = false
      if (next.phase === 'IDLE') {
        if (next.lastError) Message.error(next.lastError)
        else Message.success(`已同步，共 ${next.mirrorCount} 笔`)
        void reloadRef.current()
      }
    }
    setMirror(next)
  }, [])
  useEffect(() => {
    let active = true
    void Promise.resolve().then(() => { if (active) void readMirror().catch(() => undefined) })
    return () => { active = false }
  }, [readMirror])
  const syncing = mirror !== null && RUNNING.has(mirror.phase)
  useEffect(() => {
    if (!syncing) return
    const timer = setInterval(() => { void readMirror().catch(() => undefined) }, POLL_MS)
    return () => clearInterval(timer)
  }, [syncing, readMirror])
  const sync = async () => {
    if (starting) return
    setStarting(true)
    try {
      const response = await http.post<ApiResponses['POST /outsource/mes-mirror/run']>('/outsource/mes-mirror/run', { mode: 'ALL' })
      watching.current = true
      setMirror(response.data)
    } catch {
      void readMirror().catch(() => undefined) // 统一请求层提示；拒绝时显示正在进行的抓取
    } finally {
      setStarting(false)
    }
  }
  const syncLabel = syncing && mirror ? `同步中 ${mirror.fetched} / ${mirror.total ?? '—'}` : '同步'

  const apply = (prop: string, text: string) => {
    setFilters((current) => {
      const next = { ...current }
      if (text.trim()) next[prop] = text.trim(); else delete next[prop]
      return next
    })
    setPage(1)
  }
  const leaf = (column: MesColumn, fixed?: 'left'): TableColumnProps<Order> => ({
    // The width IPMS gives a column fits its data; the header also holds the filter icon and the resize handle.
    title: column.label, key: column.prop, width: Math.max(column.width, headerWidth(column.label)), render: cell(column), ...(fixed ? { fixed } : {}),
    filteredValue: filters[column.prop] ? [filters[column.prop]] : [],
    filterDropdown: ({ confirm }: { confirm?: Function }) => <FilterPanel key={filters[column.prop] ?? ''} column={column}
      value={filters[column.prop] ?? ''} onApply={(text) => { apply(column.prop, text); confirm?.() }} />,
  })

  useEffect(() => {
    const timers: number[] = []
    for (let count = FIRST_ROWS + ROWS_PER_STEP, step = 1; count < orders.length + ROWS_PER_STEP; count += ROWS_PER_STEP, step += 1)
      timers.push(window.setTimeout(() => setGrown({ source: orders, count }), step * 30))
    return () => timers.forEach((timer) => window.clearTimeout(timer))
  }, [orders])
  const shown = orders.slice(0, grown.source === orders ? grown.count : FIRST_ROWS)

  const columns: TableColumnProps<Order>[] = [
    { title: '序号', key: 'seq', width: 72, fixed: 'left', render: (_, __, index) => (page - 1) * pageSize + index + 1 },
    ...MES_FIXED_COLUMNS.map((column) => leaf(column, MES_PINNED_PROPS.includes(column.prop) ? 'left' : undefined)),
    ...MES_LAYOUT.map((item): TableColumnProps<Order> => isGroup(item)
      ? { title: item.label, key: item.label, children: item.columns.map((column) => leaf(column)) }
      : leaf(item)),
  ]

  const activeFilterEntries = Object.entries(filters)

  return <Card className="page-card page-card--table outsource-ipms-data-page">
    <h1 className="visually-hidden">IPMS 资料</h1>
    <div className="page-toolbar responsive-toolbar">
      <Space wrap size={12}>
        <Button type="primary" icon={<IconSync />} loading={starting || syncing}
          disabled={mirror === null || mirror.phase === 'AWAITING_CONFIRM'} onClick={() => void sync()}>{syncLabel}</Button>
        {canImport && <MesExcelImportControl onImported={() => void reloadRef.current()} />}
        {activeFilterEntries.length > 0 && <div role="region" aria-label="已启用筛选">
          <Space wrap size={8}>
            {activeFilterEntries.map(([prop, value]) => (
              <Tag key={prop} closable onClose={() => apply(prop, '')}>
                {`${MES_COLUMN_LABELS[prop] ?? prop}：${formatFilterValue(prop, value)}`}
              </Tag>
            ))}
            <Button size="mini" onClick={() => { setFilters({}); setPage(1) }}>清空筛选</Button>
          </Space>
        </div>}
      </Space>
    </div>
    {error ? <LoadError message="IPMS 资料加载失败" onRetry={() => void load()} />
      : <ResizableTable<Order> tableId="outsource-ipms-orders" className="page-table" rowKey="workOrderNo" loading={loading} data={shown}
        columns={columns} noDataElement={loading ? <span /> : <Empty description="暂无资料" />} scroll={{ y: tableHeight }}
        border={{ wrapper: true, headerCell: true, bodyCell: true, cell: true }}
        pagination={{ current: page, pageSize, total, onChange: (next, size) => { setPage(size === pageSize ? next : 1); setPageSize(size) } }} />}
  </Card>
}
