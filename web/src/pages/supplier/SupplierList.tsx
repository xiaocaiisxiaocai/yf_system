import { useEffect, useRef, useState } from 'react'
import {
  Button, Card, Form, Input, Message, Popconfirm, Select, Space,
} from '@arco-design/web-react'
import ResizableTable from '../../components/ResizableTable'
import { IconPlus } from '@arco-design/web-react/icon'
import http from '../../api/client'
import { isVersionConflict } from '../../api/versionConflict'
import { actionSlots } from '../../components/ActionSlots'
import { useAuth } from '../../store/auth'
import { fmtTime } from '../../api/types'
import type { ApiResponses, PageResp, SupplierContact } from '../../api/types'
import ExcelImportDialog, { type ImportBatchOutcome, type ImportPreviewRow, type PreparedImport } from '../../components/ExcelImportDialog'
import { downloadExcelWorkbook, type ExcelImportRow, type ExcelWorkbookSheet } from '../../utils/excelWorkbook'
import { SupplierAccountsDrawer } from './components/SupplierAccountsDrawer'
import { SupplierContactsDrawer } from './components/SupplierContactsDrawer'
import { SupplierEditModal } from './components/SupplierEditModal'
import type { Account, RoleOption, Supplier } from './components/supplierTypes'
import { useSupplierListState } from './hooks/useSupplierListState'
import LoadError from '../../components/LoadError'
import { validateInitialPassword } from '../../utils/password'
import ToolbarMenuButton from '../../components/ToolbarMenuButton'
import { ACCOUNT_NAME_MESSAGE, isValidAccountName } from '../../utils/accountName'
import StatusTag from '../../components/StatusTag'

const SUPPLIER_ACCOUNT_HEADERS = ['供应商名称', '账号', '角色', '初始密码'] as const
const SUPPLIER_ACCOUNT_EXPORT_HEADERS = ['供应商名称', '账号', '角色', '状态', '最近登录'] as const
const SUPPLIER_CONTACT_HEADERS = ['供应商名称', '联络人姓名', '邮箱', '排序'] as const
const SUPPLIER_CONTACT_EXPORT_HEADERS = ['状态'] as const

interface SupplierAccountWorkbookRow {
  rowNumber: number
  supplierId: number
  employeeNo: string
  roleId?: number
  initialPassword: string
}

interface SupplierContactWorkbookRow {
  rowNumber: number
  supplierId: number
  name: string
  email: string
  sortNo: number
}

const SUPPLIER_ACCOUNT_WORKBOOK: ExcelWorkbookSheet[] = [
  { name: '数据', headers: [...SUPPLIER_ACCOUNT_HEADERS], rows: [] },
  { name: '说明', headers: ['说明'], rows: [
    ['一行一个账号；同一供应商有多个账号时，重复填写供应商名称。'],
    ['供应商必须已存在，名称须与系统中的供应商名称一致。'],
    ['角色留空时使用系统默认供应商角色；填写时只能使用当前账号可授予的角色。'],
    ['只新增账号，已存在的账号会跳过；初始密码为 5–20 个字符，新账号首次登录须修改密码。'],
  ] },
]

const SUPPLIER_CONTACT_WORKBOOK: ExcelWorkbookSheet[] = [
  { name: '数据', headers: [...SUPPLIER_CONTACT_HEADERS], rows: [] },
  { name: '说明', headers: ['说明'], rows: [
    ['一行一个联络人；同一供应商有多个联络人时，重复填写供应商名称。'],
    ['供应商必须已存在，名称须与系统中的供应商名称一致。'],
    ['只新增联络人；同一供应商下已存在的同名联络人会跳过。'],
  ] },
]

