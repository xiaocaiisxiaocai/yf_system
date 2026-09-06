import { useCallback, useEffect, useState } from 'react'
import { Button, Card, Form, Input, InputNumber, Message, Modal, Popconfirm, Tag, Tree, Typography } from '@arco-design/web-react'
import { IconPlus } from '@arco-design/web-react/icon'
import http from '../../api/client'
import { useAuth } from '../../store/auth'

interface DeptNode {
  id: number
  name: string
  parentId?: number | null
  sortNo: number
  status: 'ACTIVE' | 'DISABLED'
  children?: DeptNode[]
}

interface DeptTreeData { key: string; title: React.ReactNode; children?: DeptTreeData[] }
function allKeys(nodes: DeptNode[]): string[] {
  return nodes.flatMap((n) => [String(n.id), ...(n.children ? allKeys(n.children) : [])])
}

function treeStats(nodes: DeptNode[]) {
  return nodes.reduce((acc, node) => {
    acc.total += 1
    if (node.status === 'ACTIVE') acc.active += 1
    else acc.disabled += 1
    const child = treeStats(node.children || [])
    acc.total += child.total
    acc.active += child.active
    acc.disabled += child.disabled
    return acc
  }, { total: 0, active: 0, disabled: 0 })
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
  return nodes.map((n) => ({
    key: String(n.id),
    title: (
      <span className="dept-tree-node">
        <span className="dept-tree-name">{n.name}</span>
        {n.status === 'DISABLED' && <Tag color="red" size="small">禁用</Tag>}
      </span>
    ),
    children: n.children && n.children.length ? toTreeData(n.children) : undefined,
  }))
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
  const [tree, setTree] = useState<DeptNode[]>([])
  const [selected, setSelected] = useState<DeptNode | null>(null)
  const [editOpen, setEditOpen] = useState(false)
  const [editing, setEditing] = useState<DeptNode | null>(null)
  const [parentForNew, setParentForNew] = useState<number | null>(null)
  const [form] = Form.useForm()

  const fetchTree = useCallback(async () => {
    const r = await http.get('/departments')
    return r.data as DeptNode[]
  }, [])

  const applyTree = useCallback((next: DeptNode[]) => {
    setTree(next)
    setSelected((current) => current ? findNode(next, current.id) : null)
  }, [])

  const load = useCallback(async () => {
    applyTree(await fetchTree())
  }, [applyTree, fetchTree])

  useEffect(() => {
    let active = true
    fetchTree().then((next) => {
      if (active) applyTree(next)
    })
    return () => {
      active = false
    }
  }, [applyTree, fetchTree])

  const openCreate = (parentId: number | null) => {
    setEditing(null)
    setParentForNew(parentId)
    form.resetFields()
    form.setFieldsValue({ name: '', sortNo: 0 })
    setEditOpen(true)
  }

  const submit = async () => {
    const v = await form.validate().catch(() => null)
    if (!v) return
    if (editing) {
      await http.put(`/admin/departments/${editing.id}`, { name: v.name, parentId: editing.parentId ?? null, sortNo: v.sortNo ?? 0 })
      Message.success('部门已更新')
    } else {
      await http.post('/admin/departments', { name: v.name, parentId: parentForNew, sortNo: v.sortNo ?? 0 })
      Message.success('部门已创建')
    }
    setEditOpen(false)
    form.resetFields()
    load()
  }

  const toggle = async (d: DeptNode) => {
    await http.put(`/admin/departments/${d.id}/status`, { status: d.status === 'ACTIVE' ? 'DISABLED' : 'ACTIVE' })
    load()
  }

  const stats = treeStats(tree)
  const selectedPath = selected ? findPath(tree, selected.id).map((node) => node.name).join(' / ') : ''
  const selectedChildrenCount = selected?.children?.length || 0

  return (
    <Card className="page-card dept-page">
      <div className="dept-page-header">
        <div className="dept-page-title">
          <Typography.Title heading={5} style={{ margin: 0 }}>部门管理</Typography.Title>
          <Typography.Text type="secondary">维护公司组织架构、上下级部门和启停状态</Typography.Text>
        </div>
        <div className="dept-summary">
          <div><b>{stats.total}</b><span>总部门</span></div>
          <div><b>{stats.active}</b><span>启用</span></div>
          <div><b>{stats.disabled}</b><span>禁用</span></div>
        </div>
        {canManage && (
          <Button
            type="primary"
            icon={<IconPlus />}
            onClick={() => openCreate(null)}
          >
            新增顶级部门
          </Button>
        )}
      </div>

      <div className="dept-workspace">
        <section className="dept-tree-panel">
          <div className="dept-panel-head">
            <div>
              <Typography.Text bold>组织架构</Typography.Text>
              <Typography.Text type="secondary">点击节点查看和维护部门</Typography.Text>
            </div>
          </div>
          <div className="dept-tree-scroll">
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
          </div>
        </section>

        <section className="dept-detail-panel">
          {selected ? (
            <>
              <div className="dept-detail-head">
                <div>
                  <Typography.Text type="secondary">当前部门</Typography.Text>
                  <Typography.Title heading={5} style={{ margin: '4px 0 0' }}>{selected.name}</Typography.Title>
                </div>
                <Tag color={selected.status === 'ACTIVE' ? 'green' : 'red'}>{selected.status === 'ACTIVE' ? '启用' : '禁用'}</Tag>
              </div>

              <div className="dept-detail-grid">
                <span>层级路径</span><b>{selectedPath || selected.name}</b>
                <span>排序号</span><b>{selected.sortNo}</b>
                <span>子部门</span><b>{selectedChildrenCount} 个</b>
                <span>上级部门</span><b>{selected.parentId ? '有上级部门' : '顶级部门'}</b>
              </div>

              {canManage && (
                <div className="dept-actions">
                  <Button type="primary" onClick={() => openCreate(selected.id)}>新增子部门</Button>
                  <Button
                    onClick={() => {
                      setEditing(selected)
                      form.resetFields()
                      form.setFieldsValue({ name: selected.name, sortNo: selected.sortNo })
                      setEditOpen(true)
                    }}
                  >
                    编辑部门
                  </Button>
                  <Popconfirm title={selected.status === 'ACTIVE' ? '禁用该部门？' : '启用该部门？'} onOk={() => toggle(selected)}>
                    <Button status={selected.status === 'ACTIVE' ? 'danger' : 'success'}>
                      {selected.status === 'ACTIVE' ? '禁用部门' : '启用部门'}
                    </Button>
                  </Popconfirm>
                </div>
              )}
            </>
          ) : (
            <div className="dept-empty-detail">
              <Typography.Title heading={5} style={{ margin: 0 }}>请选择一个部门</Typography.Title>
              <Typography.Text type="secondary">左侧选择部门后，可以在这里查看路径、状态和维护操作。</Typography.Text>
              {canManage && <Button type="primary" icon={<IconPlus />} onClick={() => openCreate(null)}>新增顶级部门</Button>}
            </div>
          )}
        </section>
      </div>

      <Modal
        title={editing ? '编辑部门' : parentForNew ? '新增子部门' : '新增顶级部门'}
        visible={editOpen}
        unmountOnExit
        onOk={submit}
        onCancel={() => {
          setEditOpen(false)
          form.resetFields()
        }}
      >
        <Form form={form} layout="vertical">
          <Form.Item label="部门名称" field="name" rules={[{ required: true, message: '请输入部门名称' }]}>
            <Input maxLength={50} />
          </Form.Item>
          <Form.Item label="排序号" field="sortNo">
            <InputNumber min={0} defaultValue={0} />
          </Form.Item>
        </Form>
      </Modal>
    </Card>
  )
}
