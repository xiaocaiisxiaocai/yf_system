import { useCallback, useEffect, useRef, useState } from 'react'
import { Button, Card, Dropdown, Form, Input, InputNumber, Menu, Message, Modal, Spin, Tag, Tree, Typography } from '@arco-design/web-react'
import { IconDown, IconPlus, IconRight, IconSearch } from '@arco-design/web-react/icon'
import http from '../../api/client'
import { useAuth } from '../../store/auth'
import './DeptManage.css'
import type { ApiResponses } from '../../api/types'

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

function filterTree(nodes: DeptNode[], keyword: string): DeptNode[] {
  const query = keyword.trim().toLocaleLowerCase()
  if (!query) return nodes
  return nodes.flatMap((node) => {
    if (node.name.toLocaleLowerCase().includes(query)) return [node]
    const children = filterTree(node.children || [], query)
    return children.length ? [{ ...node, children }] : []
  })
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
        <span className="org-view-tree-node">
          <span className="org-view-tree-name" title={n.name}>{n.name}</span>
          <span className="org-view-tree-kind">{ORG_KIND[kind].label}</span>
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
  const [moreFor, setMoreFor] = useState<number | null>(null)
  const [keyword, setKeyword] = useState('')
  const [expandedKeys, setExpandedKeys] = useState<string[] | null>(null)
  const [editOpen, setEditOpen] = useState(false)
  const [saving, setSaving] = useState(false)
  const saveInFlight = useRef(false)
  const [editing, setEditing] = useState<DeptNode | null>(null)
  const [parentForNew, setParentForNew] = useState<DeptNode | null>(null)
  const [form] = Form.useForm()

  const fetchTree = useCallback(async () => {
    const r = await http.get<ApiResponses['GET /departments']>('/departments')
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
    const siblings = parent ? (parent.children || []) : tree
    const sortNo = siblings.reduce((max, node) => Math.max(max, node.sortNo), 0) + 1
    form.setFieldsValue({ name: '', sortNo })
    setEditOpen(true)
  }

  const submit = async () => {
    if (saveInFlight.current) return
    saveInFlight.current = true
    try {
      const v = await form.validate().catch(() => null)
      if (!v) return
      setSaving(true)
      if (editing) {
        await http.put<ApiResponses['PUT /admin/departments/{id}']>(`/admin/departments/${editing.id}`, { name: v.name, parentId: editing.parentId ?? null, sortNo: v.sortNo ?? 0 })
        Message.success(`${ORG_KIND[nodeKind(editing)].label}已更新`)
      } else {
        await http.post<ApiResponses['POST /admin/departments']>('/admin/departments', { name: v.name, parentId: parentForNew?.id ?? null, sortNo: v.sortNo ?? 0 })
        Message.success(`${ORG_KIND[creatingKind].label}已创建`)
      }
      setEditOpen(false)
      form.resetFields()
      load()
    } finally {
      saveInFlight.current = false
      setSaving(false)
    }
  }

  const toggle = async (d: DeptNode) => {
    await http.put<ApiResponses['PUT /admin/departments/{id}/status']>(`/admin/departments/${d.id}/status`, { status: d.status === 'ACTIVE' ? 'DISABLED' : 'ACTIVE' })
    load()
  }

  const remove = async (d: DeptNode) => {
    await http.delete<ApiResponses['DELETE /admin/departments/{id}']>(`/admin/departments/${d.id}`)
    Message.success(`${ORG_KIND[nodeKind(d)].label}已删除`)
    setSelected((current) => current?.id === d.id ? null : current)
    load()
  }

  const stats = treeStats(tree)
  const selectedKind = selected ? nodeKind(selected) : null
  const selectedMeta = selectedKind ? ORG_KIND[selectedKind] : null
  const selectedPath = selected ? findPath(tree, selected.id).map((node) => node.name).join(' > ') : ''
  const selectedParent = selected ? findPath(tree, selected.id).slice(-2, -1)[0] : undefined
  const selectedChildrenCount = selected?.children?.length || 0
  const visibleTree = filterTree(tree, keyword)
  const childNodes = selected ? selected.children || [] : tree
  const navigateTo = (node: DeptNode | null) => {
    setSelected(node)
    setKeyword('')
    setExpandedKeys((current) => [...new Set([...(current || allKeys(tree)), ...(node ? findPath(tree, node.id).map((item) => String(item.id)) : [])])])
  }

  return (
    <Card className="page-card org-view-page">
      <div className="org-view-page-header page-heading">
        <div className="org-view-page-title">
          <h1>组织架构</h1>
          <div className="org-view-summary" aria-label="组织统计">
            <span><b>{stats.division}</b> 个事业部</span>
            <span><b>{stats.department}</b> 个部门</span>
            <span><b>{stats.section}</b> 个课别</span>
          </div>
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

      <div className="org-view-workspace">
        <section className="org-view-tree-panel">
          <div className="org-view-panel-head">
            <div className="org-view-tree-heading">
              <Typography.Text bold>组织目录</Typography.Text>
              <div className="org-view-tree-tools">
                <Button type="text" size="mini" onClick={() => setExpandedKeys(allKeys(visibleTree))}>展开</Button>
                <Button type="text" size="mini" onClick={() => setExpandedKeys([])}>收起</Button>
              </div>
            </div>
            <Input allowClear prefix={<IconSearch />} placeholder="搜索组织名称" value={keyword} onChange={(value) => {
              setKeyword(value)
              setExpandedKeys(allKeys(filterTree(tree, value)))
            }} />
          </div>
          <div className="org-view-tree-scroll">
            {loadError ? (
              <div style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '24px 0' }}>
                <Typography.Text type="error">加载失败</Typography.Text>
                <Button size="small" onClick={load}>重试</Button>
              </div>
            ) : (
              <Spin loading={loading} style={{ width: '100%' }}>
                {visibleTree.length ? <Tree
                  blockNode
                  showLine
                  treeData={toTreeData(visibleTree)}
                  expandedKeys={expandedKeys ?? allKeys(visibleTree)}
                  onExpand={(keys) => setExpandedKeys(keys.map(String))}
                  selectedKeys={selected ? [String(selected.id)] : []}
                  onSelect={(keys) => {
                    const id = Number(keys[0])
                    setSelected(id ? findNode(tree, id) : null)
                  }}
                /> : !loading && <div className="org-view-tree-empty">{keyword.trim() ? '未找到匹配的组织' : '暂无组织'}</div>}
              </Spin>
            )}
          </div>
        </section>

        <section className="org-view-detail-panel">
          <nav className="org-view-breadcrumb" aria-label="层级路径">
            <button type="button" onClick={() => navigateTo(null)}>全部组织</button>
            {selected && findPath(tree, selected.id).map((node) => <span key={node.id}>
              <IconRight />
              <button type="button" title={node.name} aria-current={node.id === selected.id ? 'location' : undefined} onClick={() => navigateTo(node)}>{node.name}</button>
            </span>)}
          </nav>
          {selected && selectedMeta ? (
            <>
              <div className="org-view-detail-head">
                <div className="org-view-identity">
                  <div className="org-view-identity-text">
                    <span className="org-view-detail-kind">{selectedMeta.label}</span>
                    <h2 className="org-view-name">{selected.name}</h2>
                    <div className="org-view-detail-meta"><Tag color={selected.status === 'ACTIVE' ? 'green' : 'red'}>{selected.status === 'ACTIVE' ? '启用' : '禁用'}</Tag><span>排序号 {selected.sortNo}</span></div>
                  </div>
                </div>
                {canManage && (
                  <div className="org-view-actions">
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
                    <Dropdown
                      key={selected.id}
                      trigger="click"
                      position="br"
                      triggerProps={{ escToClose: true }}
                      popupVisible={moreFor === selected.id}
                      onVisibleChange={(visible) => setMoreFor(visible ? selected.id : null)}
                      droplist={<Menu onClickMenuItem={(action) => {
                        setMoreFor(null)
                        const target = selected
                        const label = selectedMeta.label
                        if (action === 'delete' && canDelete) {
                          Modal.confirm({
                            title: `删除${label}`,
                            content: `删除后不可恢复；有下级或关联用户时无法删除。确认删除该${label}？`,
                            okText: '确认删除', cancelText: '取消', okButtonProps: { status: 'danger' },
                            onOk: () => remove(target),
                          })
                        } else if (action === 'status') {
                          const verb = target.status === 'ACTIVE' ? '禁用' : '启用'
                          Modal.confirm({
                            title: `${verb}${label}`,
                            content: `确认${verb}“${target.name}”？`,
                            okText: `确认${verb}`, cancelText: '取消',
                            onOk: () => toggle(target),
                          })
                        }
                      }}>
                        <Menu.Item key="status">{selected.status === 'ACTIVE' ? `禁用${selectedMeta.label}` : `启用${selectedMeta.label}`}</Menu.Item>
                        {canDelete && <Menu.Item key="delete">{`删除${selectedMeta.label}`}</Menu.Item>}
                      </Menu>}
                    >
                      <Button className="org-view-more-trigger" aria-haspopup="menu" aria-expanded={moreFor === selected.id}>
                        <span>更多</span><IconDown />
                      </Button>
                    </Dropdown>
                  </div>
                )}
              </div>
              {!selectedMeta.childLabel && <div className="org-view-leaf-info">
                <h3>基本信息</h3>
                <dl><div><dt>所属部门</dt><dd>{selectedParent?.name || '—'}</dd></div><div><dt>层级路径</dt><dd>{selectedPath}</dd></div></dl>
              </div>}
            </>
          ) : (
            <div className="org-view-overview-head">
              <div><h2 className="org-view-name">全部组织</h2><Typography.Text type="secondary">事业部 · 部门 · 课别</Typography.Text></div>
            </div>
          )}
          {(!selected || selectedMeta?.childLabel) && <div className="org-view-children">
            <div className="org-view-children-heading"><h3>{selected ? `直属${selectedMeta?.childLabel}` : '事业部'}</h3><span>{selected ? selectedChildrenCount : tree.length} 个</span></div>
            {loading ? <Spin /> : loadError ? <div className="org-view-tree-empty">组织加载失败<Button type="text" onClick={load}>重试</Button></div> : childNodes.length ? <div className="org-view-child-list">
              {childNodes.map((node) => <button type="button" className="org-view-child" key={node.id} onClick={() => navigateTo(node)}>
                <span className="org-view-child-main"><strong>{node.name}</strong><small>{ORG_KIND[nodeKind(node)].label}{ORG_KIND[nodeKind(node)].childLabel ? ` · ${node.children?.length || 0} 个${ORG_KIND[nodeKind(node)].childLabel}` : ''}</small></span>
                <span className={`org-view-child-status${node.status === 'DISABLED' ? ' is-disabled' : ''}`}>{node.status === 'ACTIVE' ? '启用' : '禁用'}</span><IconRight />
              </button>)}
            </div> : <div className="org-view-tree-empty">{selected ? `暂无直属${selectedMeta?.childLabel}` : '暂无事业部'}</div>}
          </div>}
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
              <Input maxLength={64} placeholder={`请输入${modalKindMeta.label}名称`} />
            </Form.Item>
            <Form.Item label="排序号" field="sortNo">
              <InputNumber min={0} precision={0} placeholder="数字越小越靠前" style={{ width: '100%' }} />
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
