import { useCallback, useEffect, useState } from 'react'
import {
  Button, Card, Form, Input, Message, Modal, Popconfirm, Select, Space, Table, Tag, TreeSelect,
} from '@arco-design/web-react'
import { IconPlus } from '@arco-design/web-react/icon'
import http from '../../api/client'
import { actionSlots } from '../../components/ActionSlots'
import { type PageResp, fmtTime } from '../../api/types'

interface UserRow {
  id: number
  username: string
  realName: string
  email: string
  phone?: string
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
  code: string
}

interface DeptNode {
  id: number
  name: string
  children?: DeptNode[]
}

interface DeptTreeData { key: string; title: string; value: string; children?: DeptTreeData[] }
function toTreeData(nodes: DeptNode[]): DeptTreeData[] {
  return nodes.map((n) => ({
    key: String(n.id),
    title: n.name,
    value: String(n.id),
    children: n.children && n.children.length ? toTreeData(n.children) : undefined,
  }))
}

export default function UserList() {
  const [data, setData] = useState<PageResp<UserRow>>({ list: [], total: 0, page: 1, pageSize: 10 })
  const [loading, setLoading] = useState(true)
  const [reloadKey, setReloadKey] = useState(0)
  const [keyword, setKeyword] = useState('')
  const [departmentId, setDepartmentId] = useState<number>()
  const [status, setStatus] = useState<string>()
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(10)
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

  const load = useCallback(async () => {
    setLoading(true)
    try {
      setData(await fetchUsers())
    } finally {
      setLoading(false)
    }
  }, [fetchUsers])

  useEffect(() => {
    let active = true
    fetchUsers()
      .then((next) => {
        if (active) setData(next)
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
        phone: payload.phone,
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
      <Space className="responsive-toolbar" style={{ marginBottom: 16, width: '100%', justifyContent: 'space-between' }}>
        <Space wrap>
          <Input.Search
          allowClear
          placeholder="用户名 / 姓名 / 邮箱"
          style={{ width: 260 }}
          onSearch={(v) => {
            setLoading(true); setReloadKey((value) => value + 1)
            setPage(1)
            setKeyword(v)
          }}
          onClear={() => {
            setLoading(true); setReloadKey((value) => value + 1)
            setPage(1)
            setKeyword('')
          }}
          />
          <TreeSelect
            allowClear
            placeholder="部门"
            style={{ width: 150 }}
            treeData={toTreeData(depts)}
            value={departmentId ? String(departmentId) : undefined}
            onChange={(v) => { setLoading(true); setReloadKey((value) => value + 1); setPage(1); setDepartmentId(v ? Number(v) : undefined) }}
          />
          <Select
            allowClear
            placeholder="状态"
            style={{ width: 110 }}
            value={status}
            onChange={(v) => { setLoading(true); setReloadKey((value) => value + 1); setPage(1); setStatus(v as string | undefined) }}
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
      </Space>
      <Table
        className="page-table"
        rowKey="id"
        loading={loading}
        data={data.list}
        scroll={{ x: 1015, y: 'var(--page-table-scroll-y)' }}
        columns={[
          { title: '用户名', dataIndex: 'username', width: 110, align: 'center' as const, ellipsis: true },
          { title: '姓名', dataIndex: 'realName', width: 100, align: 'center' as const, ellipsis: true },
          { title: '部门', dataIndex: 'departmentName', width: 85, align: 'center' as const, ellipsis: true, render: (v?: string) => v || '-' },
          {
            title: '角色',
            dataIndex: 'roleName',
            width: 120,
            align: 'center' as const,
            render: (v?: string) => (v ? <Tag size="small">{v}</Tag> : '-'),
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
            width: 220,
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
                      phone: r.phone,
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
                r.username !== 'admin' && (
                  <Popconfirm key="status" title={r.status === 'ACTIVE' ? '禁用后立即无法登录，确认？' : '确认启用？'} onOk={() => toggle(r)}>
                    <Button size="mini" type="text" status={r.status === 'ACTIVE' ? 'danger' : 'success'}>
                      {r.status === 'ACTIVE' ? '禁用' : '启用'}
                    </Button>
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
            setLoading(true); setReloadKey((value) => value + 1)
            setPage(p)
            setPageSize(ps)
          },
        }}
      />

      <Modal title={editing ? '编辑用户' : '新增用户'} visible={editOpen} onOk={submit} onCancel={() => setEditOpen(false)}>
        <Form form={form} layout="vertical">
          {!editing && (
            <>
              <Form.Item label="用户名" field="username" rules={[{ required: true, message: '请输入用户名' }, { match: /^[a-zA-Z0-9_]{3,32}$/, message: '3-32 位字母/数字/下划线' }]}>
                <Input />
              </Form.Item>
              <Form.Item
                label="初始密码"
                field="password"
                rules={[{ required: true, message: '请输入初始密码' }, { match: /^(?=.*[A-Za-z])(?=.*\d).{8,}$/, message: '至少 8 位，含字母和数字' }]}
              >
                <Input.Password />
              </Form.Item>
            </>
          )}
          <Form.Item label="姓名" field="realName" rules={[{ required: true, message: '请输入姓名' }]}>
            <Input />
          </Form.Item>
          <Form.Item label="邮箱" field="email" rules={[{ required: true, message: '请输入邮箱' }, { type: 'email', message: '邮箱格式不正确' }]}>
            <Input />
          </Form.Item>
          <Form.Item label="电话" field="phone">
            <Input />
          </Form.Item>
          <Form.Item label="部门" field="departmentId">
            <TreeSelect allowClear placeholder="选择部门" treeData={toTreeData(depts)} />
          </Form.Item>
          <Form.Item label="角色" field="roleId" rules={[{ required: true, message: '请选择角色' }]}>
            <Select showSearch placeholder="搜索并选择一个启用角色">
              {roles.map((r) => (
                <Select.Option key={r.id} value={r.id}>
                  {r.name}（{r.code}）
                </Select.Option>
              ))}
            </Select>
          </Form.Item>
        </Form>
      </Modal>

      <Modal title={`重置密码 · ${resetTarget?.username ?? ''}`} visible={!!resetTarget} onOk={resetPwd} onCancel={() => setResetTarget(null)}>
        <Form form={pwdForm} layout="vertical">
          <Form.Item
            label="新密码"
            field="newPassword"
            rules={[{ required: true, message: '请输入新密码' }, { match: /^(?=.*[A-Za-z])(?=.*\d).{8,}$/, message: '至少 8 位，含字母和数字' }]}
          >
            <Input.Password />
          </Form.Item>
        </Form>
      </Modal>
    </Card>
  )
}
