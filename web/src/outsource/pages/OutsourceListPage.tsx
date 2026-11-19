import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  Button, Card, Checkbox, DatePicker, Empty, Form, Input, Message, Modal, Popconfirm, Select, Space, Tag, Tooltip,
} from '@arco-design/web-react'
import type { TableColumnProps } from '@arco-design/web-react'
import { IconCheckCircle, IconDownload, IconEdit, IconEmail, IconHistory, IconSync, IconUpload } from '@arco-design/web-react/icon'
import http from '../../api/client'
import { fmtTime, type ApiResponses } from '../../api/types'
import EnterSubmit from '../../components/EnterSubmit'
import ExcelImportDialog, { type ImportBatchOutcome, type ImportPreviewRow, type ImportRowOutcome } from '../../components/ExcelImportDialog'
import LoadError from '../../components/LoadError'
import { useAuth } from '../../store/auth'
import type { ExcelImportRow } from '../../utils/excelWorkbook'
import { OUTSOURCE_PERMISSIONS } from '../outsourceNavigation'
import { normalizeImportDate } from '../importDate'
import { ItemHistoryDrawer, LatestDueCell } from './ItemHistory'
import SendMailDialog from './SendMailDialog'
import './OutsourceListPage.css'
import ResizableTable from '../../components/ResizableTable'
import { requestErrorMessage } from '../../api/requestError'
import { saveBlob } from '../../utils/saveBlob'

type Item = ApiResponses['GET /outsource/items']['list'][number]
type Vendor = ApiResponses['GET /outsource/settings/vendors'][number]
type OutsourceType = ApiResponses['GET /outsource/settings/types'][number]
type SyncStatus = ApiResponses['GET /outsource/sync-status']
type FieldColumn = ApiResponses['GET /outsource/fields']['fields'][number]
type ImportRow = { rowNumber: number; outsourceDate: string; workOrderNo: string; vendorName: string; typeName: string }

const STATUS_OPTIONS = [
  { label: '正常', value: 'NORMAL' },
  { label: '暂停', value: 'PAUSED' },
  { label: '取消', value: 'CANCELLED' },
]
const SEND_STATUS_OPTIONS = [
  { label: '未发送', value: 'UNSENT' },
  { label: '已发送', value: 'SENT' },
  { label: '有变更待通知', value: 'PENDING_NOTICE' },
]
const STATUS_TAG: Record<string, { label: string; color: string }> = {
  NORMAL: { label: '正常', color: 'green' },
  PAUSED: { label: '暂停', color: 'red' },
  CANCELLED: { label: '取消', color: 'gold' },
}
const SEND_TAG: Record<string, { label: string; color: string }> = {
  UNSENT: { label: '未发送', color: 'gray' },
  SENT: { label: '已发送', color: 'arcoblue' },
  PENDING_NOTICE: { label: '有变更待通知', color: 'orange' },
}
const MATCH_PROBLEM: Record<string, string> = {
  UNKNOWN_TYPE: '外发类型不在字典中，不能发送',
  VENDOR_MISMATCH: '外发类型与厂商不符，不能发送',
}
/** A custom field's cell text: yes/no fields are stored as true/false. */
const customText = (field: Pick<FieldColumn, 'dataType'>, value?: string | null) =>
  field.dataType === 'BOOLEAN' ? (value === 'true' ? '是' : value === 'false' ? '否' : '') : (value ?? '')
const BOOLEAN_OPTIONS = [{ label: '是', value: 'true' }, { label: '否', value: 'false' }]

const IMPORT_HEADERS = ['外发日期', '工令', '外发类型', '外发厂商'] as const

interface Filters {
  keyword: string
  vendorId?: number
  typeId?: number
  status?: string
  sendStatus?: string
  dateRange?: [string, string]
  includeClosed: boolean
}

const today = () => {
  const now = new Date(Date.now() + 8 * 3600_000)
  return now.toISOString().slice(0, 10).replaceAll('-', '')
}

