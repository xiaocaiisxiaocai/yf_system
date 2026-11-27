import { useCallback, useEffect, useRef, useState } from 'react'
import {
  Alert, Button, Drawer, Empty, Form, Input, InputNumber, Message, Modal, Pagination, Popconfirm, Select, Space, Spin, Tag, Tooltip, Typography,
} from '@arco-design/web-react'
import { DEFAULT_PAGE_SIZE } from '../../../components/tablePagination'
import ResizableTable from '../../../components/ResizableTable'
import { IconPlus } from '@arco-design/web-react/icon'
import { fmtTime } from '../../../api/types'
import { oemRequestError } from '../../api/requestError'
import type { Company, CompanyContact, InternalRecipient, VendorAccount } from '../../api/types'
import { useCan, useOem } from '../../OemContext'
import ExcelImportDialog, { type ImportBatchOutcome, type ImportPreviewRow, type PreparedImport } from '../../../components/ExcelImportDialog'
import { downloadExcelWorkbook, type ExcelImportRow, type ExcelWorkbookSheet } from '../../../utils/excelWorkbook'
import { PASSWORD_MAX_CHARS, PASSWORD_MIN_CHARS, passwordRule, validateInitialPassword, validatePassword } from '../../../utils/password'
import { useLatestRequest, useLoadEffect } from '../../useLoadEffect'
import './CompaniesPage.css'
import EnterSubmit from '../../../components/EnterSubmit'
import PasswordInput from '../../../components/PasswordInput'
import ToolbarMenuButton from '../../../components/ToolbarMenuButton'
import { ACCOUNT_NAME_MESSAGE, ACCOUNT_NAME_PATTERN, ACCOUNT_NAME_PLACEHOLDER, isValidAccountName } from '../../../utils/accountName'
import StatusTag from '../../../components/StatusTag'
import DangerPopconfirm from '../../../components/DangerPopconfirm'

const OEM_COMPANY_IMPORT_HEADERS = ['厂商名称', '备注'] as const
const OEM_COMPANY_WORKBOOK: ExcelWorkbookSheet[] = [
  { name: '数据', headers: OEM_COMPANY_IMPORT_HEADERS, rows: [['示例厂商', '示例备注']] },
  { name: '说明', headers: ['说明'], rows: [
    ['本模板仅新增 ODM/OEMA 厂商资料；已存在的厂商名称会跳过，不覆盖现有资料。'],
    ['“状态”、账号数和联络人数为只读信息，不参与导入。新建厂商默认为启用。'],
  ] },
]
const OEM_ACCOUNT_IMPORT_HEADERS = ['厂商名称', '账号', '初始密码'] as const
const OEM_ACCOUNT_EXPORT_HEADERS = ['厂商名称', '账号', '状态', '最近登录'] as const
const OEM_ACCOUNT_WORKBOOK: ExcelWorkbookSheet[] = [
  { name: '数据', headers: OEM_ACCOUNT_IMPORT_HEADERS, rows: [['示例厂商', 'demo001', 'T7!kmQ4n']] },
  { name: '说明', headers: ['说明'], rows: [
    ['一行一个账号；同一厂商有多个账号时，重复填写厂商名称。'],
    ['厂商必须已存在，名称须与系统中的厂商名称一致。'],
    ['只新增账号，已存在的账号会跳过。初始密码为 5–20 个字符，新账号首次登录须修改密码。'],
  ] },
]

interface OemAccountImportPayload {
  rowNumber: number
  companyId: number
  companyName: string
  employeeNo: string
  initialPassword: string
}
interface OemCompanyImportPayload {
  rowNumber: number
  name: string
  remark: string
}

function EllipsisText({ value }: { value: string | null | undefined }) {
  const text = value || '-'
  return (
    <Tooltip content={text}>
      <span className="oem-directory-ellipsis">{text}</span>
    </Tooltip>
  )
}

interface AccountsPanelProps {
  company: Company
  refreshVersion: number
  onAccountCreated: (company: Company) => Promise<void>
  onAccountStatusToggled: (company: Company, account: VendorAccount, nextStatus: 'ACTIVE' | 'DISABLED') => void
  onAccountDeleted: (company: Company, account: VendorAccount) => Promise<void>
}

const OEM_CONTACT_IMPORT_HEADERS = ['厂商名称', '联络人姓名', '邮箱', '排序'] as const
const OEM_CONTACT_EXPORT_HEADERS = ['状态'] as const
const OEM_CONTACT_WORKBOOK: ExcelWorkbookSheet[] = [
  { name: '数据', headers: OEM_CONTACT_IMPORT_HEADERS, rows: [['示例厂商', '示例联络人', 'example@example.com', '1']] },
  { name: '说明', headers: ['说明'], rows: [
    ['一行一个联络人；同一厂商有多个联络人时，重复填写厂商名称。'],
    ['厂商必须已存在，名称须与系统中的厂商名称一致。'],
    ['只新增联络人；同一厂商有多位联络人时重复填写厂商名称。邮箱用于厂商邮件通知。'],
  ] },
]

interface OemContactImportPayload {
  rowNumber: number
  companyId: number
  companyName: string
  name: string
  email: string
  sortNo: number
}

interface InternalRecipientsDrawerProps {
  open: boolean
  onClose: () => void
}

/**
 * The notification list, the same for every vendor. Everyone with ODM/OEMA view + download is on it by default;
 * people removed here are no longer offered to a vendor when it sends files.
 */
