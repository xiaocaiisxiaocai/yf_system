import { optionalTextLengthRule, textLengthRule } from '../../utils/textRules'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import {
  Button, Card, Form, Input, Message, Modal, Popconfirm, Radio, Result, Select, Space, Tag, Tooltip, TreeSelect, Typography,
} from '@arco-design/web-react'
import ResizableTable from '../../components/ResizableTable'
import { IconPlus } from '@arco-design/web-react/icon'
import http, { type QuietRequestConfig } from '../../api/client'
import { createSessionQueryScope, queryClient } from '../../api/queryClient'
import { actionSlots } from '../../components/ActionSlots'
import DangerPopconfirm from '../../components/DangerPopconfirm'
import PasswordInput from '../../components/PasswordInput'
import { useShallow } from 'zustand/react/shallow'
import { useAuth } from '../../store/auth'
import { type PageResp, fmtTime } from '../../api/types'
import { passwordRule, validateInitialPassword } from '../../utils/password'
import type { ApiResponses } from '../../api/types'
import type { DataScopeResponse, DepartmentTreeNode as ApiDepartmentTreeNode, UserBatchFailure, UserBatchUpdate, UserResponse } from '../../api/generated/api-types'
import LoadError from '../../components/LoadError'
import EnterSubmit from '../../components/EnterSubmit'
import ExcelImportDialog, { type ImportBatchOutcome, type ImportPreviewRow, type PreparedImport } from '../../components/ExcelImportDialog'
import { downloadExcelWorkbook, type ExcelImportRow, type ExcelWorkbookSheet } from '../../utils/excelWorkbook'
import { ACCOUNT_NAME_MESSAGE, ACCOUNT_NAME_PATTERN, ACCOUNT_NAME_PLACEHOLDER, isValidAccountName } from '../../utils/accountName'
import StatusTag from '../../components/StatusTag'

interface UserRow {
  id: number
  employeeNo: string
  realName: string
  email: string
  departmentName?: string | null
  departmentId?: number | null
  roleId?: number | null
  roleName?: string | null
  /** 旧接口兼容字段，待外部消费者完成迁移后移除。 */
  roleIds?: number[]
  roleNames?: string[]
  status: 'ACTIVE' | 'DISABLED'
  networkScope: NetworkScope
  mustChangePassword?: boolean
  lastLoginAt?: string | null
  createdAt: string
}

interface RoleOpt {
  id: number
  name: string
}

const EMPTY_ROLE_OPTIONS: RoleOpt[] = []

const NETWORK_SCOPE_LABELS = { INTERNAL_ONLY: '仅内网', ANY: '内外网均可' } as const
type NetworkScope = keyof typeof NETWORK_SCOPE_LABELS & UserResponse['networkScope']

const MAX_BATCH_USERS = 100
const BATCH_EDIT_LABELS = { roleId: '更换角色', networkScope: '设置访问范围', departmentId: '调整组织' } as const
type BatchEditField = keyof typeof BATCH_EDIT_LABELS
type BatchChanges = Omit<UserBatchUpdate, 'userIds'>

// Department tree from the API, with kind narrowed to the fixed three-level hierarchy.
type DeptNode = Omit<ApiDepartmentTreeNode, 'kind' | 'children'> & {
  kind: 'DIVISION' | 'DEPARTMENT' | 'SECTION'
  children: DeptNode[]
}

const ORG_KIND_LABEL = { DIVISION: '事业部', DEPARTMENT: '部门', SECTION: '课别' } as const
// 数据范围与动作权限分开：管理员在这里按人覆盖角色默认值，不需要新增权限点或改代码。
const SCOPE_DOMAINS = [
  { domain: 'project', label: '项目范围' },
  { domain: 'oem_transfer', label: 'ODM/OEMA 传递单范围' },
] as const
const SCOPE_LABELS: Record<string, string> = {
  SELF: '仅本人', ORG: '本组织及下级', ALL: '全部',
}
const USER_IMPORT_HEADERS = ['账号', '姓名', '邮箱', '所属组织', '角色', '初始密码'] as const
const USER_IMPORT_EXAMPLE = ['demo001', '示例员工', 'example@example.com', '示例部门', '示例角色', 'T7!kmQ4n'] as const
// 2026-11 之前的模板第 4 列叫“课别”，旧文件仍可导入。
const USER_IMPORT_HEADER_ALIASES = { 课别: '所属组织' } as const
const USER_EXPORT_HEADERS = ['状态', '需改密', '最近登录'] as const
const USER_OPTION_HEADERS = ['所属组织', '组织层级', '角色名称'] as const

interface DepartmentSectionOption { id: number; name: string; active: boolean; kind: string }
interface InternalUserImportPayload {
  rowNumber: number
  employeeNo: string
  realName: string
  email: string
  departmentId: number
  roleId: number
  initialPassword: string
}

interface DeptTreeData { key: string; title: string; value: string; disabled?: boolean; children?: DeptTreeData[] }
function toTreeData(
  nodes: DeptNode[],
  options: { disableInactive?: boolean; allowedInactiveId?: number | null; parentActive?: boolean } = {},
): DeptTreeData[] {
  const { disableInactive = false, allowedInactiveId = null, parentActive = true } = options
  return nodes.map((n) => {
    const active = parentActive && n.status === 'ACTIVE'
    const baseTitle = n.kind && ORG_KIND_LABEL[n.kind] ? `${n.name}（${ORG_KIND_LABEL[n.kind]}）` : n.name
    const title = active ? baseTitle : `${baseTitle}（已停用）`
    return {
      key: String(n.id),
      title,
      value: String(n.id),
      disabled: disableInactive && !active && n.id !== allowedInactiveId,
      children: n.children && n.children.length
        ? toTreeData(n.children, { disableInactive, allowedInactiveId, parentActive: active })
        : undefined,
    }
  })
}

