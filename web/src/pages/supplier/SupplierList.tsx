import { useCallback, useEffect, useState } from 'react'
import {
  Button, Card, Drawer, Form, Input, Message, Modal, Popconfirm, Select, Space, Table, Tag, Typography,
} from '@arco-design/web-react'
import { IconPlus } from '@arco-design/web-react/icon'
import http from '../../api/client'
import { actionSlots } from '../../components/ActionSlots'
import { useAuth } from '../../store/auth'
import { type PageResp, fmtTime } from '../../api/types'

interface Supplier {
  id: number
  name: string
  code: string
  contactName?: string
  contactPhone?: string
  contactEmail?: string
  status: 'ACTIVE' | 'DISABLED'
  createdAt: string
}

interface Account {
  id: number
  username: string
  realName: string
  email: string
  phone?: string
  status: 'ACTIVE' | 'DISABLED'
  lastLoginAt?: string | null
  createdAt: string
}

export default function SupplierList() {
  const { hasPerm } = useAuth()
  const canManageAccounts = hasPerm('supplier:account')
  const [data, setData] = useState<PageResp<Supplier>>({ list: [], total: 0, page: 1, pageSize: 10 })
  const [loading, setLoading] = useState(true)
  const [reloadKey, setReloadKey] = useState(0)
  const [keyword, setKeyword] = useState('')
  const [status, setStatus] = useState<string>()
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(10)
  const [editOpen, setEditOpen] = useState(false)
  const [editing, setEditing] = useState<Supplier | null>(null)
  const [accTarget, setAccTarget] = useState<Supplier | null>(null)
  const [form] = Form.useForm()

  const fetchSuppliers = useCallback(async () => {
    const r = await http.get('/admin/suppliers', { params: { page, pageSize, keyword: keyword || undefined, status } })
    return r.data as PageResp<Supplier>
  }, [page, pageSize, keyword, status])

  const load = useCallback(async () => {
    setLoading(true)
    try {
      setData(await fetchSuppliers())
    } finally {
      setLoading(false)
    }
  }, [fetchSuppliers])

  useEffect(() => {
    let active = true
    fetchSuppliers()
      .then((next) => {
        if (active) setData(next)
      })
      .finally(() => {
        if (active) setLoading(false)
      })
    return () => {
      active = false
    }
  }, [fetchSuppliers, reloadKey])

  const submit = async () => {
    const v = await form.validate().catch(() => null)
    if (!v) return
    if (editing) {
      await http.put(`/admin/suppliers/${editing.id}`, v)
      Message.success('供应商已更新')
    } else {
      await http.post('/admin/suppliers', v)
      Message.success('供应商已创建')
    }
    setEditOpen(false)
    load()
  }

  const toggleStatus = async (s: Supplier) => {
    await http.put(`/admin/suppliers/${s.id}/status`, { status: s.status === 'ACTIVE' ? 'DISABLED' : 'ACTIVE' })
    Message.success(s.status === 'ACTIVE' ? '已禁用（其账号将全部无法登录）' : '已启用')
    load()
  }

  return (
    <Card className="page-card page-card--table">
      <Space className="responsive-toolbar" style={{ marginBottom: 16, width: '100%', justifyContent: 'space-between' }}>
        <Space>
          <Input.Search
          allowClear
          placeholder="名称 / 编码"
          style={{ width: 260 }}
          onSearch={(v) => {
            setLoading(true); setReloadKey((value) => value + 1)
            setPage(1)
            setKeyword(v)
          }}
          onClear={() => {
            setLoading(true); setReloadKey((value) => value + 1)
            setPage(1)
            setKeyword('')
          }}
          />
          <Select
            allowClear
            placeholder="状态"
            style={{ width: 110 }}
            value={status}
            onChange={(v) => { setLoading(true); setReloadKey((value) => value + 1); setPage(1); setStatus(v as string | undefined) }}
          >
            <Select.Option value="ACTIVE">启用</Select.Option>
            <Select.Option value="DISABLED">禁用</Select.Option>
          </Select>
        </Space>
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
      </Space>
      <Table
        className="page-table"
        rowKey="id"
        loading={loading}
        data={data.list}
        scroll={{ x: 1010, y: 'var(--page-table-scroll-y)' }}
        columns={[
          { title: '编码', dataIndex: 'code', width: 80, align: 'center' as const },
          { title: '名称', dataIndex: 'name', width: 140, ellipsis: true },
          { title: '联系人', dataIndex: 'contactName', width: 80, align: 'center' as const, ellipsis: true, render: (v?: string) => v || '-' },
          { title: '联系电话', dataIndex: 'contactPhone', width: 110, align: 'center' as const, render: (v?: string) => v || '-' },
          { title: '邮箱', dataIndex: 'contactEmail', width: 140, ellipsis: true, render: (v?: string) => v || '-' },
          {
            title: '状态',
            dataIndex: 'status',
            width: 70,
            align: 'center' as const,
            render: (v: string) => (v === 'ACTIVE' ? <Tag color="green">启用</Tag> : <Tag color="red">禁用</Tag>),
          },
          { title: '创建时间', dataIndex: 'createdAt', width: 180, align: 'center' as const, render: fmtTime },
          {
            title: '操作',
            width: 210,
            align: 'center' as const,
            render: (_: unknown, r: Supplier) => actionSlots([
              canManageAccounts && <Button key="accounts" size="mini" type="text" onClick={() => setAccTarget(r)}>
                账号管理
              </Button>,
              <Button
                key="edit"
                size="mini"
                type="text"
                onClick={() => {
                  setEditing(r)
                  form.setFieldsValue(r)
                  setEditOpen(true)
                }}
              >
                编辑
              </Button>,
              <Popconfirm
                key="status"
                title={r.status === 'ACTIVE' ? '禁用后其所有账号无法登录，确认？' : '确认启用？'}
                onOk={() => toggleStatus(r)}
              >
                <Button size="mini" type="text" status={r.status === 'ACTIVE' ? 'danger' : 'success'}>
                  {r.status === 'ACTIVE' ? '禁用' : '启用'}
                </Button>
              </Popconfirm>,
            ], 'supplier'),
          },
        ]}
        pagination={{
          total: data.total,
          current: page,
          pageSize,
          showTotal: true,
          sizeCanChange: true,
          onChange: (p, ps) => {
            setLoading(true); setReloadKey((value) => value + 1)
            setPage(p)
            setPageSize(ps)
          },
        }}
      />

      <Modal
        title={editing ? '编辑供应商' : '新增供应商'}
        visible={editOpen}
        onOk={submit}
        onCancel={() => setEditOpen(false)}
      >
        <Form form={form} layout="vertical">
          <Form.Item label="供应商编码" field="code" rules={[{ required: true, message: '请输入编码' }]}>
            <Input placeholder="如 SUP-HY" disabled={!!editing} />
          </Form.Item>
          <Form.Item label="供应商名称" field="name" rules={[{ required: true, message: '请输入名称' }]}>
            <Input placeholder="公司全称" />
          </Form.Item>
          <Form.Item label="联系人" field="contactName">
            <Input />
          </Form.Item>
          <Form.Item label="联系电话" field="contactPhone">
            <Input />
          </Form.Item>
          <Form.Item label="邮箱" field="contactEmail" rules={[{ type: 'email', message: '邮箱格式不正确' }]}>
            <Input />
          </Form.Item>
          <Form.Item label="地址" field="address">
            <Input />
          </Form.Item>
          <Form.Item label="备注" field="remark">
            <Input.TextArea rows={2} maxLength={300} />
          </Form.Item>
        </Form>
      </Modal>

      {canManageAccounts && <AccountsDrawer key={accTarget?.id} supplier={accTarget} onClose={() => setAccTarget(null)} />}
    </Card>
  )
}

