import { useCallback, useEffect, useState } from 'react'
import {
  Button, Card, Form, Input, Message, Modal, Popconfirm, Select, Space, Table, Tag, TreeSelect, Typography,
} from '@arco-design/web-react'
import { IconPlus } from '@arco-design/web-react/icon'
import http from '../../api/client'
import { actionSlots } from '../../components/ActionSlots'
import { useAuth } from '../../store/auth'
import { type PageResp, fmtTime } from '../../api/types'

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

interface DeptNode {
  id: number
  name: string
  kind?: 'DIVISION' | 'DEPARTMENT' | 'SECTION'
  children?: DeptNode[]
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

export default function UserList() {
  const me = useAuth((s) => s.user)
  const canDelete = useAuth((s) => s.hasPerm('user:delete'))
  const [data, setData] = useState<PageResp<UserRow>>({ list: [], total: 0, page: 1, pageSize: 10 })
  const [loading, setLoading] = useState(true)
  const [reloadKey, setReloadKey] = useState(0)
  const [keyword, setKeyword] = useState('')
  const [departmentId, setDepartmentId] = useState<number>()
  const [status, setStatus] = useState<string>()
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(10)
  const [loadError, setLoadError] = useState(false)
  const [editOpen, setEditOpen] = useState(false)
  const [editing, setEditing] = useState<UserRow | null>(null)
  const [resetTarget, setResetTarget] = useState<UserRow | null>(null)
  const [depts, setDepts] = useState<DeptNode[]>([])
  const [roles, setRoles] = useState<RoleOpt[]>([])
  const [form] = Form.useForm()
  const [pwdForm] = Form.useForm()

  const fetchUsers = useCallback(async () => {
    const r = await http.get('/admin/users', { params: { page, pageSize, keyword: keyword || undefined, departmentId, status } })
    return r.data as PageResp<UserRow>
  }, [page, pageSize, keyword, departmentId, status])

  const load = useCallback(() => {
    setLoading(true)
    setLoadError(false)
    setReloadKey((value) => value + 1)
  }, [])

  useEffect(() => {
    let active = true
    fetchUsers()
      .then((next) => {
        if (active) {
          setData(next)
          setLoadError(false)
        }
      })
      .catch(() => {
        if (active) setLoadError(true)
      })
      .finally(() => {
        if (active) setLoading(false)
      })
    return () => {
      active = false
    }
  }, [fetchUsers, reloadKey])

  useEffect(() => {
    http.get('/departments').then((r) => setDepts(r.data))
    http.get('/admin/user-role-options').then((r) => setRoles(r.data))
  }, [])

  const submit = async () => {
    const v = await form.validate().catch(() => null)
    if (!v) return
    const { roleId, ...values } = v
    const payload = {
      ...values,
      departmentId: v.departmentId ? Number(v.departmentId) : null,
      roleId: Number(roleId),
    }
    if (editing) {
      // 资料与角色同一接口事务提交，避免两次 PUT 的半失败
      await http.put(`/admin/users/${editing.id}`, {
        realName: payload.realName,
        email: payload.email,
        departmentId: payload.departmentId,
        roleId: payload.roleId,
      })
      Message.success('用户已更新')
    } else {
      await http.post('/admin/users', payload)
      Message.success('用户已创建（首次登录需改密）')
    }
    setEditOpen(false)
    load()
  }

  const toggle = async (u: UserRow) => {
    await http.put(`/admin/users/${u.id}/status`, { status: u.status === 'ACTIVE' ? 'DISABLED' : 'ACTIVE' })
    load()
  }

  const remove = async (u: UserRow) => {
    await http.delete(`/admin/users/${u.id}`)
    Message.success('用户已删除')
    load()
  }

  const resetPwd = async () => {
    const v = await pwdForm.validate().catch(() => null)
    if (!v) return
    await http.put(`/admin/users/${resetTarget!.id}/password`, { newPassword: v.newPassword })
    Message.success('密码已重置，该用户所有登录态已失效')
    setResetTarget(null)
    pwdForm.resetFields()
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
              setLoading(true); setLoadError(false); setReloadKey((value) => value + 1)
              setPage(1)
              setKeyword(v)
            }}
            onClear={() => {
              setLoading(true); setLoadError(false); setReloadKey((value) => value + 1)
              setPage(1)
              setKeyword('')
            }}
          />
          <TreeSelect
            allowClear
            placeholder="全部组织"
            style={{ width: 180 }}
            treeData={toTreeData(depts)}
            value={departmentId ? String(departmentId) : undefined}
            onChange={(v) => { setLoading(true); setLoadError(false); setReloadKey((value) => value + 1); setPage(1); setDepartmentId(v ? Number(v) : undefined) }}
          />
          <Select
            allowClear
            placeholder="全部状态"
            style={{ width: 110 }}
            value={status}
            onChange={(v) => { setLoading(true); setLoadError(false); setReloadKey((value) => value + 1); setPage(1); setStatus(v as string | undefined) }}
          >
            <Select.Option value="ACTIVE">启用</Select.Option>
            <Select.Option value="DISABLED">禁用</Select.Option>
          </Select>
        </Space>
        <Button
          type="primary"
          icon={<IconPlus />}
          onClick={() => {
            setEditing(null)
            form.resetFields()
            setEditOpen(true)
          }}
        >
          新增用户
        </Button>
      </div>
      {loadError ? (
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '24px 0' }}>
          <Typography.Text type="error">加载失败</Typography.Text>
          <Button size="small" onClick={load}>重试</Button>
        </div>
      ) : (
        <Table
          className="page-table"
          rowKey="id"
          loading={loading}
          data={data.list}
          scroll={{ x: 1160, y: 'var(--page-table-scroll-y)' }}
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
                <Button key="reset-password" size="mini" type="text" onClick={() => setResetTarget(r)}>
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
              setLoading(true); setLoadError(false); setReloadKey((value) => value + 1)
              setPage(p)
              setPageSize(ps)
            },
          }}
        />
      )}

      <Modal
        className="form-dialog"
        title={editing ? '编辑用户' : '新增用户'}
        visible={editOpen}
        okText={editing ? '保存用户' : '创建用户'}
        onOk={submit}
        onCancel={() => setEditOpen(false)}
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
                  rules={[{ required: true, message: '请输入初始密码' }, { match: /^.{6,20}$/, message: '密码需 6-20 位' }]}
                >
                  <Input.Password placeholder="6-20 位" />
                </Form.Item>
              </>
            )}
            <Form.Item label="姓名" field="realName" rules={[{ required: true, message: '请输入姓名' }]}>
              <Input placeholder="姓名" />
            </Form.Item>
            <Form.Item label="邮箱" field="email" rules={[{ required: true, message: '请输入邮箱' }, { type: 'email', message: '邮箱格式不正确' }]}>
              <Input placeholder="name@example.com" />
            </Form.Item>
            <Form.Item label="所属组织" field="departmentId">
              <TreeSelect allowClear placeholder="选择组织" treeData={toTreeData(depts)} />
            </Form.Item>
            <Form.Item label="角色" field="roleId" rules={[{ required: true, message: '请选择角色' }]}>
              <Select showSearch placeholder="选择角色">
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
        okText="确认重置"
        onOk={resetPwd}
        onCancel={() => setResetTarget(null)}
      >
        <Form form={pwdForm} layout="vertical">
          <Form.Item
            label="新密码"
            field="newPassword"
                rules={[{ required: true, message: '请输入新密码' }, { match: /^.{6,20}$/, message: '密码需 6-20 位' }]}
          >
            <Input.Password placeholder="6-20 位" />
          </Form.Item>
          <div className="dialog-note">重置后将退出当前登录</div>
        </Form>
      </Modal>
    </Card>
  )
}