function departmentSections(nodes: DeptNode[], parentActive = true): DepartmentSectionOption[] {
  return nodes.flatMap(node => {
    const active = parentActive && node.status === 'ACTIVE'
    return [
      { id: node.id, name: node.name, active, kind: node.kind },
      ...departmentSections(node.children, active),
    ]
  })
}

async function fetchAllUsers(query: { keyword?: string; departmentId?: number; status?: string } = {}): Promise<UserRow[]> {
  const pageSize = 100
  const first = await http.get<ApiResponses['GET /admin/users']>('/admin/users', { params: { ...query, page: 1, pageSize } })
  const firstPage = first.data as PageResp<UserRow>
  const users = [...firstPage.list]
  const pages = Math.ceil(firstPage.total / pageSize)
  for (let pageIndex = 2; pageIndex <= pages; pageIndex++) {
    const response = await http.get<ApiResponses['GET /admin/users']>('/admin/users', {
      params: { ...query, page: pageIndex, pageSize },
    })
    users.push(...(response.data as PageResp<UserRow>).list)
  }
  return users
}

function isAuthorizationError(error: unknown): boolean {
  if (!error || typeof error !== 'object') return false
  const candidate = error as { status?: unknown; response?: { status?: unknown } }
  const status = candidate.response?.status ?? candidate.status
  return status === 401 || status === 403
}

