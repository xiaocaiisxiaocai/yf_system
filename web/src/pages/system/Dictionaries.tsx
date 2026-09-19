import { useCallback, useEffect, useRef, useState } from 'react'
import { Button, Card, Empty, Form, Input, InputNumber, Message, Modal, Popconfirm, Select, Space, Switch, Table, Tabs, Tag, Typography } from '@arco-design/web-react'
import type { TableColumnProps } from '@arco-design/web-react'
import { IconPlus } from '@arco-design/web-react/icon'
import http from '../../api/client'
import './Dictionaries.css'

type DictionaryType = 'ROBOT_VENDOR' | 'ROBOT_MODEL' | 'PRIORITY'

// With a fixed header (scroll.y) Arco renders the header as its own table, which browsers expose as
// plain cells; scope="col" keeps them announced as column headers.
const columnHeaderProps = () => ({ scope: 'col' }) as unknown as ReturnType<NonNullable<TableColumnProps['onHeaderCell']>>
const withColumnHeaders = <T extends TableColumnProps>(columns: T[]) => columns.map((column) => ({ ...column, onHeaderCell: columnHeaderProps }))
interface DictionaryItem {
  id: number
  type: DictionaryType
  name: string
  parentId: number | null
  parentName?: string | null
  sortNo: number
  enabled: boolean
  inUse?: boolean
}
const TYPES: { key: DictionaryType; name: string }[] = [
  { key: 'ROBOT_VENDOR', name: 'Robot 厂商' },
  { key: 'ROBOT_MODEL', name: 'Robot 型号' },
  { key: 'PRIORITY', name: '优先级' },
]