function InternalRecipientsDrawer({ open, onClose }: InternalRecipientsDrawerProps) {
  const { api } = useOem()
  const [rows, setRows] = useState<InternalRecipient[]>([])
  const [keyword, setKeyword] = useState('')
  const [loading, setLoading] = useState(false)
  const [failed, setFailed] = useState(false)
  const [busyId, setBusyId] = useState<number | null>(null)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(DEFAULT_PAGE_SIZE)
  const [importOpen, setImportOpen] = useState(false)
  const [importing, setImporting] = useState(false)
  const [picked, setPicked] = useState<number[]>([])
  const loadId = useRef(0)

  const load = useCallback(async () => {
    const current = ++loadId.current
    if (!open) { setRows([]); setFailed(false); setLoading(false); return }
    setLoading(true)
    try {
      const result = await api.allInternalRecipients()
      if (loadId.current !== current) return
      setRows(result)
      setFailed(false)
    } catch {
      if (loadId.current === current) setFailed(true)
    } finally {
      if (loadId.current === current) setLoading(false)
    }
  }, [api, open])
  useLoadEffect(load)

  const toggle = async (person: InternalRecipient) => {
    if (busyId !== null) return
    setBusyId(person.userId)
    try {
      const result = await api.setRecipientRemovedFromAll(person.userId, !person.removed)
      loadId.current += 1
      setRows(result)
    } catch {
      // The request layer displays the reason.
    } finally {
      setBusyId(null)
    }
  }

  const removedRows = rows.filter(person => person.removed)
  const openImport = () => { setPicked([]); setImportOpen(true) }
  /** Re-import: the removed people the administrator ticks go back on the list. */
  const reimport = async () => {
    if (importing || picked.length === 0) return
    setImporting(true)
    try {
      const result = await api.restoreRecipients(picked)
      loadId.current += 1
      setRows(result)
      setImportOpen(false)
      Message.success(`已重新导入 ${picked.length} 人`)
    } catch {
      // The request layer displays the reason.
    } finally {
      setImporting(false)
    }
  }

  const term = keyword.trim().toLowerCase()
  const shown = rows
    .filter(person => !term || [person.realName, person.employeeNo, person.departmentName ?? ''].some(value => value.toLowerCase().includes(term)))
    .sort((a, b) => Number(a.removed) - Number(b.removed))
  return (
    <Drawer width="min(760px, 100vw)" title="可通知人员" visible={open}
      onCancel={onClose} unmountOnExit footer={<Button onClick={onClose}>关闭</Button>}>
      {failed && <Alert type="error" style={{ marginBottom: 12 }} content="可通知人员加载失败，请重试。" action={<Button size="small" loading={loading} onClick={() => void load()}>重试</Button>} />}
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 12, marginBottom: 12 }}>
        <Input.Search allowClear placeholder="姓名、工号或组织" style={{ width: 260 }} value={keyword} onChange={(value) => { setKeyword(value); setPage(1) }} />
        <Button disabled={loading || removedRows.length === 0} onClick={openImport}>重新导入</Button>
      </div>
      <Modal title="重新导入" visible={importOpen} onCancel={() => !importing && setImportOpen(false)} onOk={() => void reimport()}
        okText="导入" confirmLoading={importing} closable={!importing} maskClosable={!importing} escToExit={!importing}
        cancelButtonProps={{ disabled: importing }} okButtonProps={{ disabled: picked.length === 0 }} unmountOnExit>
        <ResizableTable tableId="oem-internal-recipients-reimport" rowKey="userId" size="small" pagination={false} data={removedRows}
          scroll={{ y: 320 }}
          rowSelection={{ type: 'checkbox', selectedRowKeys: picked, onChange: (keys) => setPicked(keys as number[]) }}
          columns={[
            { title: '姓名', render: (_: unknown, person: InternalRecipient) => `${person.realName}（${person.employeeNo}）` },
            { title: '组织', render: (_: unknown, person: InternalRecipient) => person.departmentName ?? '-' },
          ]}
        />
      </Modal>
      <ResizableTable tableId="oem-internal-recipients" rowKey="userId" size="small" loading={loading} data={shown}
        scroll={{ y: 'calc(100dvh - 330px)' }}
        pagination={{ total: shown.length, current: page, pageSize,
          onChange: (nextPage, nextSize) => { setPage(nextPage); setPageSize(nextSize) } }}
        noDataElement={<Empty description="没有具备 ODM/OEMA 查看和下载权限的人员" />}
        columns={[
          { title: '姓名', width: 160, render: (_: unknown, person: InternalRecipient) => `${person.realName}（${person.employeeNo}）` },
          { title: '组织', width: 140, render: (_: unknown, person: InternalRecipient) => person.departmentName ?? '-' },
          { title: '邮箱', width: 180, render: (_: unknown, person: InternalRecipient) => person.email ?? '-' },
          {
            title: '状态', width: 110, render: (_: unknown, person: InternalRecipient) => person.removed
              ? <Tag>已移除</Tag>
              : person.canReceive ? <Tag color="green">可通知</Tag> : <Tag color="orange">{person.unavailableReason ?? '无法接收'}</Tag>,
          },
          {
            title: '操作', width: 80, render: (_: unknown, person: InternalRecipient) => (
              <Button size="mini" type="text" status={person.removed ? undefined : 'danger'} loading={busyId === person.userId}
                disabled={busyId !== null && busyId !== person.userId} onClick={() => void toggle(person)}>
                {person.removed ? '恢复' : '移除'}
              </Button>
            ),
          },
        ]}
      />
    </Drawer>
  )
}

interface ContactsPanelProps {
  company: Company
  refreshVersion: number
  onChanged: () => void
}