export default function UserList() {
  const auth = useAuth(useShallow((state) => ({
    user: state.user,
    generation: state.generation,
    permissions: state.permissions,
    menus: state.menus,
    mustChangePassword: state.mustChangePassword,
  })))
  const canDelete = useAuth((state) => state.hasPerm('user:delete'))
  // 编辑（含数据范围）需要 user:manage；只有 user:view 的账号只看列表，不给一个必然 403 的入口。
  const canManageUsers = useAuth((state) => state.hasPerm('user:manage'))
  const [sessionUserId, sessionGeneration, sessionGrants] = createSessionQueryScope(auth)
  const sessionScope = useMemo(
    () => [sessionUserId, sessionGeneration, sessionGrants] as const,
    [sessionGeneration, sessionGrants, sessionUserId],
  )
  const me = auth.user
  const [keyword, setKeyword] = useState('')
  const [searchText, setSearchText] = useState('')
  const [departmentId, setDepartmentId] = useState<number>()
  const [status, setStatus] = useState<string>()
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(10)
  const [selectedIds, setSelectedIds] = useState<number[]>([])
  const [batchField, setBatchField] = useState<BatchEditField | null>(null)
  const [batchChanges, setBatchChanges] = useState<BatchChanges>({})
  const [batchFailures, setBatchFailures] = useState<UserBatchFailure[]>([])
  const batchInFlight = useRef(false)
  const [editOpen, setEditOpen] = useState(false)
  const [importOpen, setImportOpen] = useState(false)
  const [exporting, setExporting] = useState(false)
  const [editing, setEditing] = useState<UserRow | null>(null)
  const [resetTarget, setResetTarget] = useState<UserRow | null>(null)
  const [scopeValues, setScopeValues] = useState<Record<string, string>>({})
  const [scopeOriginal, setScopeOriginal] = useState<Record<string, string>>({})
  const [scopeEffective, setScopeEffective] = useState<Record<string, string>>({})
  const [scopesLoading, setScopesLoading] = useState(false)
  const [scopeLoadedFor, setScopeLoadedFor] = useState<number | null>(null)
  const saveInFlight = useRef(false)
  const passwordResetInFlight = useRef(false)
  const [form] = Form.useForm()
  const [pwdForm] = Form.useForm()
  const usersQueryKey = useMemo(() => ['admin-users', sessionScope] as const, [sessionScope])
  const optionsQueryKey = useMemo(() => ['admin-user-options', sessionScope] as const, [sessionScope])

  const effectiveDepartmentId = canManageUsers ? departmentId : undefined
  const hasActiveFilters = Boolean(keyword || effectiveDepartmentId || status)
  const usersQuery = useQuery({
    queryKey: [...usersQueryKey, { page, pageSize, keyword, departmentId: effectiveDepartmentId, status }],
    queryFn: async ({ signal }) => {
      const response = await http.get<ApiResponses['GET /admin/users']>('/admin/users', {
        params: { page, pageSize, keyword: keyword || undefined, departmentId: effectiveDepartmentId, status }, signal,
      })
      return response.data as PageResp<UserRow>
    },
  }, queryClient)
  const optionsQuery = useQuery({
    queryKey: optionsQueryKey,
    // 只读账号（user:view）不需要编辑用的组织/角色选项；请求它只会 403 并连带清空列表。
    enabled: canManageUsers,
    queryFn: async ({ signal }) => {
      const config: QuietRequestConfig = { signal, quietNetworkError: true }
      const [departmentsResponse, rolesResponse] = await Promise.all([
        http.get<ApiResponses['GET /departments']>('/departments', config),
        http.get<ApiResponses['GET /admin/user-role-options']>('/admin/user-role-options', config),
      ])
      return { depts: departmentsResponse.data as DeptNode[], roles: rolesResponse.data as RoleOpt[] }
    },
  }, queryClient)

  const authorizationError = isAuthorizationError(usersQuery.error)
  const data = !authorizationError && usersQuery.data ? usersQuery.data : { list: [], total: 0, page, pageSize }
  const loading = usersQuery.isFetching
  // 只有在还没有任何数据时才用错误视图替换表格；后台刷新失败保留上次加载的列表并在上方提示。
  const usersFailed = usersQuery.isError && !usersQuery.isFetching
  const loadError = usersFailed && (authorizationError || !usersQuery.data)
  const refreshError = usersFailed && !loadError
  const roles = authorizationError || optionsQuery.isError ? EMPTY_ROLE_OPTIONS : optionsQuery.data?.roles ?? EMPTY_ROLE_OPTIONS
  const optionsLoading = canManageUsers && optionsQuery.isFetching
  const optionsError = canManageUsers && optionsQuery.isError && !optionsQuery.isFetching
  const loadedDepts = authorizationError || optionsQuery.isError ? undefined : optionsQuery.data?.depts
  const deptTreeData = useMemo(() => toTreeData(loadedDepts ?? []), [loadedDepts])
  const batchDeptTreeData = useMemo(() => toTreeData(loadedDepts ?? [], { disableInactive: true }), [loadedDepts])
  const formDeptTreeData = useMemo(
    () => toTreeData(loadedDepts ?? [], { disableInactive: true, allowedInactiveId: editing?.departmentId ?? null }),
    [editing?.departmentId, loadedDepts],
  )
  // 账号可以挂在事业部、部门或课别任一层级（如部门主管只属于部门）。
  const activeOrganizations = useMemo(() => departmentSections(loadedDepts ?? [])
    .filter(item => item.active), [loadedDepts])
  const userWorkbookSheets = useMemo<ExcelWorkbookSheet[]>(() => {
    const optionRows = Array.from({ length: Math.max(activeOrganizations.length, roles.length) }, (_, index) => [
      activeOrganizations[index]?.name ?? '',
      activeOrganizations[index] ? ORG_KIND_LABEL[activeOrganizations[index].kind as keyof typeof ORG_KIND_LABEL] ?? '' : '',
      roles[index]?.name ?? '',
    ])
    return [
      { name: '数据', headers: USER_IMPORT_HEADERS, rows: [USER_IMPORT_EXAMPLE] },
      { name: '选项', headers: USER_OPTION_HEADERS, rows: optionRows },
      { name: '说明', headers: ['说明'], rows: [
        ['仅新增账号；已存在账号会跳过，不会覆盖。'],
        ['所属组织可填事业部、部门或课别名称，必须来自“选项”工作表；组织名称重名时请先区分名称再导入。角色必须来自“选项”工作表。'],
        ['邮箱可留空；未填邮箱的账号不会收到邮件通知。'],
        ['必须填写初始密码（5–20 个字符，如 123456 也可）；新账号首次登录须修改为符合密码规则的新密码。导出文件不包含密码。'],
      ] },
    ]
  }, [activeOrganizations, roles])
  const editingRoleId = editing?.roleId ?? editing?.roleIds?.[0]
  const editingRoleUnavailable = editingRoleId != null
    && !optionsLoading
    && !optionsError
    && !roles.some((role) => role.id === editingRoleId)

  useEffect(() => {
    if (!authorizationError) return
    void queryClient.cancelQueries({ queryKey: usersQueryKey })
    void queryClient.cancelQueries({ queryKey: optionsQueryKey })
    queryClient.removeQueries({ queryKey: usersQueryKey })
    queryClient.removeQueries({ queryKey: optionsQueryKey })
  }, [authorizationError, optionsQueryKey, usersQueryKey])

  useEffect(() => {
    const next = usersQuery.data
    if (!next) return
    const lastPage = Math.max(1, Math.ceil(next.total / (next.pageSize || pageSize)))
    // eslint-disable-next-line react-hooks/set-state-in-effect -- the server can shrink the last page between requests
    if (page > lastPage) setPage(lastPage)
  }, [page, pageSize, usersQuery.data])

  const invalidateUsers = () => queryClient.invalidateQueries({ queryKey: usersQueryKey })
  const prepareUserImport = async (sourceRows: ExcelImportRow[]): Promise<PreparedImport<InternalUserImportPayload>> => {
    if (optionsLoading || optionsError || roles.length === 0 || !loadedDepts)
      throw new Error('组织或角色选项暂不可用，请刷新后重试')
    const allUsers = await fetchAllUsers()
    const existingEmployees = new Set(allUsers.map(user => user.employeeNo.trim().toUpperCase()))
    const organizationsByName = new Map<string, DepartmentSectionOption[]>()
    for (const organization of activeOrganizations) {
      const key = organization.name.trim().toLowerCase()
      organizationsByName.set(key, [...(organizationsByName.get(key) ?? []), organization])
    }
    const roleByName = new Map(roles.map(role => [role.name.trim().toLowerCase(), role.id]))
    const sourceCounts = new Map<string, number>()
    for (const row of sourceRows) {
      const employeeNo = row.values['账号']?.trim().toUpperCase() ?? ''
      if (employeeNo) sourceCounts.set(employeeNo, (sourceCounts.get(employeeNo) ?? 0) + 1)
    }
    const payload: InternalUserImportPayload[] = []
    const preview: ImportPreviewRow[] = sourceRows.map(row => {
      const employeeNo = row.values['账号']?.trim() ?? ''
      const realName = row.values['姓名']?.trim() ?? ''
      const email = row.values['邮箱']?.trim() ?? ''
      const departmentName = row.values['所属组织']?.trim() ?? ''
      const roleName = row.values['角色']?.trim() ?? ''
      const initialPassword = row.values['初始密码'] ?? ''
      const key = employeeNo || `第 ${row.rowNumber} 行`
      const detail = [realName, departmentName, roleName].filter(Boolean).join(' · ')
      const normalizedEmployeeNo = employeeNo.toUpperCase()
      if (normalizedEmployeeNo && (sourceCounts.get(normalizedEmployeeNo) ?? 0) > 1)
        return { rowNumber: row.rowNumber, key, detail, status: 'SKIPPED', message: '文件内账号重复，本重复组未导入' }
      if (employeeNo && existingEmployees.has(normalizedEmployeeNo))
        return { rowNumber: row.rowNumber, key, detail, status: 'SKIPPED', message: '账号已存在，不会覆盖现有资料' }
      if (!employeeNo || !isValidAccountName(employeeNo))
        return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: ACCOUNT_NAME_MESSAGE }
      if (row.numericCells.has('账号'))
        return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: '账号列请设置为文本格式，以保留前导零' }
      if (!realName || Array.from(realName).length > 64)
        return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: '姓名不能为空且不能超过 64 个字符' }
      if (email && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email))
        return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: '邮箱格式不正确' }
      const organizations = organizationsByName.get(departmentName.toLowerCase()) ?? []
      if (organizations.length > 1)
        return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: '组织名称重名，请先区分组织名称再导入' }
      const organization = organizations[0]
      if (!organization) return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: '所属组织不存在或已停用' }
      const departmentId = organization.id
      const roleId = roleByName.get(roleName.toLowerCase())
      if (!roleId) return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: '角色不存在或当前账号不可授予' }
      const passwordError = validateInitialPassword(initialPassword)
      if (passwordError) return { rowNumber: row.rowNumber, key, detail, status: 'INVALID', message: passwordError }
      payload.push({ rowNumber: row.rowNumber, employeeNo, realName, email, departmentId, roleId, initialPassword })
      return { rowNumber: row.rowNumber, key, detail, status: 'READY' }
    })
    return { payload, preview }
  }

  const exportUsers = async () => {
    if (exporting) return
    setExporting(true)
    try {
      if (optionsError || !loadedDepts) throw new Error('组织和角色资料暂不可用，请重试')
      const allUsers = await fetchAllUsers({ keyword: keyword || undefined, departmentId, status })
      const sectionsById = new Map(departmentSections(loadedDepts).map(item => [item.id, item.name]))
      const rows = allUsers.map(user => [
        user.employeeNo, user.realName, user.email,
        user.departmentId ? sectionsById.get(user.departmentId) ?? user.departmentName ?? '' : '',
        user.roleName ?? '', '', user.status, user.mustChangePassword ? '是' : '否', user.lastLoginAt ?? '',
      ])
      await downloadExcelWorkbook('公司内部账号.xlsx', [
        { name: '数据', headers: [...USER_IMPORT_HEADERS, ...USER_EXPORT_HEADERS], rows },
        ...userWorkbookSheets.slice(1),
      ])
      Message.success(`已导出 ${rows.length} 个账号；导出文件不包含密码`)
    } catch (error) {
      Message.error(error instanceof Error ? error.message : '账号导出失败')
    } finally {
      setExporting(false)
    }
  }

  const importUsers = async (rows: InternalUserImportPayload[]) => {
    const response = await http.post<ApiResponses['POST /admin/users/import']>('/admin/users/import', { rows })
    return response.data as unknown as ImportBatchOutcome
  }
  const saveMutation = useMutation({
    mutationFn: async ({ id, payload }: { id?: number; payload: Record<string, unknown> }) => id
      ? http.put<ApiResponses['PUT /admin/users/{id}']>(`/admin/users/${id}`, payload)
      : http.post<ApiResponses['POST /admin/users']>('/admin/users', payload),
    onSuccess: invalidateUsers,
  }, queryClient)
  const statusMutation = useMutation({
    mutationFn: (u: UserRow) => http.put<ApiResponses['PUT /admin/users/{id}/status']>(
      `/admin/users/${u.id}/status`, { status: u.status === 'ACTIVE' ? 'DISABLED' : 'ACTIVE' },
    ),
    onSuccess: invalidateUsers,
  }, queryClient)
  const batchMutation = useMutation({
    mutationFn: (request: UserBatchUpdate) =>
      http.put<ApiResponses['PUT /admin/users/batch']>('/admin/users/batch', request),
  }, queryClient)
  const deleteMutation = useMutation({
    mutationFn: (u: UserRow) => http.delete<ApiResponses['DELETE /admin/users/{id}']>(`/admin/users/${u.id}`),
    onSuccess: invalidateUsers,
  }, queryClient)
  const passwordMutation = useMutation({
    mutationFn: ({ id, newPassword }: { id: number; newPassword: string }) =>
      http.put<ApiResponses['PUT /admin/users/{id}/password']>(`/admin/users/${id}/password`, { newPassword }),
    onSuccess: invalidateUsers,
  }, queryClient)
  const saving = saveMutation.isPending
  const resettingPassword = passwordMutation.isPending
  const batchPending = batchMutation.isPending
  const batchDisabled = batchPending || selectedIds.length > MAX_BATCH_USERS

  const openBatchEdit = (field: BatchEditField) => {
    setBatchChanges({})
    setBatchField(field)
  }

  const submitBatch = async (changes: BatchChanges) => {
    if (!canManageUsers || batchInFlight.current || batchDisabled || !selectedIds.length) return
    batchInFlight.current = true
    try {
      const { data: result } = await batchMutation.mutateAsync({ userIds: selectedIds, ...changes })
      const succeeded = new Set(result.succeeded)
      setSelectedIds((ids) => ids.filter((id) => !succeeded.has(id)))
      setBatchField(null)
      if (result.failed.length) setBatchFailures(result.failed)
      else Message.success(`已更新 ${result.succeeded.length} 项`)
      void invalidateUsers()
    } catch {
      // 全局请求拦截器已显示错误，保留选择与输入以便重试。
    } finally {
      batchInFlight.current = false
    }
  }

  // 打开编辑时读取该账号的显式覆盖值；加载失败就保持“跟随角色默认”，避免误清空已有设置。
  const loadScopes = async (userId: number) => {
    setScopesLoading(true)
    setScopeLoadedFor(null)
    try {
      const response = await http.get<ApiResponses['GET /admin/users/{id}/data-scopes']>(
        `/admin/users/${userId}/data-scopes`, { quietNetworkError: true } as QuietRequestConfig)
      const rows = response.data as DataScopeResponse[]
      const values: Record<string, string> = {}
      const effective: Record<string, string> = {}
      for (const row of rows) {
        values[row.domain] = row.localScope ?? ''
        effective[row.domain] = row.effectiveScopeLabel
      }
      setScopeValues(values)
      setScopeOriginal(values)
      setScopeEffective(effective)
      setScopeLoadedFor(userId)
    } catch {
      Message.error('数据范围加载失败，本次保存不会修改数据范围')
    } finally {
      setScopesLoading(false)
    }
  }

  const submit = async () => {
    if (saveInFlight.current) return
    saveInFlight.current = true
    try {
      const v = await form.validate().catch(() => null)
      if (!v) return
      const selectedDepartmentId = v.departmentId ? Number(v.departmentId) : 0
      if (!selectedDepartmentId) {
        Message.error('请选择所属组织')
        return
      }
      const selectedRoleId = Number(v.roleId)
      const keepsUnavailableRole = editing?.status === 'DISABLED'
        && editingRoleUnavailable
        && selectedRoleId === editingRoleId
      if (optionsLoading || optionsError || (!roles.some((role) => role.id === selectedRoleId) && !keepsUnavailableRole)) {
        Message.error('组织和角色选项尚未就绪，请重试加载')
        return
      }
      const { roleId, networkScope, ...values } = v
      const payload = {
        ...values,
        // 邮箱可选；留空时提交空字符串（清空已有邮箱），该账号不再收到邮件通知。
        email: typeof values.email === 'string' ? values.email.trim() : '',
        departmentId: selectedDepartmentId,
        roleId: Number(roleId),
      }
      if (editing) {
        // 只提交真正改过的业务域：另一域可能已被他人调整，整份写回会把它恢复成旧值。
        // 范围草稿只在确实加载完成（属于当前编辑对象）时参与比较，避免切换账号时沿用上一人的草稿。
        const changedScopes = scopeLoadedFor === editing.id
          ? SCOPE_DOMAINS
            .filter(({ domain }) => (scopeValues[domain] ?? '') !== (scopeOriginal[domain] ?? ''))
            .map(({ domain }) => ({ domain, value: scopeValues[domain] ?? '' }))
          : []
        if (changedScopes.length > 0) {
          // The range may have changed since the dialog opened (another administrator tightened it).
          // Re-read before writing so an old form cannot silently restore a wider range.
          const latest = await http.get<ApiResponses['GET /admin/users/{id}/data-scopes']>(
            `/admin/users/${editing.id}/data-scopes`, { quietNetworkError: true } as QuietRequestConfig)
          const remote = new Map((latest.data as DataScopeResponse[]).map((row) => [row.domain, row.localScope ?? '']))
          const conflict = changedScopes.some(({ domain }) => (remote.get(domain) ?? '') !== (scopeOriginal[domain] ?? ''))
          if (conflict) {
            Message.error('数据范围已被他人修改，请关闭编辑框后重新打开再保存')
            return
          }
        }
        // 资料与范围同一接口、同一事务提交：任何一步被拒绝都不会留下半状态。
        await saveMutation.mutateAsync({
          id: editing.id,
          payload: {
            realName: payload.realName,
            email: payload.email,
            departmentId: payload.departmentId,
            roleId: payload.roleId,
            ...(networkScope !== (editing.networkScope ?? 'ANY') ? { networkScope } : {}),
            ...(changedScopes.length > 0
              ? { scopes: changedScopes.map(({ domain, value }) => ({ domain, scope: value || null })) }
              : {}),
          },
        })
        Message.success('用户已更新')
      } else {
        // 创建时给出的范围直接写在同一个事务里；留空表示跟随角色默认。
        const newScopes = SCOPE_DOMAINS
          .filter(({ domain }) => scopeValues[domain])
          .map(({ domain }) => ({ domain, scope: scopeValues[domain] }))
        await saveMutation.mutateAsync({
          payload: { ...payload, networkScope, ...(newScopes.length > 0 ? { scopes: newScopes } : {}) },
        })
        Message.success('用户已创建（首次登录需改密）')
      }
      setEditOpen(false)
    } finally {
      saveInFlight.current = false
    }
  }

  const toggle = async (u: UserRow) => {
    await statusMutation.mutateAsync(u)
    Message.success(u.status === 'ACTIVE' ? '用户已停用' : '用户已启用')
  }

  const remove = async (u: UserRow) => {
    await deleteMutation.mutateAsync(u)
    Message.success('用户已删除')
  }

  const resetPwd = async () => {
    if (passwordResetInFlight.current) return
    passwordResetInFlight.current = true
    try {
      const v = await pwdForm.validate().catch(() => null)
      if (!v) return
      await passwordMutation.mutateAsync({ id: resetTarget!.id, newPassword: v.newPassword })
      Message.success('密码已重置，该用户所有登录态已失效')
      setResetTarget(null)
      pwdForm.resetFields()
    } finally {
      passwordResetInFlight.current = false
    }
  }

  const openPasswordReset = (user: UserRow) => {
    pwdForm.resetFields()
    setResetTarget(user)
  }

  const closePasswordReset = () => {
    if (resettingPassword) return
    pwdForm.resetFields()
    setResetTarget(null)
  }

  if (authorizationError) {
    return (
      <Result
        status="403"
        title="用户管理不可用"
        subTitle="当前会话无权读取用户管理数据，请重新登录或联系管理员确认权限。"
      />
    )
  }

  return (
    <Card className="page-card page-card--table">
      <h1 className="visually-hidden">用户管理</h1>
      <div className="page-toolbar responsive-toolbar">
        <Space wrap>
          <Input.Search
            allowClear
            placeholder="账号 / 姓名 / 邮箱"
            style={{ width: 260 }}
            value={searchText}
            onChange={setSearchText}
            onSearch={(v) => {
              const sameQuery = page === 1 && keyword === v
              setPage(1)
              setKeyword(v)
              if (sameQuery) void usersQuery.refetch()
            }}
            onClear={() => {
              const sameQuery = page === 1 && keyword === ''
              setPage(1)
              setKeyword('')
              setSearchText('')
              if (sameQuery) void usersQuery.refetch()
            }}
          />
          {canManageUsers && (
            <TreeSelect
              allowClear
              showSearch
              placeholder="全部组织"
              style={{ width: 180 }}
              treeData={deptTreeData}
              loading={optionsLoading}
              disabled={optionsError}
              filterTreeNode={(input, node) => String(node.props?.title ?? '').toLowerCase().includes(input.trim().toLowerCase())}
              value={departmentId ? String(departmentId) : undefined}
              onChange={(v) => {
                const next = v ? Number(v) : undefined
                const sameQuery = page === 1 && departmentId === next
                setPage(1)
                setDepartmentId(next)
                if (sameQuery) void usersQuery.refetch()
              }}
            />
          )}
          <Select
            allowClear
            placeholder="全部状态"
            style={{ width: 110 }}
            value={status}
            onChange={(v) => {
              const next = v as string | undefined
              const sameQuery = page === 1 && status === next
              setPage(1)
              setStatus(next)
              if (sameQuery) void usersQuery.refetch()
            }}
          >
            <Select.Option value="ACTIVE">启用</Select.Option>
            <Select.Option value="DISABLED">停用</Select.Option>
          </Select>
          {hasActiveFilters && (
            <Button onClick={() => {
              setSearchText('')
              setKeyword('')
              setDepartmentId(undefined)
              setStatus(undefined)
              setPage(1)
            }}>
              重置筛选
            </Button>
          )}
        </Space>
        {canManageUsers && (
          <Space>
            {optionsError && <Button size="small" onClick={() => { void optionsQuery.refetch() }}>重试加载组织和角色</Button>}
            {!optionsLoading && !optionsError && roles.length === 0 && <Typography.Text type="warning">暂无启用的角色</Typography.Text>}
            <Button disabled={optionsLoading || optionsError || roles.length === 0} onClick={() => setImportOpen(true)}>
              导入 Excel
            </Button>
            <Button loading={exporting} disabled={optionsLoading || optionsError} onClick={() => void exportUsers()}>
              导出 Excel
            </Button>
            <Button
              type="primary"
              icon={<IconPlus />}
              disabled={optionsLoading || optionsError || roles.length === 0}
              onClick={() => {
                setEditing(null)
                setScopeValues({})
                setScopeOriginal({})
                setScopeEffective({})
                setScopeLoadedFor(null)
                form.resetFields()
                form.setFieldValue('networkScope', 'ANY')
                setEditOpen(true)
              }}
            >
              新增用户
            </Button>
          </Space>
        )}
      </div>
      {canManageUsers && selectedIds.length > 0 && (
        <div className="page-toolbar responsive-toolbar">
          <Space wrap>
            <Typography.Text>已选 {selectedIds.length} 项</Typography.Text>
            <Button type="text" size="small" disabled={batchPending} onClick={() => setSelectedIds([])}>取消选择</Button>
            {selectedIds.length > MAX_BATCH_USERS && <Typography.Text type="error">每次最多操作 100 项</Typography.Text>}
            <Button size="small" disabled={batchDisabled || optionsLoading || optionsError || roles.length === 0} onClick={() => openBatchEdit('roleId')}>
              {BATCH_EDIT_LABELS.roleId}
            </Button>
            <Button size="small" disabled={batchDisabled} onClick={() => openBatchEdit('networkScope')}>
              {BATCH_EDIT_LABELS.networkScope}
            </Button>
            <Button size="small" disabled={batchDisabled || optionsLoading || optionsError || activeOrganizations.length === 0} onClick={() => openBatchEdit('departmentId')}>
              {BATCH_EDIT_LABELS.departmentId}
            </Button>
            <DangerPopconfirm title={`启用所选 ${selectedIds.length} 个账号？`} okText="确认启用" onOk={() => submitBatch({ status: 'ACTIVE' })}>
              <Button size="small" disabled={batchDisabled}>启用</Button>
            </DangerPopconfirm>
            <DangerPopconfirm title={`停用所选 ${selectedIds.length} 个账号？`} okText="确认停用" onOk={() => submitBatch({ status: 'DISABLED' })}>
              <Button size="small" status="danger" disabled={batchDisabled}>停用</Button>
            </DangerPopconfirm>
          </Space>
        </div>
      )}
      {refreshError && (
        <div role="status" style={{ display: 'flex', alignItems: 'center', gap: 8, paddingBottom: 8 }}>
          <Typography.Text type="error">加载失败</Typography.Text>
          <Typography.Text type="secondary">当前显示上次加载的数据</Typography.Text>
          <Button size="small" onClick={() => { void usersQuery.refetch() }}>重试</Button>
        </div>
      )}
      {loadError ? (
        <LoadError onRetry={() => { void usersQuery.refetch() }} />
      ) : (
        <ResizableTable tableId="users"
          className="page-table"
          rowKey="id"
          rowSelection={canManageUsers ? {
            type: 'checkbox',
            selectedRowKeys: selectedIds,
            checkCrossPage: true,
            preserveSelectedRowKeys: true,
            onChange: (keys) => { if (!batchInFlight.current) setSelectedIds(keys.map(Number)) },
            checkboxProps: (row) => ({ disabled: batchPending, 'aria-label': `选择 ${row.employeeNo}` }),
          } : undefined}
          loading={loading}
          data={data.list}
          scroll={{ x: 1060, y: 'var(--page-table-scroll-y)' }}
          columns={[
          { title: '账号', dataIndex: 'employeeNo', width: 110, ellipsis: true },
          { title: '姓名', dataIndex: 'realName', width: 110, align: 'left' as const, ellipsis: true },
          {
            title: '组织',
            dataIndex: 'departmentName',
            width: 140,
            align: 'left' as const,
            render: (v?: string) => <span className="table-cell-wrap">{v || '-'}</span>,
          },
          {
            title: '角色',
            dataIndex: 'roleName',
            width: 150,
            render: (v?: string) => (v ? <Tag size="small" className="table-cell-wrap">{v}</Tag> : '-'),
          },
          { title: '邮箱', dataIndex: 'email', width: 220, align: 'left' as const, ellipsis: true, render: (v?: string) => v || '-' },
          {
            title: '访问范围', dataIndex: 'networkScope', width: 112,
            render: (value: NetworkScope) => <Tag size="small" color={value === 'INTERNAL_ONLY' ? 'orange' : undefined}>
              {NETWORK_SCOPE_LABELS[value ?? 'ANY']}
            </Tag>,
          },
          {
            title: '状态',
            dataIndex: 'status',
            width: 72,
            align: 'center' as const,
            render: (v: string) => <StatusTag status={v} />,
          },
          { title: '最近登录', dataIndex: 'lastLoginAt', width: 160, render: fmtTime },
          {
            title: '操作',
            width: 200,
            fixed: 'right' as const,
            render: (_: unknown, r: UserRow) => actionSlots([
                canManageUsers && <Button
                  key="edit"
                  size="mini"
                  type="text"
                  onClick={() => {
                    // 切换编辑对象时清掉上一人的范围草稿：加载失败时旧草稿不能被当成本次编辑的输入。
                    setScopeValues({})
                    setScopeOriginal({})
                    setScopeEffective({})
                    setScopeLoadedFor(null)
                    setEditing(r)
                    void loadScopes(r.id)
                    form.setFieldsValue({
                      realName: r.realName,
                      email: r.email,
                      departmentId: r.departmentId ? String(r.departmentId) : undefined,
                      roleId: r.roleId ?? r.roleIds?.[0],
                      networkScope: r.networkScope ?? 'ANY',
                    })
                    setEditOpen(true)
                  }}
                >
                  编辑
                </Button>,
                canManageUsers && <Button key="reset-password" size="mini" type="text" onClick={() => openPasswordReset(r)}>
                  重置密码
                </Button>,
                // 内置管理员和自己的账号不能停用或删除：按钮保留但不可用，各行操作上下对齐。
                canManageUsers && ((r.employeeNo === 'admin' || r.id === me?.id) ? (
                  <Tooltip key="status" content={r.employeeNo === 'admin' ? '内置管理员不可停用' : '当前登录账号不可停用'}>
                    <Button size="mini" type="text" status={r.status === 'ACTIVE' ? 'danger' : 'success'} disabled>
                      {r.status === 'ACTIVE' ? '停用' : '启用'}
                    </Button>
                  </Tooltip>
                ) : r.status === 'ACTIVE' ? (
                  <DangerPopconfirm key="status" title="停用后立即无法登录，确认？" onOk={() => toggle(r)}>
                    <Button size="mini" type="text" status="danger">停用</Button>
                  </DangerPopconfirm>
                ) : (
                  <Popconfirm key="status" title="确认启用？" onOk={() => toggle(r)}>
                    <Button size="mini" type="text" status="success">启用</Button>
                  </Popconfirm>
                )),
                canDelete && ((r.employeeNo === 'admin' || r.id === me?.id) ? (
                  <Tooltip key="delete" content={r.employeeNo === 'admin' ? '内置管理员不可删除' : '当前登录账号不可删除'}>
                    <Button size="mini" type="text" status="danger" disabled>删除</Button>
                  </Tooltip>
                ) : (
                  <DangerPopconfirm key="delete"
                    title="删除后不可恢复；存在历史业务记录时请改为停用。确认删除该用户？" onOk={() => remove(r)}>
                    <Button size="mini" type="text" status="danger">删除</Button>
                  </DangerPopconfirm>
                )),
              ], 'user'),
          },
          ]}
          pagination={{
            total: data.total,
            current: page,
            pageSize,
            onChange: (p, ps) => {
              const sameQuery = page === p && pageSize === ps
              setPage(p)
              setPageSize(ps)
              if (sameQuery) void usersQuery.refetch()
            },
          }}
        />
      )}

      <Modal
        title={batchField ? BATCH_EDIT_LABELS[batchField] : ''}
        visible={canManageUsers && batchField !== null}
        style={{ width: 360, maxWidth: 'calc(100vw - 32px)' }}
        unmountOnExit
        confirmLoading={batchPending}
        closable={!batchPending}
        maskClosable={!batchPending}
        escToExit={!batchPending}
        okButtonProps={{ disabled: !batchField || batchChanges[batchField] == null || batchDisabled }}
        cancelButtonProps={{ disabled: batchPending }}
        onOk={() => submitBatch(batchChanges)}
        onCancel={() => { if (!batchInFlight.current) setBatchField(null) }}
      >
        {batchField === 'roleId' && (
          <Select showSearch placeholder="选择角色" value={batchChanges.roleId ?? undefined} onChange={(roleId) => setBatchChanges({ roleId })}>
            {roles.map((role) => <Select.Option key={role.id} value={role.id}>{role.name}</Select.Option>)}
          </Select>
        )}
        {batchField === 'networkScope' && (
          <Radio.Group aria-label="访问范围" value={batchChanges.networkScope} onChange={(networkScope) => setBatchChanges({ networkScope })}>
            <Radio value="INTERNAL_ONLY">{NETWORK_SCOPE_LABELS.INTERNAL_ONLY}</Radio>
            <Radio value="ANY">{NETWORK_SCOPE_LABELS.ANY}</Radio>
          </Radio.Group>
        )}
        {batchField === 'departmentId' && (
          <TreeSelect
            showSearch
            placeholder="选择组织"
            treeData={batchDeptTreeData}
            value={batchChanges.departmentId?.toString()}
            onChange={(id) => setBatchChanges({ departmentId: Number(id) })}
            filterTreeNode={(input, node) => String(node.props?.title ?? '').toLowerCase().includes(input.trim().toLowerCase())}
          />
        )}
      </Modal>
      <Modal
        className="form-dialog"
        title={`操作失败（${batchFailures.length} 项）`}
        visible={canManageUsers && batchFailures.length > 0}
        unmountOnExit
        footer={<Button type="primary" onClick={() => setBatchFailures([])}>关闭</Button>}
        onCancel={() => setBatchFailures([])}
      >
        <ul style={{ maxHeight: 'min(50vh, 360px)', overflowY: 'auto', paddingInlineStart: 20, margin: 0 }}>
          {batchFailures.map(({ id, employeeNo, message }) => (
            <li key={id}><strong>{employeeNo ?? `#${id}`}</strong>：{message}</li>
          ))}
        </ul>
      </Modal>

      <ExcelImportDialog<InternalUserImportPayload>
        visible={importOpen}
        title="导入公司内部账号"
        templateName="公司内部账号导入模板.xlsx"
        requiredHeaders={USER_IMPORT_HEADERS}
        optionalHeaders={USER_EXPORT_HEADERS}
        headerAliases={USER_IMPORT_HEADER_ALIASES}
        templateSheets={userWorkbookSheets}
        maxRows={100}
        prepare={prepareUserImport}
        commit={importUsers}
        onImported={() => void invalidateUsers()}
        onClose={() => setImportOpen(false)}
      />

      <Modal
        className="form-dialog"
        title={editing ? '编辑用户' : '新增用户'}
        visible={editOpen}
        unmountOnExit
        confirmLoading={saving}
        closable={!saving}
        maskClosable={!saving}
        escToExit={!saving}
        cancelButtonProps={{ disabled: saving }}
        okText={editing ? '保存用户' : '创建用户'}
        onOk={submit}
        onCancel={() => { if (!saving) setEditOpen(false) }}
      >
        <Form onSubmit={() => void submit()} form={form} layout="vertical">
          <div className="form-grid">
            {!editing && (
              <>
                <Form.Item label="账号" field="employeeNo" rules={[{ required: true, message: '请输入账号' }, { match: ACCOUNT_NAME_PATTERN, message: ACCOUNT_NAME_MESSAGE }]}>
                  <Input autoFocus placeholder={ACCOUNT_NAME_PLACEHOLDER} />
                </Form.Item>
                <Form.Item
                  label="初始密码"
                  field="password"
                  rules={[{ required: true, message: '请输入初始密码' }, passwordRule]}
                >
                  <PasswordInput placeholder="5-20 位" />
                </Form.Item>
              </>
            )}
            <Form.Item label="姓名" field="realName" rules={[{ required: true, message: '请输入姓名' }, textLengthRule('姓名', 32)]}>
              <Input placeholder="姓名" />
            </Form.Item>
            <Form.Item label="邮箱" field="email" rules={[{ type: 'email', message: '邮箱格式不正确' }, optionalTextLengthRule('邮箱', 128)]}>
              <Input placeholder="name@example.com" />
            </Form.Item>
            <Form.Item label="所属组织" field="departmentId" rules={[{ required: true, message: '请选择所属组织' }]}>
              <TreeSelect
                showSearch
                placeholder="选择组织"
                treeData={formDeptTreeData}
                loading={optionsLoading}
                disabled={optionsError}
                filterTreeNode={(input, node) => String(node.props?.title ?? '').toLowerCase().includes(input.trim().toLowerCase())}
              />
            </Form.Item>
            <Form.Item label="角色" field="roleId" rules={[{ required: true, message: '请选择角色' }]}>
              <Select showSearch placeholder="选择角色" loading={optionsLoading} disabled={optionsError}>
                {editingRoleUnavailable && (
                  <Select.Option key={editingRoleId} value={editingRoleId} disabled>
                    {editing?.roleName || `角色 #${editingRoleId}`}（已停用）
                  </Select.Option>
                )}
                {roles.map((r) => (
                  <Select.Option key={r.id} value={r.id}>
                    {r.name}
                  </Select.Option>
                ))}
              </Select>
            </Form.Item>
            <Form.Item className="form-grid-full" label="访问范围" field="networkScope">
              <Radio.Group aria-label="访问范围">
                <Radio value="INTERNAL_ONLY">{NETWORK_SCOPE_LABELS.INTERNAL_ONLY}</Radio>
                <Radio value="ANY">{NETWORK_SCOPE_LABELS.ANY}</Radio>
              </Radio.Group>
            </Form.Item>
            {canManageUsers && SCOPE_DOMAINS.map(({ domain, label }) => (
              <Form.Item key={domain} label={label}>
                <Select
                  value={scopeValues[domain] ?? ''}
                  loading={scopesLoading}
                  disabled={!canManageUsers || scopesLoading || (editing !== null && scopeLoadedFor !== editing.id)}
                  onChange={(value) => setScopeValues((prev) => ({ ...prev, [domain]: value }))}
                >
                  <Select.Option value="">
                    跟随角色默认{scopeEffective[domain] ? `（当前：${scopeEffective[domain]}）` : ''}
                  </Select.Option>
                  <Select.Option value="SELF">{SCOPE_LABELS.SELF}</Select.Option>
                  <Select.Option value="ORG">{SCOPE_LABELS.ORG}</Select.Option>
                  <Select.Option value="ALL">{SCOPE_LABELS.ALL}</Select.Option>
                </Select>
              </Form.Item>
            ))}
          </div>
          {editing && (
            <div className="dialog-note">
              数据范围决定能看到谁的项目/传递单；“全部”只扩大可见范围，不会让账号获得编辑他人数据的权限。
            </div>
          )}
          {!editing && <div className="dialog-note">新用户首次登录时需要修改初始密码。</div>}
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
          <div className="dialog-note">重置后，该用户的全部登录会话将立即退出</div>
        <EnterSubmit /></Form>
      </Modal>
    </Card>
  )
}
