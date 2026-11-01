import { useCallback, useRef, useState } from 'react'
import {
  Alert, Button, Card, Drawer, Form, Input, InputNumber, Message, Modal, Popconfirm, Space, Table, Tag, Tooltip, Typography,
} from '@arco-design/web-react'
import { IconPlus } from '@arco-design/web-react/icon'
import { fmtTime } from '../../../api/types'
import { oemRequestError } from '../../api/requestError'
import type { Company, CompanyContact, VendorAccount } from '../../api/types'
import { useCan, useOem } from '../../OemContext'
import { ColumnResizeProvider, ColumnVisibilityPicker } from '../../../components/columnResize'
import ExcelImportDialog, { type ImportBatchOutcome, type ImportPreviewRow, type PreparedImport } from '../../../components/ExcelImportDialog'
import { type ColumnLayoutSpec } from '../../../components/tableColumnLayout'
import { useAdjustableColumns } from '../../../components/useAdjustableColumns'
import { downloadExcelWorkbook, type ExcelImportRow, type ExcelWorkbookSheet } from '../../../utils/excelWorkbook'

const OEM_COMPANY_IMPORT_HEADERS = ['厂商名称', '主联系人', '联系电话', '联系邮箱', '备注'] as const
const OEM_ACCOUNT_IMPORT_HEADERS = ['厂商名称', '登录账号', '初始密码'] as const
const OEM_ACCOUNT_EXPORT_HEADERS = ['厂商名称', '登录账号', '状态', '需改密', '锁定', '最近登录'] as const
const OEM_ACCOUNT_WORKBOOK: ExcelWorkbookSheet[] = [
  { name: '数据', headers: [...OEM_ACCOUNT_IMPORT_HEADERS], rows: [] },
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
  contactName: string
  contactPhone: string
  contactEmail: string
  remark: string
}

const OEM_ACCOUNT_COLUMNS: readonly ColumnLayoutSpec[] = [
  { key: 'employeeNo', title: '登录账号', defaultWidth: 110, minWidth: 96, locked: true },
  { key: 'status', title: '状态', defaultWidth: 136, minWidth: 112 },
  { key: 'lastLoginAt', title: '最近登录', defaultWidth: 160, minWidth: 140 },
  { key: 'actions', title: '操作', defaultWidth: 214, minWidth: 180, maxWidth: 360, locked: true },
]

const OEM_COMPANY_COLUMNS: readonly ColumnLayoutSpec[] = [
  { key: 'name', title: '厂商名称', defaultWidth: 230, minWidth: 140, locked: true },
  { key: 'contactName', title: '联系人', defaultWidth: 120, minWidth: 88 },
  { key: 'contact', title: '联系方式', defaultWidth: 270, minWidth: 140 },
  { key: 'accounts', title: '账号', defaultWidth: 100, minWidth: 72 },
  { key: 'contacts', title: '其他联络人', defaultWidth: 112, minWidth: 88 },
  { key: 'status', title: '状态', defaultWidth: 90, minWidth: 72 },
  { key: 'actions', title: '操作', defaultWidth: 300, minWidth: 180, maxWidth: 420, locked: true },
]
import { PASSWORD_MAX_CHARS, PASSWORD_MIN_CHARS, passwordRule, validatePassword } from '../../../utils/password'
import { useLatestRequest, useLoadEffect } from '../../useLoadEffect'
import './CompaniesPage.css'
import EnterSubmit from '../../../components/EnterSubmit'

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

const OEM_CONTACT_IMPORT_HEADERS = ['厂商名称', '联络人姓名', '联系电话', '联系邮箱', '排序'] as const
const OEM_CONTACT_EXPORT_HEADERS = ['状态'] as const
const OEM_CONTACT_WORKBOOK: ExcelWorkbookSheet[] = [
  { name: '数据', headers: [...OEM_CONTACT_IMPORT_HEADERS], rows: [] },
  { name: '说明', headers: ['说明'], rows: [
    ['一行一个联络人；同一厂商有多个联络人时，重复填写厂商名称。'],
    ['厂商必须已存在，名称须与系统中的厂商名称一致。'],
    ['只新增其他联络人；主联系人在厂商资料中维护。电话和邮箱至少填写一项。'],
  ] },
]

