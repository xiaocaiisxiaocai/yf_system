import { useCallback, useEffect, useRef, useState } from 'react'
import { Button, Empty, Form, Input, Message, Modal, Radio, Select, Space, Switch, Tag } from '@arco-design/web-react'
import type { TableColumnProps } from '@arco-design/web-react'
import { IconArrowDown, IconArrowUp, IconPlus } from '@arco-design/web-react/icon'
import http from '../../api/client'
import type { ApiResponses } from '../../api/types'
import { actionSlots } from '../../components/ActionSlots'
import EnterSubmit from '../../components/EnterSubmit'
import LoadError from '../../components/LoadError'
import ResizableTable from '../../components/ResizableTable'
import StatusTag from '../../components/StatusTag'

type Settings = ApiResponses['GET /outsource/settings/fields']
type Field = Settings['fields'][number]

const FIELD_TYPE_LABEL: Record<string, string> = {
  TEXT: '文字', BOOLEAN: '是/否', DATE: '日期', NUMBER: '数字', SELECT: '下拉选项',
}
const SCOPE_OPTIONS = [
  { label: '所有有编辑权限的人员', value: 'ALL' },
  { label: '指定角色', value: 'ROLES' },
  { label: '指定人员', value: 'USERS' },
  { label: '内部不填写', value: 'NONE' },
]

