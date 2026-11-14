import { optionalTextLengthRule, textLengthRule } from '../../utils/textRules'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import {
  Button, Card, Form, Input, Message, Modal, Popconfirm, Result, Select, Space, Tag, TreeSelect, Typography,
} from '@arco-design/web-react'
import ResizableTable from '../../components/ResizableTable'
import { IconPlus } from '@arco-design/web-react/icon'
import http, { type QuietRequestConfig } from '../../api/client'
import { createSessionQueryScope, queryClient } from '../../api/queryClient'
import { actionSlots } from '../../components/ActionSlots'
import PasswordInput from '../../components/PasswordInput'
import { useShallow } from 'zustand/react/shallow'
import { useAuth } from '../../store/auth'
import { type PageResp, fmtTime } from '../../api/types'
import { passwordRule, validateInitialPassword } from '../../utils/password'
import type { ApiResponses } from '../../api/types'
import type { DepartmentTreeNode as ApiDepartmentTreeNode } from '../../api/generated/api-types'
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
  mustChangePassword?: boolean
  lastLoginAt?: string | null
  createdAt: string
}

interface RoleOpt {
  id: number
  name: string
}

const EMPTY_ROLE_OPTIONS: RoleOpt[] = []

// Department tree from the API, with kind narrowed to the fixed three-level hierarchy.
type DeptNode = Omit<ApiDepartmentTreeNode, 'kind' | 'children'> & {
  kind: 'DIVISION' | 'DEPARTMENT' | 'SECTION'
  children: DeptNode[]
}

const ORG_KIND_LABEL = { DIVISION: '事业部', DEPARTMENT: '部门', SECTION: '课别' } as const
const USER_IMPORT_HEADERS = ['账号', '姓名', '邮箱', '所属组织', '角色', '初始密码'] as const
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