interface OemContactImportPayload {
  rowNumber: number
  companyId: number
  companyName: string
  name: string
  phone: string
  email: string
  sortNo: number
}

interface ContactsDrawerProps {
  company: Company | null
  onClose: () => void
  onChanged: () => void
}

function ContactsDrawer({ company, onClose, onChanged }: ContactsDrawerProps) {
  const { api } = useOem()
  const can = useCan()
  const [rows, setRows] = useState<CompanyContact[]>([])
  const [loading, setLoading] = useState(false)
  const [failed, setFailed] = useState(false)
  const [adding, setAdding] = useState(false)
  const [saving, setSaving] = useState(false)
  const [form] = Form.useForm()
  const requestId = useRef(0)

  const load = useCallback(async () => {
    const current = ++requestId.current
    if (!company) { setRows([]); setFailed(false); setLoading(false); return }
    setLoading(true)
    try {
      const result = await api.contacts(company.id)
      if (requestId.current === current) { setRows(result); setFailed(false) }
    } catch {
      if (requestId.current === current) setFailed(true)
    } finally {
      if (requestId.current === current) setLoading(false)
    }
  }, [api, company])
  useLoadEffect(load)

  const createContact = async () => {
    if (!company || saving) return
    const values = await form.validate().catch(() => null)
    if (!values) return
    const phone = String(values.phone ?? '').trim()
    const email = String(values.email ?? '').trim()
    if (!phone && !email) { Message.warning('电话和邮箱至少填写一项'); return }
    setSaving(true)
    try {
      await api.createContact(company.id, { name: values.name.trim(), phone: phone || null, email: email || null, sortNo: values.sortNo ?? 0 })
      setAdding(false)
      form.resetFields()
      await load()
      onChanged()
      Message.success('联络人已添加')
    } catch {
      // The request layer displays errors; keep the form open for correction.
    } finally {
      setSaving(false)
    }
  }

  return (
    <Drawer width={820} title={`${company?.name ?? ''} · 联络人`} visible={!!company}
      onCancel={onClose} footer={null} unmountOnExit>
      {!can.manageCompanies ? <span>当前账号没有 OEM 厂商管理权限。</span> : <>
        {failed && <Alert type="error" style={{ marginBottom: 12 }} content="联络人列表加载失败，请重试。" action={<Button size="small" loading={loading} onClick={() => void load()}>重试</Button>} />}
        <div className="drawer-toolbar">
          <Typography.Text type="secondary">共 {rows.length} 位其他联络人</Typography.Text>
          <Space>
            <Button type="primary" size="small" icon={<IconPlus />} disabled={company?.status !== 'ACTIVE'} onClick={() => { form.resetFields(); setAdding(true) }}>新增联络人</Button>
          </Space>
        </div>
        <Table rowKey="id" size="small" loading={loading} data={rows} pagination={false} scroll={{ x: 680 }} columns={[
          { title: '姓名', dataIndex: 'name', width: 150 },
          { title: '联系电话', dataIndex: 'phone', width: 160, render: (value: string | null) => value || '-' },
          { title: '联系邮箱', dataIndex: 'email', width: 220, render: (value: string | null) => value || '-' },
          { title: '排序', dataIndex: 'sortNo', width: 80 },
        ]} />
        <Modal title="新增联络人" visible={adding} confirmLoading={saving} onCancel={() => { if (!saving) setAdding(false) }} onOk={() => void createContact()} unmountOnExit>
          <Form form={form} layout="vertical">
            <Form.Item field="name" label="姓名" rules={[{ required: true }, { maxLength: 64 }]}><Input autoFocus /></Form.Item>
            <Form.Item field="phone" label="联系电话"><Input maxLength={32} /></Form.Item>
            <Form.Item field="email" label="联系邮箱" rules={[{ type: 'email' }]}><Input /></Form.Item>
            <Form.Item field="sortNo" label="排序"><InputNumber min={0} max={9999} precision={0} placeholder="0" /></Form.Item>
          </Form>
        </Modal>
      </>}
    </Drawer>
  )
}