function ContactsPanel({ company, refreshVersion, onChanged }: ContactsPanelProps) {
  const { api } = useOem()
  const [rows, setRows] = useState<CompanyContact[]>([])
  const [loading, setLoading] = useState(true)
  const [failed, setFailed] = useState(false)
  const [adding, setAdding] = useState(false)
  const [editingContact, setEditingContact] = useState<CompanyContact | null>(null)
  const [saving, setSaving] = useState(false)
  const [form] = Form.useForm()
  const beginContactRequest = useLatestRequest()
  const companyId = company.id

  const load = useCallback(async () => {
    const isLatest = beginContactRequest()
    setLoading(true)
    try {
      const result = await api.contacts(companyId)
      if (!isLatest()) return
      setRows(result)
      setFailed(false)
    } catch {
      if (isLatest()) setFailed(true)
    } finally {
      if (isLatest()) setLoading(false)
    }
  }, [api, companyId, beginContactRequest])
  useLoadEffect(load)

  const appliedRefreshRef = useRef(refreshVersion)
  useEffect(() => {
    if (appliedRefreshRef.current === refreshVersion) return
    appliedRefreshRef.current = refreshVersion
    void load()
  }, [refreshVersion, load])

  const createContact = async () => {
    if (saving) return
    const values = await form.validate().catch(() => null)
    if (!values) return
    const email = String(values.email ?? '').trim()
    if (!email) { Message.warning('请输入邮箱'); return }
    setSaving(true)
    try {
      const body = { name: values.name.trim(), email: email || null, sortNo: values.sortNo ?? 0 }
      if (editingContact) await api.updateContact(editingContact.id, body)
      else await api.createContact(companyId, body)
      setAdding(false)
      form.resetFields()
      await load()
      onChanged()
      Message.success(editingContact ? '联络人已更新' : '联络人已添加')
    } catch {
      // The request layer displays errors; keep the form open for correction.
    } finally {
      setSaving(false)
    }
  }

  return (
    <section className="oem-subpanel oem-contacts-panel" aria-label="联络人">
      <div className="oem-subpanel-head">
        <div className="oem-subpanel-title">
          <h3>联络人</h3>
          <Typography.Text type="secondary">共 {rows.length} 位联络人</Typography.Text>
        </div>
        <Button type="primary" size="small" icon={<IconPlus />} disabled={company.status !== 'ACTIVE'}
          onClick={() => { setEditingContact(null); form.resetFields(); setAdding(true) }}>新增联络人</Button>
      </div>
      <div className="oem-subpanel-body">
        {failed && <Alert type="error" style={{ marginBottom: 12 }} content="联络人列表加载失败，请重试。" action={<Button size="small" loading={loading} onClick={() => void load()}>重试</Button>} />}
        {!loading && !failed && rows.length > 0 && rows.every((item) => item.status !== 'ACTIVE') && (
          <Alert type="warning" style={{ marginBottom: 12 }} content="该厂商所有联络人均已停用，传递单发布后将不会收到邮件通知。" />
        )}
        <ResizableTable tableId="oem-company-contacts" className="oem-contact-table" rowKey="id" size="small" loading={loading} data={rows}
          pagination={false} scroll={{ x: 600 }} noDataElement={<Empty description="暂无联络人" />} columns={[
            { title: '姓名', dataIndex: 'name', width: 140, ellipsis: true, render: (value: string) => <EllipsisText value={value} /> },
            { title: '邮箱', dataIndex: 'email', width: 220, ellipsis: true, render: (value: string | null) => <EllipsisText value={value} /> },
            { title: '排序', dataIndex: 'sortNo', width: 64, align: 'center' as const },
            { title: '状态', width: 68, align: 'center' as const, render: (_: unknown, row: CompanyContact) => <StatusTag status={row.status} size="small" /> },
            { title: '操作', width: 124, fixed: 'right' as const, render: (_: unknown, row: CompanyContact) => <Space size={4}>
              <Button size="mini" type="text" onClick={() => { setEditingContact(row); form.setFieldsValue(row); setAdding(true) }}>编辑</Button>
              {row.status === 'ACTIVE' ? (
                <DangerPopconfirm title="确认停用该联络人？" onOk={async () => { await api.setContactStatus(row.id, 'DISABLED'); await load(); onChanged() }}>
                  <Button size="mini" type="text" status="danger">停用</Button>
                </DangerPopconfirm>
              ) : (
                <Popconfirm title="确认启用该联络人？" onOk={async () => { await api.setContactStatus(row.id, 'ACTIVE'); await load(); onChanged() }}>
                  <Button size="mini" type="text" status="success">启用</Button>
                </Popconfirm>
              )}
            </Space> },
          ]} />
        <Modal className="form-dialog" title={editingContact ? '编辑联络人' : '新增联络人'} visible={adding} confirmLoading={saving}
          closable={!saving} maskClosable={!saving} escToExit={!saving} cancelButtonProps={{ disabled: saving }}
          onCancel={() => { if (!saving) setAdding(false) }} onOk={() => void createContact()} unmountOnExit>
          <Form form={form} layout="vertical" onSubmit={() => void createContact()}>
            <Form.Item field="name" label="姓名" rules={[{ required: true }, { maxLength: 64 }]}><Input autoFocus /></Form.Item>
            <Form.Item field="email" label="邮箱" rules={[{ required: true, message: '请输入邮箱' }, { type: 'email' }]}><Input /></Form.Item>
            <Form.Item field="sortNo" label="排序"><InputNumber style={{ width: '100%' }} min={0} max={9999} precision={0} placeholder="0" /></Form.Item>
            <EnterSubmit />
          </Form>
        </Modal>
      </div>
    </section>
  )
}

