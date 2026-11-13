import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  Button, Card, Checkbox, DatePicker, Empty, Form, Input, Message, Modal, Popconfirm, Select, Space, Table, Tabs, Tag, Tooltip,
} from '@arco-design/web-react'
import type { TableColumnProps } from '@arco-design/web-react'
import { IconDownload, IconEdit, IconEmail, IconHistory, IconSync, IconUpload } from '@arco-design/web-react/icon'
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

type Item = ApiResponses['GET /outsource/items']['list'][number]
type Vendor = ApiResponses['GET /outsource/settings/vendors'][number]
type OutsourceType = ApiResponses['GET /outsource/settings/types'][number]
type Summary = ApiResponses['GET /outsource/items/summary']
type SyncStatus = ApiResponses['GET /outsource/sync-status']
type ImportRow = { rowNumber: number; outsourceDate: string; workOrderNo: string; vendorName: string; typeName: string }

const ALL = 'all'
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
  CANCELLED: { label: '取消', color: 'gray' },
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
const IMPORT_HEADERS = ['外发日期', '工令', '外发厂商', '外发类型'] as const

interface Filters {
  keyword: string
  typeId?: number
  status?: string
  sendStatus?: string
  dateRange?: [string, string]
  includeClosed: boolean
}

const columnHeaderProps = () => ({ scope: 'col' }) as unknown as ReturnType<NonNullable<TableColumnProps['onHeaderCell']>>
const today = () => {
  const now = new Date(Date.now() + 8 * 3600_000)
  return now.toISOString().slice(0, 10).replaceAll('-', '')
}
const serverMessage = (error: unknown) =>
  (error as { response?: { data?: { message?: string } } })?.response?.data?.message