function AccountsDrawer({ company, onClose, onAccountDeleted }: AccountsDrawerProps) {
  const { api, userId } = useOem()
  const can = useCan()
  const [rows, setRows] = useState<VendorAccount[]>([])
  const [loadError, setLoadError] = useState('')
  const [accountsLoading, setAccountsLoading] = useState(false)
  const [adding, setAdding] = useState(false)
  const [saving, setSaving] = useState(false)
  const [actionError, setActionError] = useState('')
  const accountsRequestId = useRef(0)
  const accountHostRef = useRef<HTMLDivElement>(null)
  const deletingIdsRef = useRef(new Set<number>())
  const [deletingIds, setDeletingIds] = useState<ReadonlySet<number>>(new Set())
  const saveInFlight = useRef(false)
  const [form] = Form.useForm()

  const load = useCallback(async () => {
    const requestId = ++accountsRequestId.current
    if (!company) {
      setRows([])
      setLoadError('')
      setAccountsLoading(false)
      return
    }
    setAccountsLoading(true)
    try {
      const nextRows = await api.accounts(company.id)
      if (accountsRequestId.current !== requestId) return
      setRows(nextRows)
      setLoadError('')
    } catch (error) {
      if (accountsRequestId.current === requestId) setLoadError(oemRequestError(error, '账号列表加载失败，请重试'))
    } finally {
      if (accountsRequestId.current === requestId) setAccountsLoading(false)
    }
  }, [api, company])
  useLoadEffect(load)

  const save = async () => {
    if (saveInFlight.current) return
    saveInFlight.current = true
    try {
      const values = await form.validate().catch(() => null)
      if (!values) return
      setSaving(true)
      if (!company) return
      await api.createAccount(company.id, values)
      Message.success('账号已创建（首次登录需改密）')
      setAdding(false)
      await load()
    } finally {
      saveInFlight.current = false
      setSaving(false)
    }
  }

  const reset = (account: VendorAccount) => {
    let password = ''
    Modal.confirm({
      title: `重置账号 ${account.employeeNo} 的密码`,
      content: <Input.Password autoFocus autoComplete="new-password" placeholder={`新的初始密码（${PASSWORD_MIN_CHARS}-${PASSWORD_MAX_CHARS} 个字符）`}
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
    setActionError('')
    try {
      await api.setAccountStatus(account.id, disabling ? 'DISABLED' : 'ACTIVE')
    } catch (error) {
      setActionError(`${disabling ? '停用' : '启用'}失败：${oemRequestError(error, '操作失败，请稍后重试')}`)
      return
    }
    await load()
  }

  const removeAccount = async (account: VendorAccount) => {
    if (!company || deletingIdsRef.current.has(account.id)) return
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
      setActionError('账号已删除，但列表刷新失败；请重新打开账号抽屉重试。')
    }
  }

  const confirmDeleteAccount = (account: VendorAccount) => {
    Modal.confirm({
      title: `删除账号“${account.employeeNo}”`,
      content: `确认永久删除账号“${account.employeeNo}”？删除后不可恢复；如存在业务历史，系统会拒绝删除并说明原因，此时请停用账号。`,
      okText: '确认删除',
      cancelText: '取消',
      okButtonProps: { status: 'danger' },
      onOk: () => removeAccount(account),
    })
  }

  const accountColumns = [
    { key: 'employeeNo', title: '登录账号', dataIndex: 'employeeNo', ellipsis: true, render: (value: string) => <EllipsisText value={value} /> },
    {
      key: 'status', title: '状态',
      render: (_: unknown, row: VendorAccount) => (
        <Space size={4}>
          <Tag color={row.status === 'ACTIVE' ? 'green' : 'gray'} size="small">{row.status === 'ACTIVE' ? '启用' : '停用'}</Tag>
          {row.mustChangePassword && <Tag size="small">待改密</Tag>}
          {row.locked && <Tag color="red" size="small">已锁定</Tag>}
        </Space>
      ),
    },
    { key: 'lastLoginAt', title: '最近登录', render: (_: unknown, row: VendorAccount) => (row.lastLoginAt ? fmtTime(row.lastLoginAt) : '-') },
    {
      key: 'actions', title: '操作', fixed: 'right' as const,
      render: (_: unknown, row: VendorAccount) => (
        <Space size={4}>
          <Button size="mini" type="text" onClick={() => reset(row)}>重置密码</Button>
          <Button size="mini" type="text" status={row.status === 'ACTIVE' ? 'danger' : 'success'}
            onClick={() => void toggleAccountStatus(row)}>
            {row.status === 'ACTIVE' ? '停用' : '启用'}
          </Button>
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
      ),
    },
  ]
  const accountTable = useAdjustableColumns('oem-company-accounts', userId, OEM_ACCOUNT_COLUMNS, accountColumns, 'employeeNo', accountHostRef, can.manageAccounts)

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
      {loadError && (
        <Alert type="error" style={{ marginBottom: 12 }} content={loadError}
          action={<Button size="small" loading={accountsLoading} onClick={() => void load()}>重试</Button>} />
      )}
      {can.manageAccounts ? (
        <>
          {/* 与协作端供应商账号抽屉一致：左侧数量，右侧操作。 */}
          <div className="drawer-toolbar">
            <Typography.Text type="secondary">共 {rows.length} 个账号</Typography.Text>
            <Space>
              <ColumnVisibilityPicker
                specs={OEM_ACCOUNT_COLUMNS}
                hidden={accountTable.hidden}
                onToggle={accountTable.toggle}
                onShowAll={accountTable.showAll}
                onHideOptional={accountTable.hideOptional}
                onReset={accountTable.reset}
              />
              <Button type="primary" icon={<IconPlus />} onClick={() => { form.resetFields(); setAdding(true) }}>新增账号</Button>
            </Space>
          </div>
          {actionError && <Typography.Text className="oem-delete-error" type="error" role="alert">{actionError}</Typography.Text>}
          <ColumnResizeProvider actions={accountTable.actions}>
            <div ref={accountHostRef} className={accountTable.hostClassName}>
              <Table
                className={`oem-account-table ${accountTable.tableClassName}`}
                rowKey="id"
                data={rows}
                pagination={false}
                scroll={{ x: accountTable.scrollX }}
                columns={accountTable.columns}
                components={accountTable.components}
              />
            </div>
          </ColumnResizeProvider>
        </>
      ) : <span>当前账号没有 OEM 账号管理权限。</span>}
      <Modal
        title="新增账号"
        visible={adding}
        confirmLoading={saving}
        closable={!saving}
        maskClosable={!saving}
        escToExit={!saving}
        cancelButtonProps={{ disabled: saving }}
        onCancel={() => { if (!saving) setAdding(false) }}
        onOk={save}
        unmountOnExit
      >
        <Form onSubmit={() => void save()} form={form} layout="vertical">
          <Form.Item field="employeeNo" label="登录账号" rules={[{ required: true }, { match: /^[A-Za-z0-9_]{3,32}$/, message: '3~32 位字母/数字/下划线' }]}>
            <Input autoFocus />
          </Form.Item>
          <Form.Item field="password" label="初始密码" rules={[{ required: true, message: '请输入初始密码' }, passwordRule]}><Input.Password /></Form.Item>
        <EnterSubmit /></Form>
      </Modal>
    </Drawer>
  )
}