function AccountsPanel({ company, refreshVersion, onAccountCreated, onAccountStatusToggled, onAccountDeleted }: AccountsPanelProps) {
  const { api } = useOem()
  const can = useCan()
  const [rows, setRows] = useState<VendorAccount[]>([])
  const [loadError, setLoadError] = useState('')
  const [accountsLoading, setAccountsLoading] = useState(true)
  const [adding, setAdding] = useState(false)
  const [saving, setSaving] = useState(false)
  const [actionError, setActionError] = useState('')
  const beginAccountRequest = useLatestRequest()
  const deletingIdsRef = useRef(new Set<number>())
  const [deletingIds, setDeletingIds] = useState<ReadonlySet<number>>(new Set())
  const saveInFlight = useRef(false)
  const [form] = Form.useForm()
  const confirmation = useRef<ReturnType<typeof Modal.confirm> | null>(null)
  const companyId = company.id
  // Arco's static dialogs live outside the panel. Close them when switching vendors or losing permission.
  useEffect(() => () => confirmation.current?.close(), [])

  const load = useCallback(async () => {
    const isLatest = beginAccountRequest()
    setAccountsLoading(true)
    try {
      const nextRows = await api.accounts(companyId)
      if (!isLatest()) return
      setRows(nextRows)
      setLoadError('')
    } catch (error) {
      if (isLatest()) setLoadError(oemRequestError(error, '账号列表加载失败，请重试'))
    } finally {
      if (isLatest()) setAccountsLoading(false)
    }
  }, [api, companyId, beginAccountRequest])
  useLoadEffect(load)

  const appliedRefreshRef = useRef(refreshVersion)
  useEffect(() => {
    if (appliedRefreshRef.current === refreshVersion) return
    appliedRefreshRef.current = refreshVersion
    void load()
  }, [refreshVersion, load])

  const save = async () => {
    if (saveInFlight.current) return
    saveInFlight.current = true
    try {
      const values = await form.validate().catch(() => null)
      if (!values) return
      setSaving(true)
      await api.createAccount(companyId, values)
      Message.success('账号已创建（首次登录需改密）')
      setAdding(false)
      await Promise.allSettled([load(), onAccountCreated(company)])
    } finally {
      saveInFlight.current = false
      setSaving(false)
    }
  }

  const reset = (account: VendorAccount) => {
    let password = ''
    confirmation.current = Modal.confirm({
      title: `重置账号 ${account.employeeNo} 的密码`,
      content: <PasswordInput autoFocus autoComplete="new-password" placeholder={`新的初始密码（${PASSWORD_MIN_CHARS}-${PASSWORD_MAX_CHARS} 个字符）`}
        onChange={(value) => { password = value }} />,
      onOk: async () => {
        const invalid = password ? validatePassword(password) : '请输入新的初始密码'
        if (invalid) {
          Message.warning(invalid)
          // Keep the dialog open so the administrator can correct the password.
          throw new Error(invalid)
        }
        // A failure is reported by the HTTP layer; the rejection keeps the dialog open for a retry.
        await api.resetAccountPassword(account.id, password)
        Message.success('已重置，账号下次登录须修改密码，现有登录已失效')
      },
    })
  }

  // The server refuses e.g. disabling a vendor's last active account; keep its reason visible.
  const toggleAccountStatus = async (account: VendorAccount) => {
    const disabling = account.status === 'ACTIVE'
    const nextStatus = disabling ? 'DISABLED' : 'ACTIVE'
    setActionError('')
    try {
      await api.setAccountStatus(account.id, nextStatus)
    } catch (error) {
      setActionError(`${disabling ? '停用' : '启用'}失败：${oemRequestError(error, '操作失败，请稍后重试')}`)
      return
    }
    onAccountStatusToggled(company, account, nextStatus)
    await load()
  }

  const removeAccount = async (account: VendorAccount) => {
    if (deletingIdsRef.current.has(account.id)) return
    const pending = new Set(deletingIdsRef.current).add(account.id)
    deletingIdsRef.current = pending
    setDeletingIds(pending)
    setActionError('')
    try {
      await api.deleteAccount(account.id)
    } catch (error) {
      setActionError(`删除失败：${oemRequestError(error, '无法删除该账号，请稍后重试；如有业务历史请改为停用。')}`)
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
      setActionError('账号已删除，但列表刷新失败；请点击重试刷新。')
    }
  }

  const confirmDeleteAccount = (account: VendorAccount) => {
    confirmation.current = Modal.confirm({
      title: `删除账号“${account.employeeNo}”`,
      content: `确认永久删除账号“${account.employeeNo}”？该账号创建的未发送记录也会一并删除。已有文件、上传或其他业务记录时，请停用账号。`,
      okText: '确认删除',
      cancelText: '取消',
      okButtonProps: { status: 'danger' },
      onOk: () => removeAccount(account),
    })
  }

  const accountColumns = [
    { key: 'employeeNo', title: '账号', dataIndex: 'employeeNo', width: 160, ellipsis: true, render: (value: string) => <EllipsisText value={value} /> },
    {
      key: 'status', title: '状态', width: 72, align: 'center' as const,
      render: (_: unknown, row: VendorAccount) => (
        <Space size={4}>
          <StatusTag status={row.status} size="small" />
        </Space>
      ),
    },
    { key: 'lastLoginAt', title: '最近登录', width: 168, render: (_: unknown, row: VendorAccount) => (row.lastLoginAt ? fmtTime(row.lastLoginAt) : '-') },
    {
      key: 'actions', title: '操作', width: 204, fixed: 'right' as const,
      render: (_: unknown, row: VendorAccount) => (can.manageAccounts ? (
        <Space size={4}>
          <Button size="mini" type="text" onClick={() => reset(row)}>重置密码</Button>
          {row.status === 'ACTIVE' ? (
            <DangerPopconfirm title="停用后该账号将立即退出登录，确定停用？" onOk={() => void toggleAccountStatus(row)}>
              <Button size="mini" type="text" status="danger">停用</Button>
            </DangerPopconfirm>
          ) : (
            <Button size="mini" type="text" status="success" onClick={() => void toggleAccountStatus(row)}>启用</Button>
          )}
          {can.deleteAccounts && (
            <Button
              size="mini"
              type="text"
              status="danger"
              loading={deletingIds.has(row.id)}
              disabled={deletingIds.has(row.id)}
              onClick={() => confirmDeleteAccount(row)}
            >
              删除
            </Button>
          )}
        </Space>
      ) : <span>只读</span>),
    },
  ]

  return (
    <section className="oem-subpanel oem-accounts-panel" aria-label="账号管理">
      <div className="oem-subpanel-head">
        <div className="oem-subpanel-title">
          <h3>账号管理</h3>
          <Typography.Text type="secondary">共 {rows.length} 个账号</Typography.Text>
        </div>
        <Button type="primary" size="small" icon={<IconPlus />}
          onClick={() => { form.resetFields(); setAdding(true) }}
          disabled={!can.manageAccounts || company.status !== 'ACTIVE'}>新增账号</Button>
      </div>
      <div className="oem-subpanel-body">
        {loadError && (
          <Alert type="error" style={{ marginBottom: 12 }} content={loadError}
            action={<Button size="small" loading={accountsLoading} onClick={() => void load()}>重试</Button>} />
        )}
        {!accountsLoading && !loadError && rows.length > 0 && rows.every((item) => item.status !== 'ACTIVE') && (
          <Alert type="warning" style={{ marginBottom: 12 }} content="该厂商所有账号均已停用，暂时无法登录门户接收或回传文件。" />
        )}
        {actionError && <Typography.Text className="oem-delete-error" type="error" role="alert">{actionError}</Typography.Text>}
        <ResizableTable tableId="oem-company-accounts"
          className="account-table oem-account-table" size="small" loading={accountsLoading}
          rowKey="id"
          data={rows}
          pagination={false}
          scroll={{ x: 604 }}
          noDataElement={<Empty description="暂无账号" />}
          columns={accountColumns}
        />
        <Modal
          className="form-dialog"
          title="新增账号"
          visible={adding}
          confirmLoading={saving}
          closable={!saving}
          maskClosable={!saving}
          escToExit={!saving}
          cancelButtonProps={{ disabled: saving }}
          onCancel={() => { if (!saving) setAdding(false) }}
          onOk={save}
          okText="创建账号"
          unmountOnExit
        >
          <Form onSubmit={() => void save()} form={form} layout="vertical">
            <Form.Item field="employeeNo" label="账号" extra="不区分大小写，所有 ODM/OEMA 厂商之间不可重名" rules={[{ required: true }, { match: ACCOUNT_NAME_PATTERN, message: ACCOUNT_NAME_MESSAGE }]}>
              <Input autoFocus placeholder={ACCOUNT_NAME_PLACEHOLDER} />
            </Form.Item>
            <Form.Item field="password" label="初始密码" extra="首次登录需改密" rules={[{ required: true, message: '请输入初始密码' }, passwordRule]}><PasswordInput placeholder="5-20 位" /></Form.Item>
          <EnterSubmit /></Form>
        </Modal>
      </div>
    </section>
  )
}

