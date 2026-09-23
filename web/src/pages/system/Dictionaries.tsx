import { useCallback, useEffect, useRef, useState } from 'react'
import { Button, Card, Empty, Form, Input, InputNumber, Message, Modal, Popconfirm, Select, Space, Switch, Table, Tabs, Tag, Tooltip, Typography } from '@arco-design/web-react'
import type { TableColumnProps } from '@arco-design/web-react'
import { IconPlus } from '@arco-design/web-react/icon'
import http from '../../api/client'
import './Dictionaries.css'
import type { ApiResponses, ProjectDictionaryOption, RobotPart } from '../../api/types'

type TabType = 'ROBOT_PART' | 'PRIORITY'
type SupplierItem = ApiResponses['GET /robot-part-supplier-options'][number]
type ManagedItem = RobotPart | ProjectDictionaryOption

const TYPES: { key: TabType; name: string }[] = [
  { key: 'ROBOT_PART', name: 'Robot 料号' },
  { key: 'PRIORITY', name: '优先级' },
]
const columnHeaderProps = () => ({ scope: 'col' }) as unknown as ReturnType<NonNullable<TableColumnProps['onHeaderCell']>>
const withColumnHeaders = <T extends TableColumnProps>(columns: T[]) => columns.map((column) => ({ ...column, onHeaderCell: columnHeaderProps }))
const isRobotPart = (item: ManagedItem): item is RobotPart => 'partNumber' in item

function expectArray<T>(value: unknown, label: string): T[] {
  if (!Array.isArray(value)) throw new Error(`${label}接口返回格式错误`)
  return value as T[]
}

