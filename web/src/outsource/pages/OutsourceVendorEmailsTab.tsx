import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Alert, Button, Empty, Form, Input, Message, Modal, Popconfirm, Select, Space, Tag } from '@arco-design/web-react'
import type { TableColumnProps } from '@arco-design/web-react'
import { IconPlus } from '@arco-design/web-react/icon'
import http from '../../api/client'
import type { ApiResponses } from '../../api/types'
import { requestErrorMessage } from '../../api/requestError'
import { actionSlots } from '../../components/ActionSlots'
import EnterSubmit from '../../components/EnterSubmit'
import ExcelImportDialog, { type ImportBatchOutcome, type ImportPreviewRow, type ImportRowOutcome, type PreparedImport } from '../../components/ExcelImportDialog'
import LoadError from '../../components/LoadError'
import ResizableTable from '../../components/ResizableTable'
import { TABLE_PAGINATION } from '../../components/tablePagination'
import { downloadExcelWorkbook, type ExcelImportRow, type ExcelWorkbookSheet } from '../../utils/excelWorkbook'

type Vendor = ApiResponses['GET /outsource/settings/vendors'][number]
type VendorEmail = ApiResponses['GET /outsource/settings/vendor-emails'][number]
interface ImportPayload { rowNumber: number; vendorName: string; email: string }

const ACTIVE = 'ACTIVE'
const MAXIMUM_PER_VENDOR = 50
const EMAIL_PATTERN = /^[^\s@]+@[^\s@]+\.[^\s@]+$/
const IMPORT_HEADERS = ['厂商名称', '邮箱'] as const
const WORKBOOK: ExcelWorkbookSheet[] = [
  { name: '数据', headers: IMPORT_HEADERS, rows: [['示例厂商', 'example@example.com']] },
  { name: '说明', headers: ['说明'], rows: [
    ['一行一个邮箱；同一厂商有多个邮箱时，重复填写厂商名称。'],
    ['厂商是 ODM/OEMA 厂商管理里已有的厂商，名称须与系统中的一致。'],
    [`外协清单邮件寄给这里的邮箱，与 ODM/OEMA 联络人分开；只新增，已有的邮箱会跳过；每家厂商最多 ${MAXIMUM_PER_VENDOR} 个。`],
  ] },
]

