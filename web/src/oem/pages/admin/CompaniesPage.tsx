import { useCallback, useRef, useState } from 'react'
import {
  Button, Card, Drawer, Form, Input, Message, Modal, Popconfirm, Space, Table, Tag, Tooltip, Typography,
} from '@arco-design/web-react'
import { IconPlus } from '@arco-design/web-react/icon'
import { fmtTime } from '../../../api/types'
import type { Company, VendorAccount } from '../../api/types'
import { useCan, useOem } from '../../OemContext'
import { useLoadEffect } from '../../useLoadEffect'
import './CompaniesPage.css'

function deleteErrorMessage(error: unknown, fallback: string): string {
  const message = (error as { response?: { data?: { message?: unknown } } })?.response?.data?.message
  if (typeof message === 'string' && message.trim()) return message
  if (error instanceof Error && error.message.trim()) return error.message
  return fallback
}

function EllipsisText({ value }: { value: string | null | undefined }) {
  const text = value || '-'
  return (
    <Tooltip content={text}>
      <span className="oem-directory-ellipsis">{text}</span>
    </Tooltip>
  )
}

interface AccountsDrawerProps {
  company: Company | null
  onClose: () => void
  onAccountDeleted: (company: Company, account: VendorAccount) => Promise<void>
}

function AccountsDrawer({ company, onClose, onAccountDeleted }: AccountsDrawerProps) {
  const { api } = useOem()
  const can = useCan()
  const [rows, setRows] = useState<VendorAccount[]>([])
  const [editing, setEditing] = useState<VendorAccount | 'new' | null>(null)
  const [saving, setSaving] = useState(false)
  const [deleteError, setDeleteError] = useState('')
  const accountsRequestId = useRef(0)
  const deletingIdsRef = useRef(new Set<number>())
  const [deletingIds, setDeletingIds] = useState<ReadonlySet<number>>(new Set())
  const saveInFlight = useRef(false)
  const [form] = Form.useForm()

  const load = useCallback(async () => {
    const requestId = ++accountsRequestId.current
    if (!company) {
      setRows([])
      return
    }
    const nextRows = await api.accounts(company.id)
    if (accountsRequestId.current === requestId) setRows(nextRows)
  }, [api, company])
  useLoadEffect(load)

  const save = async () => {
    if (saveInFlight.current) return
    saveInFlight.current = true
    try {
      const values = await form.validate().catch(() => null)
      if (!values) return
      setSaving(true)
      if (editing === 'new') await api.createAccount(company!.id, values)
      else if (editing) await api.updateAccount(editing.id, values)
      Message.success('已保存')
      setEditing(null)
      await load()
    } finally {
      saveInFlight.current = false
      setSaving(false)
    }
  }

  const reset = (account: VendorAccount) => {
    let password = ''
    Modal.confirm({
      title: `重置 ${account.realName} 的密码`,
      content: <Input.Password placeholder="新的初始密码（6-20 位）" onChange={(value) => { password = value }} />,
      onOk: async () => {
        await api.resetAccountPassword(account.id, password)
        Message.success('已重置，账号下次登录须修改密码，现有登录已失效')
      },
    })
  }

  const removeAccount = async (account: VendorAccount) => {
    if (!company || deletingIdsRef.current.has(account.id)) return
    const pending = new Set(deletingIdsRef.current).add(account.id)
    deletingIdsRef.current = pending
    setDeletingIds(pending)
    setDeleteError('')
    try {
      await api.deleteAccount(account.id)
    } catch (error) {
      setDeleteError(`删除失败：${deleteErrorMessage(error, '无法删除该账号，请稍后重试；如有业务历史请改为停用。')}`)
      return
    } finally {
      const remaining = new Set(deletingIdsRef.current)
      remaining.delete(account.id)
      deletingIdsRef.current = remaining
      setDeletingIds(remaining)
    }

    setRows((current) => current.filter((item) => item.id !== account.id))
    Message.success('账号已删除')
    const refreshed = await Promise.allSettled([load(), onAccountDeleted(company, account)])
    if (refreshed.some((result) => result.status === 'rejected')) {
      setDeleteError('账号已删除，但列表刷新失败；请重新打开账号抽屉重试。')
    }
  }

  const confirmDeleteAccount = (account: VendorAccount) => {
    Modal.confirm({
      title: `删除账号“${account.employeeNo}”`,
      content: `确认永久删除“${account.realName}（${account.employeeNo}）”？删除后不可恢复；如存在业务历史，系统会拒绝删除并说明原因，此时请停用账号。`,
      okText: '确认删除',
      cancelText: '取消',
      okButtonProps: { status: 'danger' },
      onOk: () => removeAccount(account),
    })
  }

  return (
    <Drawer
      width="min(920px, 100vw)"
      wrapClassName="oem-accounts-drawer"
      bodyStyle={{ overflowX: 'hidden' }}
      title={`${company?.name ?? ''} · 登录账号`}
      visible={Boolean(company)}
      onCancel={onClose}
      footer={null}
      unmountOnExit
    >
      {can.manageAccounts ? (
        <>
          <Button type="primary" icon={<IconPlus />} style={{ marginBottom: 12 }} onClick={() => { form.resetFields(); setEditing('new') }}>新增账号</Button>
          {deleteError && <Typography.Text className="oem-delete-error" type="error" role="alert">{deleteError}</Typography.Text>}
          <Table
            className="oem-account-table"
            rowKey="id"
            data={rows}
            pagination={false}
            scroll={{ x: 1100 }}
            columns={[
              { title: '登录账号', dataIndex: 'employeeNo', width: 150, ellipsis: true, render: (value: string) => <EllipsisText value={value} /> },
              { title: '姓名', dataIndex: 'realName', width: 110, ellipsis: true, render: (value: string) => <EllipsisText value={value} /> },
              { title: '邮箱', dataIndex: 'email', width: 230, ellipsis: true, render: (value: string) => <EllipsisText value={value} /> },
              {
                title: '状态', width: 190,
                render: (_: unknown, row: VendorAccount) => (
                  <Space size={4}>
                    <Tag color={row.status === 'ACTIVE' ? 'green' : 'gray'} size="small">{row.status === 'ACTIVE' ? '启用' : '停用'}</Tag>
                    {row.mustChangePassword && <Tag size="small">待改密</Tag>}
                    {row.locked && <Tag color="red" size="small">已锁定</Tag>}
                  </Space>
                ),
              },
              { title: '最近登录', width: 160, render: (_: unknown, row: VendorAccount) => (row.lastLoginAt ? fmtTime(row.lastLoginAt) : '-') },
              {
                title: '操作', width: 260, fixed: 'right' as const,
                render: (_: unknown, row: VendorAccount) => (
                  <Space size={4}>
                    <Button size="mini" onClick={() => { form.setFieldsValue(row); setEditing(row) }}>编辑</Button>
                    <Button size="mini" onClick={() => reset(row)}>重置密码</Button>
                    <Button size="mini" status={row.status === 'ACTIVE' ? 'warning' : 'success'}
                      onClick={async () => { await api.setAccountStatus(row.id, row.status === 'ACTIVE' ? 'DISABLED' : 'ACTIVE'); await load() }}>
                      {row.status === 'ACTIVE' ? '停用' : '启用'}
                    </Button>
                    {can.deleteAccounts && (
                      <Button
                        size="mini"
                        status="danger"
                        loading={deletingIds.has(row.id)}
                        disabled={deletingIds.has(row.id)}
                        onClick={() => confirmDeleteAccount(row)}
                      >
                        删除
                      </Button>
                    )}
                  </Space>
                ),
              },
            ]}
          />
        </>
      ) : <span>当前账号没有 OEM 账号管理权限。</span>}
      <Modal
        title={editing === 'new' ? '新增账号' : '编辑账号'}
        visible={Boolean(editing)}
        confirmLoading={saving}
        closable={!saving}
        maskClosable={!saving}
        escToExit={!saving}
        cancelButtonProps={{ disabled: saving }}
        onCancel={() => { if (!saving) setEditing(null) }}
        onOk={save}
        unmountOnExit
      >
        <Form form={form} layout="vertical">
          {editing === 'new' && (
            <Form.Item field="employeeNo" label="登录账号" rules={[{ required: true }, { match: /^[A-Za-z0-9_]{3,32}$/, message: '3~32 位字母/数字/下划线' }]}>
              <Input />
            </Form.Item>
          )}
          <Form.Item field="realName" label="姓名" rules={[{ required: true }, { maxLength: 64 }]}><Input /></Form.Item>
          <Form.Item field="email" label="邮箱" rules={[{ required: true, type: 'email' }]}><Input /></Form.Item>
          {editing === 'new' && (
            <Form.Item field="password" label="初始密码" rules={[{ required: true }, { minLength: 6, maxLength: 20 }]}><Input.Password /></Form.Item>
          )}
        </Form>
      </Modal>
    </Drawer>
  )
}

