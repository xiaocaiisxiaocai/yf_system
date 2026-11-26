import { textLengthRule } from '../../utils/textRules'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  Button, Card, Drawer, Form, Input, Message, Modal, Popconfirm, Select, Space, Spin, Tag, Tooltip, Tree, Typography,
} from '@arco-design/web-react'
import ResizableTable from '../../components/ResizableTable'
import { IconPlus } from '@arco-design/web-react/icon'
import http, { type QuietRequestConfig } from '../../api/client'
import { isVersionConflict } from '../../api/versionConflict'
import { actionSlots } from '../../components/ActionSlots'
import DangerPopconfirm from '../../components/DangerPopconfirm'
import { useAuth } from '../../store/auth'
import { type PageResp } from '../../api/types'
import type { ApiResponses } from '../../api/types'
import type { DataScopeResponse, PermissionResponse } from '../../api/generated/api-types'
import LoadError from '../../components/LoadError'
import EnterSubmit from '../../components/EnterSubmit'
import StatusTag from '../../components/StatusTag'

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

// Permission points from the API, with type narrowed to the two kinds the tree distinguishes.
type Perm = Omit<PermissionResponse, 'type'> & { type: 'MENU' | 'ACTION' }

// 数据范围是独立维度：角色只给默认值，管理员可在用户上覆盖。
const SCOPE_DOMAINS = [
  { domain: 'project', label: '项目范围' },
  { domain: 'oem_transfer', label: 'ODM/OEMA 传递单范围' },
] as const
const SCOPE_LABELS: Record<string, string> = {
  SELF: '仅本人', ORG: '本组织及下级', ALL: '全部',
}