/** Each vendor's outsourcing mail addresses (外协清单契约 → 外协邮箱); the vendors are the ODM/OEMA companies. */
export default function OutsourceVendorEmailsTab({ vendors }: { vendors: Vendor[] }) {
  const [rows, setRows] = useState<VendorEmail[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(false)
  const [vendorId, setVendorId] = useState<number>()
  const [email, setEmail] = useState('')
  const [adding, setAdding] = useState(false)
  const [editing, setEditing] = useState<VendorEmail | null>(null)
  const [saving, setSaving] = useState(false)
  const [importOpen, setImportOpen] = useState(false)
  const [form] = Form.useForm()
  const sequence = useRef(0)

  const load = useCallback(async () => {
    const seq = ++sequence.current
    setLoading(true); setError(false)
    try {
      const response = await http.get<ApiResponses['GET /outsource/settings/vendor-emails']>('/outsource/settings/vendor-emails')
      if (seq === sequence.current) setRows(response.data)
    } catch {
      if (seq === sequence.current) setError(true)
    } finally {
      if (seq === sequence.current) setLoading(false)
    }
  }, [])

  useEffect(() => {
    let active = true
    void Promise.resolve().then(() => { if (active) void load() })
    return () => { active = false; sequence.current += 1 }
  }, [load])

  const activeVendors = useMemo(() => vendors.filter((vendor) => vendor.status === ACTIVE), [vendors])
  const disabledIds = useMemo(() => new Set(vendors.filter((vendor) => vendor.status !== ACTIVE).map((vendor) => vendor.id)), [vendors])
  const withoutEmail = useMemo(() => {
    const served = new Set(rows.map((row) => row.vendorId))
    return activeVendors.filter((vendor) => !served.has(vendor.id))
  }, [rows, activeVendors])

  const add = async () => {
    const address = email.trim()
    if (vendorId === undefined || !address || adding) return
    if (!EMAIL_PATTERN.test(address)) { Message.warning('邮箱格式不正确'); return }
    setAdding(true)
    try {
      await http.post<ApiResponses['POST /outsource/settings/vendor-emails']>('/outsource/settings/vendor-emails', { vendorId, email: address })
      Message.success('外协邮箱已添加'); setEmail(''); await load()
    } catch {
      // 统一请求层提示，保留输入
    } finally {
      setAdding(false)
    }
  }

  const saveEdit = async () => {
    if (!editing || saving) return
    const values = await form.validate().catch(() => null)
    if (!values) return
    setSaving(true)
    try {
      await http.put<ApiResponses['PUT /outsource/settings/vendor-emails/{id}']>(`/outsource/settings/vendor-emails/${editing.id}`, { email: String(values.email).trim() })
      Message.success('外协邮箱已更新'); setEditing(null); await load()
    } catch {
      // 统一请求层提示，保留输入
    } finally {
      setSaving(false)
    }
  }

  const remove = async (row: VendorEmail) => {
    try {
      await http.delete<ApiResponses['DELETE /outsource/settings/vendor-emails/{id}']>(`/outsource/settings/vendor-emails/${row.id}`)
      Message.success('外协邮箱已删除'); await load()
    } catch {
      // 统一请求层提示
    }
  }

  const exportAll = async () => {
    try {
      await downloadExcelWorkbook('外协邮箱.xlsx', [
        { name: '数据', headers: [...IMPORT_HEADERS], rows: rows.map((row) => [row.vendorName, row.email]) },
        ...WORKBOOK.slice(1),
      ])
      Message.success(`已导出 ${rows.length} 个外协邮箱`)
    } catch (failure) {
      Message.error(requestErrorMessage(failure, '外协邮箱导出失败'))
    }
  }

  // The preview checks the file against the vendors and addresses on screen; the server checks again when saving.
  const prepareImport = (sourceRows: ExcelImportRow[]): PreparedImport<ImportPayload> => {
    const byName = new Map(vendors.map((vendor) => [vendor.name.trim().toLowerCase(), vendor]))
    const held = new Map<number, Set<string>>()
    for (const row of rows) held.set(row.vendorId, (held.get(row.vendorId) ?? new Set<string>()).add(row.email.trim().toLowerCase()))
    const repeats = new Map<string, number>()
    for (const row of sourceRows) {
      const vendor = byName.get((row.values['厂商名称']?.trim() ?? '').toLowerCase())
      const address = row.values['邮箱']?.trim().toLowerCase() ?? ''
      if (vendor && address) repeats.set(`${vendor.id}:${address}`, (repeats.get(`${vendor.id}:${address}`) ?? 0) + 1)
    }
    const payload: ImportPayload[] = []
    const preview: ImportPreviewRow[] = sourceRows.map((row) => {
      const vendorName = row.values['厂商名称']?.trim() ?? ''
      const address = row.values['邮箱']?.trim() ?? ''
      const key = address || `第 ${row.rowNumber} 行`
      const vendor = byName.get(vendorName.toLowerCase())
      const result = (status: ImportPreviewRow['status'], message?: string): ImportPreviewRow =>
        ({ rowNumber: row.rowNumber, key, detail: vendorName, status, message })
      if (!vendor) return result('INVALID', 'ODM/OEMA 厂商不存在，请先建立厂商资料')
      if (vendor.status !== ACTIVE) return result('INVALID', '厂商已停用，不能新增外协邮箱')
      if (!address) return result('INVALID', '请输入邮箱')
      if (!EMAIL_PATTERN.test(address)) return result('INVALID', '邮箱格式不正确')
      const mark = `${vendor.id}:${address.toLowerCase()}`
      if ((repeats.get(mark) ?? 0) > 1) return result('SKIPPED', '文件内该厂商的外协邮箱重复，本重复组未导入')
      if (held.get(vendor.id)?.has(address.toLowerCase())) return result('SKIPPED', '该厂商已有这个外协邮箱，未覆盖现有资料')
      const current = held.get(vendor.id) ?? new Set<string>()
      if (current.size >= MAXIMUM_PER_VENDOR) return result('INVALID', `每个厂商最多维护 ${MAXIMUM_PER_VENDOR} 个外协邮箱`)
      held.set(vendor.id, current.add(address.toLowerCase()))
      payload.push({ rowNumber: row.rowNumber, vendorName, email: address })
      return result('READY')
    })
    return { payload, preview }
  }

  const commitImport = async (payload: ImportPayload[]): Promise<ImportBatchOutcome> => {
    const { data } = await http.post<ApiResponses['POST /outsource/settings/vendor-emails/import']>('/outsource/settings/vendor-emails/import', { rows: payload })
    return { ...data, rows: data.rows.map((row) => ({ ...row, status: row.status as ImportRowOutcome['status'] })) }
  }

  const columns: TableColumnProps<VendorEmail>[] = [
    {
      title: '厂商', dataIndex: 'vendorName', width: 260, ellipsis: true,
      render: (_: unknown, row: VendorEmail) => <Space size={4}>
        <span>{row.vendorName}</span>
        {disabledIds.has(row.vendorId) && <Tag size="small">已停用</Tag>}
      </Space>,
    },
    { title: '邮箱', dataIndex: 'email', width: 360, ellipsis: true },
    {
      title: '操作', width: 120, align: 'center', render: (_: unknown, row: VendorEmail) => actionSlots([
        <Button key="edit" type="text" size="mini" onClick={() => { form.setFieldsValue({ email: row.email }); setEditing(row) }}>编辑</Button>,
        <Popconfirm key="delete" title={`删除“${row.vendorName}”的外协邮箱 ${row.email}？`} onOk={() => remove(row)}>
          <Button type="text" size="mini" status="danger">删除</Button>
        </Popconfirm>,
      ], 'single'),
    },
  ]

  return <>
    {!loading && !error && withoutEmail.length > 0 && <Alert type="warning" style={{ marginBottom: 12 }}
      content={`以下厂商还没有外协邮箱，不能给它们发送外协邮件：${withoutEmail.map((vendor) => vendor.name).join('、')}`} />}
    <div className="page-toolbar responsive-toolbar outsource-settings-toolbar">
      <Space wrap>
        <Select aria-label="选择厂商" placeholder="选择厂商（ODM/OEMA）" showSearch allowClear style={{ width: 260 }} value={vendorId} onChange={setVendorId}
          filterOption={(input, option) => String((option as { props?: { children?: unknown } }).props?.children ?? '')
            .toLowerCase().includes(input.trim().toLowerCase())}
          options={activeVendors.map((vendor) => ({ label: vendor.name, value: vendor.id }))} />
        <Input aria-label="邮箱" placeholder="外协邮箱" style={{ width: 280 }} value={email} maxLength={128} onChange={setEmail} onPressEnter={() => void add()} />
        <Button type="primary" icon={<IconPlus />} loading={adding} disabled={vendorId === undefined || !email.trim()} onClick={() => void add()}>加入</Button>
      </Space>
      <Space wrap>
        <Button onClick={() => setImportOpen(true)}>导入</Button>
        <Button disabled={rows.length === 0} onClick={() => void exportAll()}>导出</Button>
      </Space>
    </div>
    {error ? <LoadError message="外协邮箱加载失败" onRetry={load} />
      : <ResizableTable<VendorEmail> tableId="outsource-vendor-emails" className="page-table" rowKey="id" loading={loading} data={rows}
        pagination={{ ...TABLE_PAGINATION }} noDataElement={loading ? <span /> : <Empty description="暂无外协邮箱" />}
        scroll={{ x: 740, y: 'var(--page-table-scroll-y)' }} columns={columns} />}
    <Modal className="form-dialog" title={editing ? `编辑外协邮箱 · ${editing.vendorName}` : '编辑外协邮箱'} visible={editing !== null}
      onOk={() => void saveEdit()} onCancel={() => { if (!saving) setEditing(null) }} confirmLoading={saving} closable={!saving} maskClosable={!saving}
      okText="保存" cancelText="取消" unmountOnExit>
      <Form form={form} layout="vertical" disabled={saving} onSubmit={() => void saveEdit()}>
        <Form.Item field="email" label="邮箱" rules={[{ required: true, message: '请输入邮箱' }, { type: 'email', message: '邮箱格式不正确' }]}>
          <Input autoFocus maxLength={128} />
        </Form.Item>
        <EnterSubmit />
      </Form>
    </Modal>
    <ExcelImportDialog<ImportPayload> visible={importOpen} title="导入外协邮箱" templateName="外协邮箱导入模板.xlsx"
      requiredHeaders={IMPORT_HEADERS} templateSheets={WORKBOOK} keyTitle="邮箱" maxRows={500}
      notice={<>按厂商名称新增外协邮箱，一行一个邮箱；厂商必须是 ODM/OEMA 厂商管理里已有且启用的厂商。厂商已有的邮箱和文件内重复的邮箱会跳过，不会覆盖；每家厂商最多 {MAXIMUM_PER_VENDOR} 个。最多 500 行。</>}
      prepare={prepareImport} commit={commitImport}
      onImported={() => { void load() }} onClose={() => setImportOpen(false)} />
  </>
}
