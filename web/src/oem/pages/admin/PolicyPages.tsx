import { useCallback, useEffect, useState } from 'react'
import { Button, Card, Form, Input, InputNumber, Message, Modal, Select, Space, Switch, Table, Tabs, Tag, Typography } from '@arco-design/web-react'
import { IconPlus } from '@arco-design/web-react/icon'
import { fmtTime } from '../../../api/types'
import type { AuditRow, RetentionTemplate, SettingItem } from '../../api/types'
import { useCan, useOem } from '../../OemContext'
import { useLoadEffect } from '../../useLoadEffect'

const MODES = [
  { value: 'KEEP', label: '不自动删除' },
  { value: 'AFTER_RELEASE', label: '发布后到期删除' },
  { value: 'AFTER_FIRST_RECEIPT', label: '首次接收后宽限删除' },
  { value: 'FIRST_RECEIPT_OR_DEADLINE', label: '首次接收宽限或最晚期限（先到为准）' },
]

export function RetentionPage() {
  const { api } = useOem()
  const [rows, setRows] = useState<RetentionTemplate[]>([])
  const [editing, setEditing] = useState<RetentionTemplate | 'new' | null>(null)
  const [form] = Form.useForm()
  const mode = Form.useWatch('mode', form) as RetentionTemplate['mode'] | undefined

  const load = useCallback(async () => setRows(await api.retentionTemplates()), [api])
  useLoadEffect(load)

  const open = (row: RetentionTemplate | 'new') => {
    form.resetFields()
    if (row !== 'new') form.setFieldsValue({ ...row, active: row.status === 'ACTIVE' })
    else form.setFieldsValue({ mode: 'AFTER_FIRST_RECEIPT', receiptGraceMinutes: 1440, active: true })
    setEditing(row)
  }
  const save = async () => {
    const values = await form.validate()
    const body = {
      name: values.name, mode: values.mode,
      releaseTtlMinutes: ['AFTER_RELEASE', 'FIRST_RECEIPT_OR_DEADLINE'].includes(values.mode) ? values.releaseTtlMinutes : null,
      receiptGraceMinutes: ['AFTER_FIRST_RECEIPT', 'FIRST_RECEIPT_OR_DEADLINE'].includes(values.mode) ? values.receiptGraceMinutes : null,
    }
    if (editing === 'new') await api.createRetention(body)
    else if (editing) await api.updateRetention(editing.id, { ...body, status: values.active ? 'ACTIVE' : 'DISABLED', version: editing.version })
    Message.success('已保存，仅影响之后发送的传递单')
    setEditing(null)
    await load()
  }

  return (
    <Card title="删除策略模板" extra={<Button type="primary" icon={<IconPlus />} onClick={() => open('new')}>新建策略</Button>}>
      <Table rowKey="id" data={rows} pagination={false} columns={[
        { title: '名称', dataIndex: 'name' },
        { title: '规则', dataIndex: 'summary' },
        { title: '状态', width: 90, render: (_: unknown, row: RetentionTemplate) => <Tag color={row.status === 'ACTIVE' ? 'green' : 'gray'}>{row.status === 'ACTIVE' ? '启用' : '停用'}</Tag> },
        { title: '操作', width: 90, render: (_: unknown, row: RetentionTemplate) => <Button size="mini" onClick={() => open(row)}>编辑</Button> },
      ]} />
      <Modal title={editing === 'new' ? '新建删除策略' : '编辑删除策略'} visible={Boolean(editing)} onCancel={() => setEditing(null)} onOk={save} unmountOnExit>
        <Form form={form} layout="vertical">
          <Form.Item field="name" label="名称" rules={[{ required: true }, { maxLength: 64 }]}><Input /></Form.Item>
          <Form.Item field="mode" label="删除方式" rules={[{ required: true }]}><Select options={MODES} /></Form.Item>
          {(mode === 'AFTER_RELEASE' || mode === 'FIRST_RECEIPT_OR_DEADLINE') && (
            <Form.Item field="releaseTtlMinutes" label="发布后最长保留（分钟）" rules={[{ required: true }]}>
              <InputNumber min={1} max={5256000} />
            </Form.Item>
          )}
          {(mode === 'AFTER_FIRST_RECEIPT' || mode === 'FIRST_RECEIPT_OR_DEADLINE') && (
            <Form.Item field="receiptGraceMinutes" label="首次完整下载后宽限（分钟）" rules={[{ required: true }]}>
              <InputNumber min={1} max={5256000} />
            </Form.Item>
          )}
          {mode === 'AFTER_FIRST_RECEIPT' && <Typography.Text type="warning">注意：无人下载的文件在此策略下不会自动删除。</Typography.Text>}
          {editing !== 'new' && <Form.Item field="active" label="启用" triggerPropName="checked"><Switch /></Form.Item>}
        </Form>
      </Modal>
    </Card>
  )
}