/** Which columns the outsourcing list has, who fills each one in and what vendors see (外协清单契约 → 栏位设定). */
export default function OutsourceFieldsTab() {
  const [data, setData] = useState<Settings | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(false)
  const [saving, setSaving] = useState(false)
  const [editing, setEditing] = useState<Field | null>(null)
  const [creating, setCreating] = useState(false)
  const lock = useRef(false)

  const load = useCallback(async () => {
    setLoading(true); setError(false)
    try {
      setData((await http.get<ApiResponses['GET /outsource/settings/fields']>('/outsource/settings/fields')).data)
    } catch { setError(true) } finally { setLoading(false) }
  }, [])

  useEffect(() => {
    let active = true
    void Promise.resolve().then(() => { if (active) void load() })
    return () => { active = false }
  }, [load])

  /** One write at a time; the server's answer is the new settings list. */
  const run = async (action: () => Promise<Settings>) => {
    if (lock.current) return false
    lock.current = true; setSaving(true)
    try { setData(await action()); return true } catch { return false } finally { lock.current = false; setSaving(false) }
  }
  const update = (field: Field, body: Record<string, unknown>) =>
    run(async () => (await http.put<ApiResponses['PUT /outsource/settings/fields/{id}']>(`/outsource/settings/fields/${field.id}`, body)).data)
  const toggleStatus = (field: Field) => run(async () => (await http.put<ApiResponses['PUT /outsource/settings/fields/{id}/status']>(
    `/outsource/settings/fields/${field.id}/status`, { status: field.status === 'ACTIVE' ? 'DISABLED' : 'ACTIVE' })).data)
  const move = (field: Field, offset: number) => {
    const movable = (data?.fields ?? []).filter((item) => !item.pinned)
    const index = movable.findIndex((item) => item.id === field.id)
    const target = index + offset
    if (index < 0 || target < 0 || target >= movable.length) return
    const ids = movable.map((item) => item.id)
    ;[ids[index], ids[target]] = [ids[target], ids[index]]
    void run(async () => (await http.put<ApiResponses['PUT /outsource/settings/fields/order']>('/outsource/settings/fields/order', { fieldIds: ids })).data)
  }

  const roleName = new Map((data?.roles ?? []).map((role) => [role.id, role.name]))
  const userName = new Map((data?.users ?? []).map((user) => [user.id, user.realName]))
  const movable = (data?.fields ?? []).filter((item) => !item.pinned)
  const columns: TableColumnProps<Field>[] = [
    {
      title: '栏位', width: 200, ellipsis: true,
      render: (_, field) => <Space size={4}>
        <span>{field.label}</span>
        {field.pinned && <Tag size="small">固定在左</Tag>}
      </Space>,
    },
    {
      title: '类型', width: 200, ellipsis: true,
      render: (_, field) => field.dataType === 'SELECT' && field.options.length
        ? `${FIELD_TYPE_LABEL.SELECT}：${field.options.join('、')}` : FIELD_TYPE_LABEL[field.dataType] ?? field.dataType,
    },
    {
      title: '厂商可见', width: 100, align: 'center',
      render: (_, field) => <Switch size="small" aria-label={`${field.label} 厂商可见`} checked={field.vendorVisible}
        disabled={saving || field.vendorVisibleLocked} onChange={(value) => void update(field, { vendorVisible: value })} />,
    },
    {
      title: '内部填写', width: 260, ellipsis: true,
      render: (_, field) => !field.editorScopeAllowed ? <span className="outsource-muted">—</span>
        : field.editorScope === 'ROLES' ? `指定角色：${field.editorRoleIds.map((id) => roleName.get(id) ?? id).join('、')}`
          : field.editorScope === 'USERS' ? `指定人员：${(field.editorUserIds ?? []).map((id) => userName.get(id) ?? id).join('、')}`
          : SCOPE_OPTIONS.find((option) => option.value === field.editorScope)?.label,
    },
    { title: '状态', width: 80, align: 'center', render: (_, field) => <StatusTag status={field.status} /> },
    {
      title: '操作', width: 240, align: 'center',
      render: (_, field) => {
        const index = movable.findIndex((item) => item.id === field.id)
        return actionSlots([
          <Button key="up" type="text" size="mini" icon={<IconArrowUp />} aria-label={`${field.label} 上移`}
            disabled={saving || field.pinned || index <= 0} onClick={() => move(field, -1)} />,
          <Button key="down" type="text" size="mini" icon={<IconArrowDown />} aria-label={`${field.label} 下移`}
            disabled={saving || field.pinned || index < 0 || index >= movable.length - 1} onClick={() => move(field, 1)} />,
          <Button key="edit" type="text" size="mini" disabled={saving || (field.builtIn && !field.editorScopeAllowed)}
            onClick={() => setEditing(field)}>编辑</Button>,
          <Button key="status" type="text" size="mini" status={field.status === 'ACTIVE' ? 'danger' : undefined}
            disabled={saving || field.builtIn} onClick={() => void toggleStatus(field)}>{field.status === 'ACTIVE' ? '停用' : '启用'}</Button>,
        ], 'outsource')
      },
    },
  ]

  if (error) return <LoadError message="栏位设定加载失败" onRetry={load} />
  return <>
    <div className="page-toolbar responsive-toolbar outsource-settings-toolbar">
      <span />
      <Button type="primary" icon={<IconPlus />} disabled={loading || saving} onClick={() => setCreating(true)}>新增栏位</Button>
    </div>
    <ResizableTable<Field> tableId="outsource-fields" className="page-table" rowKey="id" loading={loading} data={data?.fields ?? []}
      pagination={false} noDataElement={loading ? <span /> : <Empty description="暂无栏位" />}
      scroll={{ x: 1080, y: 'var(--page-table-scroll-y)' }} columns={columns} />
    <FieldDialog field={editing} visible={creating || editing !== null} roles={data?.roles ?? []} users={data?.users ?? []}
      onClose={() => { setCreating(false); setEditing(null) }}
      onSave={async (body) => {
        const ok = editing
          ? await update(editing, body)
          : await run(async () => (await http.post<ApiResponses['POST /outsource/settings/fields']>('/outsource/settings/fields', body)).data)
        if (ok) { Message.success('栏位已保存'); setCreating(false); setEditing(null) }
      }} saving={saving} />
  </>
}