export default function CompaniesPage() {
  const { api } = useOem()
  const can = useCan()
  const [rows, setRows] = useState<Company[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(DEFAULT_PAGE_SIZE)
  const [status, setStatus] = useState<string | undefined>()
  const [keyword, setKeyword] = useState('')
  const [selectedCompanyId, setSelectedCompanyId] = useState<number | null>(null)
  const [editing, setEditing] = useState<Company | 'new' | null>(null)
  const [importOpen, setImportOpen] = useState(false)
  const [exporting, setExporting] = useState(false)
  const [saving, setSaving] = useState(false)
  const [recipientsOpen, setRecipientsOpen] = useState(false)
  const [accountImportOpen, setAccountImportOpen] = useState(false)
  const [contactImportOpen, setContactImportOpen] = useState(false)
  const [accountExporting, setAccountExporting] = useState(false)
  const [contactExporting, setContactExporting] = useState(false)
  const [accountsRefreshVersion, setAccountsRefreshVersion] = useState(0)
  const [contactsRefreshVersion, setContactsRefreshVersion] = useState(0)
  const [actionError, setActionError] = useState('')
  const [listLoading, setListLoading] = useState(can.viewCompanies)
  const [listError, setListError] = useState('')
  const deletingIdsRef = useRef(new Set<number>())
  const [deletingIds, setDeletingIds] = useState<ReadonlySet<number>>(new Set())
  const saveInFlight = useRef(false)
  const beginRequest = useLatestRequest()
  const [form] = Form.useForm()
  if (accountImportOpen && !can.manageAccounts) setAccountImportOpen(false)
  if (contactImportOpen && !can.manageCompanies) setContactImportOpen(false)
  if (recipientsOpen && !can.manageCompanies) setRecipientsOpen(false)

  const selectedCompany = rows.find((item) => item.id === selectedCompanyId) ?? rows[0] ?? null

  const load = useCallback(async () => {
    if (!can.viewCompanies) return
    const isLatest = beginRequest()
    setListLoading(true)
    try {
      const result = await api.companies({ page, pageSize, keyword: keyword || undefined, status })
      if (!isLatest()) return
      setRows(result.list)
      setTotal(result.total)
      setListError('')
    } catch (error) {
      if (isLatest()) setListError(oemRequestError(error, '厂商列表加载失败，请重试'))
      throw error
    } finally {
      if (isLatest()) setListLoading(false)
    }
  }, [api, page, pageSize, keyword, status, can.viewCompanies, beginRequest])
  useLoadEffect(load)

  const fetchAllCompanies = useCallback(async (search?: string, filterStatus?: string) => {
    const pageSizeForExport = 100
    const first = await api.companies({ page: 1, pageSize: pageSizeForExport, keyword: search || undefined, status: filterStatus })
    const all = [...first.list]
    const pages = Math.ceil(first.total / pageSizeForExport)
    for (let current = 2; current <= pages; current++) {
      const result = await api.companies({ page: current, pageSize: pageSizeForExport, keyword: search || undefined, status: filterStatus })
      all.push(...result.list)
    }
    return all
  }, [api])

  const fetchAllCompanyAccounts = async (companies: readonly Company[]) => {
    const output: Array<{ company: Company; account: VendorAccount }> = []
    for (const company of companies) {
      const accounts = await api.accounts(company.id)
      output.push(...accounts.map(account => ({ company, account })))
    }
    return output
  }

  const fetchAllCompanyContacts = async (companies: readonly Company[]) => {
    const output: Array<{ company: Company; contact: CompanyContact }> = []
    for (const company of companies) {
      const contacts = await api.contacts(company.id)
      output.push(...contacts.map(contact => ({ company, contact })))
    }
    return output
  }

  const prepareAccountImport = async (sourceRows: ExcelImportRow[]): Promise<PreparedImport<OemAccountImportPayload>> => {
    const companies = await fetchAllCompanies()
    const companiesByName = new Map(companies.map(company => [company.name.trim().toLowerCase(), company]))
    const existing = new Set((await fetchAllCompanyAccounts(companies)).map(({ account }) => account.employeeNo.trim().toUpperCase()))
    const sourceCounts = new Map<string, number>()
    for (const row of sourceRows) {
      const employeeNo = row.values['账号']?.trim().toUpperCase() ?? ''
      if (employeeNo) sourceCounts.set(employeeNo, (sourceCounts.get(employeeNo) ?? 0) + 1)
    }
    const payload: OemAccountImportPayload[] = []
    const preview: ImportPreviewRow[] = sourceRows.map(row => {
      const companyName = row.values['厂商名称']?.trim() ?? ''
      const company = companiesByName.get(companyName.toLowerCase())
      const employeeNo = row.values['账号']?.trim() ?? ''
      const initialPassword = row.values['初始密码'] ?? ''
      const key = employeeNo || `第 ${row.rowNumber} 行`
      const detail = companyName
      if (!company) return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: 'ODM/OEMA 厂商不存在，请先建立厂商资料' }
      if (company.status !== 'ACTIVE') return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: '厂商已停用，不能新增账号' }
      if (employeeNo && (sourceCounts.get(employeeNo.toUpperCase()) ?? 0) > 1)
        return { rowNumber: row.rowNumber, key, detail, status: 'SKIPPED', message: '文件内账号重复，本重复组未导入' }
      if (employeeNo && existing.has(employeeNo.toUpperCase()))
        return { rowNumber: row.rowNumber, key, detail, status: 'SKIPPED', message: '登录账号已存在，不会覆盖现有资料' }
      if (!employeeNo || !isValidAccountName(employeeNo))
        return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: ACCOUNT_NAME_MESSAGE }
      if (row.numericCells.has('账号'))
        return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: '账号列请设置为文本格式，以保留前导零' }
      const passwordError = validateInitialPassword(initialPassword)
      if (passwordError)
        return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: passwordError }
      payload.push({ rowNumber: row.rowNumber, companyId: company.id, companyName, employeeNo, initialPassword })
      return { rowNumber: row.rowNumber, key, detail, status: 'READY' }
    })
    return { payload, preview }
  }

  const importAccounts = async (rows: OemAccountImportPayload[]): Promise<ImportBatchOutcome> => {
    const grouped = new Map<number, OemAccountImportPayload[]>()
    rows.forEach(row => grouped.set(row.companyId, [...(grouped.get(row.companyId) ?? []), row]))
    const outcomes: ImportBatchOutcome[] = []
    for (const [companyId, group] of grouped) {
      const result = await api.importAccounts(companyId, group.map(({ companyId: _companyId, ...row }) => row))
      outcomes.push(result as ImportBatchOutcome)
    }
    return {
      created: outcomes.reduce((sum, result) => sum + result.created, 0),
      skipped: outcomes.reduce((sum, result) => sum + result.skipped, 0),
      failed: outcomes.reduce((sum, result) => sum + result.failed, 0),
      rows: outcomes.flatMap(result => result.rows),
    }
  }

  const prepareContactImport = async (sourceRows: ExcelImportRow[]): Promise<PreparedImport<OemContactImportPayload>> => {
    const companies = await fetchAllCompanies()
    const companiesByName = new Map(companies.map(company => [company.name.trim().toLowerCase(), company]))
    const existing = new Set((await fetchAllCompanyContacts(companies)).map(({ company, contact }) =>
      `${company.id}:${contact.name.trim().toLowerCase()}`))
    const counts = new Map<string, number>()
    for (const row of sourceRows) {
      const company = companiesByName.get((row.values['厂商名称']?.trim() ?? '').toLowerCase())
      const name = row.values['联络人姓名']?.trim().toLowerCase() ?? ''
      if (company && name) {
        const key = `${company.id}:${name}`
        counts.set(key, (counts.get(key) ?? 0) + 1)
      }
    }
    const payload: OemContactImportPayload[] = []
    const preview: ImportPreviewRow[] = sourceRows.map(row => {
      const companyName = row.values['厂商名称']?.trim() ?? ''
      const company = companiesByName.get(companyName.toLowerCase())
      const name = row.values['联络人姓名']?.trim() ?? ''
      const email = row.values['邮箱']?.trim() ?? ''
      const sortText = row.values['排序']?.trim() ?? ''
      const key = name || `第 ${row.rowNumber} 行`
      const detail = [companyName, email].filter(Boolean).join(' · ')
      if (!company) return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: 'ODM/OEMA 厂商不存在，请先建立厂商资料' }
      if (company.status !== 'ACTIVE') return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: '厂商已停用，不能新增联络人' }
      const contactKey = `${company.id}:${name.toLowerCase()}`
      if (name && (counts.get(contactKey) ?? 0) > 1)
        return { rowNumber: row.rowNumber, key, detail, status: 'SKIPPED', message: '文件内该厂商的联络人姓名重复，本重复组未导入' }
      if (name && existing.has(contactKey))
        return { rowNumber: row.rowNumber, key, detail, status: 'SKIPPED', message: '该厂商已有同名联络人，未覆盖现有资料' }
      if (!name || Array.from(name).length > 64)
        return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: '联络人姓名不能为空且不能超过 64 个字符' }
      if (!email) return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: '请输入邮箱' }
      if (email && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email))
        return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: '邮箱格式不正确' }
      const sortNo = sortText === '' ? 0 : /^\d+$/.test(sortText) ? Number(sortText) : NaN
      if (!Number.isSafeInteger(sortNo) || sortNo < 0 || sortNo > 9999)
        return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: '排序需为 0–9999 的整数' }
      payload.push({ rowNumber: row.rowNumber, companyId: company.id, companyName, name, email, sortNo })
      return { rowNumber: row.rowNumber, key, detail, status: 'READY' }
    })
    return { payload, preview }
  }

  const importContacts = async (rows: OemContactImportPayload[]): Promise<ImportBatchOutcome> => {
    const grouped = new Map<number, OemContactImportPayload[]>()
    rows.forEach(row => grouped.set(row.companyId, [...(grouped.get(row.companyId) ?? []), row]))
    const outcomes: ImportBatchOutcome[] = []
    for (const [companyId, group] of grouped) {
      const result = await api.importContacts(companyId, group.map(({ companyId: _companyId, ...row }) => row))
      outcomes.push(result as ImportBatchOutcome)
    }
    return {
      created: outcomes.reduce((sum, result) => sum + result.created, 0),
      skipped: outcomes.reduce((sum, result) => sum + result.skipped, 0),
      failed: outcomes.reduce((sum, result) => sum + result.failed, 0),
      rows: outcomes.flatMap(result => result.rows),
    }
  }

  const exportAllAccounts = async () => {
    setAccountExporting(true)
    try {
      const all = await fetchAllCompanies(keyword, status)
      const data: string[][] = []
      for (const company of all) {
        const accounts = await api.accounts(company.id)
        data.push(...accounts.map(account => [
          company.name, account.employeeNo, account.status, account.lastLoginAt ?? '',
        ]))
      }
      await downloadExcelWorkbook('ODM-OEMA账号.xlsx', [
        { name: '数据', headers: [...OEM_ACCOUNT_EXPORT_HEADERS], rows: data },
      ])
      Message.success(`已导出 ${data.length} 个账号；导出文件不含密码`)
    } catch (error) {
      Message.error(oemRequestError(error, 'ODM/OEMA账号导出失败'))
    } finally {
      setAccountExporting(false)
    }
  }

  const exportAllContacts = async () => {
    setContactExporting(true)
    try {
      const all = await fetchAllCompanies(keyword, status)
      const data: string[][] = []
      for (const company of all) {
        const contacts = await api.contacts(company.id)
        data.push(...contacts.map(contact => [
          company.name, contact.name, contact.email ?? '', String(contact.sortNo), contact.status,
        ]))
      }
      await downloadExcelWorkbook('ODM-OEMA联络人.xlsx', [
        { name: '数据', headers: [...OEM_CONTACT_IMPORT_HEADERS, ...OEM_CONTACT_EXPORT_HEADERS], rows: data },
        ...OEM_CONTACT_WORKBOOK.slice(1),
      ])
      Message.success(`已导出 ${data.length} 位联络人`)
    } catch (error) {
      Message.error(oemRequestError(error, 'ODM/OEMA联络人导出失败'))
    } finally {
      setContactExporting(false)
    }
  }

  const prepareCompanyImport = async (sourceRows: ExcelImportRow[]): Promise<PreparedImport<OemCompanyImportPayload>> => {
    const existing = new Set((await fetchAllCompanies()).map(company => company.name.trim().toLowerCase()))
    const sourceCounts = new Map<string, number>()
    for (const row of sourceRows) {
      const name = row.values['厂商名称']?.trim().toLowerCase() ?? ''
      if (name) sourceCounts.set(name, (sourceCounts.get(name) ?? 0) + 1)
    }
    const payload: OemCompanyImportPayload[] = []
    const preview: ImportPreviewRow[] = sourceRows.map(row => {
      const name = row.values['厂商名称']?.trim() ?? ''
      const remark = row.values['备注']?.trim() ?? ''
      const key = name || `第 ${row.rowNumber} 行`
      const detail = remark
      const normalizedName = name.toLowerCase()
      if (normalizedName && (sourceCounts.get(normalizedName) ?? 0) > 1)
        return { rowNumber: row.rowNumber, key, detail, status: 'SKIPPED', message: '文件内厂商名称重复，本重复组未导入' }
      if (name && existing.has(normalizedName))
        return { rowNumber: row.rowNumber, key, detail, status: 'SKIPPED', message: '厂商名称已存在，不会覆盖现有资料' }
      if (!name || Array.from(name).length > 128)
        return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: '厂商名称不能为空且不能超过 128 个字符' }
      if (Array.from(remark).length > 512)
        return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: '备注超过长度限制' }
      payload.push({ rowNumber: row.rowNumber, name, remark })
      return { rowNumber: row.rowNumber, key, detail, status: 'READY' }
    })
    return { payload, preview }
  }

  const importCompanies = async (rows: OemCompanyImportPayload[]): Promise<ImportBatchOutcome> =>
    api.importCompanies(rows) as Promise<ImportBatchOutcome>

  const exportCompanies = async () => {
    if (exporting) return
    setExporting(true)
    try {
      const all = await fetchAllCompanies(keyword, status)
      const rows = all.map(company => [
        company.name,
        company.remark ?? '', company.status, String(company.accountCount ?? 0), String(company.activeAccountCount ?? 0), String(company.contactCount ?? 0),
      ])
      await downloadExcelWorkbook('ODM-OEMA厂商.xlsx', [
        { name: '数据', headers: [...OEM_COMPANY_IMPORT_HEADERS, '状态', '账号数', '启用账号数', '联络人'], rows },
        ...OEM_COMPANY_WORKBOOK.slice(1),
      ])
      Message.success(`已导出 ${rows.length} 家厂商`)
    } catch (error) {
      Message.error(oemRequestError(error, '厂商导出失败，请稍后重试'))
    } finally {
      setExporting(false)
    }
  }

  const save = async () => {
    if (saveInFlight.current) return
    saveInFlight.current = true
    try {
      const values = await form.validate().catch(() => null)
      if (!values) return
      setSaving(true)
      if (editing === 'new') {
        const created = await api.createCompany(values)
        if (created?.id) setSelectedCompanyId(created.id)
      } else if (editing) {
        await api.updateCompany(editing.id, values)
      }
      Message.success('已保存')
      setEditing(null)
      await load()
    } finally {
      saveInFlight.current = false
      setSaving(false)
    }
  }

  // The server refuses disabling a vendor with in-flight transfers; keep its reason visible.
  const toggleCompanyStatus = async (company: Company) => {
    const disabling = company.status === 'ACTIVE'
    setActionError('')
    try {
      await api.setCompanyStatus(company.id, disabling ? 'DISABLED' : 'ACTIVE')
    } catch (error) {
      setActionError(`${disabling ? '停用' : '启用'}失败：${oemRequestError(error, '操作失败，请稍后重试')}`)
      return
    }
    await load()
  }

  const removeCompany = async (company: Company) => {
    if (deletingIdsRef.current.has(company.id)) return
    const pending = new Set(deletingIdsRef.current).add(company.id)
    deletingIdsRef.current = pending
    setDeletingIds(pending)
    setActionError('')
    try {
      await api.deleteCompany(company.id)
    } catch (error) {
      setActionError(`删除失败：${oemRequestError(error, '无法删除该厂商，请稍后重试；如有业务历史请改为停用。')}`)
      return
    } finally {
      const remaining = new Set(deletingIdsRef.current)
      remaining.delete(company.id)
      deletingIdsRef.current = remaining
      setDeletingIds(remaining)
    }

    setRows((current) => current.filter((item) => item.id !== company.id))
    setTotal((current) => Math.max(0, current - 1))
    if (selectedCompanyId === company.id) setSelectedCompanyId(null)
    Message.success('厂商已删除')
    if (rows.length === 1 && page > 1) {
      setPage(page - 1)
      return
    }
    try {
      await load()
    } catch {
      setActionError('厂商已删除，但列表刷新失败；请重新搜索或切换分页重试。')
    }
  }

  const confirmToggleCompanyStatus = (company: Company) => {
    const disabling = company.status === 'ACTIVE'
    Modal.confirm({
      title: disabling ? `停用厂商“${company.name}”` : `启用厂商“${company.name}”`,
      content: disabling ? '停用后该厂商所有账号将立即退出登录，确定？' : '确定启用该厂商？',
      okText: '确定',
      cancelText: '取消',
      okButtonProps: disabling ? { status: 'danger' } : undefined,
      onOk: () => toggleCompanyStatus(company),
    })
  }

  const confirmDeleteCompany = (company: Company) => {
    Modal.confirm({
      title: `删除厂商“${company.name}”`,
      content: `确认永久删除“${company.name}”？请先删除关联账号。该厂商剩余的未发送记录也会一并删除。已有业务记录时，请停用厂商。`,
      okText: '确认删除',
      cancelText: '取消',
      okButtonProps: { status: 'danger' },
      onOk: () => removeCompany(company),
    })
  }

  const handleAccountCreated = useCallback(async (company: Company) => {
    setRows((current) => current.map((row) => row.id === company.id ? {
      ...row,
      accountCount: (row.accountCount ?? 0) + 1,
      activeAccountCount: (row.activeAccountCount ?? 0) + 1,
    } : row))
    await load()
  }, [load])

  const handleAccountStatusToggled = useCallback((company: Company, account: VendorAccount, nextStatus: 'ACTIVE' | 'DISABLED') => {
    if (account.status === nextStatus) return
    const delta = nextStatus === 'ACTIVE' ? 1 : -1
    setRows((current) => current.map((row) => row.id === company.id ? {
      ...row,
      activeAccountCount: Math.max(0, (row.activeAccountCount ?? 0) + delta),
    } : row))
  }, [])

  const handleAccountDeleted = useCallback(async (company: Company, account: VendorAccount) => {
    setRows((current) => current.map((row) => row.id === company.id ? {
      ...row,
      accountCount: Math.max(0, (row.accountCount ?? 0) - 1),
      activeAccountCount: Math.max(0, (row.activeAccountCount ?? 0) - (account.status === 'ACTIVE' ? 1 : 0)),
    } : row))
    await load()
  }, [load])

  const vendorMetaSummary = (row: Company) => [
    can.viewAccounts ? `账号 ${row.accountCount ?? 0}` : null,
    can.manageCompanies ? `联络人 ${row.contactCount ?? 0}` : null,
  ].filter(Boolean).join(' · ')

  return (
    <>
      <div className="page-toolbar responsive-toolbar">
        <Space>
          <Input.Search allowClear placeholder="厂商名称" style={{ width: 260 }} onSearch={(value) => { setKeyword(value); setPage(1) }} onClear={() => { setKeyword(''); setPage(1) }} />
          <Select allowClear placeholder="全部状态" style={{ width: 110 }} value={status} onChange={(value) => { setStatus(value); setPage(1) }}>
            <Select.Option value="ACTIVE">启用</Select.Option><Select.Option value="DISABLED">停用</Select.Option>
          </Select>
        </Space>
        <Space wrap>
          <ToolbarMenuButton label="导入" items={[
            can.manageCompanies && { key: 'companies', label: '导入厂商', onClick: () => setImportOpen(true) },
            can.manageAccounts && { key: 'accounts', label: '导入账号', onClick: () => setAccountImportOpen(true) },
            can.manageCompanies && { key: 'contacts', label: '导入联络人', onClick: () => setContactImportOpen(true) },
          ]} />
          <ToolbarMenuButton label="导出" items={[
            (can.manageCompanies || can.manageAccounts) && { key: 'companies', label: '导出厂商', loading: exporting, onClick: () => void exportCompanies() },
            can.manageAccounts && { key: 'accounts', label: '导出账号', loading: accountExporting, onClick: () => void exportAllAccounts() },
            can.manageCompanies && { key: 'contacts', label: '导出联络人', loading: contactExporting, onClick: () => void exportAllContacts() },
          ]} />
          {can.manageCompanies && <Button onClick={() => setRecipientsOpen(true)}>可通知人员</Button>}
          {can.manageCompanies && (
            <Button type="primary" icon={<IconPlus />} onClick={() => { form.resetFields(); setEditing('new') }}>新增厂商</Button>
          )}
        </Space>
      </div>
      {actionError && <Typography.Text className="oem-delete-error" type="error" role="alert">{actionError}</Typography.Text>}
      {listError && (
        <Alert type="error" style={{ marginBottom: 12 }} content={`厂商列表加载失败：${listError}`}
          action={<Button size="small" loading={listLoading} onClick={() => void load().catch(() => undefined)}>重试</Button>} />
      )}

      <div className="oem-directory-workspace">
        <aside className="oem-vendor-panel" aria-label="厂商列表">
          <div className="oem-vendor-panel-head">
            <span className="oem-vendor-panel-title">厂商列表</span>
            <span className="oem-vendor-panel-count">共 {total} 家</span>
          </div>
          <div className="oem-vendor-list oem-directory-table">
            {listLoading && rows.length === 0 ? (
              <div className="oem-vendor-state"><Spin /></div>
            ) : rows.length === 0 ? (
              <div className="oem-vendor-state"><Empty description="暂无厂商" /></div>
            ) : (
              <Spin loading={listLoading} className="oem-vendor-spin">
                <ul className="oem-vendor-items">
                  {rows.map((row) => {
                    const selected = selectedCompany?.id === row.id
                    const counts = vendorMetaSummary(row)
                    const createdLabel = fmtTime(row.createdAt)
                    return (
                      <li key={row.id}>
                        <button
                          type="button"
                          className={`oem-vendor-item${selected ? ' is-selected' : ''}`}
                          aria-current={selected ? 'true' : undefined}
                          onClick={() => setSelectedCompanyId(row.id)}
                        >
                          <span className="oem-vendor-item-top">
                            <span className="oem-vendor-item-name" title={row.name}>{row.name}</span>
                            <StatusTag status={row.status} size="small" />
                          </span>
                          <span className="oem-vendor-item-meta">
                            <span>{counts || createdLabel}</span>
                            {counts && <span className="oem-vendor-item-time" title={createdLabel}>{createdLabel.slice(0, 10)}</span>}
                          </span>
                        </button>
                      </li>
                    )
                  })}
                </ul>
              </Spin>
            )}
          </div>
          {total > pageSize && (
            <div className="oem-vendor-footer">
              <Pagination
                size="small"
                current={page}
                total={total}
                pageSize={pageSize}
                bufferSize={1}
                showTotal={false}
                sizeCanChange={false}
                onChange={(nextPage, nextSize) => { setPage(nextPage); setPageSize(nextSize) }}
              />
            </div>
          )}
        </aside>

        <div className="oem-detail-panel">
          {!selectedCompany ? (
            <div className="oem-detail-empty">
              <Empty description={listLoading ? '正在加载厂商…' : '暂无厂商'} />
            </div>
          ) : (
            <>
              <section className="oem-company-summary" aria-label="厂商信息">
                <div className="oem-company-summary-main">
                  <div className="oem-company-summary-head">
                    <h2 className="oem-company-summary-name">{selectedCompany.name}</h2>
                    <StatusTag status={selectedCompany.status} />
                  </div>
                  <div className="oem-company-summary-meta">
                    <span>创建时间：{fmtTime(selectedCompany.createdAt)}</span>
                    {selectedCompany.remark && <span className="oem-company-summary-remark">备注：{selectedCompany.remark}</span>}
                  </div>
                </div>
                {can.manageCompanies && (
                  <Space size={8} wrap className="oem-company-summary-actions">
                    <Button size="small" onClick={() => { form.setFieldsValue(selectedCompany); setEditing(selectedCompany) }}>编辑</Button>
                    <Button
                      size="small"
                      status={selectedCompany.status === 'ACTIVE' ? 'danger' : 'success'}
                      onClick={() => confirmToggleCompanyStatus(selectedCompany)}
                    >
                      {selectedCompany.status === 'ACTIVE' ? '停用' : '启用'}
                    </Button>
                    {can.deleteCompanies && (
                      <Button
                        size="small"
                        status="danger"
                        loading={deletingIds.has(selectedCompany.id)}
                        disabled={deletingIds.has(selectedCompany.id)}
                        onClick={() => confirmDeleteCompany(selectedCompany)}
                      >
                        删除
                      </Button>
                    )}
                  </Space>
                )}
              </section>

              {can.viewAccounts && (
                <AccountsPanel
                  key={`${selectedCompany.id}-${can.manageAccounts}-${can.deleteAccounts}`}
                  company={selectedCompany}
                  refreshVersion={accountsRefreshVersion}
                  onAccountCreated={handleAccountCreated}
                  onAccountStatusToggled={handleAccountStatusToggled}
                  onAccountDeleted={handleAccountDeleted}
                />
              )}

              {can.manageCompanies && (
                <ContactsPanel
                  key={selectedCompany.id}
                  company={selectedCompany}
                  refreshVersion={contactsRefreshVersion}
                  onChanged={() => void load().catch(() => undefined)}
                />
              )}
            </>
          )}
        </div>
      </div>

      <ExcelImportDialog<OemCompanyImportPayload>
        visible={importOpen}
        title="导入 ODM/OEMA 厂商资料"
        templateName="ODM-OEMA厂商导入模板.xlsx"
        requiredHeaders={OEM_COMPANY_IMPORT_HEADERS}
        optionalHeaders={['状态', '账号数', '启用账号数', '联络人']}
        templateSheets={OEM_COMPANY_WORKBOOK}
        maxRows={500}
        prepare={prepareCompanyImport}
        commit={importCompanies}
        onImported={() => void load()}
        onClose={() => setImportOpen(false)}
      />
      {can.manageAccounts && <ExcelImportDialog<OemAccountImportPayload>
        visible={accountImportOpen}
        title="导入 ODM/OEMA 厂商账号"
        templateName="ODM-OEMA账号导入模板.xlsx"
        requiredHeaders={OEM_ACCOUNT_IMPORT_HEADERS}
        optionalHeaders={OEM_ACCOUNT_EXPORT_HEADERS}
        templateSheets={OEM_ACCOUNT_WORKBOOK}
        maxRows={100}
        prepare={prepareAccountImport}
        commit={importAccounts}
        onImported={() => { setAccountsRefreshVersion((version) => version + 1); void load().catch(() => undefined) }}
        onClose={() => setAccountImportOpen(false)}
      />}
      <ExcelImportDialog<OemContactImportPayload>
        visible={contactImportOpen}
        title="导入 ODM/OEMA 厂商联络人"
        templateName="ODM-OEMA联络人导入模板.xlsx"
        requiredHeaders={OEM_CONTACT_IMPORT_HEADERS}
        optionalHeaders={OEM_CONTACT_EXPORT_HEADERS}
        templateSheets={OEM_CONTACT_WORKBOOK}
        maxRows={100}
        prepare={prepareContactImport}
        commit={importContacts}
        onImported={() => { setContactsRefreshVersion((version) => version + 1); void load().catch(() => undefined) }}
        onClose={() => setContactImportOpen(false)}
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
        <Form onSubmit={() => void save()} form={form} layout="vertical">
          <Form.Item field="name" label="厂商名称" rules={[{ required: true, message: '请输入厂商名称' }, { maxLength: 128 }]}><Input autoFocus /></Form.Item>
          <Form.Item field="remark" label="备注"><Input.TextArea maxLength={512} /></Form.Item>
        <EnterSubmit /></Form>
      </Modal>
      <InternalRecipientsDrawer open={recipientsOpen} onClose={() => setRecipientsOpen(false)} />
    </>
  )
}