function SettingsForm({ group }: { group: 'file' | 'notify' }) {
  const { api } = useOem()
  const [items, setItems] = useState<SettingItem[]>([])
  const [values, setValues] = useState<Record<string, string>>({})
  const load = useCallback(async () => {
    const list = await api.settings(group)
    setItems(list)
    setValues(Object.fromEntries(list.map((item) => [item.key, item.value])))
  }, [api, group])
  useLoadEffect(load)

  const save = async () => {
    const changed = items.filter((item) => !item.readOnly && values[item.key] !== item.value).map((item) => ({ key: item.key, value: values[item.key] }))
    if (changed.length === 0) return
    await api.updateSettings(group, changed)
    Message.success('已保存')
    await load()
  }

  return (
    <Space direction="vertical" style={{ width: '100%' }}>
      {items.map((item) => (
        <Space key={item.key} style={{ width: '100%' }}>
          <span style={{ display: 'inline-block', width: 260 }}>{item.label}</span>
          {item.kind === 'boolean'
            ? <Switch aria-label={item.label} disabled={item.readOnly} checked={values[item.key] === 'true'} onChange={(checked) => setValues((v) => ({ ...v, [item.key]: String(checked) }))} />
            : <Input style={{ width: item.kind === 'extensions' ? 520 : 220 }} value={values[item.key]}
                aria-label={item.label} disabled={item.readOnly}
                onChange={(value) => setValues((v) => ({ ...v, [item.key]: value }))} />}
          {item.min !== null && <Typography.Text type="secondary">{item.min} ~ {item.max}</Typography.Text>}
          {item.unsupportedReason && <Typography.Text type="warning">{item.unsupportedReason}</Typography.Text>}
          {item.hint && <Typography.Text type="secondary">{item.hint}</Typography.Text>}
        </Space>
      ))}
      <Button type="primary" onClick={() => void save()}>保存</Button>
    </Space>
  )
}

export function SettingsPage() {
  const can = useCan()
  return (
    <Card title="文件策略与邮件提醒">
      <Tabs defaultActiveTab={can.manageFilePolicy ? 'file' : 'notify'}>
        {can.manageFilePolicy && <Tabs.TabPane key="file" title="文件与下载策略"><SettingsForm group="file" /></Tabs.TabPane>}
        {can.manageNotify && <Tabs.TabPane key="notify" title="邮件提醒"><SettingsForm group="notify" /></Tabs.TabPane>}
      </Tabs>
    </Card>
  )
}

export function AuditPage() {
  const { api } = useOem()
  const [rows, setRows] = useState<AuditRow[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [keyword, setKeyword] = useState('')
  useEffect(() => {
    void api.auditLogs({ page, pageSize: 20, keyword: keyword || undefined }).then((result) => { setRows(result.list); setTotal(result.total) })
  }, [api, page, keyword])

  return (
    <Card title="OEM 审计日志">
      <Input.Search allowClear placeholder="搜索账号、动作或内容" style={{ width: 280, marginBottom: 12 }} onSearch={(value) => { setKeyword(value); setPage(1) }} />
      <Table rowKey="id" data={rows} pagination={{ current: page, total, pageSize: 20, onChange: setPage }} columns={[
        { title: '时间', width: 170, render: (_: unknown, row: AuditRow) => fmtTime(row.createdAt) },
        { title: '动作', dataIndex: 'action', width: 230 },
        {
          title: '操作人', width: 180,
          render: (_: unknown, row: AuditRow) => row.actorRealm === 'system' ? '系统'
            : `${row.employeeNo ?? '-'}${row.actorRealm === 'oem' ? '（OEM）' : ''}`,
        },
        { title: '对象', width: 160, render: (_: unknown, row: AuditRow) => (row.targetType ? `${row.targetType} #${row.targetId ?? ''}` : '-') },
        {
          title: '详情',
          render: (_: unknown, row: AuditRow) => {
            const detail = { ...(row.detail ?? {}) }
            delete (detail as Record<string, unknown>).auditContext
            return <Typography.Text ellipsis={{ showTooltip: true }} style={{ maxWidth: 420 }}>{JSON.stringify(detail)}</Typography.Text>
          },
        },
      ]} />
    </Card>
  )
}
