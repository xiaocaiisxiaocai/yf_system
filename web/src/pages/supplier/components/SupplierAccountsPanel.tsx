import { useCallback, useEffect, useRef, useState } from 'react'
import {
  Alert, Button, Empty, Form, Input, Message, Modal, Select, Tooltip, Typography,
} from '@arco-design/web-react'
import ResizableTable from '../../../components/ResizableTable'
import { IconPlus } from '@arco-design/web-react/icon'
import http, { type QuietRequestConfig } from '../../../api/client'
import { actionSlots } from '../../../components/ActionSlots'
import DangerPopconfirm from '../../../components/DangerPopconfirm'
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

function EllipsisText({ value }: { value: string | null | undefined }) {
  const text = value || '-'
  return (
    <Tooltip content={text}>
      <span className="supplier-ellipsis">{text}</span>
    </Tooltip>
  )
}

interface SupplierAccountsPanelProps {
  supplier: Supplier
  refreshVersion?: number
}

export function SupplierAccountsPanel({ supplier, refreshVersion = 0 }: SupplierAccountsPanelProps) {
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
  const supplierId = supplier.id

  const fetchAccounts = useCallback(async () => {
    const response = await http.get<ApiResponses['GET /admin/suppliers/{id}/accounts']>(
      `/admin/suppliers/${supplierId}/accounts`,
      { quietNetworkError: true } as QuietRequestConfig,
    )
    return { supplierId, list: response.data as Account[] }
  }, [supplierId])

  const load = useCallback(async () => {
    const requestId = ++accountsRequestId.current
    setRefreshing(true)
    try {
      const next = await fetchAccounts()
      if (accountsRequestId.current === requestId) setAccountsState(next)
    } catch {
      if (accountsRequestId.current === requestId) {
        setAccountsState({ supplierId, list: [], error: true })
      }
    } finally {
      if (accountsRequestId.current === requestId) setRefreshing(false)
    }
  }, [fetchAccounts, supplierId])

  useEffect(() => {
    let active = true
    const requestId = ++accountsRequestId.current
    fetchAccounts().then((next) => {
      if (active && accountsRequestId.current === requestId) setAccountsState(next)
    }).catch(() => {
      if (active && accountsRequestId.current === requestId) {
        setAccountsState({ supplierId, list: [], error: true })
      }
    })
    return () => {
      active = false
      if (accountsRequestId.current === requestId) accountsRequestId.current += 1
    }
  }, [fetchAccounts, supplierId])

  const appliedRefreshRef = useRef(refreshVersion)
  useEffect(() => {
    if (appliedRefreshRef.current === refreshVersion) return
    appliedRefreshRef.current = refreshVersion
    void load()
  }, [refreshVersion, load])

  const accounts = accountsState.supplierId === supplierId ? accountsState.list : []
  const loading = refreshing || accountsState.supplierId !== supplierId
  const accountsFailed = !!accountsState.error && accountsState.supplierId === supplierId

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
      if (supplier.status === 'DISABLED') {
        Message.warning('供应商已停用，不能新增账号')
        return
      }
      const values = await form.validate().catch(() => null)
      if (!values) return
      setSaving(true)
      await http.post<ApiResponses['POST /admin/suppliers/{id}/accounts']>(`/admin/suppliers/${supplierId}/accounts`, values)
      Message.success('账号已创建（首次登录需改密）')
      setEditOpen(false)
      void load()
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
      void load()
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
    void load()
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

  return (
    <section className="supplier-subpanel supplier-accounts-panel" aria-label="账号管理">
      <div className="supplier-subpanel-head">
        <div className="supplier-subpanel-title">
          <h3>账号管理</h3>
          <Typography.Text type="secondary">
            {loading ? '账号加载中…' : accountsFailed ? '账号列表暂不可用' : `共 ${accounts.length} 个账号`}
          </Typography.Text>
        </div>
        <Tooltip content="供应商已停用，无法新增账号" disabled={supplier.status !== 'DISABLED'}>
          <Button
            type="primary"
            size="small"
            icon={<IconPlus />}
            disabled={supplier.status === 'DISABLED'}
            onClick={() => {
              form.resetFields()
              setEditOpen(true)
              void loadRoleOptions()
            }}
          >
            新增账号
          </Button>
        </Tooltip>
      </div>
      <div className="supplier-subpanel-body">
        {accountsFailed && (
          <Alert
            type="error"
            style={{ marginBottom: 12 }}
            content="账号列表加载失败，请重试。"
            action={<Button size="small" loading={refreshing} onClick={() => void load()}>重试</Button>}
          />
        )}
        {!loading && !accountsFailed && accounts.length > 0 && accounts.every((item) => item.status !== 'ACTIVE') && (
          <Alert type="warning" style={{ marginBottom: 12 }} content="该供应商所有账号均已停用，无法登录协作平台或被选入新建项目。" />
        )}
        {!accountsFailed && <ResizableTable tableId="supplier-accounts"
          className="account-table supplier-account-table"
          rowKey="id"
          size="small"
          loading={loading}
          noDataElement={loading ? <span /> : <Empty description="暂无账号" />}
          data={accounts}
          pagination={false}
          scroll={{ x: 700 }}
          columns={[
            { title: '账号', dataIndex: 'employeeNo', width: 140, ellipsis: true, render: (value: string) => <EllipsisText value={value} /> },
            { title: '角色', dataIndex: 'roleName', width: 116, ellipsis: true, render: (value: string | null) => <EllipsisText value={value} /> },
            {
              title: '状态',
              dataIndex: 'status',
              width: 72,
              align: 'center' as const,
              render: (value: string) => <StatusTag status={value} size="small" />,
            },
            { title: '最近登录', dataIndex: 'lastLoginAt', width: 168, align: 'center' as const, ellipsis: true, render: fmtTime },
            {
              title: '操作',
              width: 204,
              fixed: 'right' as const,
              render: (_: unknown, account: Account) => actionSlots([
                <Button key="reset-password" size="mini" type="text" onClick={() => openPasswordReset(account)}>
                  重置密码
                </Button>,
                account.status === 'ACTIVE' ? (
                  <DangerPopconfirm key="status" title="停用后该账号立即无法登录，确认停用？" disabled={togglingAccountIds.has(account.id)} onOk={() => toggle(account)}>
                    <Button
                      size="mini"
                      type="text"
                      status="danger"
                      loading={togglingAccountIds.has(account.id)}
                      disabled={togglingAccountIds.has(account.id)}
                    >
                      停用
                    </Button>
                  </DangerPopconfirm>
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
                  <DangerPopconfirm key="delete" title="删除后不可恢复，确认删除该账号？" onOk={() => removeAccount(account)}>
                    <Button size="mini" type="text" status="danger">删除</Button>
                  </DangerPopconfirm>
                ),
              ], 'account'),
            },
          ]}
        />}

        <Modal
          className="form-dialog"
          title="新增供应商账号"
          visible={editOpen}
          unmountOnExit
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
          unmountOnExit
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
      </div>
    </section>
  )
}
