import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Button, Card, Checkbox, Empty, Form, Input, Message, Modal } from '@arco-design/web-react'
import type { TableColumnProps } from '@arco-design/web-react'
import { IconDownload, IconSearch } from '@arco-design/web-react/icon'
import type { ApiResponses } from '../../api/types'
import { isVersionConflict } from '../../api/versionConflict'
import { actionSlots } from '../../components/ActionSlots'
import LoadError from '../../components/LoadError'
import ResizableTable from '../../components/ResizableTable'
import { TABLE_PAGINATION } from '../../components/tablePagination'
import { saveBlob } from '../../utils/saveBlob'
import { useOem } from '../OemContext'
import '../../outsource/pages/OutsourceListPage.css'

type Page = ApiResponses['GET /oem/outsource-items']
type Row = Page['list'][number]

const WIDTHS: Record<string, number> = {
  serialNo: 70, outsourceDate: 110, status: 80, electricalHandoff: 150, workOrderNo: 110, customer: 150, machineModel: 120, type: 200, vendor: 120,
  remark: 180, quantity: 70,
}
const FIXED = new Set(['serialNo', 'outsourceDate', 'status', 'electricalHandoff', 'workOrderNo'])
const CENTERED = new Set(['serialNo', 'status', 'electricalHandoff', 'quantity'])
const handoffLabel = (handoff: boolean) => handoff ? '撤回' : '转迅得电控'

/** Row colours as on the internal list and in the mail: 正常 green, 暂停 red, 取消 yellow. */
const rowClass = (row: Row) => [
  row.status === 'PAUSED' ? 'outsource-row--paused' : row.status === 'CANCELLED' ? 'outsource-row--cancelled' : 'outsource-row--normal',
  row.closed ? 'outsource-row--closed' : '',
].filter(Boolean).join(' ')

/** The vendor's own outsourcing records and electrical handoff actions. */
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
  const [changing, setChanging] = useState<Row | null>(null)
  const [signerDraft, setSignerDraft] = useState({ itemId: 0, name: '' })
  const [saving, setSaving] = useState(false)
  const writeLock = useRef(false)
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

  const signerName = signerDraft.name.trim()
  const signerTooLong = Array.from(signerName).length > 32
  const signatureValid = signerName.length > 0 && !signerTooLong
  const saveHandoff = async () => {
    if (!changing || !signatureValid || writeLock.current) return
    writeLock.current = true; setSaving(true)
    try {
      await api.setOutsourceElectricalHandoff(changing.id, {
        handoff: !changing.electricalHandoff, expected: changing.electricalHandoff, signerName,
      })
      setChanging(null); setSignerDraft({ itemId: 0, name: '' })
      Message.success(changing.electricalHandoff ? '已撤回转迅得电控' : '已转迅得电控')
      await load()
    } catch (error) {
      if (isVersionConflict(error)) {
        // 请求层已提示冲突，保留签名。重新选择操作时才使用刷新后的状态。
        setChanging(null)
        await load()
      }
    } finally {
      writeLock.current = false; setSaving(false)
    }
  }

  const columns = useMemo<TableColumnProps<Row>[]>(() => [
    ...(data?.columns ?? []).map((column) => ({
      title: column.title, key: column.key, width: WIDTHS[column.key] ?? 110, ellipsis: true,
      fixed: FIXED.has(column.key) ? 'left' as const : undefined,
      align: CENTERED.has(column.key) ? 'center' as const : undefined,
      render: (_: unknown, row: Row) => row.values[column.key] ?? '',
    })),
    {
      title: '操作', width: 112, align: 'center', fixed: 'right',
      render: (_, row) => actionSlots([
        <Button key="handoff" type="text" size="mini" disabled={row.closed || saving || loading} onClick={() => {
          setChanging(row)
          setSignerDraft((draft) => draft.itemId === row.id ? draft : { itemId: row.id, name: '' })
        }}>{handoffLabel(row.electricalHandoff)}</Button>,
      ], 'outsource-handoff'),
    },
  ], [data?.columns, loading, saving])

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
    <Modal className="form-dialog" title={changing ? `${handoffLabel(changing.electricalHandoff)} · 工令 ${changing.values.workOrderNo ?? ''}` : ''}
      visible={changing !== null} onOk={() => void saveHandoff()}
      onCancel={() => { if (!writeLock.current) { setChanging(null); setSignerDraft({ itemId: 0, name: '' }) } }}
      confirmLoading={saving} closable={!saving} maskClosable={!saving} okButtonProps={{ disabled: !signatureValid }}
      okText="确认" cancelText="取消" unmountOnExit>
      <Form layout="vertical">
        <Form.Item label="签名" required validateStatus={signerTooLong ? 'error' : undefined}
          help={signerTooLong ? '签名不能超过 32 个字符' : undefined}>
          <Input aria-label="签名" aria-required placeholder="填写操作人姓名" value={signerDraft.name} autoFocus
            disabled={saving} onChange={(name) => setSignerDraft((draft) => ({ ...draft, name }))}
            onPressEnter={() => void saveHandoff()} />
        </Form.Item>
      </Form>
    </Modal>
  </Card>
}