export default function SupplierList() {
  const canManageSuppliers = useAuth((state) => state.hasPerm('supplier:manage'))
  const canManageAccounts = useAuth((state) => state.hasPerm('supplier:account'))
  const canDelete = useAuth((state) => state.hasPerm('supplier:delete'))
  const {
    data, loading, keyword, status, page, pageSize, loadError,
    load, search, clearSearch, changeStatus, changePage,
  } = useSupplierListState()
  const [editOpen, setEditOpen] = useState(false)
  const [saving, setSaving] = useState(false)
  const [editing, setEditing] = useState<Supplier | null>(null)
  const [accTarget, setAccTarget] = useState<Supplier | null>(null)
  const [contactTarget, setContactTarget] = useState<Supplier | null>(null)
  const [accountImportOpen, setAccountImportOpen] = useState(false)
  const [contactImportOpen, setContactImportOpen] = useState(false)
  const [accountExporting, setAccountExporting] = useState(false)
  const [contactExporting, setContactExporting] = useState(false)
  const accountTrigger = useRef<HTMLButtonElement | null>(null)
  const saveInFlight = useRef(false)
  const togglingSupplierIdsRef = useRef(new Set<number>())
  const [togglingSupplierIds, setTogglingSupplierIds] = useState<ReadonlySet<number>>(new Set())
  const [form] = Form.useForm()

  const fetchAllSuppliers = async (query: { keyword?: string; status?: string } = {}) => {
    const pageSizeForExport = 100
    const first = await http.get<ApiResponses['GET /admin/suppliers']>('/admin/suppliers', {
      params: { ...query, page: 1, pageSize: pageSizeForExport },
    })
    const firstPage = first.data as PageResp<Supplier>
    const all = [...firstPage.list]
    const pages = Math.ceil(firstPage.total / pageSizeForExport)
    for (let current = 2; current <= pages; current++) {
      const response = await http.get<ApiResponses['GET /admin/suppliers']>('/admin/suppliers', {
        params: { ...query, page: current, pageSize: pageSizeForExport },
      })
      all.push(...(response.data as PageResp<Supplier>).list)
    }
    return all
  }

  const fetchSupplierAccounts = async (suppliers: readonly Supplier[]) => {
    const bySupplier = new Map<number, Account[]>()
    for (const supplier of suppliers) {
      const response = await http.get<ApiResponses['GET /admin/suppliers/{id}/accounts']>(`/admin/suppliers/${supplier.id}/accounts`)
      bySupplier.set(supplier.id, response.data as Account[])
    }
    return bySupplier
  }

  const fetchSupplierContacts = async (suppliers: readonly Supplier[]) => {
    const bySupplier = new Map<number, SupplierContact[]>()
    for (const supplier of suppliers) {
      const response = await http.get<ApiResponses['GET /admin/suppliers/{id}/contacts']>(`/admin/suppliers/${supplier.id}/contacts`)
      bySupplier.set(supplier.id, response.data as SupplierContact[])
    }
    return bySupplier
  }

  const prepareSupplierAccountImport = async (sourceRows: ExcelImportRow[]): Promise<PreparedImport<SupplierAccountWorkbookRow>> => {
    const [suppliers, rolesResponse] = await Promise.all([
      fetchAllSuppliers(),
      http.get<ApiResponses['GET /admin/supplier-role-options']>('/admin/supplier-role-options'),
    ])
    const accountsBySupplier = await fetchSupplierAccounts(suppliers)
    const supplierByName = new Map(suppliers.map(supplier => [supplier.name.trim().toLowerCase(), supplier]))
    const existingEmployees = new Set(Array.from(accountsBySupplier.values()).flatMap(accounts =>
      accounts.map(account => account.employeeNo.trim().toUpperCase())))
    const roles = rolesResponse.data as RoleOption[]
    const rolesByName = new Map(roles.map(role => [role.name.trim().toLowerCase(), role.id]))
    const defaultRoleId = roles.find(role => role.name === '供应商人员')?.id ?? (roles.length === 1 ? roles[0]?.id : undefined)
    const counts = new Map<string, number>()
    for (const row of sourceRows) {
      const employeeNo = row.values['账号']?.trim().toUpperCase() ?? ''
      if (employeeNo) counts.set(employeeNo, (counts.get(employeeNo) ?? 0) + 1)
    }
    const payload: SupplierAccountWorkbookRow[] = []
    const preview: ImportPreviewRow[] = sourceRows.map(row => {
      const supplierName = row.values['供应商名称']?.trim() ?? ''
      const supplier = supplierByName.get(supplierName.toLowerCase())
      const employeeNo = row.values['账号']?.trim() ?? ''
      const roleName = row.values['角色']?.trim() ?? ''
      const initialPassword = row.values['初始密码'] ?? ''
      const key = employeeNo || `第 ${row.rowNumber} 行`
      const detail = [supplierName, roleName || '供应商人员'].filter(Boolean).join(' · ')
      if (!supplier) return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: '供应商不存在，请使用已建立的供应商名称' }
      if (supplier.status !== 'ACTIVE') return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: '供应商已停用，不能新增账号' }
      if (employeeNo && (counts.get(employeeNo.toUpperCase()) ?? 0) > 1)
        return { rowNumber: row.rowNumber, key, detail, status: 'SKIPPED', message: '文件内账号重复，本重复组未导入' }
      if (employeeNo && existingEmployees.has(employeeNo.toUpperCase()))
        return { rowNumber: row.rowNumber, key, detail, status: 'SKIPPED', message: '账号已存在，不会覆盖现有资料' }
      if (!employeeNo || !isValidAccountName(employeeNo))
        return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: ACCOUNT_NAME_MESSAGE }
      if (row.numericCells.has('账号'))
        return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: '账号列请设置为文本格式，以保留前导零' }
      const roleId = roleName ? rolesByName.get(roleName.toLowerCase()) : defaultRoleId
      if (roleName && !roleId) return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: '角色不存在或当前账号不可授予' }
      if (!roleId) return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: '角色选项不可用，请先设置供应商角色' }
      const passwordError = validateInitialPassword(initialPassword)
      if (passwordError)
        return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: passwordError }
      payload.push({ rowNumber: row.rowNumber, supplierId: supplier.id, employeeNo, roleId, initialPassword })
      return { rowNumber: row.rowNumber, key, detail, status: 'READY' }
    })
    return { payload, preview }
  }

  const importSupplierAccounts = async (rows: SupplierAccountWorkbookRow[]): Promise<ImportBatchOutcome> => {
    const grouped = new Map<number, SupplierAccountWorkbookRow[]>()
    rows.forEach(row => grouped.set(row.supplierId, [...(grouped.get(row.supplierId) ?? []), row]))
    const outcomes: ImportBatchOutcome[] = []
    for (const [supplierId, group] of grouped) {
      const response = await http.post<ApiResponses['POST /admin/suppliers/{id}/accounts/import']>(
        `/admin/suppliers/${supplierId}/accounts/import`,
        { rows: group.map(({ supplierId: _supplierId, ...row }) => row) },
      )
      outcomes.push(response.data as unknown as ImportBatchOutcome)
    }
    return {
      created: outcomes.reduce((sum, result) => sum + result.created, 0),
      skipped: outcomes.reduce((sum, result) => sum + result.skipped, 0),
      failed: outcomes.reduce((sum, result) => sum + result.failed, 0),
      rows: outcomes.flatMap(result => result.rows),
    }
  }

  const prepareSupplierContactImport = async (sourceRows: ExcelImportRow[]): Promise<PreparedImport<SupplierContactWorkbookRow>> => {
    const suppliers = await fetchAllSuppliers()
    const contactsBySupplier = await fetchSupplierContacts(suppliers)
    const supplierByName = new Map(suppliers.map(supplier => [supplier.name.trim().toLowerCase(), supplier]))
    const existing = new Set(Array.from(contactsBySupplier, ([supplierId, contacts]) =>
      contacts.map(contact => `${supplierId}:${contact.name.trim().toLowerCase()}`)).flat())
    const counts = new Map<string, number>()
    for (const row of sourceRows) {
      const supplier = supplierByName.get((row.values['供应商名称']?.trim() ?? '').toLowerCase())
      const name = row.values['联络人姓名']?.trim().toLowerCase() ?? ''
      const key = supplier && name ? `${supplier.id}:${name}` : ''
      if (key) counts.set(key, (counts.get(key) ?? 0) + 1)
    }
    const payload: SupplierContactWorkbookRow[] = []
    const preview: ImportPreviewRow[] = sourceRows.map(row => {
      const supplierName = row.values['供应商名称']?.trim() ?? ''
      const supplier = supplierByName.get(supplierName.toLowerCase())
      const name = row.values['联络人姓名']?.trim() ?? ''
      const email = row.values['邮箱']?.trim() ?? ''
      const sortText = row.values['排序']?.trim() ?? ''
      const key = name || `第 ${row.rowNumber} 行`
      const detail = [supplierName, email].filter(Boolean).join(' · ')
      if (!supplier) return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: '供应商不存在，请使用已建立的供应商名称' }
      const contactKey = `${supplier.id}:${name.toLowerCase()}`
      if (name && (counts.get(contactKey) ?? 0) > 1)
        return { rowNumber: row.rowNumber, key, detail, status: 'SKIPPED', message: '文件内该供应商的联络人姓名重复，本重复组未导入' }
      if (name && existing.has(contactKey))
        return { rowNumber: row.rowNumber, key, detail, status: 'SKIPPED', message: '该供应商已有同名联络人，未覆盖现有资料' }
      if (!name || Array.from(name).length > 32)
        return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: '联络人姓名需为 1–32 个字符' }
      if (!email || !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email))
        return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: '邮箱格式不正确' }
      const sortNo = sortText === '' ? 0 : /^\d+$/.test(sortText) ? Number(sortText) : NaN
      if (!Number.isSafeInteger(sortNo) || sortNo < 0 || sortNo > 9999)
        return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: '排序需为 0–9999 的整数' }
      payload.push({ rowNumber: row.rowNumber, supplierId: supplier.id, name, email, sortNo })
      return { rowNumber: row.rowNumber, key, detail, status: 'READY' }
    })
    return { payload, preview }
  }

  const importSupplierContacts = async (rows: SupplierContactWorkbookRow[]): Promise<ImportBatchOutcome> => {
    const grouped = new Map<number, SupplierContactWorkbookRow[]>()
    rows.forEach(row => grouped.set(row.supplierId, [...(grouped.get(row.supplierId) ?? []), row]))
    const outcomes: ImportBatchOutcome[] = []
    for (const [supplierId, group] of grouped) {
      const response = await http.post<ApiResponses['POST /admin/suppliers/{id}/contacts/import']>(
        `/admin/suppliers/${supplierId}/contacts/import`,
        { rows: group.map(({ supplierId: _supplierId, ...row }) => row) },
      )
      outcomes.push(response.data as unknown as ImportBatchOutcome)
    }
    return {
      created: outcomes.reduce((sum, result) => sum + result.created, 0),
      skipped: outcomes.reduce((sum, result) => sum + result.skipped, 0),
      failed: outcomes.reduce((sum, result) => sum + result.failed, 0),
      rows: outcomes.flatMap(result => result.rows),
    }
  }

  const exportSupplierAccounts = async () => {
    setAccountExporting(true)
    try {
      const suppliers = await fetchAllSuppliers({ keyword: keyword || undefined, status })
      const rows: string[][] = []
      for (const supplier of suppliers) {
        const accounts = (await fetchSupplierAccounts([supplier])).get(supplier.id) ?? []
        rows.push(...accounts.map(account => [
          supplier.name, account.employeeNo, account.roleName ?? '', account.status, account.lastLoginAt ?? '',
        ]))
      }
      await downloadExcelWorkbook('Robot供应商账号.xlsx', [
        { name: '数据', headers: [...SUPPLIER_ACCOUNT_EXPORT_HEADERS], rows },
      ])
      Message.success(`已导出 ${rows.length} 个账号；导出文件不含密码`)
    } catch (error) {
      Message.error(error instanceof Error ? error.message : '供应商账号导出失败')
    } finally {
      setAccountExporting(false)
    }
  }

  const exportSupplierContacts = async () => {
    setContactExporting(true)
    try {
      const suppliers = await fetchAllSuppliers({ keyword: keyword || undefined, status })
      const rows: string[][] = []
      for (const supplier of suppliers) {
        const contacts = (await fetchSupplierContacts([supplier])).get(supplier.id) ?? []
        rows.push(...contacts.map(contact => [supplier.name, contact.name, contact.email, String(contact.sortNo), contact.status]))
      }
      await downloadExcelWorkbook('Robot供应商联络人.xlsx', [
        { name: '数据', headers: [...SUPPLIER_CONTACT_HEADERS, ...SUPPLIER_CONTACT_EXPORT_HEADERS], rows },
        ...SUPPLIER_CONTACT_WORKBOOK.slice(1),
      ])
      Message.success(`已导出 ${rows.length} 位联络人`)
    } catch (error) {
      Message.error(error instanceof Error ? error.message : '供应商联络人导出失败')
    } finally {
      setContactExporting(false)
    }
  }

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
    } catch (error) {
      if (isVersionConflict(error)) load()
      else throw error
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
      Message.success(supplier.status === 'ACTIVE' ? '已停用（其账号将全部无法登录）' : '已启用')
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
      <h1 className="visually-hidden">供应商管理</h1>
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
            <Select.Option value="DISABLED">停用</Select.Option>
          </Select>
        </Space>
        <Space wrap>
          <ToolbarMenuButton label="导入" items={[
            canManageAccounts && { key: 'accounts', label: '导入账号', onClick: () => setAccountImportOpen(true) },
            canManageSuppliers && { key: 'contacts', label: '导入联络人', onClick: () => setContactImportOpen(true) },
          ]} />
          <ToolbarMenuButton label="导出" items={[
            canManageAccounts && { key: 'accounts', label: '导出账号', loading: accountExporting, onClick: () => void exportSupplierAccounts() },
            canManageSuppliers && { key: 'contacts', label: '导出联络人', loading: contactExporting, onClick: () => void exportSupplierContacts() },
          ]} />
          {canManageSuppliers && <Button
            type="primary"
            icon={<IconPlus />}
            onClick={() => {
              setEditing(null)
              form.resetFields()
              setEditOpen(true)
            }}
          >
            新增供应商
          </Button>}
        </Space>
      </div>
      {loadError ? (
        <LoadError onRetry={load} />
      ) : (
        <ResizableTable tableId="suppliers"
          className="page-table"
          rowKey="id"
          loading={loading}
          data={data.list}
          scroll={{ x: 740, y: 'var(--page-table-scroll-y)' }}
          columns={[
            { title: '名称', dataIndex: 'name', width: 240, ellipsis: true },
            {
              title: '状态',
              dataIndex: 'status',
              width: 70,
              align: 'center' as const,
              render: (value: string) => <StatusTag status={value} />,
            },
            { title: '创建时间', dataIndex: 'createdAt', width: 180, align: 'center' as const, render: fmtTime },
            {
              title: '操作',
              width: 300,
              fixed: 'right' as const,
              render: (_: unknown, supplier: Supplier) => actionSlots([
                canManageAccounts && <Button key="accounts" size="mini" type="text" onClick={(event) => {
                  accountTrigger.current = (event?.currentTarget as HTMLButtonElement | undefined) ?? null
                  setAccTarget(supplier)
                }}>
                  账号管理
                </Button>,
                canManageSuppliers && <Button key="contacts" size="mini" type="text" onClick={() => setContactTarget(supplier)}>
                  联络人
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
                  title={supplier.status === 'ACTIVE' ? '停用后其所有账号无法登录，确认？' : '确认启用？'}
                  onOk={() => toggleStatus(supplier)}
                >
                  <Button
                    size="mini"
                    type="text"
                    status={supplier.status === 'ACTIVE' ? 'danger' : 'success'}
                    loading={togglingSupplierIds.has(supplier.id)}
                    disabled={togglingSupplierIds.has(supplier.id)}
                  >
                    {supplier.status === 'ACTIVE' ? '停用' : '启用'}
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
      {canManageSuppliers && <SupplierContactsDrawer key={contactTarget?.id} supplier={contactTarget} onClose={() => setContactTarget(null)} />}
      <ExcelImportDialog<SupplierAccountWorkbookRow>
        visible={accountImportOpen}
        title="导入 Robot 供应商账号"
        templateName="Robot供应商账号导入模板.xlsx"
        requiredHeaders={SUPPLIER_ACCOUNT_HEADERS}
        optionalHeaders={SUPPLIER_ACCOUNT_EXPORT_HEADERS}
        templateSheets={SUPPLIER_ACCOUNT_WORKBOOK}
        maxRows={100}
        prepare={prepareSupplierAccountImport}
        commit={importSupplierAccounts}
        onImported={() => load()}
        onClose={() => setAccountImportOpen(false)}
      />
      <ExcelImportDialog<SupplierContactWorkbookRow>
        visible={contactImportOpen}
        title="导入 Robot 供应商联络人"
        templateName="Robot供应商联络人导入模板.xlsx"
        requiredHeaders={SUPPLIER_CONTACT_HEADERS}
        optionalHeaders={SUPPLIER_CONTACT_EXPORT_HEADERS}
        templateSheets={SUPPLIER_CONTACT_WORKBOOK}
        maxRows={100}
        prepare={prepareSupplierContactImport}
        commit={importSupplierContacts}
        onImported={() => load()}
        onClose={() => setContactImportOpen(false)}
      />
    </Card>
  )
}
