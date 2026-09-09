import { useCallback, useEffect, useState } from 'react'
import { Button, Card, Form, Input, InputNumber, Message, Modal, Popconfirm, Spin, Tag, Tree, Typography } from '@arco-design/web-react'
import { IconPlus } from '@arco-design/web-react/icon'
import http from '../../api/client'
import { useAuth } from '../../store/auth'

type OrgKind = 'DIVISION' | 'DEPARTMENT' | 'SECTION'

const ORG_KIND: Record<OrgKind, { label: string; childKind: OrgKind | null; childLabel: string | null; color: string }> = {
  DIVISION: { label: '事业部', childKind: 'DEPARTMENT', childLabel: '部门', color: 'arcoblue' },
  DEPARTMENT: { label: '部门', childKind: 'SECTION', childLabel: '课别', color: 'green' },
  SECTION: { label: '课别', childKind: null, childLabel: null, color: 'cyan' },
}

interface DeptNode {
  id: number
  name: string
  kind?: OrgKind
  parentId?: number | null
  sortNo: number
  status: 'ACTIVE' | 'DISABLED'
  children?: DeptNode[]
}

interface DeptTreeData { key: string; title: React.ReactNode; children?: DeptTreeData[] }

function nodeKind(node: DeptNode): OrgKind {
  return node.kind && ORG_KIND[node.kind] ? node.kind : 'DIVISION'
}

function allKeys(nodes: DeptNode[]): string[] {
  return nodes.flatMap((n) => [String(n.id), ...(n.children ? allKeys(n.children) : [])])
}

function treeStats(nodes: DeptNode[]) {
  return nodes.reduce((acc, node) => {
    const kind = nodeKind(node)
    if (kind === 'DIVISION') acc.division += 1
    else if (kind === 'DEPARTMENT') acc.department += 1
    else acc.section += 1
    const child = treeStats(node.children || [])
    acc.division += child.division
    acc.department += child.department
    acc.section += child.section
    return acc
  }, { division: 0, department: 0, section: 0 })
}

function findPath(nodes: DeptNode[], id: number, parents: DeptNode[] = []): DeptNode[] {
  for (const node of nodes) {
    const next = [...parents, node]
    if (node.id === id) return next
    const child = node.children && findPath(node.children, id, next)
    if (child && child.length) return child
  }
  return []
}

function toTreeData(nodes: DeptNode[]): DeptTreeData[] {
  return nodes.map((n) => {
    const kind = nodeKind(n)
    return {
      key: String(n.id),
      title: (
        <span className="dept-tree-node">
          <Tag color={ORG_KIND[kind].color} size="small">{ORG_KIND[kind].label}</Tag>
          <span className="dept-tree-name">{n.name}</span>
          {n.status === 'DISABLED' && <Tag color="red" size="small">禁用</Tag>}
        </span>
      ),
      children: n.children && n.children.length ? toTreeData(n.children) : undefined,
    }
  })
}

function findNode(nodes: DeptNode[], id: number): DeptNode | null {
  for (const node of nodes) {
    if (node.id === id) return node
    const child = node.children && findNode(node.children, id)
    if (child) return child
  }
  return null
}