/** The outsourcing list: overall table plus one tab per vendor (外协清单契约 → 页面). */
export default function OutsourceListPage() {
  const permissions = useAuth((state) => state.permissions)
  const canImport = permissions.includes(OUTSOURCE_PERMISSIONS.import)
  const canManage = permissions.includes(OUTSOURCE_PERMISSIONS.manage)
  const [tab, setTab] = useState<string>(ALL)
  const [filters, setFilters] = useState<Filters>({ keyword: '', includeClosed: false })
  const [keywordDraft, setKeywordDraft] = useState('')
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [items, setItems] = useState<Item[]>([])
  const [total, setTotal] = useState(0)
  const [summary, setSummary] = useState<Summary>({ total: 0, vendors: [] })
  const [vendors, setVendors] = useState<Vendor[]>([])
  const [types, setTypes] = useState<OutsourceType[]>([])
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
    vendorId: tab === ALL ? undefined : Number(tab),
    typeId: filters.typeId,
    status: filters.status,
    sendStatus: filters.sendStatus,
    dateFrom: filters.dateRange?.[0],
    dateTo: filters.dateRange?.[1],
    keyword: filters.keyword || undefined,
    includeClosed: filters.includeClosed ? 'true' : undefined,
  }), [tab, filters])

  const loadReference = useCallback(async () => {
    const [vendorResponse, typeResponse] = await Promise.all([
      http.get<ApiResponses['GET /outsource/settings/vendors']>('/outsource/settings/vendors'),
      http.get<ApiResponses['GET /outsource/settings/types']>('/outsource/settings/types'),
    ])
    setVendors(vendorResponse.data); setTypes(typeResponse.data)
  }, [])

  const load = useCallback(async () => {
    const seq = ++sequence.current
    setLoading(true); setError(false)
    try {
      const [listResponse, summaryResponse, syncResponse] = await Promise.all([
        http.get<ApiResponses['GET /outsource/items']>('/outsource/items', { params: { ...params, page, pageSize } }),
        http.get<ApiResponses['GET /outsource/items/summary']>('/outsource/items/summary', { params: { includeClosed: params.includeClosed } }),
        http.get<ApiResponses['GET /outsource/sync-status']>('/outsource/sync-status'),
      ])
      if (seq !== sequence.current) return
      setItems(listResponse.data.list); setTotal(Number(listResponse.data.total)); setSummary(summaryResponse.data); setSyncStatus(syncResponse.data)
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
  const counts = new Map(summary.vendors.map((row) => [row.vendorId, row.count]))
  const tabVendors = vendors.filter((vendor) => vendor.status === 'ACTIVE' || (counts.get(vendor.id) ?? 0) > 0)
  const selectedItems = items.filter((item) => selected.includes(item.id))

  const syncNow = async () => {
    if (syncing) return
    setSyncing(true)
    try {
      const response = await http.post<ApiResponses['POST /outsource/items/sync']>('/outsource/items/sync', {
        itemIds: selected.length > 0 ? selected : null,
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
      const vendorName = tab === ALL ? '' : `_${vendors.find((vendor) => vendor.id === Number(tab))?.name ?? ''}`
      const url = URL.createObjectURL(response.data as Blob)
      const anchor = document.createElement('a')
      anchor.href = url; anchor.download = `外协清单${vendorName}_${today()}.xlsx`
      document.body.appendChild(anchor); anchor.click(); anchor.remove()
      window.setTimeout(() => URL.revokeObjectURL(url), 0)
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
      throw new Error(serverMessage(error) ?? '预览失败，请稍后重试')
    }
  }
  const commitImport = async (rows: ImportRow[]): Promise<ImportBatchOutcome> => {
    const { data } = await http.post<ApiResponses['POST /outsource/items/import']>('/outsource/items/import', { rows })
    return { ...data, rows: data.rows.map((row) => ({ ...row, status: row.status as ImportRowOutcome['status'] })) }
  }

  const date = (value?: string | null) => value ?? ''
  const rawColumns: TableColumnProps<Item>[] = [
    { title: '外发日期', dataIndex: 'outsourceDate', width: 110, fixed: 'left' },
    { title: '工令', dataIndex: 'workOrderNo', width: 110, fixed: 'left' },
    { title: '客户', dataIndex: 'customer', width: 150, ellipsis: true },
    { title: '机型', dataIndex: 'machineModel', width: 120, ellipsis: true },
    { title: '机构课别', dataIndex: 'mechanicalSection', width: 110, ellipsis: true },
    { title: '机构执行人', dataIndex: 'mechanicalOwner', width: 100, ellipsis: true },
    { title: '电控执行人', dataIndex: 'electricalOwner', width: 100, ellipsis: true },
    { title: '工令下发', width: 110, render: (_, item) => date(item.workOrderIssuedOn) },
    { title: 'BOM时间', width: 110, render: (_, item) => date(item.bomOn) },
    { title: '发图', width: 110, render: (_, item) => date(item.drawingReleasedOn) },
    {
      title: '外发类型', width: 200, render: (_, item) => <Space size={4}>
        <span>{item.typeLabel ?? item.typeCode ?? ''}</span>
        {MATCH_PROBLEM[item.matchState] && <Tooltip content={MATCH_PROBLEM[item.matchState]}><Tag size="small" color="orangered">不符</Tag></Tooltip>}
      </Space>,
    },
    { title: '原始交期', width: 110, render: (_, item) => date(item.originalDueDate) },
    { title: '最新交期', width: 110, render: (_, item) => <LatestDueCell item={item} /> },
    { title: '数量', dataIndex: 'quantity', width: 70, align: 'center' },
    { title: '外发厂商', dataIndex: 'vendorName', width: 110, ellipsis: true },
    { title: '备注', dataIndex: 'remark', width: 180, ellipsis: true },
    {
      title: '状态', width: 80, align: 'center',
      render: (_, item) => <Tag color={STATUS_TAG[item.status]?.color}>{STATUS_TAG[item.status]?.label ?? item.status}</Tag>,
    },
    {
      title: '发送状态', width: 120, align: 'center',
      render: (_, item) => <Space size={4}>
        <Tag color={SEND_TAG[item.sendStatus]?.color}>{SEND_TAG[item.sendStatus]?.label ?? item.sendStatus}</Tag>
        {item.closed && <Tag color="gray">已结案</Tag>}
      </Space>,
    },
    {
      title: '操作', width: canManage ? 80 : 56, align: 'center', fixed: 'right',
      render: (_, item) => <Space size={0}>
        <Button type="text" size="mini" icon={<IconHistory />} aria-label={`工令 ${item.workOrderNo} 变更履历`} onClick={() => setHistory(item)} />
        {canManage && <Button type="text" size="mini" disabled={item.closed} icon={<IconEdit />}
          aria-label={`编辑工令 ${item.workOrderNo}`} onClick={() => setEditing(item)} />}
      </Space>,
    },
  ]
  const columns = rawColumns.map((column) => ({ ...column, onHeaderCell: columnHeaderProps }))

  return <Card className="page-card page-card--table outsource-list-page">
    <h1 className="visually-hidden">外协清单</h1>
    <Tabs className="outsource-list-tabs" activeTab={tab} onChange={(value) => { setTab(value); setPage(1); setSelected([]) }}>
      <Tabs.TabPane key={ALL} title={`总表 (${summary.total})`} />
      {tabVendors.map((vendor) => <Tabs.TabPane key={String(vendor.id)} title={`${vendor.name} (${counts.get(vendor.id) ?? 0})`} />)}
    </Tabs>
    <div className="page-toolbar responsive-toolbar outsource-list-toolbar">
      <Space wrap>
        <Input.Search aria-label="搜索工令、客户或机型" placeholder="工令、客户或机型" value={keywordDraft} allowClear style={{ width: 200 }}
          onChange={setKeywordDraft} onSearch={(value) => changeFilters({ keyword: value.trim() })}
          onClear={() => changeFilters({ keyword: '' })} />
        <Select aria-label="外发类型" placeholder="外发类型" allowClear style={{ width: 200 }} value={filters.typeId}
          onChange={(value) => changeFilters({ typeId: value })}
          options={types.map((type) => ({ label: `${type.code}【${type.name}】`, value: type.id }))} />
        <Select aria-label="状态" placeholder="状态" allowClear style={{ width: 100 }} value={filters.status}
          onChange={(value) => changeFilters({ status: value })} options={STATUS_OPTIONS} />
        <Select aria-label="发送状态" placeholder="发送状态" allowClear style={{ width: 140 }} value={filters.sendStatus}
          onChange={(value) => changeFilters({ sendStatus: value })} options={SEND_STATUS_OPTIONS} />
        <DatePicker.RangePicker aria-label="外发日期" style={{ width: 240 }} placeholder={['外发日期起', '外发日期止']}
          value={filters.dateRange} onChange={(value) => changeFilters({ dateRange: value?.length === 2 ? [value[0], value[1]] : undefined })} />
        <Checkbox checked={filters.includeClosed} onChange={(value) => changeFilters({ includeClosed: value })}>显示已结案</Checkbox>
      </Space>
      <Space wrap>
        {canManage && selected.length > 0 && <>
          <Button type="primary" icon={<IconEmail />} onClick={() => setMailing(selected)}>发送邮件 ({selected.length})</Button>
          <Button icon={<IconEdit />} onClick={() => setBatchOpen(true)}>批量修改 ({selected.length})</Button>
          <Popconfirm title={`删除所选 ${selected.length} 条记录？已发送给厂商的记录不能删除。`} onOk={removeSelected}>
            <Button status="danger">删除</Button>
          </Popconfirm>
        </>}
        {syncStatus && <SyncBadge status={syncStatus} />}
        {canManage && <Button icon={<IconSync />} loading={syncing} disabled={!syncStatus?.ipmsConfigured || syncStatus.running}
          onClick={() => void syncNow()}>{selected.length > 0 ? `同步所选 (${selected.length})` : '立即同步'}</Button>}
        <Button icon={<IconDownload />} loading={exporting} onClick={() => void exportItems()}>导出</Button>
        {canImport && <Button type="primary" icon={<IconUpload />} onClick={() => setImportOpen(true)}>汇入</Button>}
      </Space>
    </div>
    {error ? <LoadError message="外协清单加载失败" onRetry={() => { void loadReference().catch(() => undefined); void load() }} />
      : <Table<Item> className="page-table outsource-table" rowKey="id" loading={loading} data={items} columns={columns}
        rowClassName={(item) => item.status === 'PAUSED' ? 'outsource-row--paused' : item.status === 'CANCELLED' ? 'outsource-row--cancelled' : ''}
        rowSelection={canManage ? {
          type: 'checkbox', selectedRowKeys: selected, onChange: (keys) => setSelected(keys as number[]),
          checkboxProps: (item) => ({ disabled: item.closed }),
        } : undefined}
        noDataElement={loading ? <span /> : <Empty description="暂无外协记录" />}
        scroll={{ x: 2350, y: 'var(--page-table-scroll-y)' }}
        pagination={{
          current: page, pageSize, total, showTotal: true, sizeCanChange: true, sizeOptions: [20, 50, 100],
          onChange: (next, size) => { setPage(next); setPageSize(size) },
        }} />}
    <ItemHistoryDrawer item={history} onClose={() => setHistory(null)} />
    <EditDialog item={editing} vendors={vendors} types={types} onClose={() => setEditing(null)}
      onSaved={async () => { const ids = editing ? [editing.id] : []; setEditing(null); await load(); offerMail(ids) }} />
    <BatchDialog visible={batchOpen} items={selectedItems} vendors={vendors} types={types} onClose={() => setBatchOpen(false)}
      onSaved={async () => { const ids = selectedItems.map((item) => item.id); setBatchOpen(false); await load(); offerMail(ids) }} />
    <SendMailDialog itemIds={mailing} onClose={() => setMailing(null)}
      onSent={() => { setMailing(null); setSelected([]); void load() }} />
    <ExcelImportDialog<ImportRow> visible={importOpen} title="汇入外协清单" templateName="外协清单汇入模板.xlsx"
      requiredHeaders={IMPORT_HEADERS} maxRows={500} keyTitle="工令"
      notice={<>填写外发日期、工令、外发厂商和外发类型，其余字段由 IPMS 带出。已存在或文件内重复的工令会跳过，外发类型与厂商不符的行不会汇入。最多 500 行。</>}
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

function EditDialog({ item, vendors, types, onClose, onSaved }: {
  item: Item | null; vendors: Vendor[]; types: OutsourceType[]; onClose: () => void; onSaved: () => Promise<void>
}) {
  const [form] = Form.useForm()
  const [saving, setSaving] = useState(false)
  const selectedTypeId = Form.useWatch('typeId', form) as number | undefined
  useEffect(() => {
    if (item) form.setFieldsValue({ outsourceDate: item.outsourceDate, vendorId: item.vendorId, typeId: item.typeId ?? undefined, status: item.status, remark: item.remark ?? '' })
  }, [item, form])
  const save = async () => {
    if (!item || saving) return
    const values = await form.validate().catch(() => null)
    if (!values) return
    setSaving(true)
    try {
      await http.put<ApiResponses['PUT /outsource/items/{id}']>(`/outsource/items/${item.id}`, {
        outsourceDate: values.outsourceDate, vendorId: values.vendorId, typeId: values.typeId, status: values.status,
        remark: values.remark?.trim() ?? '', expectedUpdatedAt: item.updatedAt,
      })
      Message.success('外协记录已保存'); await onSaved()
    } catch { /* 统一请求层提示，保留输入 */ } finally { setSaving(false) }
  }
  return <Modal className="form-dialog" title={item ? `编辑工令 ${item.workOrderNo}` : '编辑'} visible={item !== null} onOk={() => void save()}
    onCancel={() => { if (!saving) onClose() }} confirmLoading={saving} closable={!saving} maskClosable={!saving} okText="保存" cancelText="取消" unmountOnExit>
    <Form form={form} layout="vertical" disabled={saving} onSubmit={() => void save()}>
      <Form.Item field="outsourceDate" label="外发日期" rules={[{ required: true, message: '请选择外发日期' }]}><DatePicker style={{ width: '100%' }} /></Form.Item>
      <Form.Item field="typeId" label="外发类型" rules={[{ required: true, message: '请选择外发类型' }]}
        extra={item && item.typeId == null ? '当前外发类型不在字典中，请重新选择' : undefined}>
        <Select aria-label="外发类型" options={typeOptions(types)}
          onChange={(value: number) => {
            const allowed = types.find((type) => type.id === value)?.vendorIds ?? []
            const vendorId = form.getFieldValue('vendorId') as number | undefined
            if (vendorId !== undefined && !(item && value === item.typeId) && !allowed.includes(vendorId)) form.setFieldValue('vendorId', undefined)
          }} />
      </Form.Item>
      <Form.Item field="vendorId" label="外发厂商" rules={[{ required: true, message: '请选择外发厂商' }]}>
        <Select aria-label="外发厂商" disabled={!item || selectedTypeId === undefined} options={item ? vendorOptions(vendors, types, item, selectedTypeId) : []} />
      </Form.Item>
      <Form.Item field="status" label="状态" rules={[{ required: true }]}><Select options={STATUS_OPTIONS} /></Form.Item>
      <Form.Item field="remark" label="备注" rules={[{ maxLength: 500, message: '备注最多 500 个字符' }]}>
        <Input.TextArea autoSize={{ minRows: 2, maxRows: 5 }} maxLength={500} />
      </Form.Item>
      <EnterSubmit />
    </Form>
  </Modal>
}

function BatchDialog({ visible, items, vendors, types, onClose, onSaved }: {
  visible: boolean; items: Item[]; vendors: Vendor[]; types: OutsourceType[]; onClose: () => void; onSaved: () => Promise<void>
}) {
  const [outsourceDate, setOutsourceDate] = useState<string>()
  const [vendorId, setVendorId] = useState<number>()
  const [typeId, setTypeId] = useState<number>()
  const [status, setStatus] = useState<string>()
  const [changeRemark, setChangeRemark] = useState(false)
  const [remark, setRemark] = useState('')
  const [saving, setSaving] = useState(false)
  const reset = () => { setOutsourceDate(undefined); setVendorId(undefined); setTypeId(undefined); setStatus(undefined); setChangeRemark(false); setRemark('') }
  const nothing = !outsourceDate && vendorId === undefined && typeId === undefined && !status && !changeRemark
  const save = async () => {
    if (saving || nothing) return
    setSaving(true)
    try {
      await http.put<ApiResponses['PUT /outsource/items/batch']>('/outsource/items/batch', {
        items: items.map((item) => ({ id: item.id, expectedUpdatedAt: item.updatedAt })),
        outsourceDate, vendorId, typeId, status, remark: changeRemark ? remark.trim() : undefined,
      })
      Message.success(`已修改 ${items.length} 条记录`); reset(); await onSaved()
    } catch { /* 统一请求层提示，保留输入 */ } finally { setSaving(false) }
  }
  return <Modal className="form-dialog" title={`批量修改 ${items.length} 条记录`} visible={visible} onOk={() => void save()}
    okButtonProps={{ disabled: nothing }} onCancel={() => { if (!saving) { reset(); onClose() } }} confirmLoading={saving}
    okText="保存" cancelText="取消">
    <Form layout="vertical" disabled={saving}>
      <Form.Item label="外发日期"><DatePicker style={{ width: '100%' }} placeholder="不修改" value={outsourceDate} onChange={(value) => setOutsourceDate(value || undefined)} /></Form.Item>
      <Form.Item label="外发厂商"><Select allowClear placeholder="不修改" value={vendorId} onChange={setVendorId}
        options={vendors.filter((vendor) => vendor.status === 'ACTIVE').map((vendor) => ({ label: vendor.name, value: vendor.id }))} /></Form.Item>
      <Form.Item label="外发类型"><Select aria-label="外发类型" allowClear placeholder="不修改" value={typeId} onChange={setTypeId}
        options={typeOptions(types)} /></Form.Item>
      <Form.Item label="状态"><Select allowClear placeholder="不修改" value={status} onChange={setStatus} options={STATUS_OPTIONS} /></Form.Item>
      <Form.Item label={<Checkbox checked={changeRemark} onChange={setChangeRemark}>修改备注</Checkbox>}>
        <Input.TextArea aria-label="备注" disabled={!changeRemark} value={remark} onChange={setRemark} maxLength={500} autoSize={{ minRows: 2, maxRows: 4 }} />
      </Form.Item>
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
