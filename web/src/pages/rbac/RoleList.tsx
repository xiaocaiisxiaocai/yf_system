import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  Button, Card, Drawer, Form, Input, Message, Modal, Popconfirm, Space, Table, Tag, Tree, Typography,
} from '@arco-design/web-react'
import { IconPlus } from '@arco-design/web-react/icon'
import http from '../../api/client'
import { actionSlots } from '../../components/ActionSlots'
import { useAuth } from '../../store/auth'
import { type PageResp } from '../../api/types'

interface Role {
  id: number
  name: string
  description?: string
  isBuiltIn: boolean
  permissionsLocked?: boolean
  status: 'ACTIVE' | 'DISABLED'
  permissionIds: number[]
  assignedUserCount: number
}

interface Perm {
  id: number
  name: string
  type: 'MENU' | 'ACTION'
  parentId: number | null
}

export default function RoleList() {
  const user = useAuth((state) => state.user)
  const canManage = useAuth((state) => state.hasPerm('role:manage'))
  const canDelete = user?.isSystemAdmin === true && canManage
  const [data, setData] = useState<PageResp<Role>>({ list: [], total: 0, page: 1, pageSize: 20 })
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState(false)
  const [reloadKey, setReloadKey] = useState(0)
  const [page, setPage] = useState(1)
  const [perms, setPerms] = useState<Perm[]>([])
  const [editOpen, setEditOpen] = useState(false)
  const [editing, setEditing] = useState<Role | null>(null)
  const [permTarget, setPermTarget] = useState<Role | null>(null)
  // 菜单与操作独立保存，避免修改其他节点时丢失单独授予的菜单。
  const [checked, setChecked] = useState<string[]>([])
  const [form] = Form.useForm()

  const fetchRoles = useCallback(async () => {
    const r = await http.get('/admin/roles', { params: { page, pageSize: 20 } })
    return r.data as PageResp<Role>
  }, [page])

  const load = useCallback(() => {
    setLoading(true)
    setLoadError(false)
    setReloadKey((value) => value + 1)
  }, [])

  useEffect(() => {
    let active = true
    fetchRoles()
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
  }, [fetchRoles, reloadKey])

  useEffect(() => {
    let active = true
    http.get('/permissions').then((r) => {
      if (active) setPerms(r.data)
    })
    return () => {
      active = false
    }
  }, [])

  const permTree = useMemo(() => {
    const menus = perms.filter((p) => p.type === 'MENU')
    return menus.map((m) => ({
      key: String(m.id),
      title: m.name,
      children: perms
        .filter((p) => p.parentId === m.id)
        .map((p) => ({ key: String(p.id), title: p.name })),
    }))
  }, [perms])

  const submit = async () => {
    const v = await form.validate().catch(() => null)
    if (!v) return
    if (editing) {
      await http.put(`/admin/roles/${editing.id}`, v)
      Message.success('角色已更新')
    } else {
      await http.post('/admin/roles', v)
      Message.success('角色已创建')
    }
    setEditOpen(false)
    load()
  }

  const savePerms = async () => {
    const ids = Array.from(new Set(checked)).map(Number)
    await http.put(`/admin/roles/${permTarget!.id}/permissions`, { permissionIds: ids })
    Message.success('权限已保存')
    setPermTarget(null)
    load()
  }

  const toggle = async (r: Role) => {
    await http.put(`/admin/roles/${r.id}/status`, { status: r.status === 'ACTIVE' ? 'DISABLED' : 'ACTIVE' })
    load()
  }

  const remove = async (r: Role) => {
    await http.delete(`/admin/roles/${r.id}`)
    Message.success('角色已删除')
    load()
  }

  return (
    <Card className="page-card page-card--table">
      <div className="page-heading">
        <div>
          <h1>角色与权限</h1>
        </div>
      </div>
      <div className="page-toolbar responsive-toolbar">
        <Typography.Text type="secondary">共 {data.total} 个角色</Typography.Text>
        <Button
          type="primary"
          icon={<IconPlus />}
          onClick={() => {
            setEditing(null)
            form.resetFields()
            setEditOpen(true)
          }}
        >
          新增角色
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
          scroll={{ x: 1010, y: 'var(--page-table-scroll-y)' }}
          columns={[
          {
            title: '名称',
            dataIndex: 'name',
            width: 220,
            render: (v: string) => <span style={{ display: 'block', whiteSpace: 'normal', overflowWrap: 'anywhere', lineHeight: '20px' }}>{v}</span>,
          },
          {
            title: '说明',
            dataIndex: 'description',
            width: 230,
            render: (v?: string) => <span style={{ display: 'block', whiteSpace: 'normal', overflowWrap: 'anywhere', lineHeight: '20px' }}>{v || '-'}</span>,
          },
          { title: '权限点数', dataIndex: 'permissionIds', width: 90, align: 'center' as const, render: (v: number[]) => v.length },
          { title: '绑定用户', dataIndex: 'assignedUserCount', width: 90, align: 'center' as const },
          {
            title: '类型',
            dataIndex: 'isBuiltIn',
            width: 90,
            align: 'center' as const,
            render: (v: boolean) => (v ? <Tag>内置</Tag> : <Tag color="arcoblue">自定义</Tag>),
          },
          {
            title: '状态',
            dataIndex: 'status',
            width: 80,
            align: 'center' as const,
            render: (v: string) => (v === 'ACTIVE' ? <Tag color="green">启用</Tag> : <Tag color="red">禁用</Tag>),
          },
          {
            title: '操作',
            width: 254,
            align: 'center' as const,
            render: (_: unknown, r: Role) => actionSlots([
              <Button
                key="permissions"
                size="mini"
                type="text"
                disabled={r.permissionsLocked}
                onClick={() => {
                  setPermTarget(r)
                  setChecked(r.permissionIds.map(String))
                }}
              >
                分配权限
              </Button>,
              <Button
                key="edit"
                size="mini"
                type="text"
                onClick={() => {
                  setEditing(r)
                  form.setFieldsValue({ name: r.name, description: r.description })
                  setEditOpen(true)
                }}
              >
                编辑
              </Button>,
              !r.permissionsLocked && (
                <Popconfirm
                  key="status"
                  title={
                    r.status === 'ACTIVE'
                      ? r.assignedUserCount > 0
                        ? `该角色绑定 ${r.assignedUserCount} 个用户；启用用户未更换角色前不能禁用。`
                        : '确认禁用该角色？'
                      : '确认启用该角色？'
                  }
                  onOk={() => toggle(r)}
                >
                  <Button size="mini" type="text" status={r.status === 'ACTIVE' ? 'danger' : 'success'}>
                    {r.status === 'ACTIVE' ? '禁用' : '启用'}
                  </Button>
                </Popconfirm>
              ),
              canDelete && !r.isBuiltIn && (
                <Popconfirm key="delete" title="删除后不可恢复，仍绑定用户时无法删除。确认？" onOk={() => remove(r)}>
                  <Button size="mini" type="text" status="danger">删除</Button>
                </Popconfirm>
              ),
            ], 'role'),
          },
          ]}
          pagination={{
            total: data.total,
            current: page,
            pageSize: 20,
            showTotal: true,
            onChange: (nextPage) => {
              setLoading(true)
              setLoadError(false)
              setPage(nextPage)
            },
          }}
        />
      )}

      <Modal
        className="form-dialog"
        title={editing ? '编辑角色' : '新增角色'}
        visible={editOpen}
        okText={editing ? '保存角色' : '创建角色'}
        onOk={submit}
        onCancel={() => setEditOpen(false)}
      >
        <Form form={form} layout="vertical">
          <Form.Item label="角色名称" field="name" rules={[{ required: true, message: '请输入名称' }]}>
            <Input placeholder="角色名称" />
          </Form.Item>
          <Form.Item label="角色说明" field="description">
            <Input.TextArea rows={3} maxLength={200} placeholder="选填" />
          </Form.Item>
        </Form>
      </Modal>

      <Drawer
        width={440}
        title={permTarget ? `分配权限 · ${permTarget.name}` : ''}
        visible={!!permTarget}
        onCancel={() => setPermTarget(null)}
        footer={
          <Space style={{ width: '100%', justifyContent: 'flex-end' }}>
            <Button onClick={() => setPermTarget(null)}>取消</Button>
            <Button type="primary" onClick={savePerms}>
              保存权限
            </Button>
          </Space>
        }
      >
        <div className="section-heading">权限范围</div>
        <Tree
          checkable
          checkStrictly
          defaultExpandedKeys={permTree.map((g) => g.key)}
          treeData={permTree}
          checkedKeys={checked}
          onCheck={(keys, extra) => {
            const next = keys.map(String)
            if (extra.checked) {
              const permission = perms.find((p) => String(p.id) === String(extra.node.key))
              if (permission?.parentId) next.push(String(permission.parentId))
            }
            setChecked(Array.from(new Set(next)))
          }}
        />
        <div className="dialog-note">勾选操作会关联菜单；仅勾选菜单不授予操作权限。</div>
      </Drawer>
    </Card>
  )
}