export default function DeptManage() {
  const canManage = useAuth((s) => s.hasPerm('dept:manage'))
  const canDelete = useAuth((s) => s.hasPerm('dept:delete'))
  const [tree, setTree] = useState<DeptNode[]>([])
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState(false)
  const [reloadKey, setReloadKey] = useState(0)
  const [selected, setSelected] = useState<DeptNode | null>(null)
  const [editOpen, setEditOpen] = useState(false)
  const [saving, setSaving] = useState(false)
  const [editing, setEditing] = useState<DeptNode | null>(null)
  const [parentForNew, setParentForNew] = useState<DeptNode | null>(null)
  const [form] = Form.useForm()

  const fetchTree = useCallback(async () => {
    const r = await http.get('/departments')
    return r.data as DeptNode[]
  }, [])

  const applyTree = useCallback((next: DeptNode[]) => {
    setTree(next)
    setSelected((current) => current ? findNode(next, current.id) : null)
  }, [])

  const load = useCallback(() => {
    setLoading(true)
    setLoadError(false)
    setReloadKey((value) => value + 1)
  }, [])

  useEffect(() => {
    let active = true
    fetchTree().then((next) => {
      if (active) {
        applyTree(next)
        setLoadError(false)
      }
    }).catch(() => {
      if (active) setLoadError(true)
    }).finally(() => {
      if (active) setLoading(false)
    })
    return () => {
      active = false
    }
  }, [applyTree, fetchTree, reloadKey])

  const creatingKind: OrgKind = parentForNew
    ? (ORG_KIND[nodeKind(parentForNew)].childKind || 'SECTION')
    : 'DIVISION'
  const modalKind: OrgKind = editing ? nodeKind(editing) : creatingKind
  const modalKindMeta = ORG_KIND[modalKind]

  const openCreate = (parent: DeptNode | null) => {
    if (parent && !ORG_KIND[nodeKind(parent)].childKind) return
    setEditing(null)
    setParentForNew(parent)
    form.resetFields()
    form.setFieldsValue({ name: '', sortNo: 0 })
    setEditOpen(true)
  }

  const submit = async () => {
    if (saving) return
    const v = await form.validate().catch(() => null)
    if (!v) return
    setSaving(true)
    try {
      if (editing) {
        await http.put(`/admin/departments/${editing.id}`, { name: v.name, parentId: editing.parentId ?? null, sortNo: v.sortNo ?? 0 })
        Message.success(`${ORG_KIND[nodeKind(editing)].label}已更新`)
      } else {
        await http.post('/admin/departments', { name: v.name, parentId: parentForNew?.id ?? null, sortNo: v.sortNo ?? 0 })
        Message.success(`${ORG_KIND[creatingKind].label}已创建`)
      }
      setEditOpen(false)
      form.resetFields()
      load()
    } finally {
      setSaving(false)
    }
  }

  const toggle = async (d: DeptNode) => {
    await http.put(`/admin/departments/${d.id}/status`, { status: d.status === 'ACTIVE' ? 'DISABLED' : 'ACTIVE' })
    load()
  }

  const remove = async (d: DeptNode) => {
    await http.delete(`/admin/departments/${d.id}`)
    Message.success(`${ORG_KIND[nodeKind(d)].label}已删除`)
    if (selected?.id === d.id) setSelected(null)
    load()
  }

  const stats = treeStats(tree)
  const selectedKind = selected ? nodeKind(selected) : null
  const selectedMeta = selectedKind ? ORG_KIND[selectedKind] : null
  const selectedPath = selected ? findPath(tree, selected.id).map((node) => node.name).join(' > ') : ''
  const selectedParent = selected ? findPath(tree, selected.id).slice(-2, -1)[0] : undefined
  const selectedChildrenCount = selected?.children?.length || 0

  return (
    <Card className="page-card dept-page">
      <div className="dept-page-header page-heading">
        <div className="dept-page-title">
          <h1>组织架构</h1>
        </div>
        <div className="dept-summary">
          <div><b>{stats.division}</b><span>事业部</span></div>
          <div><b>{stats.department}</b><span>部门</span></div>
          <div><b>{stats.section}</b><span>课别</span></div>
        </div>
        {canManage && (
          <Button
            type="primary"
            icon={<IconPlus />}
            onClick={() => openCreate(null)}
          >
            新增事业部
          </Button>
        )}
      </div>

      <div className="dept-workspace">
        <section className="dept-tree-panel">
          <div className="dept-panel-head">
            <div>
              <Typography.Text bold>组织架构</Typography.Text>
            </div>
          </div>
          <div className="dept-tree-scroll">
            {loadError ? (
              <div style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '24px 0' }}>
                <Typography.Text type="error">加载失败</Typography.Text>
                <Button size="small" onClick={load}>重试</Button>
              </div>
            ) : (
              <Spin loading={loading} style={{ width: '100%' }}>
                <Tree
                  blockNode
                  showLine
                  treeData={toTreeData(tree)}
                  defaultExpandedKeys={allKeys(tree)}
                  selectedKeys={selected ? [String(selected.id)] : []}
                  onSelect={(keys) => {
                    const id = Number(keys[0])
                    setSelected(id ? findNode(tree, id) : null)
                  }}
                />
              </Spin>
            )}
          </div>
        </section>

        <section className="dept-detail-panel">
          {selected && selectedMeta ? (
            <>
              <div className="dept-detail-head">
                <div>
                  <Typography.Text type="secondary">当前{selectedMeta.label}</Typography.Text>
                  <Typography.Title heading={5} style={{ margin: '4px 0 0', whiteSpace: 'normal', overflowWrap: 'anywhere' }}>{selected.name}</Typography.Title>
                </div>
                <Tag color={selected.status === 'ACTIVE' ? 'green' : 'red'}>{selected.status === 'ACTIVE' ? '启用' : '禁用'}</Tag>
              </div>

              <div className="dept-detail-grid">
                <span>类型</span><b>{selectedMeta.label}</b>
                <span>层级路径</span><b style={{ whiteSpace: 'normal', overflowWrap: 'anywhere' }}>{selectedPath || selected.name}</b>
                <span>排序号</span><b>{selected.sortNo}</b>
                <span>下级</span><b>{selectedMeta.childLabel ? `${selectedChildrenCount} 个${selectedMeta.childLabel}` : '课别无下级'}</b>
                <span>上级</span><b>{selectedParent ? selectedParent.name : '无（事业部）'}</b>
              </div>

              {canManage && (
                <div className="dept-actions">
                  {selectedMeta.childLabel && (
                    <Button type="primary" onClick={() => openCreate(selected)}>{`新增${selectedMeta.childLabel}`}</Button>
                  )}
                  <Button
                    onClick={() => {
                      setEditing(selected)
                      form.resetFields()
                      form.setFieldsValue({ name: selected.name, sortNo: selected.sortNo })
                      setEditOpen(true)
                    }}
                  >
                    {`编辑${selectedMeta.label}`}
                  </Button>
                  <Popconfirm title={selected.status === 'ACTIVE' ? `禁用该${selectedMeta.label}？` : `启用该${selectedMeta.label}？`} onOk={() => toggle(selected)}>
                    <Button status={selected.status === 'ACTIVE' ? 'danger' : 'success'}>
                      {selected.status === 'ACTIVE' ? `禁用${selectedMeta.label}` : `启用${selectedMeta.label}`}
                    </Button>
                  </Popconfirm>
                  {canDelete && (
                    <Popconfirm title={`删除后不可恢复；有下级或关联用户时无法删除。确认删除该${selectedMeta.label}？`} onOk={() => remove(selected)}>
                      <Button status="danger">{`删除${selectedMeta.label}`}</Button>
                    </Popconfirm>
                  )}
                </div>
              )}
            </>
          ) : (
            <div className="dept-empty-detail">
              <Typography.Title heading={5} style={{ margin: 0 }}>请选择左侧组织节点</Typography.Title>
            </div>
          )}
        </section>
      </div>

      <Modal
        className="form-dialog"
        title={editing ? `编辑${modalKindMeta.label}` : `新增${modalKindMeta.label}`}
        visible={editOpen}
        unmountOnExit
        confirmLoading={saving}
        closable={!saving}
        maskClosable={!saving}
        escToExit={!saving}
        cancelButtonProps={{ disabled: saving }}
        okText={editing ? `保存${modalKindMeta.label}` : `创建${modalKindMeta.label}`}
        onOk={submit}
        onCancel={() => {
          if (!saving) {
            setEditOpen(false)
            form.resetFields()
          }
        }}
      >
        <Form form={form} layout="vertical">
          <div className="form-grid">
            <Form.Item label={`${modalKindMeta.label}名称`} field="name" rules={[{ required: true, message: `请输入${modalKindMeta.label}名称` }]}>
              <Input maxLength={50} placeholder={`请输入${modalKindMeta.label}名称`} />
            </Form.Item>
            <Form.Item label="排序号" field="sortNo">
              <InputNumber min={0} defaultValue={0} placeholder="数字越小越靠前" style={{ width: '100%' }} />
            </Form.Item>
          </div>
          {!editing && (
            <div className="dialog-note">
              {parentForNew ? `将在“${parentForNew.name}”下创建${modalKindMeta.label}。` : '事业部将创建在组织架构顶层。'}
            </div>
          )}
        </Form>
      </Modal>
    </Card>
  )
}
