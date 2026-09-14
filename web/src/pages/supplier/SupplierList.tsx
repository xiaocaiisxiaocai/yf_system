import { textLengthRule } from '../../utils/textRules'
import { useCallback, useEffect, useRef, useState } from 'react'
import {
  Button, Card, Drawer, Form, Input, Message, Modal, Popconfirm, Select, Space, Table, Tag, Typography,
} from '@arco-design/web-react'
import { IconPlus } from '@arco-design/web-react/icon'
import http from '../../api/client'
import { actionSlots } from '../../components/ActionSlots'
import PasswordInput from '../../components/PasswordInput'
import { useAuth } from '../../store/auth'
import { type PageResp, fmtTime } from '../../api/types'
import { passwordRule } from '../../utils/password'

interface Supplier {
  id: number
  name: string
  remark?: string
  status: 'ACTIVE' | 'DISABLED'
  createdAt: string
}

interface Account {
  id: number
  employeeNo: string
  realName: string
  email: string
  status: 'ACTIVE' | 'DISABLED'
  roleId?: number | null
  roleName?: string | null
  lastLoginAt?: string | null
  createdAt: string
}

interface RoleOption {
  id: number
  name: string
}

export default function SupplierList() {
  const { hasPerm } = useAuth()
  const canManageSuppliers = hasPerm('supplier:manage')
  const canManageAccounts = hasPerm('supplier:account')
  const canDelete = hasPerm('supplier:delete')
  const [data, setData] = useState<PageResp<Supplier>>({ list: [], total: 0, page: 1, pageSize: 10 })
  const [loading, setLoading] = useState(true)
  const [reloadKey, setReloadKey] = useState(0)
  const [keyword, setKeyword] = useState('')
  const [status, setStatus] = useState<string>()
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(10)
  const [loadError, setLoadError] = useState(false)
  const [editOpen, setEditOpen] = useState(false)
  const [saving, setSaving] = useState(false)
  const [editing, setEditing] = useState<Supplier | null>(null)
  const [accTarget, setAccTarget] = useState<Supplier | null>(null)
  const togglingSupplierIdsRef = useRef(new Set<number>())
  const [togglingSupplierIds, setTogglingSupplierIds] = useState<ReadonlySet<number>>(new Set())
  const [form] = Form.useForm()

  const fetchSuppliers = useCallback(async () => {
    const r = await http.get('/admin/suppliers', { params: { page, pageSize, keyword: keyword || undefined, status } })
    return r.data as PageResp<Supplier>
  }, [page, pageSize, keyword, status])

  const load = useCallback(() => {
    setLoading(true)
    setLoadError(false)
    setReloadKey((value) => value + 1)
  }, [])

  useEffect(() => {
    let active = true
    fetchSuppliers()
      .then((next) => {
        if (active) {
          setData(next)
          setLoadError(false)
          const lastPage = Math.max(1, Math.ceil(next.total / (next.pageSize || pageSize)))
          if (page > lastPage) setPage(lastPage)
        }
      })
      .catch(() => {
        if (active) setLoadError(true)
      })
      .finally(() => {
        if (active) setLoading(false)
      })
    return () => {
      active = false
    }
  }, [fetchSuppliers, page, pageSize, reloadKey])

  const submit = async () => {
    if (saving) return
    const v = await form.validate().catch(() => null)
    if (!v) return
    setSaving(true)
    try {
      if (editing) {
        await http.put(`/admin/suppliers/${editing.id}`, v)
        Message.success('供应商已更新')
      } else {
        await http.post('/admin/suppliers', v)
        Message.success('供应商已创建')
      }
      setEditOpen(false)
      load()
    } finally {
      setSaving(false)
    }
  }

  const toggleStatus = async (s: Supplier) => {
    if (togglingSupplierIdsRef.current.has(s.id)) return
    const activeIds = new Set(togglingSupplierIdsRef.current).add(s.id)
    togglingSupplierIdsRef.current = activeIds
    setTogglingSupplierIds(activeIds)
    try {
      await http.put(`/admin/suppliers/${s.id}/status`, { status: s.status === 'ACTIVE' ? 'DISABLED' : 'ACTIVE' })
      Message.success(s.status === 'ACTIVE' ? '已禁用（其账号将全部无法登录）' : '已启用')
      load()
    } finally {
      const remainingIds = new Set(togglingSupplierIdsRef.current)
      remainingIds.delete(s.id)
      togglingSupplierIdsRef.current = remainingIds
      setTogglingSupplierIds(remainingIds)
    }
  }

  const remove = async (s: Supplier) => {
    await http.delete(`/admin/suppliers/${s.id}`)
    Message.success('供应商已删除')
    if (accTarget?.id === s.id) setAccTarget(null)
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
            onSearch={(v) => {
              setLoading(true); setLoadError(false); setReloadKey((value) => value + 1)
              setPage(1)
              setKeyword(v)
            }}
            onClear={() => {
              setLoading(true); setLoadError(false); setReloadKey((value) => value + 1)
              setPage(1)
              setKeyword('')
            }}
          />
          <Select
            allowClear
            placeholder="全部状态"
            style={{ width: 110 }}
            value={status}
            onChange={(v) => { setLoading(true); setLoadError(false); setReloadKey((value) => value + 1); setPage(1); setStatus(v as string | undefined) }}
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
            render: (v: string) => (v === 'ACTIVE' ? <Tag color="green">启用</Tag> : <Tag color="red">禁用</Tag>),
          },
          { title: '创建时间', dataIndex: 'createdAt', width: 180, align: 'center' as const, render: fmtTime },
          {
            title: '操作',
            width: 254,
            fixed: 'right' as const,
            align: 'center' as const,
            render: (_: unknown, r: Supplier) => actionSlots([
              canManageAccounts && <Button key="accounts" size="mini" type="text" onClick={() => setAccTarget(r)}>
                账号管理
              </Button>,
              canManageSuppliers && <Button
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
              canManageSuppliers && <Popconfirm
                key="status"
                title={r.status === 'ACTIVE' ? '禁用后其所有账号无法登录，确认？' : '确认启用？'}
                onOk={() => toggleStatus(r)}
              >
                <Button
                  size="mini"
                  type="text"
                  status={r.status === 'ACTIVE' ? 'danger' : 'success'}
                  loading={togglingSupplierIds.has(r.id)}
                  disabled={togglingSupplierIds.has(r.id)}
                >
                  {r.status === 'ACTIVE' ? '禁用' : '启用'}
                </Button>
              </Popconfirm>,
              canManageSuppliers && canDelete && (
                <Popconfirm
                  key="delete"
                  title="删除后不可恢复；存在任何供应商账号或关联项目时无法删除。确认？"
                  onOk={() => remove(r)}
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
            onChange: (p, ps) => {
              setLoading(true); setLoadError(false); setReloadKey((value) => value + 1)
              setPage(p)
              setPageSize(ps)
            },
          }}
        />
      )}

      <Modal
        className="form-dialog"
        title={editing ? '编辑供应商' : '新增供应商'}
        visible={editOpen}
        confirmLoading={saving}
        closable={!saving}
        maskClosable={!saving}
        escToExit={!saving}
        cancelButtonProps={{ disabled: saving }}
        okText={editing ? '保存修改' : '创建供应商'}
        onOk={submit}
        onCancel={() => { if (!saving) setEditOpen(false) }}
      >
        <Form form={form} layout="vertical">
          <Form.Item label="供应商名称" field="name" rules={[{ required: true, message: '请输入名称' }, textLengthRule('供应商名称', 64)]}>
            <Input placeholder="公司全称" />
          </Form.Item>
          <Form.Item label="备注" field="remark">
            <Input.TextArea rows={3} maxLength={500} placeholder="选填" />
          </Form.Item>
        </Form>
      </Modal>

      {canManageAccounts && <AccountsDrawer key={accTarget?.id} supplier={accTarget} onClose={() => setAccTarget(null)} />}
    </Card>
  )
}