interface DeptTreeData { key: string; title: string; value: string; children?: DeptTreeData[] }
function toTreeData(nodes: DeptNode[]): DeptTreeData[] {
  return nodes.map((n) => ({
    key: String(n.id),
    title: n.kind && ORG_KIND_LABEL[n.kind] ? `${n.name}（${ORG_KIND_LABEL[n.kind]}）` : n.name,
    value: String(n.id),
    children: n.children && n.children.length ? toTreeData(n.children) : undefined,
  }))
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
  const [sessionUserId, sessionGeneration, sessionGrants] = createSessionQueryScope(auth)
  const sessionScope = useMemo(
    () => [sessionUserId, sessionGeneration, sessionGrants] as const,
    [sessionGeneration, sessionGrants, sessionUserId],
  )
  const me = auth.user
  const [keyword, setKeyword] = useState('')
  const [departmentId, setDepartmentId] = useState<number>()
  const [status, setStatus] = useState<string>()
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(10)
  const [editOpen, setEditOpen] = useState(false)
  const [importOpen, setImportOpen] = useState(false)
  const [exporting, setExporting] = useState(false)
  const [editing, setEditing] = useState<UserRow | null>(null)
  const [resetTarget, setResetTarget] = useState<UserRow | null>(null)
  const saveInFlight = useRef(false)
  const passwordResetInFlight = useRef(false)
  const [form] = Form.useForm()
  const [pwdForm] = Form.useForm()
  const usersQueryKey = useMemo(() => ['admin-users', sessionScope] as const, [sessionScope])
  const optionsQueryKey = useMemo(() => ['admin-user-options', sessionScope] as const, [sessionScope])

  const usersQuery = useQuery({
    queryKey: [...usersQueryKey, { page, pageSize, keyword, departmentId, status }],
    queryFn: async ({ signal }) => {
      const response = await http.get<ApiResponses['GET /admin/users']>('/admin/users', {
        params: { page, pageSize, keyword: keyword || undefined, departmentId, status }, signal,
      })
      return response.data as PageResp<UserRow>
    },
  }, queryClient)
  const optionsQuery = useQuery({
    queryKey: optionsQueryKey,
    queryFn: async ({ signal }) => {
      const config: QuietRequestConfig = { signal, quietNetworkError: true }
      const [departmentsResponse, rolesResponse] = await Promise.all([
        http.get<ApiResponses['GET /departments']>('/departments', config),
        http.get<ApiResponses['GET /admin/user-role-options']>('/admin/user-role-options', config),
      ])
      return { depts: departmentsResponse.data as DeptNode[], roles: rolesResponse.data as RoleOpt[] }
    },
  }, queryClient)

  const authorizationError = isAuthorizationError(usersQuery.error) || isAuthorizationError(optionsQuery.error)
  const data = !authorizationError && usersQuery.data ? usersQuery.data : { list: [], total: 0, page, pageSize }
  const loading = usersQuery.isFetching
  // 只有在还没有任何数据时才用错误视图替换表格；后台刷新失败保留上次加载的列表并在上方提示。
  const usersFailed = usersQuery.isError && !usersQuery.isFetching
  const loadError = usersFailed && (authorizationError || !usersQuery.data)
  const refreshError = usersFailed && !loadError
  const roles = authorizationError ? EMPTY_ROLE_OPTIONS : optionsQuery.data?.roles ?? EMPTY_ROLE_OPTIONS
  const optionsLoading = optionsQuery.isFetching
  const optionsError = optionsQuery.isError && !optionsQuery.isFetching
  const loadedDepts = authorizationError ? undefined : optionsQuery.data?.depts
  const deptTreeData = useMemo(() => toTreeData(loadedDepts ?? []), [loadedDepts])
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
      { name: '数据', headers: [...USER_IMPORT_HEADERS], rows: [] },
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
      const { roleId, ...values } = v
      const payload = {
        ...values,
        // 邮箱可选；留空时提交空字符串（清空已有邮箱），该账号不再收到邮件通知。
        email: typeof values.email === 'string' ? values.email.trim() : '',
        departmentId: selectedDepartmentId,
        roleId: Number(roleId),
      }
      if (editing) {
        // 资料与角色同一接口事务提交，避免两次 PUT 的半失败
        await saveMutation.mutateAsync({
          id: editing.id,
          payload: {
            realName: payload.realName,
            email: payload.email,
            departmentId: payload.departmentId,
            roleId: payload.roleId,
          },
        })
        Message.success('用户已更新')
      } else {
        await saveMutation.mutateAsync({ payload })
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
              if (sameQuery) void usersQuery.refetch()
            }}
          />
          <TreeSelect
            allowClear
            placeholder="全部组织"
            style={{ width: 180 }}
            treeData={deptTreeData}
            loading={optionsLoading}
            disabled={optionsError}
            value={departmentId ? String(departmentId) : undefined}
            onChange={(v) => {
              const next = v ? Number(v) : undefined
              const sameQuery = page === 1 && departmentId === next
              setPage(1)
              setDepartmentId(next)
              if (sameQuery) void usersQuery.refetch()
            }}
          />
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
        </Space>
        <Space>
          {optionsError && <Button size="small" onClick={() => { void optionsQuery.refetch() }}>重试加载组织和角色</Button>}
          {!optionsLoading && !optionsError && roles.length === 0 && <Typography.Text type="warning">暂无启用的角色</Typography.Text>}
          <Button size="small" disabled={optionsLoading || optionsError || roles.length === 0} onClick={() => setImportOpen(true)}>
            导入 Excel
          </Button>
          <Button size="small" loading={exporting} disabled={optionsLoading || optionsError} onClick={() => void exportUsers()}>
            导出 Excel
          </Button>
          <Button
            type="primary"
            icon={<IconPlus />}
            disabled={optionsLoading || optionsError || roles.length === 0}
            onClick={() => {
              setEditing(null)
              form.resetFields()
              setEditOpen(true)
            }}
          >
            新增用户
          </Button>
        </Space>
      </div>
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
          loading={loading}
          data={data.list}
          scroll={{ x: 1060, y: 'var(--page-table-scroll-y)' }}
          columns={[
          { title: '账号', dataIndex: 'employeeNo', width: 110, ellipsis: true },
          { title: '姓名', dataIndex: 'realName', width: 110, ellipsis: true },
          {
            title: '组织',
            dataIndex: 'departmentName',
            width: 140,
            render: (v?: string) => <span className="table-cell-wrap">{v || '-'}</span>,
          },
          {
            title: '角色',
            dataIndex: 'roleName',
            width: 150,
            render: (v?: string) => (v ? <Tag size="small" className="table-cell-wrap">{v}</Tag> : '-'),
          },
          { title: '邮箱', dataIndex: 'email', width: 220, ellipsis: true, render: (v?: string) => v || '-' },
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
                <Button
                  key="edit"
                  size="mini"
                  type="text"
                  onClick={() => {
                    setEditing(r)
                    form.setFieldsValue({
                      realName: r.realName,
                      email: r.email,
                      departmentId: r.departmentId ? String(r.departmentId) : undefined,
                      roleId: r.roleId ?? r.roleIds?.[0],
                    })
                    setEditOpen(true)
                  }}
                >
                  编辑
                </Button>,
                <Button key="reset-password" size="mini" type="text" onClick={() => openPasswordReset(r)}>
                  重置密码
                </Button>,
                // 内置管理员和自己的账号不能停用或删除：按钮保留但不可用，各行操作上下对齐。
                <Popconfirm key="status" disabled={r.employeeNo === 'admin' || r.id === me?.id}
                  title={r.status === 'ACTIVE' ? '停用后立即无法登录，确认？' : '确认启用？'} onOk={() => toggle(r)}>
                  <Button size="mini" type="text" status={r.status === 'ACTIVE' ? 'danger' : 'success'} disabled={r.employeeNo === 'admin' || r.id === me?.id}>
                    {r.status === 'ACTIVE' ? '停用' : '启用'}
                  </Button>
                </Popconfirm>,
                canDelete && (
                  <Popconfirm key="delete" disabled={r.employeeNo === 'admin' || r.id === me?.id}
                    title="删除后不可恢复；存在历史业务记录时请改为停用。确认删除该用户？" onOk={() => remove(r)}>
                    <Button size="mini" type="text" status="danger" disabled={r.employeeNo === 'admin' || r.id === me?.id}>删除</Button>
                  </Popconfirm>
                ),
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
              <TreeSelect placeholder="选择组织" treeData={deptTreeData} loading={optionsLoading} disabled={optionsError} />
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
          </div>
          {!editing && <div className="dialog-note">新用户首次登录时需要修改初始密码。</div>}
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
          <div className="dialog-note">重置后，该用户的全部登录会话将立即退出</div>
        <EnterSubmit /></Form>
      </Modal>
    </Card>
  )
}