export default function CompaniesPage() {
  const { api } = useOem()
  const can = useCan()
  const [rows, setRows] = useState<Company[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [keyword, setKeyword] = useState('')
  const [editing, setEditing] = useState<Company | 'new' | null>(null)
  const [saving, setSaving] = useState(false)
  const [accountsOf, setAccountsOf] = useState<Company | null>(null)
  const [deleteError, setDeleteError] = useState('')
  const deletingIdsRef = useRef(new Set<number>())
  const [deletingIds, setDeletingIds] = useState<ReadonlySet<number>>(new Set())
  const saveInFlight = useRef(false)
  const [form] = Form.useForm()

  const load = useCallback(async () => {
    if (!can.manageCompanies && !can.manageAccounts) return
    const result = await api.companies({ page, pageSize: 20, keyword: keyword || undefined })
    setRows(result.list)
    setTotal(result.total)
  }, [api, page, keyword, can.manageCompanies, can.manageAccounts])
  useLoadEffect(load)

  const save = async () => {
    if (saveInFlight.current) return
    saveInFlight.current = true
    try {
      const values = await form.validate().catch(() => null)
      if (!values) return
      setSaving(true)
      if (editing === 'new') await api.createCompany(values)
      else if (editing) await api.updateCompany(editing.id, values)
      Message.success('已保存')
      setEditing(null)
      await load()
    } finally {
      saveInFlight.current = false
      setSaving(false)
    }
  }

  const removeCompany = async (company: Company) => {
    if (deletingIdsRef.current.has(company.id)) return
    const pending = new Set(deletingIdsRef.current).add(company.id)
    deletingIdsRef.current = pending
    setDeletingIds(pending)
    setDeleteError('')
    try {
      await api.deleteCompany(company.id)
    } catch (error) {
      setDeleteError(`删除失败：${deleteErrorMessage(error, '无法删除该厂商，请稍后重试；如有业务历史请改为停用。')}`)
      return
    } finally {
      const remaining = new Set(deletingIdsRef.current)
      remaining.delete(company.id)
      deletingIdsRef.current = remaining
      setDeletingIds(remaining)
    }

    setRows((current) => current.filter((item) => item.id !== company.id))
    setTotal((current) => Math.max(0, current - 1))
    if (accountsOf?.id === company.id) setAccountsOf(null)
    Message.success('厂商已删除')
    if (rows.length === 1 && page > 1) {
      setPage(page - 1)
      return
    }
    try {
      await load()
    } catch {
      setDeleteError('厂商已删除，但列表刷新失败；请重新搜索或切换分页重试。')
    }
  }

  const confirmDeleteCompany = (company: Company) => {
    Modal.confirm({
      title: `删除厂商“${company.name}”`,
      content: `确认永久删除“${company.name}”？删除后不可恢复；如存在账号或业务历史，系统会拒绝删除并说明原因，此时请停用厂商。`,
      okText: '确认删除',
      cancelText: '取消',
      okButtonProps: { status: 'danger' },
      onOk: () => removeCompany(company),
    })
  }

  const handleAccountDeleted = useCallback(async (company: Company, account: VendorAccount) => {
    setRows((current) => current.map((row) => row.id === company.id ? {
      ...row,
      accountCount: Math.max(0, (row.accountCount ?? 0) - 1),
      activeAccountCount: Math.max(0, (row.activeAccountCount ?? 0) - (account.status === 'ACTIVE' ? 1 : 0)),
    } : row))
    await load()
  }, [load])

  return (
    <Card title="OEM 厂商与账号" extra={can.manageCompanies && (
      <Button type="primary" icon={<IconPlus />} onClick={() => { form.resetFields(); setEditing('new') }}>新增厂商</Button>
    )}>
      <Input.Search allowClear placeholder="搜索厂商名称" style={{ width: 260, marginBottom: 12 }} onSearch={(value) => { setKeyword(value); setPage(1) }} />
      {deleteError && <Typography.Text className="oem-delete-error" type="error" role="alert">{deleteError}</Typography.Text>}
      <Table
        className="oem-directory-table"
        rowKey="id"
        data={rows}
        scroll={{ x: 1110 }}
        pagination={{ current: page, total, pageSize: 20, onChange: setPage }}
        columns={[
          { title: '厂商名称', dataIndex: 'name', width: 230, ellipsis: true, render: (value: string) => <EllipsisText value={value} /> },
          { title: '联系人', dataIndex: 'contactName', width: 120, ellipsis: true, render: (value: string | null) => <EllipsisText value={value} /> },
          {
            title: '联系方式', width: 270, ellipsis: true,
            render: (_: unknown, row: Company) => <EllipsisText value={[row.contactPhone, row.contactEmail].filter(Boolean).join(' / ') || '-'} />,
          },
          { title: '账号', width: 100, render: (_: unknown, row: Company) => `${row.activeAccountCount ?? 0} / ${row.accountCount ?? 0}` },
          { title: '状态', width: 90, render: (_: unknown, row: Company) => <Tag color={row.status === 'ACTIVE' ? 'green' : 'gray'}>{row.status === 'ACTIVE' ? '启用' : '停用'}</Tag> },
          {
            title: '操作', width: 300, fixed: 'right' as const,
            render: (_: unknown, row: Company) => (
              <Space size={4}>
                {can.manageAccounts && <Button size="mini" onClick={() => setAccountsOf(row)}>账号</Button>}
                {can.manageCompanies && <Button size="mini" onClick={() => { form.setFieldsValue(row); setEditing(row) }}>编辑</Button>}
                {can.manageCompanies && <Popconfirm title={row.status === 'ACTIVE' ? '停用后该厂商所有账号将立即退出登录，确定？' : '确定启用该厂商？'}
                  onOk={async () => { await api.setCompanyStatus(row.id, row.status === 'ACTIVE' ? 'DISABLED' : 'ACTIVE'); await load() }}>
                  <Button size="mini" status={row.status === 'ACTIVE' ? 'warning' : 'success'}>{row.status === 'ACTIVE' ? '停用' : '启用'}</Button>
                </Popconfirm>}
                {can.manageCompanies && can.deleteCompanies && (
                  <Button
                    size="mini"
                    status="danger"
                    loading={deletingIds.has(row.id)}
                    disabled={deletingIds.has(row.id)}
                    onClick={() => confirmDeleteCompany(row)}
                  >
                    删除
                  </Button>
                )}
              </Space>
            ),
          },
        ]}
      />
      <Modal
        title={editing === 'new' ? '新增厂商' : '编辑厂商'}
        visible={Boolean(editing)}
        confirmLoading={saving}
        closable={!saving}
        maskClosable={!saving}
        escToExit={!saving}
        cancelButtonProps={{ disabled: saving }}
        onCancel={() => { if (!saving) setEditing(null) }}
        onOk={save}
        unmountOnExit
      >
        <Form form={form} layout="vertical">
          <Form.Item field="name" label="厂商名称" rules={[{ required: true }, { maxLength: 128 }]}><Input /></Form.Item>
          <Form.Item field="contactName" label="联系人"><Input maxLength={64} /></Form.Item>
          <Form.Item field="contactPhone" label="联系电话"><Input maxLength={32} /></Form.Item>
          <Form.Item field="contactEmail" label="联系邮箱" rules={[{ type: 'email' }]}><Input /></Form.Item>
          <Form.Item field="remark" label="备注"><Input.TextArea maxLength={512} /></Form.Item>
        </Form>
      </Modal>
      <AccountsDrawer
        key={accountsOf?.id ?? 'closed'}
        company={accountsOf}
        onClose={() => setAccountsOf(null)}
        onAccountDeleted={handleAccountDeleted}
      />
    </Card>
  )
}