export default function Dictionaries() {
  const [type, setType] = useState<DictionaryType>('ROBOT_VENDOR')
  const [items, setItems] = useState<DictionaryItem[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(false)
  const [keyword, setKeyword] = useState('')
  const [vendorId, setVendorId] = useState<number>()
  const [open, setOpen] = useState(false)
  const [editing, setEditing] = useState<DictionaryItem | null>(null)
  const [saving, setSaving] = useState(false)
  const [form] = Form.useForm()
  const sequence = useRef(0)
  const saveLock = useRef(false)
  const label = TYPES.find(item => item.key === type)!.name
  const vendors = items.filter(item => item.type === 'ROBOT_VENDOR')

  const load = useCallback(async () => {
    const seq = ++sequence.current
    setLoading(true)
    setError(false)
    try {
      const responses = await Promise.all(TYPES.map(item => http.get<DictionaryItem[]>('/project-dictionaries', { params: { type: item.key } })))
      if (responses.some(response => !Array.isArray(response.data))) throw new Error('Invalid dictionary response')
      if (seq === sequence.current) setItems(responses.flatMap(response => response.data))
    } catch {
      if (seq === sequence.current) setError(true)
    } finally {
      if (seq === sequence.current) setLoading(false)
    }
  }, [])
  useEffect(() => {
    let active = true
    void Promise.resolve().then(() => { if (active) void load() })
    return () => {
      active = false
      sequence.current += 1
    }
  }, [load])

  const startEdit = (item?: DictionaryItem) => {
    if (loading || error) return
    setEditing(item ?? null)
    form.resetFields()
    form.setFieldsValue(item ?? {
      name: '', parentId: type === 'ROBOT_MODEL' ? vendorId : undefined,
      sortNo: Math.min(100000, Math.max(0, ...items.filter(row => row.type === type).map(row => row.sortNo)) + 10),
      enabled: true,
    })
    setOpen(true)
  }
  const save = async () => {
    if (saveLock.current) return
    saveLock.current = true
    setSaving(true)
    try {
      const values = await form.validate().catch(() => null)
      if (!values) return
      const body = {
        type, name: values.name.trim(),
        parentId: type === 'ROBOT_MODEL' ? values.parentId : null,
        sortNo: values.sortNo ?? 0, enabled: !!values.enabled,
      }
      if (editing) await http.put(`/project-dictionaries/${editing.id}`, body)
      else await http.post('/project-dictionaries', body)
      Message.success('字典已保存')
      setOpen(false)
      await load()
    } catch {
      // Request client reports the specific validation/conflict; keep the form for correction.
    } finally {
      saveLock.current = false
      setSaving(false)
    }
  }
  const remove = async (item: DictionaryItem) => {
    try {
      await http.delete(`/project-dictionaries/${item.id}`)
      Message.success('字典已删除')
      await load()
    } catch {
      // Referenced entries cannot be deleted; retain the row when the server refuses.
    }
  }
  const search = keyword.trim().toLowerCase()
  const rows = items.filter(item => item.type === type
    && (!vendorId || type !== 'ROBOT_MODEL' || item.parentId === vendorId)
    && (!search || item.name.toLowerCase().includes(search)))
    .sort((a, b) => a.sortNo - b.sortNo || a.id - b.id)
  const hasFilter = Boolean(search || type === 'ROBOT_MODEL' && vendorId)
  const hasEnabledVendor = vendors.some(item => item.enabled)
  const canCreate = !loading && !error && (type !== 'ROBOT_MODEL' || hasEnabledVendor)
  const resetFilters = () => { setKeyword(''); setVendorId(undefined) }
  const emptyContent = <div className="dictionary-empty">
    <Empty description={hasFilter ? '未找到匹配项' : `暂无${label}数据`} />
    <Typography.Text type="secondary">
      {hasFilter
        ? '请调整关键字或筛选条件'
        : type === 'ROBOT_MODEL' && !hasEnabledVendor
          ? '请先维护一个启用的 Robot 厂商'
          : `创建后可在项目中选择${label}`}
    </Typography.Text>
    {hasFilter
      ? <Button onClick={resetFilters}>清空筛选</Button>
      : type === 'ROBOT_MODEL' && !hasEnabledVendor
        ? <Button type="primary" onClick={() => setType('ROBOT_VENDOR')}>维护 Robot 厂商</Button>
        : <Button type="primary" icon={<IconPlus />} onClick={() => startEdit()}>新增 {label}</Button>}
  </div>

  return <Card className="page-card page-card--table dictionary-page">
    <div className="page-heading">
      <div><h1>数据字典</h1><p>维护项目使用的 Robot 厂商、型号和优先级</p></div>
    </div>
    <Tabs className="dictionary-tabs" activeTab={type} onChange={value => { setType(value as DictionaryType); setKeyword(''); setVendorId(undefined) }}>
      {TYPES.map(item => <Tabs.TabPane key={item.key} title={item.name} />)}
    </Tabs>
    <div className="page-toolbar responsive-toolbar dictionary-toolbar">
      <Space wrap>
        <Input.Search aria-label="搜索字典" placeholder="搜索名称" value={keyword} onChange={setKeyword} allowClear style={{ width: 240 }} />
        {type === 'ROBOT_MODEL' && <Select aria-label="筛选 Robot 厂商" placeholder="全部厂商" value={vendorId} onChange={setVendorId} allowClear showSearch style={{ width: 180 }}
          options={vendors.map(item => ({ label: item.name, value: item.id }))} />}
      </Space>
      <Button className="dictionary-add" aria-label={`新增 ${label}`} type="primary" icon={<IconPlus />} disabled={!canCreate} onClick={() => startEdit()}>新增 {label}</Button>
    </div>
    {error ? <Space style={{ padding: 24 }}><Typography.Text type="error">字典加载失败</Typography.Text><Button onClick={load}>重试</Button></Space> :
      <Table className="page-table" rowKey="id" loading={loading} data={rows} pagination={{ pageSize: 20, showTotal: true }}
        noDataElement={loading ? <span /> : emptyContent}
        scroll={{ x: type === 'ROBOT_MODEL' ? 740 : 560, y: 'var(--page-table-scroll-y)' }}
        columns={withColumnHeaders([
          { title: '名称', dataIndex: 'name', width: 240, ellipsis: true },
          ...(type === 'ROBOT_MODEL' ? [{ title: 'Robot 厂商', width: 160, render: (_: unknown, item: DictionaryItem) => item.parentName ?? vendors.find(vendor => vendor.id === item.parentId)?.name ?? '—' }] : []),
          { title: '排序号', dataIndex: 'sortNo', width: 90, align: 'center' as const },
          { title: '状态', width: 90, align: 'center' as const, render: (_: unknown, item: DictionaryItem) => <Tag color={item.enabled ? 'green' : 'gray'}>{item.enabled ? '启用' : '停用'}</Tag> },
          { title: '操作', width: 140, align: 'center' as const, render: (_: unknown, item: DictionaryItem) => <Space size={4}>
            <Button type="text" size="mini" onClick={() => startEdit(item)}>编辑</Button>
            <Popconfirm title={`删除“${item.name}”？已被引用的条目不能删除。`} onOk={() => remove(item)}>
              <Button type="text" size="mini" status="danger">删除</Button>
            </Popconfirm>
          </Space> },
        ])} />}
    <Modal className="form-dialog" title={`${editing ? '编辑' : '新增'}${label}`} visible={open}
      onOk={save} onCancel={() => { if (!saving) setOpen(false) }} confirmLoading={saving}
      closable={!saving} maskClosable={!saving} escToExit={!saving} cancelButtonProps={{ disabled: saving }}
      okText="保存" cancelText="取消">
      <Form form={form} layout="vertical" disabled={saving}>
        <Form.Item field="name" label="名称" rules={[{ required: true, message: '请输入名称' }, { validator: (value, callback) => { callback(!value?.trim() || Array.from(value.trim()).length > 128 ? '名称需为 1–128 个字符' : undefined) } }]}>
          <Input placeholder={label} />
        </Form.Item>
        {type === 'ROBOT_MODEL' && <Form.Item field="parentId" label="Robot 厂商" rules={[{ required: true, message: '请选择厂商' }]}>
          <Select placeholder="选择厂商" showSearch disabled={!!editing?.inUse} options={vendors.filter(item => item.enabled || item.id === editing?.parentId).map(item => ({ label: `${item.name}${item.enabled ? '' : '（已停用）'}`, value: item.id }))} />
        </Form.Item>}
        <Form.Item field="sortNo" label="排序号"><InputNumber min={0} max={100000} precision={0} style={{ width: '100%' }} /></Form.Item>
        <Form.Item field="enabled" label="启用" triggerPropName="checked"><Switch /></Form.Item>
      </Form>
    </Modal>
  </Card>
}
