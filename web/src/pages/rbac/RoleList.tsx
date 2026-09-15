import { textLengthRule } from '../../utils/textRules'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  Button, Card, Drawer, Form, Input, Message, Modal, Popconfirm, Space, Table, Tag, Tree, Typography,
} from '@arco-design/web-react'
import { IconPlus } from '@arco-design/web-react/icon'
import http, { type QuietRequestConfig } from '../../api/client'
import { actionSlots } from '../../components/ActionSlots'
import { useAuth } from '../../store/auth'
import { type PageResp } from '../../api/types'

interface Role {
  id: number
  name: string
  description?: string
  isBuiltIn: boolean
  status: 'ACTIVE' | 'DISABLED'
  permissionIds: number[]
  assignedUserCount: number
  canManage?: boolean
  supplierRestricted?: boolean
}

interface Perm {
  id: number
  name: string
  type: 'MENU' | 'ACTION'
  parentId: number | null
  grantable?: boolean
  supplierAssignable?: boolean
}

export default function RoleList() {
  const canDelete = useAuth((state) => state.hasPerm('role:delete'))
  const [data, setData] = useState<PageResp<Role>>({ list: [], total: 0, page: 1, pageSize: 20 })
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState(false)
  const [reloadKey, setReloadKey] = useState(0)
  const [page, setPage] = useState(1)
  const [perms, setPerms] = useState<Perm[]>([])
  const [permsLoading, setPermsLoading] = useState(true)
  const [permsError, setPermsError] = useState(false)
  const permsSeq = useRef(0)
  const [editOpen, setEditOpen] = useState(false)
  const [saving, setSaving] = useState(false)
  const [savingPerms, setSavingPerms] = useState(false)
  const [editing, setEditing] = useState<Role | null>(null)
  const [permTarget, setPermTarget] = useState<Role | null>(null)
  // 保存实际权限；父节点的半选显示不改变已有的仅菜单授权。
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
          const lastPage = Math.max(1, Math.ceil(next.total / (next.pageSize || 20)))
          if (page > lastPage) setPage(lastPage)
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
  }, [fetchRoles, page, reloadKey])

  const retryPermissions = () => {
    setPermsLoading(true)
    setPermsError(false)
    const seq = ++permsSeq.current
    http.get('/permissions', { quietNetworkError: true } as QuietRequestConfig)
      .then((r) => {
        if (seq === permsSeq.current) setPerms(r.data)
      })
      .catch(() => {
        if (seq === permsSeq.current) setPermsError(true)
      })
      .finally(() => {
        if (seq === permsSeq.current) setPermsLoading(false)
      })
  }

  useEffect(() => {
    const seq = ++permsSeq.current
    http.get('/permissions', { quietNetworkError: true } as QuietRequestConfig)
      .then((r) => {
        if (seq === permsSeq.current) setPerms(r.data)
      })
      .catch(() => {
        if (seq === permsSeq.current) setPermsError(true)
      })
      .finally(() => {
        if (seq === permsSeq.current) setPermsLoading(false)
      })
    return () => {
      permsSeq.current += 1
    }
  }, [])

  const mutablePermissionIds = useMemo(() => {
    if (!permTarget || permTarget.canManage === false) return new Set<string>()
    const result = new Set(perms
      .filter((permission) => permission.grantable !== false
        && (!permTarget.supplierRestricted || permission.supplierAssignable !== false))
      .map((permission) => String(permission.id)))
    for (const permission of perms) {
      if (permission.parentId && !result.has(String(permission.parentId))) result.delete(String(permission.id))
    }
    return result
  }, [permTarget, perms])

  const removablePermissionIds = useMemo(() => new Set(
    permTarget?.canManage === false ? [] : (permTarget?.permissionIds || [])
      .map(String)
      .filter((id) => checked.includes(id) && !mutablePermissionIds.has(id)),
  ), [checked, mutablePermissionIds, permTarget])

  const permTree = useMemo(() => {
    const menus = perms.filter((p) => p.type === 'MENU')
    return menus.map((m) => ({
      key: String(m.id),
      title: removablePermissionIds.has(String(m.id)) ? `${m.name}（仅可移除）` : m.name,
      disabled: !mutablePermissionIds.has(String(m.id)) && !removablePermissionIds.has(String(m.id)),
      children: perms
        .filter((p) => p.parentId === m.id)
        .map((p) => ({
          key: String(p.id),
          title: removablePermissionIds.has(String(p.id)) ? `${p.name}（仅可移除）` : p.name,
          disabled: !mutablePermissionIds.has(String(p.id)) && !removablePermissionIds.has(String(p.id)),
        })),
    }))
  }, [mutablePermissionIds, perms, removablePermissionIds])

  const displayedPermTree = useMemo(() => savingPerms
    ? permTree.map((group) => ({
      ...group,
      disabled: true,
      children: group.children.map((child) => ({ ...child, disabled: true })),
    }))
    : permTree, [permTree, savingPerms])

  const halfChecked = permTree
    .filter((group) => {
      const relevantChildren = permTarget?.canManage === false
        ? group.children
        : group.children.filter((child) => !child.disabled)
      return relevantChildren.length > 0
        && (checked.includes(group.key) || relevantChildren.some((child) => checked.includes(child.key)))
        && (!checked.includes(group.key) || !relevantChildren.every((child) => checked.includes(child.key)))
    })
    .map((group) => group.key)

  const submit = async () => {
    if (saving) return
    const v = await form.validate().catch(() => null)
    if (!v) return
    setSaving(true)
    try {
      if (editing) {
        await http.put(`/admin/roles/${editing.id}`, v)
        Message.success('角色已更新')
      } else {
        await http.post('/admin/roles', v)
        Message.success('角色已创建')
      }
      setEditOpen(false)
      load()
    } finally {
      setSaving(false)
    }
  }

  const savePerms = async () => {
    if (savingPerms || permsLoading || permsError || perms.length === 0 || permTarget?.canManage === false) return
    const ids = Array.from(new Set(checked)).map(Number)
    setSavingPerms(true)
    try {
      await http.put(`/admin/roles/${permTarget!.id}/permissions`, { permissionIds: ids })
      Message.success('权限已保存')
      setPermTarget(null)
      load()
    } catch {
      // 请求层已展示错误；保留抽屉和当前选择，允许用户直接重试。
    } finally {
      setSavingPerms(false)
    }
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
          scroll={{ x: 996, y: 'var(--page-table-scroll-y)' }}
          columns={[
          {
            title: '名称',
            dataIndex: 'name',
            width: 190,
            render: (v: string) => <span style={{ display: 'block', whiteSpace: 'normal', overflowWrap: 'anywhere', lineHeight: '20px' }}>{v}</span>,
          },
          {
            title: '说明',
            dataIndex: 'description',
            width: 200,
            render: (v?: string) => <span style={{ display: 'block', whiteSpace: 'normal', overflowWrap: 'anywhere', lineHeight: '20px' }}>{v || '-'}</span>,
          },
          { title: '权限点数', dataIndex: 'permissionIds', width: 96, align: 'center' as const, render: (v: number[]) => v.length },
          { title: '绑定用户', dataIndex: 'assignedUserCount', width: 96, align: 'center' as const },
          {
            title: '类型',
            dataIndex: 'isBuiltIn',
            width: 80,
            align: 'center' as const,
            render: (v: boolean) => (v ? <Tag>内置</Tag> : <Tag color="arcoblue">自定义</Tag>),
          },
          {
            title: '状态',
            dataIndex: 'status',
            width: 70,
            align: 'center' as const,
            render: (v: string) => (v === 'ACTIVE' ? <Tag color="green">启用</Tag> : <Tag color="red">禁用</Tag>),
          },
          {
            title: '操作',
            width: 264,
            fixed: 'right' as const,
            align: 'center' as const,
            render: (_: unknown, r: Role) => actionSlots([
              <Button
                key="permissions"
                size="mini"
                type="text"
                onClick={() => {
                  setPermTarget(r)
                  setChecked(r.permissionIds.map(String))
                }}
              >
                {r.canManage === false ? '查看权限' : '分配权限'}
              </Button>,
              r.canManage !== false && <Button
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
              r.canManage !== false && <Popconfirm
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
              </Popconfirm>,
              canDelete && r.canManage !== false && !r.isBuiltIn && (
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
        confirmLoading={saving}
        closable={!saving}
        maskClosable={!saving}
        escToExit={!saving}
        cancelButtonProps={{ disabled: saving }}
        okText={editing ? '保存角色' : '创建角色'}
        onOk={submit}
        onCancel={() => { if (!saving) setEditOpen(false) }}
      >
        <Form form={form} layout="vertical">
          <Form.Item label="角色名称" field="name" rules={[{ required: true, message: '请输入名称' }, textLengthRule('名称', 64)]}>
            <Input placeholder="角色名称" disabled={editing?.isBuiltIn} />
          </Form.Item>
          <Form.Item label="角色说明" field="description">
            <Input.TextArea rows={3} maxLength={255} placeholder="选填" />
          </Form.Item>
        </Form>
      </Modal>

      <Drawer
        width={440}
        title={permTarget ? `${permTarget.canManage === false ? '查看权限' : '分配权限'} · ${permTarget.name}` : ''}
        visible={!!permTarget}
        onCancel={() => { if (!savingPerms) setPermTarget(null) }}
        closable={!savingPerms}
        maskClosable={!savingPerms}
        footer={
          <Space style={{ width: '100%', justifyContent: 'flex-end' }}>
            <Button disabled={savingPerms} onClick={() => setPermTarget(null)}>{permTarget?.canManage === false ? '关闭' : '取消'}</Button>
            {permTarget?.canManage !== false && (
              <Button type="primary" loading={savingPerms} disabled={permsLoading || permsError || perms.length === 0} onClick={savePerms}>
                保存权限
              </Button>
            )}
          </Space>
        }
      >
        <div className="section-heading">权限范围</div>
        {permsError && (
          <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 12 }}>
            <Typography.Text type="error">权限点加载失败</Typography.Text>
            <Button size="small" onClick={retryPermissions}>重试加载权限点</Button>
          </div>
        )}
        {permsLoading && <Typography.Text type="secondary">权限点加载中…</Typography.Text>}
        {!permsLoading && !permsError && perms.length === 0 && <Typography.Text type="secondary">暂无权限点</Typography.Text>}
        {!permsLoading && !permsError && (
          <Tree
            checkable
            checkStrictly
            defaultExpandedKeys={permTree.map((g) => g.key)}
            treeData={displayedPermTree}
            checkedKeys={checked.filter((key) => !halfChecked.includes(key))}
            halfCheckedKeys={halfChecked}
            onCheck={(_, extra) => {
              if (savingPerms) return
              const next = new Set(checked)
              const key = String(extra.node.key)
              const permission = perms.find((p) => String(p.id) === key)
              const group = permTree.find((item) => item.key === key)
              if (!mutablePermissionIds.has(key)) {
                if (!extra.checked && removablePermissionIds.has(key)) {
                  next.delete(key)
                  setChecked(Array.from(next))
                }
                return
              }
              const branch = [key, ...(group?.children.map((child) => child.key) || [])]
                .filter((id) => mutablePermissionIds.has(id))
              if (extra.checked) {
                branch.forEach((id) => next.add(id))
                if (permission?.parentId && mutablePermissionIds.has(String(permission.parentId))) next.add(String(permission.parentId))
              } else {
                branch.forEach((id) => next.delete(id))
              }
              setChecked(Array.from(next))
            }}
          />
        )}
        {permTarget?.canManage === false && (
          <div className="dialog-note">该角色超出当前账号的委派范围，仅可查看。</div>
        )}
      </Drawer>
    </Card>
  )
}
