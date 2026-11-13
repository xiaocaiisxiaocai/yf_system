import { useCallback, useEffect, useRef, useState } from 'react'
import {
  Button, Card, Checkbox, Descriptions, Drawer, Empty, Form, Input, InputNumber, Message, Modal, Popconfirm, Select, Space, Switch, Table, Tabs, Tag,
  TimePicker,
} from '@arco-design/web-react'
import type { TableColumnProps } from '@arco-design/web-react'
import { IconPlus } from '@arco-design/web-react/icon'
import http from '../../api/client'
import { fmtTime, type ApiResponses } from '../../api/types'
import { actionSlots } from '../../components/ActionSlots'
import EnterSubmit from '../../components/EnterSubmit'
import LoadError from '../../components/LoadError'
import MesMirrorPanel from './MesMirrorPanel'
import './OutsourceSettingsPage.css'

type Vendor = ApiResponses['GET /outsource/settings/vendors'][number]
type Contact = Vendor['contacts'][number]
type OutsourceType = ApiResponses['GET /outsource/settings/types'][number]
type CcUser = ApiResponses['GET /outsource/settings/default-cc'][number]
type SyncStatus = ApiResponses['GET /outsource/sync-status']
type TabKey = 'vendors' | 'types' | 'cc' | 'sync'

const ACTIVE = 'ACTIVE'
const DISABLED = 'DISABLED'
const statusTag = (status: string) => <Tag color={status === ACTIVE ? 'green' : 'gray'}>{status === ACTIVE ? '启用' : '停用'}</Tag>
const nextSortNo = (rows: { sortNo: number }[]) => Math.min(9999, Math.max(0, ...rows.map((row) => row.sortNo)) + 10)
const typeLabel = (type: OutsourceType) => `${type.code}【${type.name}】`
const columnHeaderProps = () => ({ scope: 'col' }) as unknown as ReturnType<NonNullable<TableColumnProps['onHeaderCell']>>
const withColumnHeaders = <T extends TableColumnProps>(columns: T[]) => columns.map((column) => ({ ...column, onHeaderCell: columnHeaderProps }))
const lengthRule = (label: string, max: number, required = true) => ({
  validator: (value: string | undefined, callback: (error?: string) => void) => {
    const length = Array.from(value?.trim() ?? '').length
    callback((required && length === 0) || length > max ? `${label}需为 ${required ? 1 : 0}–${max} 个字符` : undefined)
  },
})

