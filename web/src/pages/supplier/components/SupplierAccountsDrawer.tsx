import { useCallback, useEffect, useRef, useState } from 'react'
import {
  Button, Drawer, Form, Input, Message, Modal, Popconfirm, Select, Space, Table, Tag, Typography,
} from '@arco-design/web-react'
import { IconPlus } from '@arco-design/web-react/icon'
import http, { type QuietRequestConfig } from '../../../api/client'
import { actionSlots } from '../../../components/ActionSlots'
import PasswordInput from '../../../components/PasswordInput'
import { useAuth } from '../../../store/auth'
import { type ApiResponses, fmtTime } from '../../../api/types'
import { passwordRule } from '../../../utils/password'
import { textLengthRule } from '../../../utils/textRules'
import type { Account, RoleOption, Supplier } from './supplierTypes'

interface SupplierAccountsDrawerProps {
  supplier: Supplier | null
  onClose: () => void
}

export function SupplierAccountsDrawer({ supplier, onClose }: SupplierAccountsDrawerProps) {
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
  const saveInFlight = useRef(false)
  const passwordResetInFlight = useRef(false)
  const [togglingAccountIds, setTogglingAccountIds] = useState<ReadonlySet<number>>(new Set())
  const [form] = Form.useForm()
  const [pwdForm] = Form.useForm()

  const fetchAccounts = useCallback(async () => {
    if (!supplier) return
    const response = await http.get<ApiResponses['GET /admin/suppliers/{id}/accounts']>(`/admin/suppliers/${supplier.id}/accounts`, { quietNetworkError: true } as QuietRequestConfig)
    return { supplierId: supplier.id, list: response.data as Account[] }
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
  const accountsFailed = !!accountsState.error && accountsState.supplierId === supplier?.id

  const loadRoleOptions = useCallback(async () => {
    const requestId = ++roleOptionsRequestId.current
    setRoleOptionsLoading(true)
    setRoleOptionsError(false)
    try {
      const response = await http.get<ApiResponses['GET /admin/supplier-role-options']>('/admin/supplier-role-options', { quietNetworkError: true } as QuietRequestConfig)
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
    if (saveInFlight.current) return
    saveInFlight.current = true
    try {
      if (!editing && supplier?.status === 'DISABLED') {
        Message.warning('供应商已禁用，不能新增账号')
        return
      }
      const values = await form.validate().catch(() => null)
      if (!values) return
      setSaving(true)
      if (editing) {
        await http.put<ApiResponses['PUT /admin/supplier-accounts/{id}']>(`/admin/supplier-accounts/${editing.id}`, values)
        Message.success('账号已更新')
      } else {
        await http.post<ApiResponses['POST /admin/suppliers/{id}/accounts']>(`/admin/suppliers/${supplier!.id}/accounts`, values)
        Message.success('账号已创建（首次登录需改密）')
      }
      setEditOpen(false)
      load()
    } finally {
      saveInFlight.current = false
      setSaving(false)
    }
  }

  const toggle = async (account: Account) => {
    if (togglingAccountIdsRef.current.has(account.id)) return
    const activeIds = new Set(togglingAccountIdsRef.current).add(account.id)
    togglingAccountIdsRef.current = activeIds
    setTogglingAccountIds(activeIds)
    try {
      await http.put<ApiResponses['PUT /admin/supplier-accounts/{id}/status']>(`/admin/supplier-accounts/${account.id}/status`, { status: account.status === 'ACTIVE' ? 'DISABLED' : 'ACTIVE' })
      load()
    } catch {
      // 请求层已展示错误；保留当前账号快照并恢复按钮供用户重试。
    } finally {
      const remainingIds = new Set(togglingAccountIdsRef.current)
      remainingIds.delete(account.id)
      togglingAccountIdsRef.current = remainingIds
      setTogglingAccountIds(remainingIds)
    }
  }

  const removeAccount = async (account: Account) => {
    await http.delete<ApiResponses['DELETE /admin/supplier-accounts/{id}']>(`/admin/supplier-accounts/${account.id}`)
    Message.success('账号已删除')
    load()
  }

  const resetPwd = async () => {
    if (passwordResetInFlight.current) return
    passwordResetInFlight.current = true
    try {
      const values = await pwdForm.validate().catch(() => null)
      if (!values) return
      setResettingPassword(true)
      await http.put<ApiResponses['PUT /admin/supplier-accounts/{id}/password']>(`/admin/supplier-accounts/${resetTarget!.id}/password`, { newPassword: values.newPassword })
      Message.success('密码已重置，下次登录需修改')
      setResetTarget(null)
      pwdForm.resetFields()
    } finally {
      passwordResetInFlight.current = false
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

  const childDialogOpen = editOpen || resetTarget !== null

  return (
    <Drawer
      className="account-drawer"
      width={920}
      title={supplier ? `账号管理 · ${supplier.name}` : ''}
      visible={!!supplier}
      focusLock={!childDialogOpen}
      closable={!childDialogOpen}
      maskClosable={!childDialogOpen}
      escToExit={!childDialogOpen}
      onCancel={() => { if (!childDialogOpen) onClose() }}
      footer={null}
    >
      {accountsFailed && (
        <Space style={{ marginBottom: 12 }}>
          <Typography.Text type="error">加载失败</Typography.Text>
          <Button size="small" loading={refreshing} onClick={load}>重试</Button>
        </Space>
      )}
      <div className="drawer-toolbar responsive-toolbar">
        <Typography.Text type="secondary">{loading ? '账号加载中…' : accountsFailed ? '账号列表暂不可用' : `共 ${accounts.length} 个账号`}</Typography.Text>
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
      {!accountsFailed && <Table
        className="account-table"
        rowKey="id"
        size="small"
        loading={loading}
        noDataElement={loading ? <span /> : undefined}
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
            render: (value: string) => (value === 'ACTIVE' ? <Tag color="green">启用</Tag> : <Tag color="red">禁用</Tag>),
          },
          { title: '最近登录', dataIndex: 'lastLoginAt', width: 140, align: 'center' as const, render: fmtTime },
          {
            title: '操作',
            width: 216,
            fixed: 'right' as const,
            align: 'center' as const,
            render: (_: unknown, account: Account) => actionSlots([
              <Button
                key="edit"
                size="mini"
                type="text"
                onClick={() => {
                  setEditing(account)
                  form.resetFields()
                  form.setFieldsValue({
                    employeeNo: account.employeeNo,
                    realName: account.realName,
                    email: account.email,
                  })
                  setEditOpen(true)
                }}
              >
                编辑
              </Button>,
              <Button key="reset-password" size="mini" type="text" onClick={() => openPasswordReset(account)}>
                重置密码
              </Button>,
              <Button
                key="status"
                size="mini"
                type="text"
                status={account.status === 'ACTIVE' ? 'danger' : 'success'}
                loading={togglingAccountIds.has(account.id)}
                disabled={togglingAccountIds.has(account.id)}
                onClick={() => toggle(account)}
              >
                {account.status === 'ACTIVE' ? '禁用' : '启用'}
              </Button>,
              canDelete && (
                <Popconfirm key="delete" title="删除后不可恢复，确认删除该账号？" onOk={() => removeAccount(account)}>
                  <Button size="mini" type="text" status="danger">删除</Button>
                </Popconfirm>
              ),
            ], 'account'),
          },
        ]}
      />}

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
                  extra="首次登录需改密"
                  rules={[{ required: true, message: '请输入初始密码' }, passwordRule]}
                >
                  <PasswordInput placeholder="6-20 位" />
                </Form.Item>
                <Form.Item label="角色" field="roleId" rules={[{ required: true, message: '请选择供应商角色' }]}
                  extra={roleOptionsError
                    ? <>角色加载失败。<Button size="mini" type="text" onClick={loadRoleOptions}>重试</Button></>
                    : !roleOptionsLoading && roleOptions.length === 0
                      ? '暂无可用供应商角色，请先由管理员在“角色与权限”中创建或启用角色，并仅授予供应商自有项目权限。'
                      : undefined}>
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
