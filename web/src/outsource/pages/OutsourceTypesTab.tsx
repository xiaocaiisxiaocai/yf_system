import { useCallback, useEffect, useRef, useState } from 'react'
import { Button, Checkbox, Empty, Form, Input, InputNumber, Message, Modal, Space, Switch, Tag, Typography } from '@arco-design/web-react'
import type { TableColumnProps } from '@arco-design/web-react'
import { IconPlus } from '@arco-design/web-react/icon'
import http from '../../api/client'
import type { OutsourceTypeCreate, OutsourceTypeUpsert } from '../../api/generated/api-types'
import type { ApiResponses } from '../../api/types'
import { actionSlots } from '../../components/ActionSlots'
import EnterSubmit from '../../components/EnterSubmit'
import LoadError from '../../components/LoadError'
import ResizableTable from '../../components/ResizableTable'
import StatusTag from '../../components/StatusTag'
import useOutsourceSaving from '../useOutsourceSaving'

type Vendor = ApiResponses['GET /outsource/settings/vendors'][number]
type OutsourceType = ApiResponses['GET /outsource/settings/types'][number]

interface TypeForm {
  code: string
  name: string
  sortNo?: number
  allowElectricalHandoff: boolean
  vendorIds?: number[]
  enabled: boolean
}

const ACTIVE = 'ACTIVE'
const DISABLED = 'DISABLED'
const typeLabel = (type: OutsourceType) => `${type.code}【${type.name}】`