export default function CompaniesPage() {
  const { api, userId } = useOem()
  const can = useCan()
  const [rows, setRows] = useState<Company[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [keyword, setKeyword] = useState('')
  const [editing, setEditing] = useState<Company | 'new' | null>(null)
  const [importOpen, setImportOpen] = useState(false)
  const [exporting, setExporting] = useState(false)
  const [saving, setSaving] = useState(false)
  const [accountsOf, setAccountsOf] = useState<Company | null>(null)
  const [contactsOf, setContactsOf] = useState<Company | null>(null)
  const [accountImportOpen, setAccountImportOpen] = useState(false)
  const [contactImportOpen, setContactImportOpen] = useState(false)
  const [accountExporting, setAccountExporting] = useState(false)
  const [contactExporting, setContactExporting] = useState(false)
  const [actionError, setActionError] = useState('')
  const [listLoading, setListLoading] = useState(false)
  const [listError, setListError] = useState('')
  const companyHostRef = useRef<HTMLDivElement>(null)
  const deletingIdsRef = useRef(new Set<number>())
  const [deletingIds, setDeletingIds] = useState<ReadonlySet<number>>(new Set())
  const saveInFlight = useRef(false)
  const beginRequest = useLatestRequest()
  const [form] = Form.useForm()

  const load = useCallback(async () => {
    if (!can.manageCompanies && !can.manageAccounts) return
    const isLatest = beginRequest()
    setListLoading(true)
    try {
      const result = await api.companies({ page, pageSize, keyword: keyword || undefined })
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
  }, [api, page, pageSize, keyword, can.manageCompanies, can.manageAccounts, beginRequest])
  useLoadEffect(load)

  const fetchAllCompanies = useCallback(async (search?: string) => {
    const pageSizeForExport = 100
    const first = await api.companies({ page: 1, pageSize: pageSizeForExport, keyword: search || undefined })
    const all = [...first.list]
    const pages = Math.ceil(first.total / pageSizeForExport)
    for (let current = 2; current <= pages; current++) {
      const result = await api.companies({ page: current, pageSize: pageSizeForExport, keyword: search || undefined })
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
      const employeeNo = row.values['登录账号']?.trim().toUpperCase() ?? ''
      if (employeeNo) sourceCounts.set(employeeNo, (sourceCounts.get(employeeNo) ?? 0) + 1)
    }
    const payload: OemAccountImportPayload[] = []
    const preview: ImportPreviewRow[] = sourceRows.map(row => {
      const companyName = row.values['厂商名称']?.trim() ?? ''
      const company = companiesByName.get(companyName.toLowerCase())
      const employeeNo = row.values['登录账号']?.trim() ?? ''
      const initialPassword = row.values['初始密码'] ?? ''
      const key = employeeNo || `第 ${row.rowNumber} 行`
      const detail = companyName
      if (!company) return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: 'ODM/OEM 厂商不存在，请先建立厂商资料' }
      if (company.status !== 'ACTIVE') return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: '厂商已停用，不能新增账号' }
      if (employeeNo && (sourceCounts.get(employeeNo.toUpperCase()) ?? 0) > 1)
        return { rowNumber: row.rowNumber, key, detail, status: 'SKIPPED', message: '文件内账号重复，本重复组未导入' }
      if (employeeNo && existing.has(employeeNo.toUpperCase()))
        return { rowNumber: row.rowNumber, key, detail, status: 'SKIPPED', message: '登录账号已存在，不会覆盖现有资料' }
      if (!employeeNo || !/^[A-Za-z0-9_]{3,32}$/.test(employeeNo))
        return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: '账号需为 3–32 位字母、数字或下划线' }
      if (row.numericCells.has('登录账号'))
        return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: '账号列请设置为文本格式，以保留前导零' }
      if (validatePassword(initialPassword))
        return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: '初始密码不符合密码规则' }
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
      const phone = row.values['联系电话']?.trim() ?? ''
      const email = row.values['联系邮箱']?.trim() ?? ''
      const sortText = row.values['排序']?.trim() ?? ''
      const key = name || `第 ${row.rowNumber} 行`
      const detail = [companyName, phone, email].filter(Boolean).join(' · ')
      if (!company) return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: 'ODM/OEM 厂商不存在，请先建立厂商资料' }
      if (company.status !== 'ACTIVE') return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: '厂商已停用，不能新增联络人' }
      const contactKey = `${company.id}:${name.toLowerCase()}`
      if (name && (counts.get(contactKey) ?? 0) > 1)
        return { rowNumber: row.rowNumber, key, detail, status: 'SKIPPED', message: '文件内该厂商的联络人姓名重复，本重复组未导入' }
      if (name && existing.has(contactKey))
        return { rowNumber: row.rowNumber, key, detail, status: 'SKIPPED', message: '该厂商已有同名联络人，未覆盖现有资料' }
      if (!name || Array.from(name).length > 64)
        return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: '联络人姓名不能为空且不能超过 64 个字符' }
      if (!phone && !email) return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: '电话和邮箱至少填写一项' }
      if (phone && Array.from(phone).length > 32)
        return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: '联系电话不能超过 32 个字符' }
      if (email && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email))
        return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: '邮箱格式不正确' }
      const sortNo = sortText === '' ? 0 : /^\d+$/.test(sortText) ? Number(sortText) : NaN
      if (!Number.isSafeInteger(sortNo) || sortNo < 0 || sortNo > 9999)
        return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: '排序需为 0–9999 的整数' }
      payload.push({ rowNumber: row.rowNumber, companyId: company.id, companyName, name, phone, email, sortNo })
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
      const all = await fetchAllCompanies(keyword)
      const data: string[][] = []
      for (const company of all) {
        const accounts = await api.accounts(company.id)
        data.push(...accounts.map(account => [
          company.name, account.employeeNo, account.status,
          account.mustChangePassword ? '是' : '否', account.locked ? '是' : '否', account.lastLoginAt ?? '',
        ]))
      }
      await downloadExcelWorkbook('ODM-OEM账号.xlsx', [
        { name: '数据', headers: [...OEM_ACCOUNT_EXPORT_HEADERS], rows: data },
      ])
      Message.success(`已导出 ${data.length} 个账号；导出文件不含密码`)
    } catch (error) {
      Message.error(oemRequestError(error, 'ODM/OEM账号导出失败'))
    } finally {
      setAccountExporting(false)
    }
  }

  const exportAllContacts = async () => {
    setContactExporting(true)
    try {
      const all = await fetchAllCompanies(keyword)
      const data: string[][] = []
      for (const company of all) {
        const contacts = await api.contacts(company.id)
        data.push(...contacts.map(contact => [
          company.name, contact.name, contact.phone ?? '', contact.email ?? '', String(contact.sortNo), contact.status,
        ]))
      }
      await downloadExcelWorkbook('ODM-OEM联络人.xlsx', [
        { name: '数据', headers: [...OEM_CONTACT_IMPORT_HEADERS, ...OEM_CONTACT_EXPORT_HEADERS], rows: data },
        ...OEM_CONTACT_WORKBOOK.slice(1),
      ])
      Message.success(`已导出 ${data.length} 位联络人`)
    } catch (error) {
      Message.error(oemRequestError(error, 'ODM/OEM联络人导出失败'))
    } finally {
      setContactExporting(false)
    }
  }

  const companyTemplateSheets: ExcelWorkbookSheet[] = [
    { name: '数据', headers: [...OEM_COMPANY_IMPORT_HEADERS], rows: [] },
    { name: '说明', headers: ['说明'], rows: [
      ['本模板仅新增 ODM/OEM 厂商资料；已存在的厂商名称会跳过，不覆盖现有资料。'],
        ['“状态”、账号数和其他联络人数为只读信息，不参与导入。新建厂商默认为启用。'],
    ] },
  ]

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
      const contactName = row.values['主联系人']?.trim() ?? ''
      const contactPhone = row.values['联系电话']?.trim() ?? ''
      const contactEmail = row.values['联系邮箱']?.trim() ?? ''
      const remark = row.values['备注']?.trim() ?? ''
      const key = name || `第 ${row.rowNumber} 行`
      const detail = [contactName, contactPhone, contactEmail].filter(Boolean).join(' · ')
      const normalizedName = name.toLowerCase()
      if (normalizedName && (sourceCounts.get(normalizedName) ?? 0) > 1)
        return { rowNumber: row.rowNumber, key, detail, status: 'SKIPPED', message: '文件内厂商名称重复，本重复组未导入' }
      if (name && existing.has(normalizedName))
        return { rowNumber: row.rowNumber, key, detail, status: 'SKIPPED', message: '厂商名称已存在，不会覆盖现有资料' }
      if (!name || Array.from(name).length > 128)
        return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: '厂商名称不能为空且不能超过 128 个字符' }
      if (Array.from(contactName).length > 64 || Array.from(contactPhone).length > 32 || Array.from(remark).length > 512)
        return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: '联系人、电话或备注超过长度限制' }
      if (contactEmail && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(contactEmail))
        return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: '联系邮箱格式不正确' }
      payload.push({ rowNumber: row.rowNumber, name, contactName, contactPhone, contactEmail, remark })
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
      const all = await fetchAllCompanies(keyword)
      const rows = all.map(company => [
        company.name, company.contactName ?? '', company.contactPhone ?? '', company.contactEmail ?? '',
        company.remark ?? '', company.status, String(company.accountCount ?? 0), String(company.activeAccountCount ?? 0), String(company.contactCount ?? 0),
      ])
      await downloadExcelWorkbook('ODM-OEM厂商.xlsx', [
        { name: '数据', headers: [...OEM_COMPANY_IMPORT_HEADERS, '状态', '账号数', '启用账号数', '其他联络人'], rows },
        ...companyTemplateSheets.slice(1),
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
    if (accountsOf?.id === company.id) setAccountsOf(null)
    if (contactsOf?.id === company.id) setContactsOf(null)
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

  const companyColumns = [
    { key: 'name', title: '厂商名称', dataIndex: 'name', ellipsis: true, render: (value: string) => <EllipsisText value={value} /> },
    { key: 'contactName', title: '联系人', dataIndex: 'contactName', ellipsis: true, render: (value: string | null) => <EllipsisText value={value} /> },
    {
      key: 'contact', title: '联系方式', ellipsis: true,
      render: (_: unknown, row: Company) => <EllipsisText value={[row.contactPhone, row.contactEmail].filter(Boolean).join(' / ') || '-'} />,
    },
    { key: 'accounts', title: '账号', render: (_: unknown, row: Company) => `${row.activeAccountCount ?? 0} / ${row.accountCount ?? 0}` },
    { key: 'contacts', title: '其他联络人', render: (_: unknown, row: Company) => row.contactCount ?? 0 },
    { key: 'status', title: '状态', render: (_: unknown, row: Company) => <Tag color={row.status === 'ACTIVE' ? 'green' : 'gray'}>{row.status === 'ACTIVE' ? '启用' : '停用'}</Tag> },
    {
      key: 'actions', title: '操作', fixed: 'right' as const,
      render: (_: unknown, row: Company) => (
        <Space size={4}>
          {can.manageAccounts && <Button size="mini" type="text" onClick={() => setAccountsOf(row)}>账号</Button>}
          {can.manageCompanies && <Button size="mini" type="text" onClick={() => setContactsOf(row)}>联络人</Button>}
          {can.manageCompanies && <Button size="mini" type="text" onClick={() => { form.setFieldsValue(row); setEditing(row) }}>编辑</Button>}
          {can.manageCompanies && <Popconfirm title={row.status === 'ACTIVE' ? '停用后该厂商所有账号将立即退出登录，确定？' : '确定启用该厂商？'}
            onOk={() => toggleCompanyStatus(row)}>
            <Button size="mini" type="text" status={row.status === 'ACTIVE' ? 'danger' : 'success'}>{row.status === 'ACTIVE' ? '停用' : '启用'}</Button>
          </Popconfirm>}
          {can.manageCompanies && can.deleteCompanies && (
            <Button
              size="mini"
              type="text"
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
  ]
  const companyTable = useAdjustableColumns('oem-companies', userId, OEM_COMPANY_COLUMNS, companyColumns, 'name', companyHostRef)

  return (
    <Card className="page-card page-card--table" title={<h1 className="visually-hidden">OEM 厂商与账号</h1>}>
      <div className="page-toolbar responsive-toolbar">
        <Input.Search allowClear placeholder="搜索厂商名称" style={{ width: 260 }} onSearch={(value) => { setKeyword(value); setPage(1) }} />
        <Space wrap>
          <ColumnVisibilityPicker
            specs={OEM_COMPANY_COLUMNS}
            hidden={companyTable.hidden}
            onToggle={companyTable.toggle}
            onShowAll={companyTable.showAll}
            onHideOptional={companyTable.hideOptional}
            onReset={companyTable.reset}
          />
          {(can.manageCompanies || can.manageAccounts) && (
            <Button size="small" loading={exporting} onClick={() => void exportCompanies()}>导出厂商</Button>
          )}
          {can.manageCompanies && (
            <Button size="small" onClick={() => setImportOpen(true)}>导入厂商</Button>
          )}
          {can.manageAccounts && <>
            <Button size="small" onClick={() => setAccountImportOpen(true)}>导入账号</Button>
            <Button size="small" loading={accountExporting} onClick={() => void exportAllAccounts()}>导出账号</Button>
          </>}
          {can.manageCompanies && <>
            <Button size="small" onClick={() => setContactImportOpen(true)}>导入联络人</Button>
            <Button size="small" loading={contactExporting} onClick={() => void exportAllContacts()}>导出联络人</Button>
          </>}
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
      <ColumnResizeProvider actions={companyTable.actions}>
        <div ref={companyHostRef} className={`${companyTable.hostClassName} page-table-host`}>
          <Table
            className={`page-table oem-directory-table ${companyTable.tableClassName}`}
            rowKey="id"
            loading={listLoading}
            data={rows}
            columns={companyTable.columns}
            components={companyTable.components}
            scroll={{ x: companyTable.scrollX, y: 'var(--page-table-scroll-y)' }}
            pagination={{ current: page, total, pageSize, showTotal: true, sizeCanChange: true, onChange: (nextPage, nextSize) => { setPage(nextPage); setPageSize(nextSize) } }}
          />
        </div>
      </ColumnResizeProvider>
      <ExcelImportDialog<OemCompanyImportPayload>
        visible={importOpen}
        title="导入 ODM/OEM 厂商资料"
        templateName="ODM-OEM厂商导入模板.xlsx"
        requiredHeaders={OEM_COMPANY_IMPORT_HEADERS}
        optionalHeaders={['状态', '账号数', '启用账号数', '其他联络人']}
        templateSheets={companyTemplateSheets}
        maxRows={500}
        prepare={prepareCompanyImport}
        commit={importCompanies}
        onImported={() => void load()}
        onClose={() => setImportOpen(false)}
      />
      <ExcelImportDialog<OemAccountImportPayload>
        visible={accountImportOpen}
        title="导入 ODM/OEM 厂商账号"
        templateName="ODM-OEM账号导入模板.xlsx"
        requiredHeaders={OEM_ACCOUNT_IMPORT_HEADERS}
        optionalHeaders={OEM_ACCOUNT_EXPORT_HEADERS}
        templateSheets={OEM_ACCOUNT_WORKBOOK}
        maxRows={100}
        prepare={prepareAccountImport}
        commit={importAccounts}
        onImported={() => void load().catch(() => undefined)}
        onClose={() => setAccountImportOpen(false)}
      />
      <ExcelImportDialog<OemContactImportPayload>
        visible={contactImportOpen}
        title="导入 ODM/OEM 厂商联络人"
        templateName="ODM-OEM联络人导入模板.xlsx"
        requiredHeaders={OEM_CONTACT_IMPORT_HEADERS}
        optionalHeaders={OEM_CONTACT_EXPORT_HEADERS}
        templateSheets={OEM_CONTACT_WORKBOOK}
        maxRows={100}
        prepare={prepareContactImport}
        commit={importContacts}
        onImported={() => void load().catch(() => undefined)}
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
          <Form.Item field="contactName" label="联系人"><Input maxLength={64} /></Form.Item>
          <Form.Item field="contactPhone" label="联系电话"><Input maxLength={32} /></Form.Item>
          <Form.Item field="contactEmail" label="联系邮箱" rules={[{ type: 'email' }]}><Input /></Form.Item>
          <Form.Item field="remark" label="备注"><Input.TextArea maxLength={512} /></Form.Item>
        <EnterSubmit /></Form>
      </Modal>
      <AccountsDrawer
        key={accountsOf?.id ?? 'closed'}
        company={accountsOf}
        onClose={() => setAccountsOf(null)}
        onAccountDeleted={handleAccountDeleted}
      />
      <ContactsDrawer
        key={contactsOf?.id ?? 'contacts-closed'}
        company={contactsOf}
        onClose={() => setContactsOf(null)}
        onChanged={() => void load().catch(() => undefined)}
      />
    </Card>
  )
}