function AccountsDrawer({ supplier, onClose }: { supplier: Supplier | null; onClose: () => void }) {
  const canDelete = useAuth((state) => state.hasPerm('supplier:account_delete'))
  const [accountsState, setAccountsState] = useState<{ supplierId: number | null; list: Account[]; error?: boolean }>({ supplierId: null, list: [] })
  const [refreshing, setRefreshing] = useState(false)
  const [editOpen, setEditOpen] = useState(false)
  const [saving, setSaving] = useState(false)
  const [resettingPassword, setResettingPassword] = useState(false)
  const [editing, setEditing] = useState<Account | null>(null)
  const [resetTarget, setResetTarget] = useState<Account | null>(null)
  const [roleOptions, setRoleOptions] = useState<RoleOption[]>([])
  const [roleOptionsLoading, setRoleOptionsLoading] = useState(false)
  const [roleOptionsError, setRoleOptionsError] = useState(false)
  const togglingAccountIdsRef = useRef(new Set<number>())
  const accountsRequestId = useRef(0)
  const roleOptionsRequestId = useRef(0)
  const [togglingAccountIds, setTogglingAccountIds] = useState<ReadonlySet<number>>(new Set())
  const [form] = Form.useForm()
  const [pwdForm] = Form.useForm()

  const fetchAccounts = useCallback(async () => {
    if (!supplier) return
    const r = await http.get(`/admin/suppliers/${supplier.id}/accounts`)
    return { supplierId: supplier.id, list: r.data as Account[] }
  }, [supplier])

  const load = useCallback(async () => {
    const requestId = ++accountsRequestId.current
    setRefreshing(true)
    try {
      const next = await fetchAccounts()
      if (accountsRequestId.current === requestId && next) setAccountsState(next)
    } catch {
      if (accountsRequestId.current === requestId) {
        setAccountsState({ supplierId: supplier?.id ?? null, list: [], error: true })
      }
    } finally {
      if (accountsRequestId.current === requestId) setRefreshing(false)
    }
  }, [fetchAccounts, supplier?.id])

  useEffect(() => {
    let active = true
    const requestId = ++accountsRequestId.current
    fetchAccounts().then((next) => {
      if (active && accountsRequestId.current === requestId && next) setAccountsState(next)
    }).catch(() => {
      if (active && accountsRequestId.current === requestId) {
        setAccountsState({ supplierId: supplier?.id ?? null, list: [], error: true })
      }
    })
    return () => {
      active = false
      if (accountsRequestId.current === requestId) accountsRequestId.current += 1
    }
  }, [fetchAccounts, supplier?.id])

  const accounts = supplier && accountsState.supplierId === supplier.id ? accountsState.list : []
  const loading = refreshing || (!!supplier && accountsState.supplierId !== supplier.id)

  const loadRoleOptions = useCallback(async () => {
    const requestId = ++roleOptionsRequestId.current
    setRoleOptionsLoading(true)
    setRoleOptionsError(false)
    try {
      const response = await http.get('/admin/supplier-role-options')
      if (!Array.isArray(response.data)) throw new Error('Invalid supplier role options response')
      if (roleOptionsRequestId.current === requestId) setRoleOptions(response.data as RoleOption[])
    } catch {
      if (roleOptionsRequestId.current === requestId) {
        setRoleOptions([])
        setRoleOptionsError(true)
      }
    } finally {
      if (roleOptionsRequestId.current === requestId) setRoleOptionsLoading(false)
    }
  }, [])

  const submit = async () => {
    if (saving) return
    if (!editing && supplier?.status === 'DISABLED') {
      Message.warning('供应商已禁用，不能新增账号')
      return
    }
    const v = await form.validate().catch(() => null)
    if (!v) return
    setSaving(true)
    try {
      if (editing) {
        await http.put(`/admin/supplier-accounts/${editing.id}`, v)
        Message.success('账号已更新')
      } else {
        await http.post(`/admin/suppliers/${supplier!.id}/accounts`, v)
        Message.success('账号已创建（首次登录需改密）')
      }
      setEditOpen(false)
      load()
    } finally {
      setSaving(false)
    }
  }

  const toggle = async (a: Account) => {
    if (togglingAccountIdsRef.current.has(a.id)) return
    const activeIds = new Set(togglingAccountIdsRef.current).add(a.id)
    togglingAccountIdsRef.current = activeIds
    setTogglingAccountIds(activeIds)
    try {
      await http.put(`/admin/supplier-accounts/${a.id}/status`, { status: a.status === 'ACTIVE' ? 'DISABLED' : 'ACTIVE' })
      load()
    } finally {
      const remainingIds = new Set(togglingAccountIdsRef.current)
      remainingIds.delete(a.id)
      togglingAccountIdsRef.current = remainingIds
      setTogglingAccountIds(remainingIds)
    }
  }

  const removeAccount = async (a: Account) => {
    await http.delete(`/admin/supplier-accounts/${a.id}`)
    Message.success('账号已删除')
    load()
  }

  const resetPwd = async () => {
    if (resettingPassword) return
    const v = await pwdForm.validate().catch(() => null)
    if (!v) return
    setResettingPassword(true)
    try {
      await http.put(`/admin/supplier-accounts/${resetTarget!.id}/password`, { newPassword: v.newPassword })
      Message.success('密码已重置，下次登录需修改')
      setResetTarget(null)
      pwdForm.resetFields()
    } finally {
      setResettingPassword(false)
    }
  }

  const openPasswordReset = (account: Account) => {
    pwdForm.resetFields()
    setResetTarget(account)
  }

  const closePasswordReset = () => {
    if (resettingPassword) return
    pwdForm.resetFields()
    setResetTarget(null)
  }

  return (
    <Drawer
      className="account-drawer"
      width={920}
      title={supplier ? `账号管理 · ${supplier.name}` : ''}
      visible={!!supplier}
      onCancel={onClose}
      footer={null}
    >
      {accountsState.error && accountsState.supplierId === supplier?.id && (
        <Space style={{ marginBottom: 12 }}>
          <Typography.Text type="error">加载失败</Typography.Text>
          <Button size="small" loading={refreshing} onClick={load}>重试</Button>
        </Space>
      )}
      <div className="drawer-toolbar responsive-toolbar">
        <Typography.Text type="secondary">共 {accounts.length} 个账号</Typography.Text>
        <Button
          type="primary"
          size="small"
          icon={<IconPlus />}
          disabled={supplier?.status === 'DISABLED'}
          onClick={() => {
            setEditing(null)
            form.resetFields()
            setEditOpen(true)
            loadRoleOptions()
          }}
        >
          新增账号
        </Button>
      </div>
      <Table
        className="account-table"
        rowKey="id"
        size="small"
        loading={loading}
        data={accounts}
        pagination={false}
        scroll={{ x: 840 }}
        columns={[
          { title: '工号', dataIndex: 'employeeNo', width: 90, align: 'center' as const, ellipsis: true },
          { title: '姓名', dataIndex: 'realName', width: 80, align: 'center' as const, ellipsis: true },
          { title: '角色', dataIndex: 'roleName', width: 100, align: 'center' as const, ellipsis: true },
          { title: '邮箱', dataIndex: 'email', width: 150, ellipsis: true },
          {
            title: '状态',
            dataIndex: 'status',
            width: 64,
            align: 'center' as const,
            render: (v: string) => (v === 'ACTIVE' ? <Tag color="green">启用</Tag> : <Tag color="red">禁用</Tag>),
          },
          { title: '最近登录', dataIndex: 'lastLoginAt', width: 140, align: 'center' as const, render: fmtTime },
          {
            title: '操作',
            width: 216,
            fixed: 'right' as const,
            align: 'center' as const,
            render: (_: unknown, r: Account) => actionSlots([
              <Button
                key="edit"
                size="mini"
                type="text"
                onClick={() => {
                  setEditing(r)
                  form.resetFields()
                  form.setFieldsValue({
                    employeeNo: r.employeeNo,
                    realName: r.realName,
                    email: r.email,
                  })
                  setEditOpen(true)
                }}
              >
                编辑
              </Button>,
              <Button key="reset-password" size="mini" type="text" onClick={() => openPasswordReset(r)}>
                重置密码
              </Button>,
              <Button
                key="status"
                size="mini"
                type="text"
                status={r.status === 'ACTIVE' ? 'danger' : 'success'}
                loading={togglingAccountIds.has(r.id)}
                disabled={togglingAccountIds.has(r.id)}
                onClick={() => toggle(r)}
              >
                {r.status === 'ACTIVE' ? '禁用' : '启用'}
              </Button>,
              canDelete && (
                <Popconfirm key="delete" title="删除后不可恢复，确认删除该账号？" onOk={() => removeAccount(r)}>
                  <Button size="mini" type="text" status="danger">删除</Button>
                </Popconfirm>
              ),
            ], 'account'),
          },
        ]}
      />

      <Modal
        className="form-dialog"
        title={editing ? '编辑供应商账号' : '新增供应商账号'}
        visible={editOpen}
        confirmLoading={saving}
        closable={!saving}
        maskClosable={!saving}
        escToExit={!saving}
        cancelButtonProps={{ disabled: saving }}
        okButtonProps={{ disabled: !editing && (roleOptionsLoading || roleOptionsError || roleOptions.length === 0) }}
        okText={editing ? '保存账号' : '创建账号'}
        onOk={submit}
        onCancel={() => { if (!saving) setEditOpen(false) }}
      >
        <Form form={form} layout="vertical">
          <div className="form-grid">
            {!editing && (
              <>
                <Form.Item label="工号" field="employeeNo" rules={[{ required: true, message: '请输入工号' }, { match: /^[a-zA-Z0-9_]{3,32}$/, message: '3-32 位字母/数字/下划线' }]}>
                  <Input placeholder="3-32 位字母、数字或下划线" />
                </Form.Item>
                <Form.Item
                  label="初始密码"
                  field="password"
                  rules={[{ required: true, message: '请输入初始密码' }, passwordRule]}
                >
                  <PasswordInput placeholder="6-20 位" />
                </Form.Item>
                <Form.Item label="角色" field="roleId" rules={[{ required: true, message: '请选择供应商角色' }]}>
                  <Select
                    placeholder={roleOptionsLoading ? '正在加载角色' : '选择供应商角色'}
                    loading={roleOptionsLoading}
                    disabled={roleOptionsLoading || roleOptionsError || roleOptions.length === 0}
                  >
                    {roleOptions.map((role) => (
                      <Select.Option key={role.id} value={role.id}>{role.name}</Select.Option>
                    ))}
                  </Select>
                </Form.Item>
              </>
            )}
            <Form.Item label="姓名" field="realName" rules={[{ required: true, message: '请输入姓名' }, textLengthRule('姓名', 32)]}>
              <Input placeholder="姓名" />
            </Form.Item>
            <Form.Item label="邮箱" field="email" rules={[{ required: true, message: '请输入邮箱' }, { type: 'email', message: '邮箱格式不正确' }, textLengthRule('邮箱', 128)]}>
              <Input placeholder="name@example.com" />
            </Form.Item>
          </div>
          {!editing && roleOptionsError && (
            <div className="dialog-note">
              角色加载失败。<Button size="mini" type="text" onClick={loadRoleOptions}>重试</Button>
            </div>
          )}
          {!editing && !roleOptionsLoading && !roleOptionsError && roleOptions.length === 0 && (
            <div className="dialog-note">暂无可用供应商角色，请先由管理员在“角色与权限”中创建或启用角色，并仅授予供应商自有项目权限。</div>
          )}
          {!editing && <div className="dialog-note">首次登录需改密</div>}
        </Form>
      </Modal>

      <Modal
        className="form-dialog"
        title={`重置密码 · ${resetTarget?.employeeNo ?? ''}`}
        visible={!!resetTarget}
        confirmLoading={resettingPassword}
        closable={!resettingPassword}
        maskClosable={!resettingPassword}
        escToExit={!resettingPassword}
        cancelButtonProps={{ disabled: resettingPassword }}
        okText="确认重置"
        onOk={resetPwd}
        onCancel={closePasswordReset}
      >
        <Form form={pwdForm} layout="vertical">
          <Form.Item
            label="新密码"
            field="newPassword"
                rules={[{ required: true, message: '请输入新密码' }, passwordRule]}
          >
            <PasswordInput placeholder="6-20 位" />
          </Form.Item>
          <div className="dialog-note">下次登录需改密</div>
        </Form>
      </Modal>
    </Drawer>
  )
}