export default function Dictionaries() {
  const [type, setType] = useState<TabType>('ROBOT_PART')
  const [parts, setParts] = useState<RobotPart[]>([])
  const [priorities, setPriorities] = useState<ProjectDictionaryOption[]>([])
  const [suppliers, setSuppliers] = useState<SupplierItem[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(false)
  const [keyword, setKeyword] = useState('')
  const [supplierId, setSupplierId] = useState<number>()
  const [open, setOpen] = useState(false)
  const [editing, setEditing] = useState<ManagedItem | null>(null)
  const [saving, setSaving] = useState(false)
  const [form] = Form.useForm()
  const sequence = useRef(0)
  const saveLock = useRef(false)
  const label = TYPES.find((item) => item.key === type)!.name

  const load = useCallback(async () => {
    const seq = ++sequence.current
    setLoading(true); setError(false)
    try {
      const [partResponse, priorityResponse, supplierResponse] = await Promise.all([
        http.get<ApiResponses['GET /robot-parts']>('/robot-parts'),
        http.get<ApiResponses['GET /project-dictionaries']>('/project-dictionaries', { params: { type: 'PRIORITY' } }),
        http.get<ApiResponses['GET /robot-part-supplier-options']>('/robot-part-supplier-options'),
      ])
      const nextParts = expectArray<RobotPart>(partResponse.data, 'Robot 料号')
      const nextPriorities = expectArray<ProjectDictionaryOption>(priorityResponse.data, '优先级')
      const supplierRows = expectArray<SupplierItem>(supplierResponse.data, 'Robot 厂商')
      if (seq === sequence.current) { setParts(nextParts); setPriorities(nextPriorities); setSuppliers(supplierRows) }
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

  const startEdit = (item?: ManagedItem) => {
    if (loading || error) return
    setEditing(item ?? null); form.resetFields()
    const rows = type === 'ROBOT_PART' ? parts : priorities
    const sortNo = Math.min(100000, Math.max(0, ...rows.map((row) => row.sortNo)) + 10)
    if (type === 'ROBOT_PART') {
      const part = item && isRobotPart(item) ? item : null
      form.setFieldsValue(part ?? { supplierId, partNumber: '', model: '', sortNo, enabled: true })
    } else {
      const priority = item && !isRobotPart(item) ? item : null
      form.setFieldsValue(priority ?? { name: '', sortNo, enabled: true })
    }
    setOpen(true)
  }

  const save = async () => {
    if (saveLock.current) return
    saveLock.current = true; setSaving(true)
    try {
      const values = await form.validate().catch(() => null)
      if (!values) return
      if (type === 'ROBOT_PART') {
        const body = { supplierId: Number(values.supplierId), partNumber: values.partNumber.trim(), model: values.model.trim(), sortNo: values.sortNo ?? 0, enabled: !!values.enabled }
        if (editing) await http.put<ApiResponses['PUT /robot-parts/{id}']>(`/robot-parts/${editing.id}`, body)
        else await http.post<ApiResponses['POST /robot-parts']>('/robot-parts', body)
        Message.success('Robot 料号已保存')
      } else {
        const body = { type: 'PRIORITY', name: values.name.trim(), parentId: null, sortNo: values.sortNo ?? 0, enabled: !!values.enabled }
        if (editing) await http.put<ApiResponses['PUT /project-dictionaries/{id}']>(`/project-dictionaries/${editing.id}`, body)
        else await http.post<ApiResponses['POST /project-dictionaries']>('/project-dictionaries', body)
        Message.success('优先级已保存')
      }
      setOpen(false); await load()
    } catch {
      // 统一请求层展示具体冲突，保留表单供修正。
    } finally {
      saveLock.current = false; setSaving(false)
    }
  }

  const remove = async (item: ManagedItem) => {
    try {
      if (isRobotPart(item)) await http.delete<ApiResponses['DELETE /robot-parts/{id}']>(`/robot-parts/${item.id}`)
      else await http.delete<ApiResponses['DELETE /project-dictionaries/{id}']>(`/project-dictionaries/${item.id}`)
      Message.success(`${isRobotPart(item) ? 'Robot 料号' : '优先级'}已删除`); await load()
    } catch {
      // 服务端拒绝引用中的条目时保留当前行。
    }
  }

  const search = keyword.trim().toLocaleLowerCase()
  const rows: ManagedItem[] = type === 'ROBOT_PART'
    ? parts.filter((item) => (!supplierId || item.supplierId === supplierId) && (!search || `${item.partNumber}\n${item.model}\n${item.supplierName}`.toLocaleLowerCase().includes(search)))
    : priorities.filter((item) => !search || item.name.toLocaleLowerCase().includes(search))
  rows.sort((a, b) => a.sortNo - b.sortNo || a.id - b.id)
  const hasFilter = Boolean(search || type === 'ROBOT_PART' && supplierId)
  const activeSuppliers = suppliers.filter((item) => item.status === 'ACTIVE')
  const canCreate = !loading && !error && (type !== 'ROBOT_PART' || activeSuppliers.length > 0)
  const resetFilters = () => { setKeyword(''); setSupplierId(undefined) }
  const emptyContent = <div className="dictionary-empty">
    <Empty description={hasFilter ? '未找到匹配项' : `暂无${label}数据`} />
    <Typography.Text type="secondary">{hasFilter ? '请调整关键字或筛选条件' : type === 'ROBOT_PART' && !activeSuppliers.length ? '请先维护一个启用的供应商' : `创建后可在项目中选择${label}`}</Typography.Text>
    {hasFilter ? <Button onClick={resetFilters}>清空筛选</Button> : <Button type="primary" icon={<IconPlus />} disabled={!canCreate} onClick={() => startEdit()}>新增 {label}</Button>}
  </div>

  const columns = type === 'ROBOT_PART'
    ? withColumnHeaders([
      { title: 'Robot 厂商', dataIndex: 'supplierName', width: 160, ellipsis: true },
      { title: 'Robot 料号', dataIndex: 'partNumber', width: 190, ellipsis: true },
      { title: 'Robot 型号', dataIndex: 'model', width: 280, ellipsis: true, render: (value: string) => <Tooltip content={value}><span>{value}</span></Tooltip> },
      { title: '排序号', dataIndex: 'sortNo', width: 90, align: 'center' as const },
      { title: '状态', width: 90, align: 'center' as const, render: (_: unknown, item: RobotPart) => <Tag color={item.enabled ? 'green' : 'gray'}>{item.enabled ? '启用' : '停用'}</Tag> },
      { title: '操作', width: 140, align: 'center' as const, render: (_: unknown, item: RobotPart) => <Space size={4}>
        <Button type="text" size="mini" onClick={() => startEdit(item)}>编辑</Button>
        {item.inUse ? <Tooltip content="已被项目引用，不能删除"><span><Button type="text" size="mini" status="danger" disabled>删除</Button></span></Tooltip> : <Popconfirm title={`删除 Robot 料号“${item.partNumber}”？`} onOk={() => remove(item)}><Button type="text" size="mini" status="danger">删除</Button></Popconfirm>}
      </Space> },
    ])
    : withColumnHeaders([
      { title: '名称', dataIndex: 'name', width: 280, ellipsis: true },
      { title: '排序号', dataIndex: 'sortNo', width: 90, align: 'center' as const },
      { title: '状态', width: 90, align: 'center' as const, render: (_: unknown, item: ProjectDictionaryOption) => <Tag color={item.enabled ? 'green' : 'gray'}>{item.enabled ? '启用' : '停用'}</Tag> },
      { title: '操作', width: 140, align: 'center' as const, render: (_: unknown, item: ProjectDictionaryOption) => <Space size={4}><Button type="text" size="mini" onClick={() => startEdit(item)}>编辑</Button><Popconfirm title={`删除“${item.name}”？已被引用的条目不能删除。`} onOk={() => remove(item)}><Button type="text" size="mini" status="danger">删除</Button></Popconfirm></Space> },
    ])

  const editingPart = editing && isRobotPart(editing) ? editing : null
  return <Card className="page-card page-card--table dictionary-page">
    <div className="page-heading"><div><h1>数据字典</h1><p>维护 Robot 料号、对应型号和项目优先级</p></div></div>
    <Tabs className="dictionary-tabs" activeTab={type} onChange={(value) => { setType(value as TabType); setKeyword(''); setSupplierId(undefined); setOpen(false); setEditing(null) }}>{TYPES.map((item) => <Tabs.TabPane key={item.key} title={item.name} />)}</Tabs>
    <div className="page-toolbar responsive-toolbar dictionary-toolbar">
      <Space wrap>
        <Input.Search aria-label={`搜索${label}`} placeholder={type === 'ROBOT_PART' ? '搜索料号或型号' : '搜索名称'} value={keyword} onChange={setKeyword} allowClear style={{ width: 240 }} />
        {type === 'ROBOT_PART' && <Select aria-label="筛选 Robot 厂商" placeholder="全部厂商" value={supplierId} onChange={setSupplierId} allowClear showSearch style={{ width: 180 }} options={suppliers.map((item) => ({ label: item.name, value: item.id }))} />}
      </Space>
      <Button className="dictionary-add" aria-label={`新增 ${label}`} type="primary" icon={<IconPlus />} disabled={!canCreate} onClick={() => startEdit()}>新增 {label}</Button>
    </div>
    {error ? <Space style={{ padding: 24 }}><Typography.Text type="error">基础数据加载失败</Typography.Text><Button onClick={load}>重试</Button></Space> : <Table<ManagedItem> className="page-table" rowKey="id" loading={loading} data={rows} pagination={{ pageSize: 20, showTotal: true }} noDataElement={loading ? <span /> : emptyContent} scroll={{ x: type === 'ROBOT_PART' ? 950 : 600, y: 'var(--page-table-scroll-y)' }} columns={columns as TableColumnProps<ManagedItem>[]} />}
    <Modal className="form-dialog" title={`${editing ? '编辑' : '新增'}${label}`} visible={open} onOk={save} onCancel={() => { if (!saving) setOpen(false) }} confirmLoading={saving} closable={!saving} maskClosable={!saving} escToExit={!saving} cancelButtonProps={{ disabled: saving }} okText="保存" cancelText="取消">
      <Form form={form} layout="vertical" disabled={saving}>
        {type === 'ROBOT_PART' ? <>
          <Form.Item field="supplierId" label="Robot 厂商" rules={[{ required: true, message: '请选择 Robot 厂商' }]} help={editingPart?.inUse ? '已被项目引用，厂商、料号和型号不可修改。' : undefined}><Select placeholder="选择 Robot 厂商" showSearch disabled={!!editingPart?.inUse} options={suppliers.filter((item) => item.status === 'ACTIVE' || item.id === editingPart?.supplierId).map((item) => ({ label: `${item.name}${item.status === 'ACTIVE' ? '' : '（已停用）'}`, value: item.id }))} /></Form.Item>
          <Form.Item field="partNumber" label="Robot 料号" rules={[{ required: true, message: '请输入 Robot 料号' }, { validator: (value, callback) => callback(!value?.trim() || Array.from(value.trim()).length > 128 ? 'Robot 料号需为 1–128 个字符' : undefined) }]}><Input placeholder="输入 Robot 料号" disabled={!!editingPart?.inUse} maxLength={128} showWordLimit /></Form.Item>
          <Form.Item field="model" label="Robot 型号" rules={[{ required: true, message: '请输入 Robot 型号' }, { validator: (value, callback) => callback(!value?.trim() || Array.from(value.trim()).length > 512 ? 'Robot 型号需为 1–512 个字符' : undefined) }]}><Input.TextArea placeholder="输入该料号对应的完整型号" disabled={!!editingPart?.inUse} autoSize={{ minRows: 2, maxRows: 5 }} maxLength={512} showWordLimit wordLimitPosition="outside" /></Form.Item>
        </> : <Form.Item field="name" label="名称" rules={[{ required: true, message: '请输入名称' }, { validator: (value, callback) => callback(!value?.trim() || Array.from(value.trim()).length > 128 ? '名称需为 1–128 个字符' : undefined) }]}><Input placeholder="优先级" /></Form.Item>}
        <Form.Item field="sortNo" label="排序号"><InputNumber min={0} max={100000} precision={0} style={{ width: '100%' }} /></Form.Item>
        <Form.Item field="enabled" label="启用" triggerPropName="checked"><Switch /></Form.Item>
      </Form>
    </Modal>
  </Card>
}
