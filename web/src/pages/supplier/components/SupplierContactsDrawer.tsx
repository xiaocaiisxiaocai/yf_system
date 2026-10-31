import { useCallback, useEffect, useRef, useState } from 'react'
import {
  Button, Drawer, Form, Input, InputNumber, Message, Modal, Popconfirm, Space, Table, Tag, Typography,
} from '@arco-design/web-react'
import { IconPlus } from '@arco-design/web-react/icon'
import http, { type QuietRequestConfig } from '../../../api/client'
import { actionSlots } from '../../../components/ActionSlots'
import { type ApiResponses, type SupplierContact } from '../../../api/types'
import { textLengthRule } from '../../../utils/textRules'
import type { Supplier } from './supplierTypes'
import EnterSubmit from '../../../components/EnterSubmit'

interface SupplierContactsDrawerProps {
  supplier: Supplier | null
  onClose: () => void
}

/** 厂商联络人：共用账号背后的实际人员，用于 Robot 负责人、留言/上传签名核对和厂商邮件。只能停用，不能删除。 */
export function SupplierContactsDrawer({ supplier, onClose }: SupplierContactsDrawerProps) {
  const [contacts, setContacts] = useState<SupplierContact[]>([])
  const [loading, setLoading] = useState(true)
  const [failed, setFailed] = useState(false)
  const [editOpen, setEditOpen] = useState(false)
  const [editing, setEditing] = useState<SupplierContact | null>(null)
  const [saving, setSaving] = useState(false)
  const [togglingIds, setTogglingIds] = useState<ReadonlySet<number>>(new Set())
  const requestId = useRef(0)
  const saveInFlight = useRef(false)
  const [form] = Form.useForm()

  const load = useCallback(async () => {
    if (!supplier) return
    const id = ++requestId.current
    setLoading(true)
    try {
      const response = await http.get<ApiResponses['GET /admin/suppliers/{id}/contacts']>(
        `/admin/suppliers/${supplier.id}/contacts`, { quietNetworkError: true } as QuietRequestConfig)
      if (id !== requestId.current) return
      setContacts(response.data as SupplierContact[]); setFailed(false)
    } catch {
      if (id === requestId.current) setFailed(true)
    } finally {
      if (id === requestId.current) setLoading(false)
    }
  }, [supplier])

  useEffect(() => {
    void Promise.resolve().then(load)
    return () => { requestId.current += 1 }
  }, [load])

  const submit = async () => {
    if (!supplier || saveInFlight.current) return
    saveInFlight.current = true
    try {
      const values = await form.validate().catch(() => null)
      if (!values) return
      setSaving(true)
      const payload = { name: values.name.trim(), email: values.email.trim(), sortNo: values.sortNo ?? 0 }
      if (editing) {
        await http.put<ApiResponses['PUT /admin/supplier-contacts/{id}']>(`/admin/supplier-contacts/${editing.id}`, payload)
        Message.success('联络人已更新')
      } else {
        await http.post<ApiResponses['POST /admin/suppliers/{id}/contacts']>(`/admin/suppliers/${supplier.id}/contacts`, payload)
        Message.success('联络人已添加')
      }
      setEditOpen(false)
      void load()
    } finally {
      saveInFlight.current = false
      setSaving(false)
    }
  }

  const toggle = async (contact: SupplierContact) => {
    if (togglingIds.has(contact.id)) return
    setTogglingIds((current) => new Set(current).add(contact.id))
    try {
      await http.put<ApiResponses['PUT /admin/supplier-contacts/{id}/status']>(`/admin/supplier-contacts/${contact.id}/status`, {
        status: contact.status === 'ACTIVE' ? 'DISABLED' : 'ACTIVE',
      })
      Message.success(contact.status === 'ACTIVE' ? '联络人已停用' : '联络人已启用')
      void load()
    } finally {
      setTogglingIds((current) => {
        const next = new Set(current)
        next.delete(contact.id)
        return next
      })
    }
  }

  return (
    <Drawer
      width={760}
      title={supplier ? `联络人 · ${supplier.name}` : ''}
      visible={!!supplier}
      focusLock={!editOpen}
      closable={!editOpen}
      maskClosable={!editOpen}
      escToExit={!editOpen}
      onCancel={() => { if (!editOpen) onClose() }}
      footer={null}
    >
      {failed && (
        <Space style={{ marginBottom: 12 }}>
          <Typography.Text type="error">加载失败</Typography.Text>
          <Button size="small" loading={loading} onClick={load}>重试</Button>
        </Space>
      )}
      <div className="drawer-toolbar responsive-toolbar">
        <Typography.Text type="secondary">{loading ? '联络人加载中…' : failed ? '联络人列表暂不可用' : `共 ${contacts.length} 位联络人`}</Typography.Text>
        <Button
          type="primary"
          size="small"
          icon={<IconPlus />}
          onClick={() => {
            setEditing(null)
            form.resetFields()
            setEditOpen(true)
          }}
        >
          新增联络人
        </Button>
      </div>
      {!failed && <Table
        rowKey="id"
        size="small"
        loading={loading}
        noDataElement={loading ? <span /> : undefined}
        data={contacts}
        pagination={false}
        scroll={{ x: 640 }}
        columns={[
          { title: '姓名', dataIndex: 'name', width: 120, ellipsis: true },
          { title: '邮箱', dataIndex: 'email', width: 220, ellipsis: true },
          { title: '排序', dataIndex: 'sortNo', width: 64, align: 'center' as const },
          {
            title: '状态',
            dataIndex: 'status',
            width: 64,
            align: 'center' as const,
            render: (value: string) => (value === 'ACTIVE' ? <Tag color="green">启用</Tag> : <Tag color="gray">停用</Tag>),
          },
          {
            title: '操作',
            width: 120,
            fixed: 'right' as const,
            render: (_: unknown, contact: SupplierContact) => actionSlots([
              <Button
                key="edit"
                size="mini"
                type="text"
                onClick={() => {
                  setEditing(contact)
                  form.resetFields()
                  form.setFieldsValue({ name: contact.name, email: contact.email, sortNo: contact.sortNo })
                  setEditOpen(true)
                }}
              >
                编辑
              </Button>,
              contact.status === 'ACTIVE' ? (
                <Popconfirm key="status" title="停用后不能再被选为 Robot 负责人或用于签名，确认停用？" onOk={() => toggle(contact)}>
                  <Button size="mini" type="text" status="danger" loading={togglingIds.has(contact.id)} disabled={togglingIds.has(contact.id)}>停用</Button>
                </Popconfirm>
              ) : (
                <Button key="status" size="mini" type="text" status="success" loading={togglingIds.has(contact.id)} disabled={togglingIds.has(contact.id)} onClick={() => toggle(contact)}>启用</Button>
              ),
            ], 'single'),
          },
        ]}
      />}

      <Modal
        className="form-dialog"
        title={editing ? '编辑联络人' : '新增联络人'}
        visible={editOpen}
        confirmLoading={saving}
        closable={!saving}
        maskClosable={!saving}
        escToExit={!saving}
        cancelButtonProps={{ disabled: saving }}
        okText={editing ? '保存联络人' : '添加联络人'}
        onOk={submit}
        onCancel={() => { if (!saving) setEditOpen(false) }}
      >
        <Form onSubmit={() => void submit()} form={form} layout="vertical">
          <div className="form-grid">
            <Form.Item label="姓名" field="name" rules={[{ required: true, message: '请输入姓名' }, textLengthRule('姓名', 32)]}>
              <Input autoFocus placeholder="签名时需与此姓名一致" />
            </Form.Item>
            <Form.Item label="邮箱" field="email" rules={[{ required: true, message: '请输入邮箱' }, { type: 'email', message: '邮箱格式不正确' }, textLengthRule('邮箱', 128)]}>
              <Input placeholder="name@example.com" />
            </Form.Item>
            <Form.Item label="排序" field="sortNo">
              <InputNumber min={0} max={9999} precision={0} placeholder="0" />
            </Form.Item>
          </div>
        <EnterSubmit /></Form>
      </Modal>
    </Drawer>
  )
}