/** The outsourcing list (外协清单契约 → 页面). */
export default function OutsourceListPage() {
  const permissions = useAuth((state) => state.permissions)
  const canImport = permissions.includes(OUTSOURCE_PERMISSIONS.import)
  const canManage = permissions.includes(OUTSOURCE_PERMISSIONS.manage)
  const [filters, setFilters] = useState<Filters>({ keyword: '', includeClosed: false })
  const [keywordDraft, setKeywordDraft] = useState('')
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [items, setItems] = useState<Item[]>([])
  const [total, setTotal] = useState(0)
  const [vendors, setVendors] = useState<Vendor[]>([])
  const [types, setTypes] = useState<OutsourceType[]>([])
  const [layout, setLayout] = useState<FieldColumn[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(false)
  const [selected, setSelected] = useState<number[]>([])
  const [editing, setEditing] = useState<Item | null>(null)
  const [history, setHistory] = useState<Item | null>(null)
  const [mailing, setMailing] = useState<number[] | null>(null)
  const [batchOpen, setBatchOpen] = useState(false)
  const [importOpen, setImportOpen] = useState(false)
  const [exporting, setExporting] = useState(false)
  const [syncStatus, setSyncStatus] = useState<SyncStatus | null>(null)
  const [syncing, setSyncing] = useState(false)
  const sequence = useRef(0)

  const params = useMemo(() => ({
    vendorId: filters.vendorId,
    typeId: filters.typeId,
    status: filters.status,
    sendStatus: filters.sendStatus,
    dateFrom: filters.dateRange?.[0],
    dateTo: filters.dateRange?.[1],
    keyword: filters.keyword || undefined,
    includeClosed: filters.includeClosed ? 'true' : undefined,
  }), [filters])

  const loadReference = useCallback(async () => {
    const [vendorResponse, typeResponse, fieldResponse] = await Promise.all([
      http.get<ApiResponses['GET /outsource/settings/vendors']>('/outsource/settings/vendors'),
      http.get<ApiResponses['GET /outsource/settings/types']>('/outsource/settings/types'),
      http.get<ApiResponses['GET /outsource/fields']>('/outsource/fields'),
    ])
    setVendors(vendorResponse.data); setTypes(typeResponse.data); setLayout(fieldResponse.data.fields)
  }, [])

  const load = useCallback(async () => {
    const seq = ++sequence.current
    setLoading(true); setError(false)
    try {
      const [listResponse, syncResponse] = await Promise.all([
        http.get<ApiResponses['GET /outsource/items']>('/outsource/items', { params: { ...params, page, pageSize } }),
        http.get<ApiResponses['GET /outsource/sync-status']>('/outsource/sync-status'),
      ])
      if (seq !== sequence.current) return
      setItems(listResponse.data.list); setTotal(Number(listResponse.data.total)); setSyncStatus(syncResponse.data)
      setSelected((current) => current.filter((id) => listResponse.data.list.some((item) => item.id === id)))
    } catch {
      if (seq === sequence.current) setError(true)
    } finally {
      if (seq === sequence.current) setLoading(false)
    }
  }, [params, page, pageSize])

  useEffect(() => {
    let active = true
    void Promise.resolve().then(() => { if (active) void loadReference().catch(() => setError(true)) })
    return () => { active = false }
  }, [loadReference])

  useEffect(() => {
    let active = true
    void Promise.resolve().then(() => { if (active) void load() })
    return () => { active = false; sequence.current += 1 }
  }, [load])

  const changeFilters = (next: Partial<Filters>) => { setFilters((current) => ({ ...current, ...next })); setPage(1) }
  const selectedItems = items.filter((item) => selected.includes(item.id))
  const openSelectedItems = selectedItems.filter((item) => !item.closed)
  const openSelected = openSelectedItems.map((item) => item.id)

  const syncNow = async () => {
    if (syncing) return
    setSyncing(true)
    try {
      const response = await http.post<ApiResponses['POST /outsource/items/sync']>('/outsource/items/sync', {
        itemIds: openSelected.length > 0 ? openSelected : null,
      })
      setSyncStatus(response.data)
      Message.success(`已同步 ${response.data.lastProcessed} 条，${response.data.lastChanged} 条有变更`)
      await load()
    } catch {
      // 统一请求层提示
    } finally {
      setSyncing(false)
    }
  }

  /** After saving, ask whether to mail the vendors about the edited items right away. */
  const offerMail = (ids: number[]) => {
    if (ids.length === 0) return
    Modal.confirm({
      title: `是否发送邮件通知厂商？`,
      content: `已保存 ${ids.length} 条记录。`,
      okText: '发送邮件', cancelText: '暂不发送',
      onOk: () => setMailing(ids),
    })
  }

  const exportItems = async () => {
    if (exporting) return
    setExporting(true)
    try {
      const response = await http.get<ApiResponses['GET /outsource/items/export']>('/outsource/items/export', {
        params, responseType: 'blob', timeout: 0,
      })
      const vendorName = filters.vendorId === undefined ? '' : `_${vendors.find((vendor) => vendor.id === filters.vendorId)?.name ?? ''}`
      saveBlob(response.data as Blob, `外协清单${vendorName}_${today()}.xlsx`)
    } catch {
      // 统一请求层提示
    } finally {
      setExporting(false)
    }
  }

  const removeSelected = async () => {
    try {
      await http.post<ApiResponses['POST /outsource/items/batch-delete']>('/outsource/items/batch-delete', {
        items: selectedItems.map((item) => ({ id: item.id, expectedUpdatedAt: item.updatedAt })),
      })
      Message.success(`已删除 ${selectedItems.length} 条记录`); setSelected([]); await load()
    } catch { /* 统一请求层提示 */ }
  }

  const setCompletion = async (completed: boolean) => {
    const targets = selectedItems.filter((item) => item.closed !== completed)
    try {
      await http.put<ApiResponses['PUT /outsource/items/completion']>('/outsource/items/completion', {
        items: targets.map((item) => ({ id: item.id, expectedUpdatedAt: item.updatedAt })), completed,
      })
      Message.success(completed ? `已将 ${targets.length} 条标记为已完成` : `已重新开启 ${targets.length} 条记录`)
      setSelected([]); await load()
    } catch { /* 统一请求层提示 */ }
  }
  const editable = useMemo(() => layout.filter((field) => field.canEdit), [layout])
  const closedSelected = selectedItems.filter((item) => item.closed).length
  const sentSelected = selectedItems.filter((item) => item.sendStatus !== 'UNSENT').length

  const prepareImport = async (rows: ExcelImportRow[]) => {
    const payload: ImportRow[] = rows.map((row) => ({
      rowNumber: row.rowNumber,
      outsourceDate: normalizeImportDate(row.values['外发日期']),
      workOrderNo: row.values['工令'],
      vendorName: row.values['外发厂商'],
      typeName: row.values['外发类型'],
    }))
    try {
      const response = await http.post<ApiResponses['POST /outsource/items/import/preview']>('/outsource/items/import/preview', { rows: payload })
      const byRow = new Map(response.data.rows.map((row) => [row.rowNumber, row]))
      const preview: ImportPreviewRow[] = payload.map((row) => {
        const result = byRow.get(row.rowNumber)
        return {
          rowNumber: row.rowNumber,
          key: row.workOrderNo,
          detail: [row.vendorName, result?.customer, result?.typeLabel].filter(Boolean).join(' · '),
          status: (result?.status ?? 'INVALID') as ImportPreviewRow['status'],
          message: result?.message ?? undefined,
        }
      })
      return { payload, preview }
    } catch (error) {
      throw new Error(requestErrorMessage(error, '预览失败，请稍后重试'))
    }
  }
  const commitImport = async (rows: ImportRow[]): Promise<ImportBatchOutcome> => {
    const { data } = await http.post<ApiResponses['POST /outsource/items/import']>('/outsource/items/import', { rows })
    return { ...data, rows: data.rows.map((row) => ({ ...row, status: row.status as ImportRowOutcome['status'] })) }
  }

  const date = (value?: string | null) => value ?? ''
  // Built-in columns by key; their titles, order and presence come from the field settings (外协清单契约 → 栏位设定).
  const builtIn: Record<string, Partial<TableColumnProps<Item>>> = {
    outsourceDate: { dataIndex: 'outsourceDate', width: 110 },
    status: { width: 80, align: 'center', render: (_, item) => <Tag color={STATUS_TAG[item.status]?.color}>{STATUS_TAG[item.status]?.label ?? item.status}</Tag> },
    workOrderNo: { dataIndex: 'workOrderNo', width: 110 },
    customer: {
      dataIndex: 'customer', width: 150, ellipsis: true,
      render: (_, item) => item.customerFullName && item.customerFullName !== item.customer
        ? <Tooltip content={item.customerFullName}><span>{item.customer}</span></Tooltip> : item.customer,
    },
    machineModel: { dataIndex: 'machineModel', width: 120, ellipsis: true },
    type: {
      width: 200, render: (_, item) => <Space size={4}>
        <span>{item.typeLabel ?? item.typeCode ?? ''}</span>
        {MATCH_PROBLEM[item.matchState] && <Tooltip content={MATCH_PROBLEM[item.matchState]}><Tag size="small" color="orangered">不符</Tag></Tooltip>}
      </Space>,
    },
    vendor: { dataIndex: 'vendorName', width: 110, ellipsis: true },
    mechanicalSection: { dataIndex: 'mechanicalSection', width: 110, ellipsis: true },
    mechanicalOwner: { dataIndex: 'mechanicalOwner', width: 100, ellipsis: true },
    electricalOwner: { dataIndex: 'electricalOwner', width: 100, ellipsis: true },
    workOrderIssuedOn: { width: 110, render: (_, item) => date(item.workOrderIssuedOn) },
    bomOn: { width: 110, render: (_, item) => date(item.bomOn) },
    drawingReleasedOn: { width: 110, render: (_, item) => date(item.drawingReleasedOn) },
    originalDueDate: { width: 110, render: (_, item) => date(item.originalDueDate) },
    latestDueDate: { width: 110, render: (_, item) => <LatestDueCell item={item} /> },
    quantity: { dataIndex: 'quantity', width: 70, align: 'center' },
    remark: { dataIndex: 'remark', width: 180, ellipsis: true },
  }
  const fieldColumns: TableColumnProps<Item>[] = layout.map((field) => {
    const base: Partial<TableColumnProps<Item>> = builtIn[field.key]
      ?? { width: field.dataType === 'TEXT' ? 160 : 110, ellipsis: true, render: (_, item) => customText(field, item.customValues?.[field.key]) }
    const column: TableColumnProps<Item> = { ...base, key: field.key, title: field.label, fixed: field.pinned ? 'left' : undefined }
    // A cell that differs from what was last mailed to the vendor is yellow, as in the mail and its Excel attachment.
    // Arco takes a cell's class from what its render function returns (`props`), not from `onCell`.
    return {
      ...column,
      render: (value: unknown, item: Item, index: number) => {
        const content = base.render ? base.render(value, item, index) : (value ?? '')
        return item.changedFields?.includes(field.key) ? { children: content, props: { className: 'outsource-cell-changed' } } : content
      },
    }
  })
  const columns: TableColumnProps<Item>[] = [
    { title: '序号', dataIndex: 'serialNo', width: 70, fixed: 'left', align: 'center' },
    ...fieldColumns.filter((column) => column.fixed),
    ...fieldColumns.filter((column) => !column.fixed),
    {
      title: '发送状态', width: 120, align: 'center',
      render: (_, item) => <Space size={4}>
        <Tag color={SEND_TAG[item.sendStatus]?.color}>{SEND_TAG[item.sendStatus]?.label ?? item.sendStatus}</Tag>
        {item.closed && <Tag color="gray">已完成</Tag>}
      </Space>,
    },
    { title: '导入人', dataIndex: 'importedBy', width: 100, ellipsis: true },
    { title: '导入时间', width: 130, render: (_, item) => item.importedAt ? fmtTime(item.importedAt).slice(0, 16) : '' },
    { title: '修改人', dataIndex: 'modifiedBy', width: 100, ellipsis: true },
    { title: '修改时间', width: 130, render: (_, item) => item.modifiedAt ? fmtTime(item.modifiedAt).slice(0, 16) : '' },
    {
      title: '操作', width: canManage ? 80 : 56, align: 'center', fixed: 'right',
      render: (_, item) => <Space size={0}>
        <Tooltip content="变更履历">
          <Button type="text" size="mini" icon={<IconHistory />} aria-label={`工令 ${item.workOrderNo} 变更履历`} onClick={() => setHistory(item)} />
        </Tooltip>
        {canManage && <Tooltip content={item.closed ? '已完成记录不可编辑' : editable.length === 0 ? '没有可由你填写的栏位' : '编辑'}>
          <Button type="text" size="mini" disabled={item.closed || editable.length === 0} icon={<IconEdit />}
            aria-label={`编辑工令 ${item.workOrderNo}`} onClick={() => setEditing(item)} />
        </Tooltip>}
      </Space>,
    },
  ]

  return <Card className="page-card page-card--table outsource-list-page">
    <h1 className="visually-hidden">外协清单</h1>
    <div className="page-toolbar responsive-toolbar outsource-list-toolbar">
      <Space wrap>
        <Input.Search aria-label="搜索工令、客户或机型" placeholder="工令、客户或机型" value={keywordDraft} allowClear style={{ width: 200 }}
          onChange={setKeywordDraft} onSearch={(value) => changeFilters({ keyword: value.trim() })}
          onClear={() => changeFilters({ keyword: '' })} />
        <Select aria-label="外发厂商" placeholder="外发厂商" allowClear showSearch style={{ width: 180 }} value={filters.vendorId}
          onChange={(value) => { changeFilters({ vendorId: value }); setSelected([]) }}
          options={vendors.map((vendor) => ({ label: vendor.status === 'ACTIVE' ? vendor.name : `${vendor.name}（已停用）`, value: vendor.id }))} />
        <Select aria-label="外发类型" placeholder="外发类型" allowClear showSearch style={{ width: 200 }} value={filters.typeId}
          onChange={(value) => changeFilters({ typeId: value })}
          options={types.map((type) => ({ label: `${type.code}【${type.name}】`, value: type.id }))} />
        <Select aria-label="状态" placeholder="状态" allowClear style={{ width: 100 }} value={filters.status}
          onChange={(value) => changeFilters({ status: value })} options={STATUS_OPTIONS} />
        <Select aria-label="发送状态" placeholder="发送状态" allowClear style={{ width: 140 }} value={filters.sendStatus}
          onChange={(value) => changeFilters({ sendStatus: value })} options={SEND_STATUS_OPTIONS} />
        <DatePicker.RangePicker aria-label="外发日期" style={{ width: 240 }} placeholder={['外发日期起', '外发日期止']}
          value={filters.dateRange} onChange={(value) => changeFilters({ dateRange: value?.length === 2 ? [value[0], value[1]] : undefined })} />
        <Checkbox checked={filters.includeClosed} onChange={(value) => changeFilters({ includeClosed: value })}>显示已完成</Checkbox>
      </Space>
      <Space wrap>
        {canManage && closedSelected > 0 && <Button onClick={() => void setCompletion(false)}>重新开启 ({closedSelected})</Button>}
        {canManage && openSelected.length > 0 && <>
          <Button type="primary" icon={<IconEmail />} onClick={() => setMailing(openSelected)}>发送邮件 ({openSelected.length})</Button>
          {editable.length > 0 && <Button icon={<IconEdit />} onClick={() => setBatchOpen(true)}>批量修改 ({openSelected.length})</Button>}
          <Popconfirm title={`将所选 ${openSelected.length} 条标记为已完成？之后不再同步、修改或发送。`} onOk={() => setCompletion(true)}>
            <Button icon={<IconCheckCircle />}>标记完成</Button>
          </Popconfirm>
        </>}
        {canManage && selected.length > 0 && (
          <Popconfirm title={[
            sentSelected > 0 ? `所选含 ${sentSelected} 条已发送给厂商的记录，删除后不会通知厂商` : '',
            closedSelected > 0 ? `所选含 ${closedSelected} 条已完成记录` : '',
            `确定删除所选 ${selected.length} 条？删除后无法恢复。`,
          ].filter(Boolean).join('；')} onOk={removeSelected}>
            <Button status="danger">删除</Button>
          </Popconfirm>
        )}
        {syncStatus && <SyncBadge status={syncStatus} />}
        {canManage && <Button icon={<IconSync />} loading={syncing} disabled={!syncStatus?.ipmsConfigured || syncStatus.running || (selected.length > 0 && openSelected.length === 0)}
          onClick={() => void syncNow()}>{openSelected.length > 0 ? `同步所选 (${openSelected.length})` : '立即同步'}</Button>}
        <Button icon={<IconDownload />} loading={exporting} onClick={() => void exportItems()}>导出</Button>
        {canImport && <Button type="primary" icon={<IconUpload />} onClick={() => setImportOpen(true)}>汇入</Button>}
      </Space>
    </div>
    {error ? <LoadError message="外协清单加载失败" onRetry={() => { void loadReference().catch(() => undefined); void load() }} />
      : <ResizableTable<Item> tableId="outsource-items" className="page-table outsource-table" rowKey="id" loading={loading} data={items} columns={columns}
        rowClassName={(item) => [item.status === 'PAUSED' ? 'outsource-row--paused' : item.status === 'CANCELLED' ? 'outsource-row--cancelled' : 'outsource-row--normal',
          item.closed ? 'outsource-row--closed' : ''].filter(Boolean).join(' ')}
        rowSelection={canManage ? {
          type: 'checkbox', selectedRowKeys: selected, onChange: (keys) => setSelected(keys as number[]),
        } : undefined}
        noDataElement={loading ? <span /> : <Empty description="暂无外协记录" />}
        scroll={{ x: 2350, y: 'var(--page-table-scroll-y)' }}
        pagination={{
          current: page, pageSize, total, onChange: (next, size) => { setPage(next); setPageSize(size) },
        }} />}
    <ItemHistoryDrawer item={history} onClose={() => setHistory(null)} />
    <EditDialog item={editing} vendors={vendors} types={types} fields={editable} onClose={() => setEditing(null)}
      onSaved={async () => { const ids = editing ? [editing.id] : []; setEditing(null); await load(); offerMail(ids) }} />
    <BatchDialog visible={batchOpen} items={openSelectedItems} vendors={vendors} types={types} fields={editable} onClose={() => setBatchOpen(false)}
      onSaved={async () => { const ids = openSelectedItems.map((item) => item.id); setBatchOpen(false); await load(); offerMail(ids) }} />
    <SendMailDialog itemIds={mailing} onClose={() => setMailing(null)}
      onSent={() => { setMailing(null); setSelected([]); void load() }} />
    <ExcelImportDialog<ImportRow> visible={importOpen} title="汇入外协清单" templateName="外协清单汇入模板.xlsx"
      requiredHeaders={IMPORT_HEADERS} maxRows={500} keyTitle="工令"
      notice={<>填写外发日期、工令、外发类型和外发厂商，序号自动编，其余字段由 IPMS 带出。已存在或文件内重复的工令会跳过，外发类型与厂商不符的行不会汇入。最多 500 行。</>}
      prepare={prepareImport} commit={commitImport}
      onImported={() => { void load() }} onClose={() => setImportOpen(false)} />
  </Card>
}

/** Vendors the chosen type allows; the item's current vendor stays selectable while its type is unchanged. */
function vendorOptions(vendors: Vendor[], types: OutsourceType[], item: Item, typeId: number | undefined) {
  const type = types.find((candidate) => candidate.id === typeId)
  return vendors.filter((vendor) => (vendor.id === item.vendorId && typeId === item.typeId)
    || (vendor.status === 'ACTIVE' && type?.vendorIds.includes(vendor.id)))
    .map((vendor) => ({ label: vendor.status === 'ACTIVE' ? vendor.name : `${vendor.name}（已停用）`, value: vendor.id }))
}

const typeOptions = (types: OutsourceType[]) => types.filter((type) => type.status === 'ACTIVE')
  .map((type) => ({ label: `${type.code}【${type.name}】`, value: type.id }))

/** The input of a custom field, by its type; `undefined` means empty. */
function CustomInput({ field, value, onChange, placeholder }: {
  field: FieldColumn; value?: string; onChange?: (value: string | undefined) => void; placeholder?: string
}) {
  const label = field.label
  if (field.dataType === 'BOOLEAN') return <Select aria-label={label} allowClear placeholder={placeholder} value={value} onChange={onChange} options={BOOLEAN_OPTIONS} />
  if (field.dataType === 'SELECT') return <Select aria-label={label} allowClear placeholder={placeholder} value={value} onChange={onChange}
    options={field.options.map((option) => ({ label: option, value: option }))} />
  if (field.dataType === 'DATE') return <DatePicker aria-label={label} style={{ width: '100%' }} placeholder={placeholder} value={value}
    onChange={(next) => onChange?.(next || undefined)} />
  return <Input aria-label={label} placeholder={placeholder} value={value} maxLength={field.dataType === 'NUMBER' ? 32 : 500}
    onChange={(next) => onChange?.(next || undefined)} />
}

const isCustom = (field: FieldColumn) => field.category === 'CUSTOM'

function EditDialog({ item, vendors, types, fields, onClose, onSaved }: {
  item: Item | null; vendors: Vendor[]; types: OutsourceType[]; fields: FieldColumn[]; onClose: () => void; onSaved: () => Promise<void>
}) {
  const [form] = Form.useForm()
  const [saving, setSaving] = useState(false)
  const selectedTypeId = Form.useWatch('typeId', form) as number | undefined
  // Only the columns this account may fill in are offered; the others are sent back unchanged.
  const can = (key: string) => fields.some((field) => field.key === key)
  const custom = useMemo(() => fields.filter(isCustom), [fields])
  useEffect(() => {
    if (item) form.setFieldsValue({
      vendorId: item.vendorId, typeId: item.typeId ?? undefined, status: item.status, remark: item.remark ?? '', bomOn: item.bomOn ?? undefined,
      drawingReleasedOn: item.drawingReleasedOn ?? undefined,
      ...Object.fromEntries(custom.map((field) => [`custom.${field.key}`, item.customValues?.[field.key] ?? undefined])),
    })
  }, [item, form, custom])
  const save = async () => {
    if (!item || saving) return
    const values = await form.validate().catch(() => null)
    if (!values) return
    setSaving(true)
    try {
      await http.put<ApiResponses['PUT /outsource/items/{id}']>(`/outsource/items/${item.id}`, {
        vendorId: can('vendor') ? values.vendorId : item.vendorId,
        typeId: can('type') ? values.typeId : undefined,
        status: can('status') ? values.status : item.status,
        bomOn: can('bomOn') ? values.bomOn || '' : undefined,
        drawingReleasedOn: can('drawingReleasedOn') ? values.drawingReleasedOn || '' : undefined,
        remark: can('remark') ? values.remark?.trim() ?? '' : item.remark ?? '',
        values: custom.length > 0 ? Object.fromEntries(custom.map((field) => [field.key, values.custom?.[field.key] ?? ''])) : undefined,
        expectedUpdatedAt: item.updatedAt,
      })
      Message.success('外协记录已保存'); await onSaved()
    } catch { /* 统一请求层提示，保留输入 */ } finally { setSaving(false) }
  }
  return <Modal className="form-dialog" title={item ? `编辑工令 ${item.workOrderNo}` : '编辑'} visible={item !== null} onOk={() => void save()}
    onCancel={() => { if (!saving) onClose() }} confirmLoading={saving} closable={!saving} maskClosable={!saving} okText="保存" cancelText="取消" unmountOnExit>
    <Form form={form} layout="vertical" disabled={saving} onSubmit={() => void save()}>
      {can('type') && <Form.Item field="typeId" label="外发类型" rules={[{ required: true, message: '请选择外发类型' }]}
        extra={item && item.typeId == null ? '当前外发类型不在字典中，请重新选择' : undefined}>
        <Select aria-label="外发类型" options={typeOptions(types)}
          onChange={(value: number) => {
            const allowed = types.find((type) => type.id === value)?.vendorIds ?? []
            const vendorId = form.getFieldValue('vendorId') as number | undefined
            if (vendorId !== undefined && !(item && value === item.typeId) && !allowed.includes(vendorId)) form.setFieldValue('vendorId', undefined)
          }} />
      </Form.Item>}
      {can('vendor') && <Form.Item field="vendorId" label="外发厂商" rules={[{ required: true, message: '请选择外发厂商' }]}>
        <Select aria-label="外发厂商" disabled={!item || (can('type') && selectedTypeId === undefined)}
          options={item ? vendorOptions(vendors, types, item, can('type') ? selectedTypeId : item.typeId ?? undefined) : []} />
      </Form.Item>}
      {can('bomOn') && <Form.Item field="bomOn" label="BOM时间"><DatePicker style={{ width: '100%' }} /></Form.Item>}
      {can('drawingReleasedOn') && <Form.Item field="drawingReleasedOn" label="发图"><DatePicker style={{ width: '100%' }} /></Form.Item>}
      {can('status') && <Form.Item field="status" label="状态" rules={[{ required: true }]}><Select options={STATUS_OPTIONS} /></Form.Item>}
      {can('remark') && <Form.Item field="remark" label="备注" rules={[{ maxLength: 500, message: '备注最多 500 个字符' }]}>
        <Input.TextArea autoSize={{ minRows: 2, maxRows: 5 }} maxLength={500} />
      </Form.Item>}
      {custom.map((field) => <Form.Item key={field.key} field={`custom.${field.key}`} label={field.label}>
        <CustomInput field={field} />
      </Form.Item>)}
      <EnterSubmit />
    </Form>
  </Modal>
}

function BatchDialog({ visible, items, vendors, types, fields, onClose, onSaved }: {
  visible: boolean; items: Item[]; vendors: Vendor[]; types: OutsourceType[]; fields: FieldColumn[]; onClose: () => void; onSaved: () => Promise<void>
}) {
  const [vendorId, setVendorId] = useState<number>()
  const [typeId, setTypeId] = useState<number>()
  const [status, setStatus] = useState<string>()
  const [bomOn, setBomOn] = useState<string>()
  const [drawingReleasedOn, setDrawingReleasedOn] = useState<string>()
  const [changeRemark, setChangeRemark] = useState(false)
  const [remark, setRemark] = useState('')
  const [customValues, setCustomValues] = useState<Record<string, string | undefined>>({})
  const [saving, setSaving] = useState(false)
  const can = (key: string) => fields.some((field) => field.key === key)
  const custom = fields.filter(isCustom)
  const reset = () => { setVendorId(undefined); setTypeId(undefined); setStatus(undefined); setBomOn(undefined); setDrawingReleasedOn(undefined); setChangeRemark(false); setRemark(''); setCustomValues({}) }
  const changedCustom = Object.entries(customValues).filter(([, value]) => value !== undefined)
  const nothing = vendorId === undefined && typeId === undefined && !status && !bomOn && !drawingReleasedOn && !changeRemark && changedCustom.length === 0
  const save = async () => {
    if (saving || nothing) return
    setSaving(true)
    try {
      await http.put<ApiResponses['PUT /outsource/items/batch']>('/outsource/items/batch', {
        items: items.map((item) => ({ id: item.id, expectedUpdatedAt: item.updatedAt })),
        vendorId, typeId, status, bomOn, drawingReleasedOn, remark: changeRemark ? remark.trim() : undefined,
        values: changedCustom.length > 0 ? Object.fromEntries(changedCustom) : undefined,
      })
      Message.success(`已修改 ${items.length} 条记录`); reset(); await onSaved()
    } catch { /* 统一请求层提示，保留输入 */ } finally { setSaving(false) }
  }
  return <Modal className="form-dialog" title={`批量修改 ${items.length} 条记录`} visible={visible} onOk={() => void save()}
    okButtonProps={{ disabled: nothing }} onCancel={() => { if (!saving) { reset(); onClose() } }} confirmLoading={saving}
    okText="保存" cancelText="取消">
    <Form layout="vertical" disabled={saving}>
      {can('type') && <Form.Item label="外发类型"><Select aria-label="外发类型" allowClear placeholder="不修改" value={typeId}
        onChange={(value: number | undefined) => {
          setTypeId(value)
          if (value !== undefined && vendorId !== undefined) {
            const allowed = types.find((type) => type.id === value)?.vendorIds ?? []
            if (!allowed.includes(vendorId)) setVendorId(undefined)
          }
        }}
        options={typeOptions(types)} /></Form.Item>}
      {can('vendor') && <Form.Item label="外发厂商"><Select aria-label="外发厂商" allowClear placeholder="不修改" value={vendorId} onChange={setVendorId}
        options={vendors.filter((vendor) => vendor.status === 'ACTIVE' && (typeId === undefined || (types.find((type) => type.id === typeId)?.vendorIds ?? []).includes(vendor.id)))
          .map((vendor) => ({ label: vendor.name, value: vendor.id }))} /></Form.Item>}
      {can('bomOn') && <Form.Item label="BOM时间"><DatePicker style={{ width: '100%' }} placeholder="不修改" value={bomOn} onChange={(value) => setBomOn(value || undefined)} /></Form.Item>}
      {can('drawingReleasedOn') && <Form.Item label="发图"><DatePicker style={{ width: '100%' }} placeholder="不修改" value={drawingReleasedOn} onChange={(value) => setDrawingReleasedOn(value || undefined)} /></Form.Item>}
      {can('status') && <Form.Item label="状态"><Select allowClear placeholder="不修改" value={status} onChange={setStatus} options={STATUS_OPTIONS} /></Form.Item>}
      {can('remark') && <Form.Item label={<Checkbox checked={changeRemark} onChange={setChangeRemark}>修改备注</Checkbox>}>
        <Input.TextArea aria-label="备注" disabled={!changeRemark} value={remark} onChange={setRemark} maxLength={500} autoSize={{ minRows: 2, maxRows: 4 }} />
      </Form.Item>}
      {custom.map((field) => <Form.Item key={field.key} label={field.label}>
        <CustomInput field={field} placeholder="不修改" value={customValues[field.key]}
          onChange={(value) => setCustomValues((current) => ({ ...current, [field.key]: value }))} />
      </Form.Item>)}
    </Form>
  </Modal>
}

/** MM-DD HH:mm in the viewer's time zone, like other timestamps in the app. */
const formatTime = (value?: string | null) => value ? fmtTime(value).slice(5, 16) : ''

/** Last IPMS sync at a glance; a failure after the last success turns it red with the reason on hover. */
function SyncBadge({ status }: { status: SyncStatus }) {
  if (!status.ipmsConfigured) return <Tag color="gray">IPMS 未设定</Tag>
  if (status.running) return <Tag color="arcoblue">IPMS 同步中</Tag>
  const failed = Boolean(status.lastError) && (!status.lastSuccessAt || (status.lastFinishedAt ?? '') > status.lastSuccessAt)
  if (failed) return <Tooltip content={status.lastError}><Tag color="red">IPMS 同步失败 {formatTime(status.lastFinishedAt)}</Tag></Tooltip>
  return status.lastSuccessAt ? <Tag>IPMS 同步 {formatTime(status.lastSuccessAt)}</Tag> : <Tag color="gray">IPMS 尚未同步</Tag>
}