export default function OutsourceTypesTab() {
  const [types, setTypes] = useState<OutsourceType[]>([])
  const [vendors, setVendors] = useState<Vendor[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(false)
  const sequence = useRef(0)
  const cancelConfirmation = useRef<(() => void) | null>(null)
  const [editing, setEditing] = useState<OutsourceType | null>(null)
  const [open, setOpen] = useState(false)
  const [form] = Form.useForm<TypeForm>()
  const { saving, run } = useOutsourceSaving()

  const load = useCallback(async () => {
    const seq = ++sequence.current
    setLoading(true)
    setError(false)
    try {
      const [vendorResponse, typeResponse] = await Promise.all([
        http.get<ApiResponses['GET /outsource/settings/vendors']>('/outsource/settings/vendors'),
        http.get<ApiResponses['GET /outsource/settings/types']>('/outsource/settings/types'),
      ])
      if (seq === sequence.current) {
        setVendors(vendorResponse.data)
        setTypes(typeResponse.data)
      }
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
      cancelConfirmation.current?.()
    }
  }, [load])

  const startEdit = (type?: OutsourceType) => {
    setEditing(type ?? null)
    form.resetFields()
    form.setFieldsValue({
      code: type?.code ?? '',
      name: type?.name ?? '',
      sortNo: type?.sortNo ?? Math.min(9999, Math.max(0, ...types.map((row) => row.sortNo)) + 10),
      allowElectricalHandoff: type?.allowElectricalHandoff ?? true,
      vendorIds: type?.vendorIds ?? [],
      enabled: !type || type.status === ACTIVE,
    })
    setOpen(true)
  }

  const save = () => run(async () => {
    const seq = sequence.current
    const values = await form.validate().catch(() => null)
    if (!values || seq !== sequence.current) return
    const body = {
      code: values.code.trim(),
      name: values.name.trim(),
      sortNo: values.sortNo ?? 0,
      allowElectricalHandoff: values.allowElectricalHandoff,
    }
    if (editing) {
      const status = values.enabled ? ACTIVE : DISABLED
      const statusChanged = editing.status !== status
      const vendorIds = values.vendorIds ?? editing.vendorIds
      const vendorsChanged = vendorIds.length !== editing.vendorIds.length || vendorIds.some((id) => !editing.vendorIds.includes(id))
      if (statusChanged && status === DISABLED) {
        const confirmed = await new Promise<boolean>((resolve) => {
          const confirmation = Modal.confirm({
            title: `确认停用外发类型“${typeLabel(editing)}”？`,
            okText: '停用', cancelText: '取消', okButtonProps: { status: 'danger' },
            onOk: () => resolve(true), onCancel: () => resolve(false),
          })
          cancelConfirmation.current = () => { confirmation.close(); resolve(false) }
        })
        cancelConfirmation.current = null
        if (!confirmed || seq !== sequence.current) return
      }
      await http.put<ApiResponses['PUT /outsource/settings/types/{id}']>(`/outsource/settings/types/${editing.id}`, {
        ...body,
        vendorIds: vendorsChanged ? vendorIds : null,
        status: statusChanged ? status : null,
      } satisfies OutsourceTypeUpsert)
    } else {
      await http.post<ApiResponses['POST /outsource/settings/types']>('/outsource/settings/types', {
        ...body,
        vendorIds: values.vendorIds ?? [],
      } satisfies OutsourceTypeCreate)
    }
    if (seq !== sequence.current) return
    Message.success('外发类型已保存')
    setOpen(false)
    await load()
  })

  const vendorName = new Map(vendors.map((vendor) => [vendor.id, vendor.name]))
  const vendorOptions = vendors.filter((vendor) => vendor.status === ACTIVE || editing?.vendorIds.includes(vendor.id))
    .map((vendor) => ({ label: vendor.status === ACTIVE ? vendor.name : `${vendor.name}（已停用）`, value: vendor.id }))
  const columns: TableColumnProps<OutsourceType>[] = [
    { title: '外发类型', width: 260, align: 'left', ellipsis: true, render: (_, type) => typeLabel(type) },
    {
      title: '可用厂商', width: 360, align: 'left', render: (_, type) => type.vendorIds.length
        ? <Space wrap size={4}>{type.vendorIds.map((id) => <Tag key={id}>{vendorName.get(id) ?? id}</Tag>)}</Space>
        : <Typography.Text type="secondary">未设置，任何厂商都无法匹配</Typography.Text>,
    },
    {
      title: '转迅得电控', dataIndex: 'allowElectricalHandoff', width: 120, align: 'center',
      render: (_, type) => <Tag color={type.allowElectricalHandoff ? 'green' : 'gray'}>{type.allowElectricalHandoff ? '是' : '否'}</Tag>,
    },
    { title: '状态', width: 90, align: 'center', render: (_, type) => <StatusTag status={type.status} /> },
    {
      title: '操作', width: 100, align: 'center', render: (_, type) => actionSlots([
        <Button key="edit" type="text" size="mini" disabled={loading} onClick={() => startEdit(type)}>编辑</Button>,
      ], 'single'),
    },
  ]

  if (error) return <LoadError message="外发类型加载失败" onRetry={load} />
  return <>
    <div className="page-toolbar responsive-toolbar outsource-settings-toolbar">
      <span />
      <Button type="primary" icon={<IconPlus />} disabled={loading} onClick={() => startEdit()}>新增外发类型</Button>
    </div>
    <ResizableTable<OutsourceType> tableId="outsource-types" className="page-table" rowKey="id" loading={loading} data={types} pagination={false}
      noDataElement={loading ? <span /> : <Empty description="暂无外发类型" />} scroll={{ x: 930, y: 'var(--page-table-scroll-y)' }}
      columns={columns} />
    <Modal className="form-dialog" title={editing ? '编辑外发类型' : '新增外发类型'} visible={open} onOk={save}
      onCancel={() => { if (!saving) setOpen(false) }} confirmLoading={saving} closable={!saving} maskClosable={!saving} escToExit={!saving}
      cancelButtonProps={{ disabled: saving }} okText={editing ? '保存' : '创建'} cancelText="取消" unmountOnExit>
      <Form form={form} layout="vertical" disabled={saving} onSubmit={() => void save()}>
        <Form.Item field="code" label="类型代码" extra="与 IPMS 中的外发类型一致，例如 OEMA"
          rules={[{ required: true, message: '请输入类型代码' }, { match: /^[A-Za-z0-9_-]{1,32}$/, message: '1–32 位字母、数字、连字符或下划线' }]}>
          <Input autoFocus maxLength={32} />
        </Form.Item>
        <Form.Item field="name" label="中文名称" rules={[{
          validator: (value: string | undefined, callback: (error?: string) => void) => {
            const length = Array.from(value?.trim() ?? '').length
            callback(length === 0 || length > 64 ? '中文名称需为 1–64 个字符' : undefined)
          },
        }]}><Input maxLength={64} /></Form.Item>
        <Form.Item field="sortNo" label="排序号"><InputNumber min={0} max={9999} precision={0} style={{ width: '100%' }} /></Form.Item>
        <Form.Item field="allowElectricalHandoff" label="允许转迅得电控" triggerPropName="checked">
          <Switch aria-label="允许转迅得电控" />
        </Form.Item>
        {vendorOptions.length > 0
          ? <Form.Item field="vendorIds" label="可用厂商"><Checkbox.Group direction="vertical" options={vendorOptions} /></Form.Item>
          : <Form.Item label="可用厂商"><Empty description="请先新增外协厂商" /></Form.Item>}
        {editing && <Form.Item field="enabled" label="状态" triggerPropName="checked">
          <Switch aria-label="状态" checkedText="启用" uncheckedText="停用" />
        </Form.Item>}
        <EnterSubmit />
      </Form>
    </Modal>
  </>
}