function FieldDialog({ field, visible, roles, users, saving, onClose, onSave }: {
  field: Field | null; visible: boolean; roles: Settings['roles']; users: NonNullable<Settings['users']>; saving: boolean
  onClose: () => void; onSave: (body: Record<string, unknown>) => Promise<void>
}) {
  const [form] = Form.useForm()
  const dataType = Form.useWatch('dataType', form) as string | undefined
  const scope = Form.useWatch('editorScope', form) as string | undefined
  const custom = field === null || !field.builtIn
  const scopeAllowed = field === null || field.editorScopeAllowed
  useEffect(() => {
    if (!visible) return
    form.resetFields()
    form.setFieldsValue(field
      ? {
          label: field.label, dataType: field.dataType, options: field.options, editorScope: field.editorScope,
          editorRoleIds: field.editorRoleIds, editorUserIds: field.editorUserIds ?? [],
        }
      : { label: '', dataType: 'TEXT', options: [] })
  }, [visible, field, form])
  const save = async () => {
    const values = await form.validate().catch(() => null)
    if (!values) return
    const body: Record<string, unknown> = {}
    if (custom) {
      body.label = values.label.trim()
      body.dataType = values.dataType
      body.options = values.dataType === 'SELECT' ? values.options : []
    }
    if (field && scopeAllowed) {
      body.editorScope = values.editorScope
      body.editorRoleIds = values.editorScope === 'ROLES' ? values.editorRoleIds : []
      body.editorUserIds = values.editorScope === 'USERS' ? values.editorUserIds : []
    }
    await onSave(body)
  }
  return <Modal className="form-dialog" title={field ? `编辑栏位 ${field.label}` : '新增栏位'} visible={visible} onOk={() => void save()}
    onCancel={() => { if (!saving) onClose() }} confirmLoading={saving} closable={!saving} maskClosable={!saving}
    okText={field ? '保存' : '创建'} cancelText="取消" unmountOnExit>
    <Form form={form} layout="vertical" disabled={saving} onSubmit={() => void save()}>
      {custom && <>
        <Form.Item field="label" label="栏位名称" rules={[{ required: true, message: '请填写栏位名称' }, { maxLength: 32, message: '最多 32 个字符' }]}>
          <Input autoFocus maxLength={32} />
        </Form.Item>
        <Form.Item field="dataType" label="类型" rules={[{ required: true }]}
          extra={field?.inUse ? '已有填写内容，不能更改类型' : undefined}>
          <Select disabled={field?.inUse} options={Object.entries(FIELD_TYPE_LABEL).map(([value, label]) => ({ label, value }))} />
        </Form.Item>
        {dataType === 'SELECT' && <Form.Item field="options" label="选项" rules={[{ required: true, type: 'array', minLength: 1, message: '请至少填写一个选项' }]}>
          <Select mode="multiple" allowCreate placeholder="输入后回车添加" />
        </Form.Item>}
      </>}
      {field && scopeAllowed && <>
        <Form.Item field="editorScope" label="内部填写">
          <Radio.Group direction="vertical" options={SCOPE_OPTIONS} />
        </Form.Item>
        {scope === 'ROLES' && <Form.Item field="editorRoleIds" label="可填写的角色"
          rules={[{ required: true, type: 'array', minLength: 1, message: '请至少选择一个角色' }]}>
          <Select aria-label="可填写的角色" mode="multiple" showSearch options={roles.map((role) => ({ label: role.name, value: role.id }))} />
        </Form.Item>}
        {scope === 'USERS' && <Form.Item field="editorUserIds" label="可填写的人员"
          rules={[{ required: true, type: 'array', minLength: 1, message: '请至少选择一位人员' }]}>
          <Select aria-label="可填写的人员" mode="multiple" showSearch
            filterOption={(input, option) => String((option as { props?: { children?: unknown } })?.props?.children ?? '').toLowerCase().includes(input.trim().toLowerCase())}>
            {users.map((user) => <Select.Option key={user.id} value={user.id}>{`${user.realName}（${user.employeeNo}）`}</Select.Option>)}
          </Select>
        </Form.Item>}
      </>}
      <EnterSubmit />
    </Form>
  </Modal>
}