export default function RoleList() {
  const canDelete = useAuth((state) => state.hasPerm('role:delete'))
  // 只读（role:view）账号可以查看权限，但新增/编辑角色需要 role:manage。
  const canManageRoles = useAuth((state) => state.hasPerm('role:manage'))
  const [data, setData] = useState<PageResp<Role>>({ list: [], total: 0, page: 1, pageSize: 20 })
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState(false)
  const [reloadKey, setReloadKey] = useState(0)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [perms, setPerms] = useState<Perm[]>([])
  const [permsLoading, setPermsLoading] = useState(true)
  const [permsError, setPermsError] = useState(false)
  const permsSeq = useRef(0)
  const saveInFlight = useRef(false)
  const permissionSaveInFlight = useRef(false)
  const [editOpen, setEditOpen] = useState(false)
  const [saving, setSaving] = useState(false)
  const [savingPerms, setSavingPerms] = useState(false)
  const [editing, setEditing] = useState<Role | null>(null)
  const [permTarget, setPermTarget] = useState<Role | null>(null)
  const [permKeyword, setPermKeyword] = useState('')
  const [roleScopes, setRoleScopes] = useState<Record<string, string>>({})
  const [roleScopesOriginal, setRoleScopesOriginal] = useState<Record<string, string>>({})
  const [roleScopesLoading, setRoleScopesLoading] = useState(false)
  const [roleScopesLoadedFor, setRoleScopesLoadedFor] = useState<number | null>(null)
  // 保存实际权限；父节点的半选显示不改变已有的仅菜单授权。
  const [checked, setChecked] = useState<string[]>([])
  const [permOriginal, setPermOriginal] = useState<string[]>([])
  const [form] = Form.useForm()

  const fetchRoles = useCallback(async () => {
    const r = await http.get<ApiResponses['GET /admin/roles']>('/admin/roles', { params: { page, pageSize } })
    return r.data as PageResp<Role>
  }, [page, pageSize])

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
    http.get<ApiResponses['GET /permissions']>('/permissions', { quietNetworkError: true } as QuietRequestConfig)
      .then((r) => {
        if (seq === permsSeq.current) setPerms(r.data as Perm[])
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
    http.get<ApiResponses['GET /permissions']>('/permissions', { quietNetworkError: true } as QuietRequestConfig)
      .then((r) => {
        if (seq === permsSeq.current) setPerms(r.data as Perm[])
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

  const normalizedPermKeyword = permKeyword.trim().toLowerCase()
  const displayedPermTree = useMemo(() => {
    const matched = !normalizedPermKeyword
      ? permTree
      : permTree.flatMap((group) => {
        const menuPerm = perms.find((p) => String(p.id) === group.key)
        const menuMatches = group.title.toLowerCase().includes(normalizedPermKeyword)
          || (menuPerm?.code ?? '').toLowerCase().includes(normalizedPermKeyword)
        if (menuMatches) return [group]
        const matchingChildren = group.children.filter((child) => {
          const childPerm = perms.find((p) => String(p.id) === child.key)
          return child.title.toLowerCase().includes(normalizedPermKeyword)
            || (childPerm?.code ?? '').toLowerCase().includes(normalizedPermKeyword)
        })
        return matchingChildren.length > 0 ? [{ ...group, children: matchingChildren }] : []
      })
    return savingPerms
      ? matched.map((group) => ({
        ...group,
        disabled: true,
        children: group.children.map((child) => ({ ...child, disabled: true })),
      }))
      : matched
  }, [normalizedPermKeyword, permTree, perms, savingPerms])

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
    if (saveInFlight.current) return
    saveInFlight.current = true
    try {
      const v = await form.validate().catch(() => null)
      if (!v) return
      setSaving(true)
      if (editing) {
        await http.put<ApiResponses['PUT /admin/roles/{id}']>(`/admin/roles/${editing.id}`, v)
        Message.success('角色已更新')
      } else {
        await http.post<ApiResponses['POST /admin/roles']>('/admin/roles', v)
        Message.success('角色已创建')
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

  // 打开抽屉时读取角色的默认数据范围；读取失败就不提交范围，避免把已有设置清空。
  const loadRoleScopes = async (roleId: number) => {
    setRoleScopesLoading(true)
    setRoleScopesLoadedFor(null)
    try {
      const response = await http.get<ApiResponses['GET /admin/roles/{id}/data-scopes']>(
        `/admin/roles/${roleId}/data-scopes`, { quietNetworkError: true } as QuietRequestConfig)
      const values: Record<string, string> = {}
      for (const row of response.data as DataScopeResponse[]) values[row.domain] = row.localScope ?? ''
      setRoleScopes(values)
      setRoleScopesOriginal(values)
      setRoleScopesLoadedFor(roleId)
    } catch {
      Message.error('数据范围加载失败，本次保存不会修改数据范围')
    } finally {
      setRoleScopesLoading(false)
    }
  }

  const savePerms = async () => {
    if (permissionSaveInFlight.current || permsLoading || permsError || perms.length === 0 || permTarget?.canManage === false) return
    permissionSaveInFlight.current = true
    const ids = Array.from(new Set(checked)).map(Number)
    setSavingPerms(true)
    try {
      // 只提交改过的业务域：另一域可能已被他人调整，整份写回会把它恢复成旧值。
      const changedScopes = roleScopesLoadedFor === permTarget!.id
        ? SCOPE_DOMAINS
          .filter(({ domain }) => (roleScopes[domain] ?? '') !== (roleScopesOriginal[domain] ?? ''))
          .map(({ domain }) => ({ domain, value: roleScopes[domain] ?? '' }))
        : []
      // 只改范围时不重写整组动作权限：否则旧页面可能把另一位管理员刚撤销的授权恢复回去。
      const permsChanged = ids.length !== permOriginal.length
        || ids.some((id) => !permOriginal.includes(String(id)))
      if (!permsChanged && changedScopes.length === 0) {
        Message.info('没有需要保存的修改')
        setPermTarget(null)
        setPermKeyword('')
        return
      }
      // 一次请求、一个事务：动作权限与数据范围要么都生效，要么都不生效。
      await http.put<ApiResponses['PUT /admin/roles/{id}/authorization']>(
        `/admin/roles/${permTarget!.id}/authorization`,
        {
          permissionIds: permsChanged ? ids : null,
          scopes: changedScopes.length > 0
            ? changedScopes.map(({ domain, value }) => ({ domain, scope: value || null }))
            : null,
        })
      Message.success('权限已保存')
      setPermTarget(null)
      setPermKeyword('')
      load()
    } catch (error) {
      // 请求层已展示错误；保留抽屉和当前选择，允许用户直接重试。
      if (isVersionConflict(error)) load()
    } finally {
      permissionSaveInFlight.current = false
      setSavingPerms(false)
    }
  }

  const toggle = async (r: Role) => {
    await http.put<ApiResponses['PUT /admin/roles/{id}/status']>(`/admin/roles/${r.id}/status`, { status: r.status === 'ACTIVE' ? 'DISABLED' : 'ACTIVE' })
    Message.success(r.status === 'ACTIVE' ? '角色已停用' : '角色已启用')
    load()
  }

  const remove = async (r: Role) => {
    await http.delete<ApiResponses['DELETE /admin/roles/{id}']>(`/admin/roles/${r.id}`)
    Message.success('角色已删除')
    load()
  }

  return (
    <Card className="page-card page-card--table">
      <h1 className="visually-hidden">角色与权限</h1>
      {canManageRoles && (
        <div className="page-toolbar responsive-toolbar">
          <span />
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
      )}
      {loadError ? (
        <LoadError onRetry={load} />
      ) : (
        <ResizableTable tableId="roles"
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
            align: 'left' as const,
            render: (v: string) => <span className="table-cell-wrap">{v}</span>,
          },
          {
            title: '说明',
            dataIndex: 'description',
            width: 200,
            align: 'left' as const,
            render: (v?: string) => <span className="table-cell-wrap">{v || '-'}</span>,
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
            render: (v: string) => <StatusTag status={v} />,
          },
          {
            title: '操作',
            width: 240,
            fixed: 'right' as const,
            render: (_: unknown, r: Role) => actionSlots([
              <Button
                key="permissions"
                size="mini"
                type="text"
                onClick={() => {
                  setPermTarget(r)
                  setPermKeyword('')
                  setChecked(r.permissionIds.map(String))
                  setPermOriginal(r.permissionIds.map(String))
                  void loadRoleScopes(r.id)
                }}
              >
                {r.canManage === false ? '查看权限' : '分配权限'}
              </Button>,
              <Button
                key="edit"
                size="mini"
                type="text"
                disabled={r.canManage === false}
                onClick={() => {
                  setEditing(r)
                  form.setFieldsValue({ name: r.name, description: r.description })
                  setEditOpen(true)
                }}
              >
                编辑
              </Button>,
              r.canManage !== false && r.status === 'ACTIVE' && r.assignedUserCount > 0 ? (
                <Tooltip key="status" content={`该角色绑定 ${r.assignedUserCount} 个用户；启用用户未更换角色前不能停用。`}>
                  <Button size="mini" type="text" status="danger" disabled>停用</Button>
                </Tooltip>
              ) : r.status === 'ACTIVE' ? (
                <DangerPopconfirm
                  key="status"
                  disabled={r.canManage === false}
                  title="确认停用该角色？"
                  onOk={() => toggle(r)}
                >
                  <Button size="mini" type="text" status="danger" disabled={r.canManage === false}>停用</Button>
                </DangerPopconfirm>
              ) : (
                <Popconfirm
                  key="status"
                  disabled={r.canManage === false}
                  title="确认启用该角色？"
                  onOk={() => toggle(r)}
                >
                  <Button size="mini" type="text" status="success" disabled={r.canManage === false}>启用</Button>
                </Popconfirm>
              ),
              canDelete && (
                <DangerPopconfirm key="delete" disabled={r.canManage === false || r.isBuiltIn}
                  title="删除后不可恢复，仍绑定用户时无法删除。确认？" onOk={() => remove(r)}>
                  <Button size="mini" type="text" status="danger" disabled={r.canManage === false || r.isBuiltIn}>删除</Button>
                </DangerPopconfirm>
              ),
            ], 'role'),
          },
          ]}
          pagination={{
            total: data.total,
            current: page,
            pageSize,
            onChange: (nextPage, nextSize) => {
              setLoading(true)
              setLoadError(false)
              setPage(nextPage)
              setPageSize(nextSize)
            },
          }}
        />
      )}

      <Modal
        className="form-dialog"
        title={editing ? '编辑角色' : '新增角色'}
        visible={editOpen}
        unmountOnExit
        confirmLoading={saving}
        closable={!saving}
        maskClosable={!saving}
        escToExit={!saving}
        cancelButtonProps={{ disabled: saving }}
        okText={editing ? '保存角色' : '创建角色'}
        onOk={submit}
        onCancel={() => { if (!saving) setEditOpen(false) }}
      >
        <Form onSubmit={() => void submit()} form={form} layout="vertical">
          <Form.Item label="角色名称" field="name" rules={[{ required: true, message: '请输入名称' }, textLengthRule('名称', 64)]}>
            <Input autoFocus={!editing?.isBuiltIn} placeholder="角色名称" disabled={editing?.isBuiltIn} />
          </Form.Item>
          <Form.Item label="角色说明" field="description">
            <Input.TextArea autoFocus={editing?.isBuiltIn} rows={3} maxLength={255} placeholder="选填" />
          </Form.Item>
        <EnterSubmit /></Form>
      </Modal>

      <Drawer
        width="min(440px, 100vw)"
        title={permTarget ? `${permTarget.canManage === false ? '查看权限' : '分配权限'} · ${permTarget.name}` : ''}
        visible={!!permTarget}
        onCancel={() => { if (!savingPerms) { setPermTarget(null); setPermKeyword('') } }}
        closable={!savingPerms}
        maskClosable={!savingPerms}
        escToExit={!savingPerms}
        footer={
          <Space style={{ width: '100%', justifyContent: 'flex-end' }}>
            <Button disabled={savingPerms} onClick={() => { setPermTarget(null); setPermKeyword('') }}>{permTarget?.canManage === false ? '关闭' : '取消'}</Button>
            {permTarget?.canManage !== false && (
              <Button type="primary" loading={savingPerms} disabled={permsLoading || permsError || perms.length === 0} onClick={savePerms}>
                保存权限
              </Button>
            )}
          </Space>
        }
      >
        <div className="section-heading">权限范围</div>
        {permTarget && (
          <div style={{ marginBottom: 12 }}>
            <Typography.Text type="secondary">
              当前绑定：<Tag color={permTarget.assignedUserCount > 0 ? 'orange' : 'gray'}>{permTarget.assignedUserCount} 位用户</Tag>
              {permTarget.assignedUserCount > 0 && (
                <span style={{ marginLeft: 6, fontSize: 12, color: 'var(--color-text-3)' }}>修改后将即时影响已绑定的用户权限</span>
              )}
            </Typography.Text>
          </div>
        )}
        {permsError && (
          <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 12 }}>
            <Typography.Text type="error">权限点加载失败</Typography.Text>
            <Button size="small" onClick={retryPermissions}>重试加载权限点</Button>
          </div>
        )}
        {permsLoading && (
          <div style={{ padding: '24px 0', textAlign: 'center' }}>
            <Spin tip="权限点加载中…" />
          </div>
        )}
        {!permsLoading && !permsError && perms.length === 0 && <Typography.Text type="secondary">暂无权限点</Typography.Text>}
        {!permsLoading && !permsError && perms.length > 0 && (
          <>
            <Input
              allowClear
              placeholder="筛选权限名称或标识"
              value={permKeyword}
              onChange={setPermKeyword}
              style={{ marginBottom: 12 }}
            />
            {displayedPermTree.length === 0 ? (
              <Typography.Text type="secondary">未找到匹配的权限点</Typography.Text>
            ) : (
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
                  // 过滤搜索时只对可见的子项做整组勾选/取消，避免连带改动被过滤隐藏的同级权限。
                  const group = displayedPermTree.find((item) => item.key === key)
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
          </>
        )}
        {permTarget && (
          <>
            <div className="section-heading" style={{ marginTop: 20 }}>默认数据范围</div>
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              角色成员的默认可见范围，可在用户上单独覆盖。“未设置”时主管按所辖组织扩大；选“仅本人”则主管也只看本人。
            </Typography.Text>
            <div style={{ marginTop: 12 }}>
              {SCOPE_DOMAINS.map(({ domain, label }) => (
                <div key={domain} style={{ display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: 12, marginBottom: 12 }}>
                  <span style={{ flex: '0 1 150px' }}>{label}</span>
                  <Select
                    style={{ flex: '1 1 160px', maxWidth: '100%' }}
                    value={roleScopes[domain] ?? ''}
                    loading={roleScopesLoading}
                    disabled={permTarget.canManage === false || roleScopesLoading || roleScopesLoadedFor !== permTarget.id}
                    onChange={(value) => setRoleScopes((prev) => ({ ...prev, [domain]: value }))}
                  >
                    <Select.Option value="">未设置（主管看所辖组织）</Select.Option>
                    <Select.Option value="SELF">仅本人（主管也不扩大）</Select.Option>
                    <Select.Option value="ORG">{SCOPE_LABELS.ORG}</Select.Option>
                    <Select.Option value="ALL">{SCOPE_LABELS.ALL}</Select.Option>
                  </Select>
                </div>
              ))}
            </div>
          </>
        )}
        {permTarget?.canManage === false && (
          <div className="dialog-note">该角色超出当前账号的委派范围，仅可查看。</div>
        )}
      </Drawer>
    </Card>
  )
}