/** Purchasing-maintained reference data of the outsourcing list (外协清单契约 → 设定维护). */
export default function OutsourceSettingsPage() {
  const [tab, setTab] = useState<TabKey>('vendors')
  const [vendors, setVendors] = useState<Vendor[]>([])
  const [types, setTypes] = useState<OutsourceType[]>([])
  const [cc, setCc] = useState<CcUser[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(false)
  const sequence = useRef(0)

  const load = useCallback(async () => {
    const seq = ++sequence.current
    setLoading(true); setError(false)
    try {
      const [vendorResponse, typeResponse, ccResponse] = await Promise.all([
        http.get<ApiResponses['GET /outsource/settings/vendors']>('/outsource/settings/vendors'),
        http.get<ApiResponses['GET /outsource/settings/types']>('/outsource/settings/types'),
        http.get<ApiResponses['GET /outsource/settings/default-cc']>('/outsource/settings/default-cc'),
      ])
      if (seq === sequence.current) { setVendors(vendorResponse.data); setTypes(typeResponse.data); setCc(ccResponse.data) }
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

  return <Card className="page-card page-card--table outsource-settings-page">
    <h1 className="visually-hidden">外协清单设定</h1>
    <Tabs className="outsource-settings-tabs" activeTab={tab} onChange={(value) => setTab(value as TabKey)}>
      <Tabs.TabPane key="vendors" title="外协厂商" />
      <Tabs.TabPane key="types" title="外发类型" />
      <Tabs.TabPane key="cc" title="默认抄送" />
      <Tabs.TabPane key="sync" title="IPMS 同步" />
    </Tabs>
    {error ? <LoadError message="设定加载失败" onRetry={load} />
      : tab === 'vendors' ? <VendorsTab vendors={vendors} loading={loading} reload={load} />
        : tab === 'types' ? <TypesTab types={types} vendors={vendors} loading={loading} reload={load} />
          : tab === 'cc' ? <DefaultCcTab cc={cc} loading={loading} onChange={setCc} />
            : <SyncTab />}
  </Card>
}

function useSaving() {
  const [saving, setSaving] = useState(false)
  const lock = useRef(false)
  const run = async (action: () => Promise<void>) => {
    if (lock.current) return
    lock.current = true; setSaving(true)
    try { await action() } catch { /* 统一请求层展示具体错误，保留表单供修正。 */ } finally { lock.current = false; setSaving(false) }
  }
  return { saving, run }
}

const toggled = (status: string) => ({ status: status === ACTIVE ? DISABLED : ACTIVE })

function VendorsTab({ vendors, loading, reload }: { vendors: Vendor[]; loading: boolean; reload: () => Promise<void> }) {
  const [editing, setEditing] = useState<Vendor | null>(null)
  const [open, setOpen] = useState(false)
  const [contactsOf, setContactsOf] = useState<number | null>(null)
  const [form] = Form.useForm()
  const { saving, run } = useSaving()
  const contactVendor = vendors.find((vendor) => vendor.id === contactsOf) ?? null

  const startEdit = (vendor?: Vendor) => {
    setEditing(vendor ?? null); form.resetFields()
    form.setFieldsValue(vendor ? { name: vendor.name, remark: vendor.remark ?? '', sortNo: vendor.sortNo } : { name: '', remark: '', sortNo: nextSortNo(vendors) })
    setOpen(true)
  }
  const save = () => run(async () => {
    const values = await form.validate().catch(() => null)
    if (!values) return
    const body = { name: values.name.trim(), remark: values.remark?.trim() || null, sortNo: values.sortNo ?? 0 }
    if (editing) await http.put<ApiResponses['PUT /outsource/settings/vendors/{id}']>(`/outsource/settings/vendors/${editing.id}`, body)
    else await http.post<ApiResponses['POST /outsource/settings/vendors']>('/outsource/settings/vendors', body)
    Message.success('外协厂商已保存'); setOpen(false); await reload()
  })
  const toggle = (vendor: Vendor) => run(async () => {
    await http.put<ApiResponses['PUT /outsource/settings/vendors/{id}/status']>(`/outsource/settings/vendors/${vendor.id}/status`, toggled(vendor.status))
    await reload()
  })

  const columns = withColumnHeaders([
    { title: '厂商', dataIndex: 'name', width: 160, ellipsis: true },
    {
      title: '联络邮箱', width: 360, render: (_: unknown, vendor: Vendor) => {
        const active = vendor.contacts.filter((contact) => contact.status === ACTIVE)
        return active.length ? <Space wrap size={4}>{active.map((contact) => <Tag key={contact.id}>{contact.email}</Tag>)}</Space>
          : <span className="outsource-muted">未设置，无法发送邮件</span>
      },
    },
    { title: '备注', dataIndex: 'remark', width: 200, ellipsis: true },
    { title: '状态', width: 90, align: 'center' as const, render: (_: unknown, vendor: Vendor) => statusTag(vendor.status) },
    {
      title: '操作', width: 200, align: 'center' as const, render: (_: unknown, vendor: Vendor) => actionSlots([
        <Button key="edit" type="text" size="mini" onClick={() => startEdit(vendor)}>编辑</Button>,
        <Button key="contacts" type="text" size="mini" onClick={() => setContactsOf(vendor.id)}>联络邮箱</Button>,
        <Button key="status" type="text" size="mini" status={vendor.status === ACTIVE ? 'danger' : undefined} disabled={saving}
          onClick={() => void toggle(vendor)}>{vendor.status === ACTIVE ? '停用' : '启用'}</Button>,
      ], 'outsource'),
    },
  ])

  return <>
    <div className="page-toolbar responsive-toolbar outsource-settings-toolbar">
      <span />
      <Button type="primary" icon={<IconPlus />} disabled={loading} onClick={() => startEdit()}>新增厂商</Button>
    </div>
    <Table<Vendor> className="page-table" rowKey="id" loading={loading} data={vendors} pagination={false}
      noDataElement={loading ? <span /> : <Empty description="暂无外协厂商" />} scroll={{ x: 1010, y: 'var(--page-table-scroll-y)' }}
      columns={columns as TableColumnProps<Vendor>[]} />
    <Modal className="form-dialog" title={editing ? '编辑外协厂商' : '新增外协厂商'} visible={open} onOk={save}
      onCancel={() => { if (!saving) setOpen(false) }} confirmLoading={saving} closable={!saving} maskClosable={!saving}
      okText={editing ? '保存' : '创建'} cancelText="取消">
      <Form form={form} layout="vertical" disabled={saving} onSubmit={() => void save()}>
        <Form.Item field="name" label="厂商名称" rules={[lengthRule('厂商名称', 64)]}><Input autoFocus maxLength={64} /></Form.Item>
        <Form.Item field="remark" label="备注" rules={[lengthRule('备注', 255, false)]}><Input.TextArea autoSize={{ minRows: 2, maxRows: 4 }} maxLength={255} /></Form.Item>
        <Form.Item field="sortNo" label="排序号"><InputNumber min={0} max={9999} precision={0} style={{ width: '100%' }} /></Form.Item>
        <EnterSubmit />
      </Form>
    </Modal>
    <ContactsDrawer vendor={contactVendor} onClose={() => setContactsOf(null)} reload={reload} />
  </>
}

function ContactsDrawer({ vendor, onClose, reload }: { vendor: Vendor | null; onClose: () => void; reload: () => Promise<void> }) {
  const [editing, setEditing] = useState<Contact | null>(null)
  const [open, setOpen] = useState(false)
  const [form] = Form.useForm()
  const { saving, run } = useSaving()
  const contacts = vendor?.contacts ?? []

  const startEdit = (contact?: Contact) => {
    setEditing(contact ?? null); form.resetFields()
    form.setFieldsValue(contact ? { name: contact.name ?? '', email: contact.email, sortNo: contact.sortNo } : { name: '', email: '', sortNo: nextSortNo(contacts) })
    setOpen(true)
  }
  const save = () => run(async () => {
    if (!vendor) return
    const values = await form.validate().catch(() => null)
    if (!values) return
    const body = { name: values.name?.trim() || null, email: values.email.trim(), sortNo: values.sortNo ?? 0 }
    if (editing) await http.put<ApiResponses['PUT /outsource/settings/contacts/{id}']>(`/outsource/settings/contacts/${editing.id}`, body)
    else await http.post<ApiResponses['POST /outsource/settings/vendors/{id}/contacts']>(`/outsource/settings/vendors/${vendor.id}/contacts`, body)
    Message.success('联络邮箱已保存'); setOpen(false); await reload()
  })
  const toggle = (contact: Contact) => run(async () => {
    await http.put<ApiResponses['PUT /outsource/settings/contacts/{id}/status']>(`/outsource/settings/contacts/${contact.id}/status`, toggled(contact.status))
    await reload()
  })

  const columns = withColumnHeaders([
    { title: '邮箱', dataIndex: 'email', width: 240, ellipsis: true },
    { title: '姓名', dataIndex: 'name', width: 120, ellipsis: true },
    { title: '状态', width: 80, align: 'center' as const, render: (_: unknown, contact: Contact) => statusTag(contact.status) },
    {
      title: '操作', width: 130, align: 'center' as const, render: (_: unknown, contact: Contact) => actionSlots([
        <Button key="edit" type="text" size="mini" onClick={() => startEdit(contact)}>编辑</Button>,
        <Button key="status" type="text" size="mini" status={contact.status === ACTIVE ? 'danger' : undefined} disabled={saving}
          onClick={() => void toggle(contact)}>{contact.status === ACTIVE ? '停用' : '启用'}</Button>,
      ], 'single'),
    },
  ])

  return <Drawer width={640} title={vendor ? `${vendor.name} · 联络邮箱` : '联络邮箱'} visible={vendor !== null} footer={null}
    onCancel={onClose} unmountOnExit>
    <div className="outsource-drawer-toolbar">
      <Button type="primary" icon={<IconPlus />} onClick={() => startEdit()}>新增邮箱</Button>
    </div>
    <Table<Contact> rowKey="id" data={contacts} pagination={false} columns={columns as TableColumnProps<Contact>[]}
      noDataElement={<Empty description="暂无联络邮箱" />} scroll={{ x: 570 }} />
    <Modal className="form-dialog" title={editing ? '编辑联络邮箱' : '新增联络邮箱'} visible={open} onOk={save}
      onCancel={() => { if (!saving) setOpen(false) }} confirmLoading={saving} closable={!saving} maskClosable={!saving}
      okText={editing ? '保存' : '创建'} cancelText="取消">
      <Form form={form} layout="vertical" disabled={saving} onSubmit={() => void save()}>
        <Form.Item field="email" label="邮箱" rules={[{ required: true, message: '请输入邮箱' }, { type: 'email', message: '邮箱格式不正确' }]}>
          <Input autoFocus maxLength={128} />
        </Form.Item>
        <Form.Item field="name" label="姓名" rules={[lengthRule('姓名', 32, false)]}><Input maxLength={32} /></Form.Item>
        <Form.Item field="sortNo" label="排序号"><InputNumber min={0} max={9999} precision={0} style={{ width: '100%' }} /></Form.Item>
        <EnterSubmit />
      </Form>
    </Modal>
  </Drawer>
}

function TypesTab({ types, vendors, loading, reload }: {
  types: OutsourceType[]; vendors: Vendor[]; loading: boolean; reload: () => Promise<void>
}) {
  const [editing, setEditing] = useState<OutsourceType | null>(null)
  const [open, setOpen] = useState(false)
  const [mapping, setMapping] = useState<OutsourceType | null>(null)
  const [selected, setSelected] = useState<number[]>([])
  const [form] = Form.useForm()
  const { saving, run } = useSaving()
  const vendorName = new Map(vendors.map((vendor) => [vendor.id, vendor.name]))

  const startEdit = (type?: OutsourceType) => {
    setEditing(type ?? null); form.resetFields()
    form.setFieldsValue(type ? { code: type.code, name: type.name, sortNo: type.sortNo } : { code: '', name: '', sortNo: nextSortNo(types) })
    setOpen(true)
  }
  const save = () => run(async () => {
    const values = await form.validate().catch(() => null)
    if (!values) return
    const body = { code: values.code.trim(), name: values.name.trim(), sortNo: values.sortNo ?? 0 }
    if (editing) await http.put<ApiResponses['PUT /outsource/settings/types/{id}']>(`/outsource/settings/types/${editing.id}`, body)
    else await http.post<ApiResponses['POST /outsource/settings/types']>('/outsource/settings/types', body)
    Message.success('外发类型已保存'); setOpen(false); await reload()
  })
  const saveMapping = () => run(async () => {
    if (!mapping) return
    await http.put<ApiResponses['PUT /outsource/settings/types/{id}/vendors']>(`/outsource/settings/types/${mapping.id}/vendors`, { vendorIds: selected })
    Message.success('可用厂商已保存'); setMapping(null); await reload()
  })
  const toggle = (type: OutsourceType) => run(async () => {
    await http.put<ApiResponses['PUT /outsource/settings/types/{id}/status']>(`/outsource/settings/types/${type.id}/status`, toggled(type.status))
    await reload()
  })

  const columns = withColumnHeaders([
    { title: '外发类型', width: 260, ellipsis: true, render: (_: unknown, type: OutsourceType) => typeLabel(type) },
    {
      title: '可用厂商', width: 360, render: (_: unknown, type: OutsourceType) => type.vendorIds.length
        ? <Space wrap size={4}>{type.vendorIds.map((id) => <Tag key={id}>{vendorName.get(id) ?? id}</Tag>)}</Space>
        : <span className="outsource-muted">未设置，任何厂商都无法匹配</span>,
    },
    { title: '状态', width: 90, align: 'center' as const, render: (_: unknown, type: OutsourceType) => statusTag(type.status) },
    {
      title: '操作', width: 200, align: 'center' as const, render: (_: unknown, type: OutsourceType) => actionSlots([
        <Button key="edit" type="text" size="mini" onClick={() => startEdit(type)}>编辑</Button>,
        <Button key="vendors" type="text" size="mini" onClick={() => { setSelected(type.vendorIds); setMapping(type) }}>可用厂商</Button>,
        <Button key="status" type="text" size="mini" status={type.status === ACTIVE ? 'danger' : undefined} disabled={saving}
          onClick={() => void toggle(type)}>{type.status === ACTIVE ? '停用' : '启用'}</Button>,
      ], 'outsource'),
    },
  ])
  const mappingOptions = vendors.filter((vendor) => vendor.status === ACTIVE || mapping?.vendorIds.includes(vendor.id))

  return <>
    <div className="page-toolbar responsive-toolbar outsource-settings-toolbar">
      <span />
      <Button type="primary" icon={<IconPlus />} disabled={loading} onClick={() => startEdit()}>新增外发类型</Button>
    </div>
    <Table<OutsourceType> className="page-table" rowKey="id" loading={loading} data={types} pagination={false}
      noDataElement={loading ? <span /> : <Empty description="暂无外发类型" />} scroll={{ x: 910, y: 'var(--page-table-scroll-y)' }}
      columns={columns as TableColumnProps<OutsourceType>[]} />
    <Modal className="form-dialog" title={editing ? '编辑外发类型' : '新增外发类型'} visible={open} onOk={save}
      onCancel={() => { if (!saving) setOpen(false) }} confirmLoading={saving} closable={!saving} maskClosable={!saving}
      okText={editing ? '保存' : '创建'} cancelText="取消">
      <Form form={form} layout="vertical" disabled={saving} onSubmit={() => void save()}>
        <Form.Item field="code" label="类型代码" extra="与 IPMS 中的外发类型一致，例如 OEMA"
          rules={[{ required: true, message: '请输入类型代码' }, { match: /^[A-Za-z0-9_-]{1,32}$/, message: '1–32 位字母、数字、连字符或下划线' }]}>
          <Input autoFocus maxLength={32} />
        </Form.Item>
        <Form.Item field="name" label="中文名称" rules={[lengthRule('中文名称', 64)]}><Input maxLength={64} /></Form.Item>
        <Form.Item field="sortNo" label="排序号"><InputNumber min={0} max={9999} precision={0} style={{ width: '100%' }} /></Form.Item>
        <EnterSubmit />
      </Form>
    </Modal>
    <Modal className="form-dialog" title={mapping ? `${typeLabel(mapping)} · 可用厂商` : '可用厂商'} visible={mapping !== null}
      onOk={saveMapping} onCancel={() => { if (!saving) setMapping(null) }} confirmLoading={saving} okText="保存" cancelText="取消">
      {mappingOptions.length
        ? <Checkbox.Group direction="vertical" value={selected} onChange={(value) => setSelected(value as number[])}
          options={mappingOptions.map((vendor) => ({ label: vendor.status === ACTIVE ? vendor.name : `${vendor.name}（已停用）`, value: vendor.id }))} />
        : <Empty description="请先新增外协厂商" />}
    </Modal>
  </>
}

function DefaultCcTab({ cc, loading, onChange }: { cc: CcUser[]; loading: boolean; onChange: (rows: CcUser[]) => void }) {
  const [options, setOptions] = useState<CcUser[]>([])
  const [picked, setPicked] = useState<number>()
  const { saving, run } = useSaving()
  const searchSequence = useRef(0)

  const search = async (keyword: string) => {
    const seq = ++searchSequence.current
    try {
      const response = await http.get<ApiResponses['GET /outsource/settings/cc-user-options']>('/outsource/settings/cc-user-options', { params: { keyword } })
      if (seq === searchSequence.current) setOptions(response.data)
    } catch { /* 统一请求层提示 */ }
  }
  const replace = (userIds: number[], message: string) => run(async () => {
    const response = await http.put<ApiResponses['PUT /outsource/settings/default-cc']>('/outsource/settings/default-cc', { userIds })
    onChange(response.data); Message.success(message)
  })
  const add = async () => {
    if (picked === undefined) return
    await replace([...cc.map((row) => row.userId), picked], '已加入默认抄送')
    setPicked(undefined)
  }

  const columns = withColumnHeaders([
    { title: '姓名', dataIndex: 'realName', width: 140, ellipsis: true },
    { title: '账号', dataIndex: 'employeeNo', width: 140, ellipsis: true },
    { title: '邮箱', dataIndex: 'email', width: 260, ellipsis: true },
    { title: '状态', width: 90, align: 'center' as const, render: (_: unknown, row: CcUser) => statusTag(row.status) },
    {
      title: '操作', width: 100, align: 'center' as const, render: (_: unknown, row: CcUser) => actionSlots([
        <Popconfirm key="remove" title={`将“${row.realName}”移出默认抄送？`}
          onOk={() => replace(cc.filter((item) => item.userId !== row.userId).map((item) => item.userId), '已移出默认抄送')}>
          <Button type="text" size="mini" status="danger" disabled={saving}>移除</Button>
        </Popconfirm>,
      ], 'single'),
    },
  ])

  return <>
    <div className="page-toolbar responsive-toolbar outsource-settings-toolbar">
      <Space wrap>
        <Select aria-label="选择抄送人员" placeholder="搜索姓名、账号或邮箱" showSearch filterOption={false} allowClear style={{ width: 280 }}
          value={picked} onChange={setPicked} onSearch={(value) => void search(value)} onFocus={() => { if (!options.length) void search('') }}
          options={options.filter((option) => !cc.some((row) => row.userId === option.userId))
            .map((option) => ({ label: `${option.realName}（${option.email}）`, value: option.userId }))} />
        <Button type="primary" icon={<IconPlus />} disabled={picked === undefined || saving} onClick={() => void add()}>加入</Button>
      </Space>
    </div>
    <Table<CcUser> className="page-table" rowKey="userId" loading={loading} data={cc} pagination={false}
      noDataElement={loading ? <span /> : <Empty description="暂无默认抄送人员" />} scroll={{ x: 730, y: 'var(--page-table-scroll-y)' }}
      columns={columns as TableColumnProps<CcUser>[]} />
  </>
}

const syncTime = (value?: string | null) => fmtTime(value)

function SyncTab() {
  const [status, setStatus] = useState<SyncStatus | null>(null)
  const [time, setTime] = useState<string>()
  const [error, setError] = useState(false)
  const { saving, run } = useSaving()

  const load = useCallback(async () => {
    setError(false)
    try {
      const response = await http.get<ApiResponses['GET /outsource/sync-status']>('/outsource/sync-status')
      setStatus(response.data); setTime(response.data.syncTime)
    } catch { setError(true) }
  }, [])
  useEffect(() => {
    let active = true
    void Promise.resolve().then(() => { if (active) void load() })
    return () => { active = false }
  }, [load])

  const setSchedule = (enabled: boolean) => run(async () => {
    const response = await http.put<ApiResponses['PUT /outsource/settings/sync-schedule']>('/outsource/settings/sync-schedule', { enabled })
    setStatus(response.data); Message.success(enabled ? '定时同步已开启' : '定时同步已停止')
  })

  const save = () => run(async () => {
    if (!time) return
    const response = await http.put<ApiResponses['PUT /outsource/settings/sync-time']>('/outsource/settings/sync-time', { syncTime: time })
    setStatus(response.data); setTime(response.data.syncTime); Message.success('同步时间已保存')
  })

  if (error) return <LoadError message="同步状态加载失败" onRetry={load} />
  if (!status) return null
  const trigger = status.lastTrigger === 'SCHEDULED' ? '定时' : status.lastTrigger === 'MANUAL' ? '手动' : '—'
  return <div className="outsource-sync-tab">
    <MesMirrorPanel />
    <section className="outsource-sync-section">
    <h2 className="outsource-sync-section__title">更新外协清单</h2>
    <Space wrap>
      <span>每日同步时间</span>
      <TimePicker aria-label="每日同步时间" format="HH:mm" value={time} allowClear={false} onChange={(value) => setTime(value)} style={{ width: 120 }} />
      <Button type="primary" loading={saving} disabled={!time || time === status.syncTime} onClick={() => void save()}>保存</Button>
      <span>定时同步</span>
      <Switch aria-label="定时同步" checkedText="开启" uncheckedText="停止" checked={status.scheduleEnabled} disabled={saving}
        onChange={(value) => void setSchedule(value)} />
    </Space>
    <Descriptions column={1} border className="outsource-sync-descriptions" data={[
      { label: '最近成功', value: syncTime(status.lastSuccessAt) },
      { label: '最近一次', value: `${syncTime(status.lastFinishedAt)}（${trigger}）` },
      { label: '结果', value: status.lastError
        ? <span className="outsource-sync-error">{status.lastError}</span>
        : `处理 ${status.lastProcessed}，变更 ${status.lastChanged}，结案 ${status.lastClosed}，IPMS 查无 ${status.lastMissing}` },
    ]} />
    </section>
  </div>
}
