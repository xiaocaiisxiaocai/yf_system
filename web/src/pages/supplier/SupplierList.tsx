import { useEffect, useRef, useState } from 'react'
import {
  Button, Card, Form, Input, Message, Popconfirm, Select, Space, Table, Tag, Typography,
} from '@arco-design/web-react'
import { IconPlus } from '@arco-design/web-react/icon'
import http from '../../api/client'
import { actionSlots } from '../../components/ActionSlots'
import { useAuth } from '../../store/auth'
import { fmtTime } from '../../api/types'
import type { ApiResponses } from '../../api/types'
import { SupplierAccountsDrawer } from './components/SupplierAccountsDrawer'
import { SupplierEditModal } from './components/SupplierEditModal'
import type { Supplier } from './components/supplierTypes'
import { useSupplierListState } from './hooks/useSupplierListState'

export default function SupplierList() {
  const { hasPerm } = useAuth()
  const canManageSuppliers = hasPerm('supplier:manage')
  const canManageAccounts = hasPerm('supplier:account')
  const canDelete = hasPerm('supplier:delete')
  const {
    data, loading, status, page, pageSize, loadError,
    load, search, clearSearch, changeStatus, changePage,
  } = useSupplierListState()
  const [editOpen, setEditOpen] = useState(false)
  const [saving, setSaving] = useState(false)
  const [editing, setEditing] = useState<Supplier | null>(null)
  const [accTarget, setAccTarget] = useState<Supplier | null>(null)
  const accountTrigger = useRef<HTMLButtonElement | null>(null)
  const saveInFlight = useRef(false)
  const togglingSupplierIdsRef = useRef(new Set<number>())
  const [togglingSupplierIds, setTogglingSupplierIds] = useState<ReadonlySet<number>>(new Set())
  const [form] = Form.useForm()

  useEffect(() => {
    if (accTarget !== null) return
    const trigger = accountTrigger.current
    accountTrigger.current = null
    if (trigger?.isConnected) trigger.focus()
  }, [accTarget])

  const submit = async () => {
    if (saveInFlight.current) return
    saveInFlight.current = true
    try {
      const values = await form.validate().catch(() => null)
      if (!values) return
      setSaving(true)
      if (editing) {
        await http.put<ApiResponses['PUT /admin/suppliers/{id}']>(`/admin/suppliers/${editing.id}`, values)
        Message.success('供应商已更新')
      } else {
        await http.post<ApiResponses['POST /admin/suppliers']>('/admin/suppliers', values)
        Message.success('供应商已创建')
      }
      setEditOpen(false)
      load()
    } finally {
      saveInFlight.current = false
      setSaving(false)
    }
  }

  const toggleStatus = async (supplier: Supplier) => {
    if (togglingSupplierIdsRef.current.has(supplier.id)) return
    const activeIds = new Set(togglingSupplierIdsRef.current).add(supplier.id)
    togglingSupplierIdsRef.current = activeIds
    setTogglingSupplierIds(activeIds)
    try {
      await http.put<ApiResponses['PUT /admin/suppliers/{id}/status']>(`/admin/suppliers/${supplier.id}/status`, { status: supplier.status === 'ACTIVE' ? 'DISABLED' : 'ACTIVE' })
      Message.success(supplier.status === 'ACTIVE' ? '已禁用（其账号将全部无法登录）' : '已启用')
      load()
    } finally {
      const remainingIds = new Set(togglingSupplierIdsRef.current)
      remainingIds.delete(supplier.id)
      togglingSupplierIdsRef.current = remainingIds
      setTogglingSupplierIds(remainingIds)
    }
  }

  const remove = async (supplier: Supplier) => {
    await http.delete<ApiResponses['DELETE /admin/suppliers/{id}']>(`/admin/suppliers/${supplier.id}`)
    Message.success('供应商已删除')
    if (accTarget?.id === supplier.id) setAccTarget(null)
    load()
  }

  return (
    <Card className="page-card page-card--table">
      <div className="page-heading">
        <div>
          <h1>供应商管理</h1>
        </div>
      </div>
      <div className="page-toolbar responsive-toolbar">
        <Space>
          <Input.Search
            allowClear
            placeholder="供应商名称"
            style={{ width: 260 }}
            onSearch={search}
            onClear={clearSearch}
          />
          <Select
            allowClear
            placeholder="全部状态"
            style={{ width: 110 }}
            value={status}
            onChange={(value) => changeStatus(value as string | undefined)}
          >
            <Select.Option value="ACTIVE">启用</Select.Option>
            <Select.Option value="DISABLED">禁用</Select.Option>
          </Select>
        </Space>
        {canManageSuppliers && (
          <Button
            type="primary"
            icon={<IconPlus />}
            onClick={() => {
              setEditing(null)
              form.resetFields()
              setEditOpen(true)
            }}
          >
            新增供应商
          </Button>
        )}
      </div>
      {loadError ? (
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '24px 0' }}>
          <Typography.Text type="error">加载失败</Typography.Text>
          <Button size="small" onClick={load}>重试</Button>
        </div>
      ) : (
        <Table
          className="page-table"
          rowKey="id"
          loading={loading}
          data={data.list}
          scroll={{ x: 680, y: 'var(--page-table-scroll-y)' }}
          columns={[
            { title: '名称', dataIndex: 'name', ellipsis: true },
            {
              title: '状态',
              dataIndex: 'status',
              width: 70,
              align: 'center' as const,
              render: (value: string) => (value === 'ACTIVE' ? <Tag color="green">启用</Tag> : <Tag color="red">禁用</Tag>),
            },
            { title: '创建时间', dataIndex: 'createdAt', width: 180, align: 'center' as const, render: fmtTime },
            {
              title: '操作',
              width: 254,
              fixed: 'right' as const,
              align: 'center' as const,
              render: (_: unknown, supplier: Supplier) => actionSlots([
                canManageAccounts && <Button key="accounts" size="mini" type="text" onClick={(event) => {
                  accountTrigger.current = (event?.currentTarget as HTMLButtonElement | undefined) ?? null
                  setAccTarget(supplier)
                }}>
                  账号管理
                </Button>,
                canManageSuppliers && <Button
                  key="edit"
                  size="mini"
                  type="text"
                  onClick={() => {
                    setEditing(supplier)
                    form.setFieldsValue(supplier)
                    setEditOpen(true)
                  }}
                >
                  编辑
                </Button>,
                canManageSuppliers && <Popconfirm
                  key="status"
                  title={supplier.status === 'ACTIVE' ? '禁用后其所有账号无法登录，确认？' : '确认启用？'}
                  onOk={() => toggleStatus(supplier)}
                >
                  <Button
                    size="mini"
                    type="text"
                    status={supplier.status === 'ACTIVE' ? 'danger' : 'success'}
                    loading={togglingSupplierIds.has(supplier.id)}
                    disabled={togglingSupplierIds.has(supplier.id)}
                  >
                    {supplier.status === 'ACTIVE' ? '禁用' : '启用'}
                  </Button>
                </Popconfirm>,
                canManageSuppliers && canDelete && (
                  <Popconfirm
                    key="delete"
                    title="删除后不可恢复；存在任何供应商账号或关联项目时无法删除。确认？"
                    onOk={() => remove(supplier)}
                  >
                    <Button size="mini" type="text" status="danger">删除</Button>
                  </Popconfirm>
                ),
              ], 'supplier'),
            },
          ]}
          pagination={{
            total: data.total,
            current: page,
            pageSize,
            showTotal: true,
            sizeCanChange: true,
            onChange: changePage,
          }}
        />
      )}

      <SupplierEditModal
        editing={editing}
        editOpen={editOpen}
        saving={saving}
        form={form}
        onOk={submit}
        onCancel={() => { if (!saving) setEditOpen(false) }}
      />

      {canManageAccounts && <SupplierAccountsDrawer key={accTarget?.id} supplier={accTarget} onClose={() => setAccTarget(null)} />}
    </Card>
  )
}
