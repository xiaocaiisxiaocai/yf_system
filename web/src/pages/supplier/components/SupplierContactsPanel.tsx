import { useCallback, useEffect, useRef, useState } from 'react'
import {
  Alert, Button, Empty, Form, Input, InputNumber, Message, Modal, Tooltip, Typography,
} from '@arco-design/web-react'
import ResizableTable from '../../../components/ResizableTable'
import { IconPlus } from '@arco-design/web-react/icon'
import http, { type QuietRequestConfig } from '../../../api/client'
import { actionSlots } from '../../../components/ActionSlots'
import DangerPopconfirm from '../../../components/DangerPopconfirm'
import { type ApiResponses, type SupplierContact } from '../../../api/types'
import { textLengthRule } from '../../../utils/textRules'
import type { Supplier } from './supplierTypes'
import EnterSubmit from '../../../components/EnterSubmit'
import StatusTag from '../../../components/StatusTag'

function EllipsisText({ value }: { value: string | null | undefined }) {
  const text = value || '-'
  return (
    <Tooltip content={text}>
      <span className="supplier-ellipsis">{text}</span>
    </Tooltip>
  )
}

interface SupplierContactsPanelProps {
  supplier: Supplier
  refreshVersion?: number
}

/** 厂商联络人：共用账号背后的实际人员，用于 Robot 负责人、留言/上传签名核对和厂商邮件。只能停用，不能删除。 */
export function SupplierContactsPanel({ supplier, refreshVersion = 0 }: SupplierContactsPanelProps) {
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
  const supplierId = supplier.id

  const load = useCallback(async () => {
    const id = ++requestId.current
    setLoading(true)
    try {
      const response = await http.get<ApiResponses['GET /admin/suppliers/{id}/contacts']>(
        `/admin/suppliers/${supplierId}/contacts`,
        { quietNetworkError: true } as QuietRequestConfig,
      )
      if (id !== requestId.current) return
      setContacts(response.data as SupplierContact[])
      setFailed(false)
    } catch {
      if (id === requestId.current) setFailed(true)
    } finally {
      if (id === requestId.current) setLoading(false)
    }
  }, [supplierId])

  useEffect(() => {
    void Promise.resolve().then(load)
    return () => { requestId.current += 1 }
  }, [load])

  const appliedRefreshRef = useRef(refreshVersion)
  useEffect(() => {
    if (appliedRefreshRef.current === refreshVersion) return
    appliedRefreshRef.current = refreshVersion
    void load()
  }, [refreshVersion, load])

  const submit = async () => {
    if (saveInFlight.current) return
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
        await http.post<ApiResponses['POST /admin/suppliers/{id}/contacts']>(`/admin/suppliers/${supplierId}/contacts`, payload)
        Message.success('联络人已添加')
      }
      setEditOpen(false)
      void load()
    } catch {
      // 请求层已提示错误；保留对话框和输入，便于修正后重试。
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
    } catch {
      // 请求层已提示错误；按钮在 finally 中恢复，可再次操作。
    } finally {
      setTogglingIds((current) => {
        const next = new Set(current)
        next.delete(contact.id)
        return next
      })
    }
  }

  return (
    <section className="supplier-subpanel supplier-contacts-panel" aria-label="联络人">
      <div className="supplier-subpanel-head">
        <div className="supplier-subpanel-title">
          <h3>联络人</h3>
          <Typography.Text type="secondary">
            {loading ? '联络人加载中…' : failed ? '联络人列表暂不可用' : `共 ${contacts.length} 位联络人`}
          </Typography.Text>
        </div>
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
      <div className="supplier-subpanel-body">
        {failed && (
          <Alert
            type="error"
            style={{ marginBottom: 12 }}
            content="联络人列表加载失败，请重试。"
            action={<Button size="small" loading={loading} onClick={() => void load()}>重试</Button>}
          />
        )}
        {!loading && !failed && contacts.length > 0 && contacts.every((item) => item.status !== 'ACTIVE') && (
          <Alert type="warning" style={{ marginBottom: 12 }} content="该供应商所有联络人均已停用，不能被选为新建项目的供应商负责人。" />
        )}
        {!failed && <ResizableTable tableId="supplier-contacts"
          className="supplier-contact-table"
          rowKey="id"
          size="small"
          loading={loading}
          noDataElement={loading ? <span /> : <Empty description="暂无联络人" />}
          data={contacts}
          pagination={false}
          scroll={{ x: 600 }}
          columns={[
            { title: '姓名', dataIndex: 'name', width: 140, ellipsis: true, render: (value: string) => <EllipsisText value={value} /> },
            { title: '邮箱', dataIndex: 'email', width: 220, ellipsis: true, render: (value: string | null) => <EllipsisText value={value} /> },
            { title: '排序', dataIndex: 'sortNo', width: 64, align: 'center' as const },
            {
              title: '状态',
              dataIndex: 'status',
              width: 68,
              align: 'center' as const,
              render: (value: string) => <StatusTag status={value} size="small" />,
            },
            {
              title: '操作',
              width: 124,
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
                  <DangerPopconfirm key="status" title="停用后不能再被选为 Robot 负责人或接收通知邮件，确认停用？" disabled={togglingIds.has(contact.id)} onOk={() => toggle(contact)}>
                    <Button size="mini" type="text" status="danger" loading={togglingIds.has(contact.id)} disabled={togglingIds.has(contact.id)}>停用</Button>
                  </DangerPopconfirm>
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
                <Input autoFocus placeholder="请输入联络人姓名" />
              </Form.Item>
              <Form.Item label="邮箱" field="email" rules={[{ required: true, message: '请输入邮箱' }, { type: 'email', message: '邮箱格式不正确' }, textLengthRule('邮箱', 128)]}>
                <Input placeholder="name@example.com" />
              </Form.Item>
              <Form.Item label="排序" field="sortNo">
                <InputNumber min={0} max={9999} precision={0} placeholder="0" style={{ width: '100%' }} />
              </Form.Item>
            </div>
          <EnterSubmit /></Form>
        </Modal>
      </div>
    </section>
  )
}