function AccountsDrawer({ supplier, onClose }: { supplier: Supplier | null; onClose: () => void }) {
  const [accountsState, setAccountsState] = useState<{ supplierId: number | null; list: Account[]; error?: boolean }>({ supplierId: null, list: [] })
  const [refreshing, setRefreshing] = useState(false)
  const [editOpen, setEditOpen] = useState(false)
  const [editing, setEditing] = useState<Account | null>(null)
  const [resetTarget, setResetTarget] = useState<Account | null>(null)
  const [form] = Form.useForm()
  const [pwdForm] = Form.useForm()

  const fetchAccounts = useCallback(async () => {
    if (!supplier) return
    const r = await http.get(`/admin/suppliers/${supplier.id}/accounts`)
    return { supplierId: supplier.id, list: r.data as Account[] }
  }, [supplier])

  const load = useCallback(async () => {
    setRefreshing(true)
    try {
      const next = await fetchAccounts()
      if (next) setAccountsState(next)
    } catch {
      setAccountsState({ supplierId: supplier?.id ?? null, list: [], error: true })
    } finally {
      setRefreshing(false)
    }
  }, [fetchAccounts, supplier?.id])

  useEffect(() => {
    let active = true
    fetchAccounts().then((next) => {
      if (active && next) setAccountsState(next)
    }).catch(() => {
      if (active) setAccountsState({ supplierId: supplier?.id ?? null, list: [], error: true })
    })
    return () => {
      active = false
    }
  }, [fetchAccounts, supplier?.id])

  const accounts = supplier && accountsState.supplierId === supplier.id ? accountsState.list : []
  const loading = refreshing || (!!supplier && accountsState.supplierId !== supplier.id)

  const submit = async () => {
    const v = await form.validate().catch(() => null)
    if (!v) return
    if (editing) {
      await http.put(`/admin/supplier-accounts/${editing.id}`, v)
      Message.success('账号已更新')
    } else {
      await http.post(`/admin/suppliers/${supplier!.id}/accounts`, v)
      Message.success('账号已创建（首次登录需改密）')
    }
    setEditOpen(false)
    load()
  }

  const toggle = async (a: Account) => {
    await http.put(`/admin/supplier-accounts/${a.id}/status`, { status: a.status === 'ACTIVE' ? 'DISABLED' : 'ACTIVE' })
    load()
  }

  const resetPwd = async () => {
    const v = await pwdForm.validate().catch(() => null)
    if (!v) return
    await http.put(`/admin/supplier-accounts/${resetTarget!.id}/password`, { newPassword: v.newPassword })
    Message.success('密码已重置，下次登录需修改')
    setResetTarget(null)
    pwdForm.resetFields()
  }

  return (
    <Drawer
      width={920}
      title={supplier ? `账号管理 · ${supplier.name}` : ''}
      visible={!!supplier}
      onCancel={onClose}
      footer={null}
    >
      {accountsState.error && accountsState.supplierId === supplier?.id && (
        <Space style={{ marginBottom: 12 }}>
          <Typography.Text type="error">账号加载失败</Typography.Text>
          <Button size="small" loading={refreshing} onClick={load}>重试</Button>
        </Space>
      )}
      <Space className="responsive-toolbar" style={{ marginBottom: 12, width: '100%', justifyContent: 'space-between' }}>
        <Typography.Text type="secondary">供应商人员账号：多人共用供应商身份协作</Typography.Text>
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
          新增账号
        </Button>
      </Space>
      <Table
        rowKey="id"
        size="small"
        loading={loading}
        data={accounts}
        pagination={false}
        columns={[
          { title: '用户名', dataIndex: 'username', width: 110, align: 'center' as const },
          { title: '姓名', dataIndex: 'realName', width: 100, align: 'center' as const },
          { title: '邮箱', dataIndex: 'email', width: 190, ellipsis: true },
          {
            title: '状态',
            dataIndex: 'status',
            width: 80,
            align: 'center' as const,
            render: (v: string) => (v === 'ACTIVE' ? <Tag color="green">启用</Tag> : <Tag color="red">禁用</Tag>),
          },
          { title: '最近登录', dataIndex: 'lastLoginAt', width: 180, align: 'center' as const, render: fmtTime },
          {
            title: '操作',
            width: 210,
            align: 'center' as const,
            render: (_: unknown, r: Account) => actionSlots([
              <Button
                key="edit"
                size="mini"
                type="text"
                onClick={() => {
                  setEditing(r)
                  form.setFieldsValue(r)
                  setEditOpen(true)
                }}
              >
                编辑
              </Button>,
              <Button key="reset-password" size="mini" type="text" onClick={() => setResetTarget(r)}>
                重置密码
              </Button>,
              <Button key="status" size="mini" type="text" status={r.status === 'ACTIVE' ? 'danger' : 'success'} onClick={() => toggle(r)}>
                {r.status === 'ACTIVE' ? '禁用' : '启用'}
              </Button>,
            ], 'account'),
          },
        ]}
      />

      <Modal title={editing ? '编辑账号' : '新增账号'} visible={editOpen} onOk={submit} onCancel={() => setEditOpen(false)}>
        <Form form={form} layout="vertical">
          {!editing && (
            <>
              <Form.Item label="用户名" field="username" rules={[{ required: true, message: '请输入用户名' }, { match: /^[a-zA-Z0-9_]{3,32}$/, message: '3-32 位字母/数字/下划线' }]}>
                <Input />
              </Form.Item>
              <Form.Item
                label="初始密码"
                field="password"
                rules={[{ required: true, message: '请输入初始密码' }, { match: /^(?=.*[A-Za-z])(?=.*\d).{8,}$/, message: '至少 8 位，含字母和数字' }]}
              >
                <Input.Password />
              </Form.Item>
            </>
          )}
          <Form.Item label="姓名" field="realName" rules={[{ required: true, message: '请输入姓名' }]}>
            <Input />
          </Form.Item>
          <Form.Item label="邮箱" field="email" rules={[{ required: true, message: '请输入邮箱' }, { type: 'email', message: '邮箱格式不正确' }]}>
            <Input />
          </Form.Item>
          <Form.Item label="电话" field="phone">
            <Input />
          </Form.Item>
        </Form>
      </Modal>

      <Modal title={`重置密码 · ${resetTarget?.username ?? ''}`} visible={!!resetTarget} onOk={resetPwd} onCancel={() => setResetTarget(null)}>
        <Form form={pwdForm} layout="vertical">
          <Form.Item
            label="新密码"
            field="newPassword"
            rules={[{ required: true, message: '请输入新密码' }, { match: /^(?=.*[A-Za-z])(?=.*\d).{8,}$/, message: '至少 8 位，含字母和数字' }]}
          >
            <Input.Password />
          </Form.Item>
        </Form>
      </Modal>
    </Drawer>
  )
}
