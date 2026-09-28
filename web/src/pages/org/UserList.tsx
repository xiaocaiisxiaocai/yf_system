import { textLengthRule } from '../../utils/textRules'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import {
  Button, Card, Form, Input, Message, Modal, Popconfirm, Result, Select, Space, Table, Tag, TreeSelect, Typography,
} from '@arco-design/web-react'
import { IconPlus } from '@arco-design/web-react/icon'
import http, { type QuietRequestConfig } from '../../api/client'
import { createSessionQueryScope, queryClient } from '../../api/queryClient'
import { actionSlots } from '../../components/ActionSlots'
import PasswordInput from '../../components/PasswordInput'
import { useShallow } from 'zustand/react/shallow'
import { useAuth } from '../../store/auth'
import { type PageResp, fmtTime } from '../../api/types'
import { passwordRule } from '../../utils/password'
import type { ApiResponses } from '../../api/types'
import type { DepartmentTreeNode as ApiDepartmentTreeNode } from '../../api/generated/api-types'

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
  lastLoginAt?: string | null
  createdAt: string
}

interface RoleOpt {
  id: number
  name: string
}

// Department tree from the API, with kind narrowed to the fixed three-level hierarchy.
type DeptNode = Omit<ApiDepartmentTreeNode, 'kind' | 'children'> & {
  kind: 'DIVISION' | 'DEPARTMENT' | 'SECTION'
  children: DeptNode[]
}

const ORG_KIND_LABEL = { DIVISION: '事业部', DEPARTMENT: '部门', SECTION: '课别' } as const

interface DeptTreeData { key: string; title: string; value: string; children?: DeptTreeData[] }
function toTreeData(nodes: DeptNode[]): DeptTreeData[] {
  return nodes.map((n) => ({
    key: String(n.id),
    title: n.kind && ORG_KIND_LABEL[n.kind] ? `${n.name}（${ORG_KIND_LABEL[n.kind]}）` : n.name,
    value: String(n.id),
    children: n.children && n.children.length ? toTreeData(n.children) : undefined,
  }))
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
  const roles = authorizationError ? [] : optionsQuery.data?.roles ?? []
  const optionsLoading = optionsQuery.isFetching
  const optionsError = optionsQuery.isError && !optionsQuery.isFetching
  const loadedDepts = authorizationError ? undefined : optionsQuery.data?.depts
  const deptTreeData = useMemo(() => toTreeData(loadedDepts ?? []), [loadedDepts])
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
    Message.success(u.status === 'ACTIVE' ? '用户已禁用' : '用户已启用')
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
      <div className="page-heading">
        <div>
          <h1>用户管理</h1>
        </div>
      </div>
      <div className="page-toolbar responsive-toolbar">
        <Space wrap>
          <Input.Search
            allowClear
            placeholder="工号 / 姓名 / 邮箱"
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
            <Select.Option value="DISABLED">禁用</Select.Option>
          </Select>
        </Space>
        <Space>
          {optionsError && <Button size="small" onClick={() => { void optionsQuery.refetch() }}>重试加载组织和角色</Button>}
          {!optionsLoading && !optionsError && roles.length === 0 && <Typography.Text type="warning">暂无启用的角色</Typography.Text>}
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
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '24px 0' }}>
          <Typography.Text type="error">加载失败</Typography.Text>
          <Button size="small" onClick={() => { void usersQuery.refetch() }}>重试</Button>
        </div>
      ) : (
        <Table
          className="page-table"
          rowKey="id"
          loading={loading}
          data={data.list}
          scroll={{ x: 1204, y: 'var(--page-table-scroll-y)' }}
          columns={[
          { title: '工号', dataIndex: 'employeeNo', width: 130, align: 'center' as const, ellipsis: true },
          { title: '姓名', dataIndex: 'realName', width: 100, align: 'center' as const, ellipsis: true },
          {
            title: '组织',
            dataIndex: 'departmentName',
            width: 150,
            render: (v?: string) => <span style={{ display: 'block', whiteSpace: 'normal', overflowWrap: 'anywhere', lineHeight: '20px' }}>{v || '-'}</span>,
          },
          {
            title: '角色',
            dataIndex: 'roleName',
            width: 180,
            render: (v?: string) => (v ? <Tag size="small" style={{ height: 'auto', whiteSpace: 'normal', overflowWrap: 'anywhere', lineHeight: '20px' }}>{v}</Tag> : '-'),
          },
          { title: '邮箱', dataIndex: 'email', width: 130, ellipsis: true },
          {
            title: '状态',
            dataIndex: 'status',
            width: 70,
            align: 'center' as const,
            render: (v: string) => (v === 'ACTIVE' ? <Tag color="green">启用</Tag> : <Tag color="red">禁用</Tag>),
          },
          { title: '最近登录', dataIndex: 'lastLoginAt', width: 180, align: 'center' as const, render: fmtTime },
          {
            title: '操作',
            width: 264,
            fixed: 'right' as const,
            align: 'center' as const,
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
                r.employeeNo !== 'admin' && (
                  <Popconfirm key="status" title={r.status === 'ACTIVE' ? '禁用后立即无法登录，确认？' : '确认启用？'} onOk={() => toggle(r)}>
                    <Button size="mini" type="text" status={r.status === 'ACTIVE' ? 'danger' : 'success'}>
                      {r.status === 'ACTIVE' ? '禁用' : '启用'}
                    </Button>
                  </Popconfirm>
                ),
                canDelete && r.employeeNo !== 'admin' && r.id !== me?.id && (
                  <Popconfirm key="delete" title="删除后不可恢复；存在历史业务记录时请改为禁用。确认删除该用户？" onOk={() => remove(r)}>
                    <Button size="mini" type="text" status="danger">删除</Button>
                  </Popconfirm>
                ),
              ], 'user'),
          },
          ]}
          pagination={{
            total: data.total,
            current: page,
            pageSize,
            showTotal: true,
            sizeCanChange: true,
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
              </>
            )}
            <Form.Item label="姓名" field="realName" rules={[{ required: true, message: '请输入姓名' }, textLengthRule('姓名', 32)]}>
              <Input placeholder="姓名" />
            </Form.Item>
            <Form.Item label="邮箱" field="email" rules={[{ required: true, message: '请输入邮箱' }, { type: 'email', message: '邮箱格式不正确' }, textLengthRule('邮箱', 128)]}>
              <Input placeholder="name@example.com" />
            </Form.Item>
            <Form.Item label="所属组织" field="departmentId" rules={[{ required: true, message: '请选择所属组织' }]}>
              <TreeSelect placeholder="选择组织" treeData={deptTreeData} loading={optionsLoading} disabled={optionsError} />
            </Form.Item>
            <Form.Item label="角色" field="roleId" rules={[{ required: true, message: '请选择角色' }]}>
              <Select showSearch placeholder="选择角色" loading={optionsLoading} disabled={optionsError}>
                {editingRoleUnavailable && (
                  <Select.Option key={editingRoleId} value={editingRoleId} disabled>
                    {editing?.roleName || `角色 #${editingRoleId}`}（已禁用）
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
          <div className="dialog-note">重置后将退出当前登录</div>
        </Form>
      </Modal>
    </Card>
  )
}
