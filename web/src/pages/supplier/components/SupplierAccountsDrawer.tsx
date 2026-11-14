import { useCallback, useEffect, useRef, useState } from 'react'
import {
  Button, Drawer, Form, Input, Message, Modal, Popconfirm, Select, Space, Typography,
} from '@arco-design/web-react'
import ResizableTable from '../../../components/ResizableTable'
import { IconPlus } from '@arco-design/web-react/icon'
import http, { type QuietRequestConfig } from '../../../api/client'
import { actionSlots } from '../../../components/ActionSlots'
import PasswordInput from '../../../components/PasswordInput'
import { useAuth } from '../../../store/auth'
import { type ApiResponses, fmtTime } from '../../../api/types'
import { passwordRule } from '../../../utils/password'
import type { Account, RoleOption, Supplier } from './supplierTypes'
import EnterSubmit from '../../../components/EnterSubmit'
import { ACCOUNT_NAME_MESSAGE, ACCOUNT_NAME_PATTERN, ACCOUNT_NAME_PLACEHOLDER } from '../../../utils/accountName'
import StatusTag from '../../../components/StatusTag'

const builtInSupplierRoleName = '供应商人员'

/** 新建账号时带上默认角色：内置「供应商人员」优先，否则仅有一个可选角色时用它。 */
function defaultSupplierRoleId(options: readonly RoleOption[]) {
  return options.find((role) => role.name === builtInSupplierRoleName)?.id
    ?? (options.length === 1 ? options[0]?.id : undefined)
}

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
      if (roleOptionsRequestId.current !== requestId) return []
      const options = response.data as RoleOption[]
      setRoleOptions(options)
      const selected = form.getFieldValue('roleId')
      const roleId = defaultSupplierRoleId(options)
      if ((selected === undefined || selected === null || selected === '') && roleId !== undefined) {
        form.setFieldValue('roleId', roleId)
      }
      return options
    } catch {
      if (roleOptionsRequestId.current === requestId) {
        setRoleOptions([])
        setRoleOptionsError(true)
      }
      return []
    } finally {
      if (roleOptionsRequestId.current === requestId) setRoleOptionsLoading(false)
    }
  }, [form])

  const submit = async () => {
    if (saveInFlight.current) return
    saveInFlight.current = true
    try {
      if (supplier?.status === 'DISABLED') {
        Message.warning('供应商已停用，不能新增账号')
        return
      }
      const values = await form.validate().catch(() => null)
      if (!values) return
      setSaving(true)
      await http.post<ApiResponses['POST /admin/suppliers/{id}/accounts']>(`/admin/suppliers/${supplier!.id}/accounts`, values)
      Message.success('账号已创建（首次登录需改密）')
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
      Message.success(account.status === 'ACTIVE' ? '账号已停用' : '账号已启用')
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
        <Space wrap>
          <Button
            type="primary"
            size="small"
            icon={<IconPlus />}
            disabled={supplier?.status === 'DISABLED'}
            onClick={() => {
              form.resetFields()
              setEditOpen(true)
              void loadRoleOptions()
            }}
          >
            新增账号
          </Button>
        </Space>
      </div>
      {!accountsFailed && <ResizableTable tableId="supplier-accounts"
        className="account-table"
        rowKey="id"
        size="small"
        loading={loading}
        noDataElement={loading ? <span /> : undefined}
        data={accounts}
        pagination={false}
        scroll={{ x: 840 }}
        columns={[
          { title: '账号', dataIndex: 'employeeNo', width: 90, align: 'center' as const, ellipsis: true },
          { title: '角色', dataIndex: 'roleName', width: 100, align: 'center' as const, ellipsis: true },
          {
            title: '状态',
            dataIndex: 'status',
            width: 64,
            align: 'center' as const,
            render: (value: string) => <StatusTag status={value} />,
          },
          { title: '最近登录', dataIndex: 'lastLoginAt', width: 140, align: 'center' as const, render: fmtTime },
          {
            title: '操作',
            width: 216,
            fixed: 'right' as const,
            render: (_: unknown, account: Account) => actionSlots([
              <Button key="reset-password" size="mini" type="text" onClick={() => openPasswordReset(account)}>
                重置密码
              </Button>,
              account.status === 'ACTIVE' ? (
                <Popconfirm key="status" title="停用后该账号立即无法登录，确认停用？" onOk={() => toggle(account)}>
                  <Button
                    size="mini"
                    type="text"
                    status="danger"
                    loading={togglingAccountIds.has(account.id)}
                    disabled={togglingAccountIds.has(account.id)}
                  >
                    停用
                  </Button>
                </Popconfirm>
              ) : (
                <Button
                  key="status"
                  size="mini"
                  type="text"
                  status="success"
                  loading={togglingAccountIds.has(account.id)}
                  disabled={togglingAccountIds.has(account.id)}
                  onClick={() => toggle(account)}
                >
                  启用
                </Button>
              ),
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
        title="新增供应商账号"
        visible={editOpen}
        confirmLoading={saving}
        closable={!saving}
        maskClosable={!saving}
        escToExit={!saving}
        cancelButtonProps={{ disabled: saving }}
        okButtonProps={{ disabled: roleOptionsLoading || roleOptionsError || roleOptions.length === 0 }}
        okText="创建账号"
        onOk={submit}
        onCancel={() => { if (!saving) setEditOpen(false) }}
      >
        <Form onSubmit={() => void submit()} form={form} layout="vertical">
          <div className="form-grid">
            <Form.Item label="账号" field="employeeNo" extra="不区分大小写，不可与内部账号或其他 Robot 厂商账号重名" rules={[{ required: true, message: '请输入账号' }, { match: ACCOUNT_NAME_PATTERN, message: ACCOUNT_NAME_MESSAGE }]}>
              <Input autoFocus placeholder={ACCOUNT_NAME_PLACEHOLDER} />
            </Form.Item>
            <Form.Item
              label="初始密码"
              field="password"
              extra="首次登录需改密"
              rules={[{ required: true, message: '请输入初始密码' }, passwordRule]}
            >
              <PasswordInput placeholder="5-20 位" />
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
          </div>
        <EnterSubmit /></Form>
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
        <Form onSubmit={() => void resetPwd()} form={pwdForm} layout="vertical">
          <Form.Item
            label="新密码"
            field="newPassword"
                rules={[{ required: true, message: '请输入新密码' }, passwordRule]}
          >
            <PasswordInput autoFocus placeholder="5-20 位" />
          </Form.Item>
          <div className="dialog-note">下次登录需改密</div>
        <EnterSubmit /></Form>
      </Modal>
    </Drawer>
  )
}
