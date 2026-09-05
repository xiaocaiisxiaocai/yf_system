import { useCallback, useEffect, useState } from 'react'
import { Button, Card, Form, Input, InputNumber, Message, Modal, Popconfirm, Space, Tag, Tree, Typography } from '@arco-design/web-react'
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
function toTreeData(nodes: DeptNode[]): DeptTreeData[] {
  return nodes.map((n) => ({
    key: String(n.id),
    title: (
      <Space>
        <span>{n.name}</span>
        {n.status === 'DISABLED' && <Tag color="red" size="small">禁用</Tag>}
      </Space>
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

  return (
    <Card className="page-card">
      <Space className="responsive-toolbar" style={{ marginBottom: 16, width: '100%', justifyContent: 'space-between' }}>
        <Typography.Text type="secondary">组织架构树：点击节点查看操作</Typography.Text>
        {canManage && <Button
          type="primary"
          icon={<IconPlus />}
          onClick={() => openCreate(null)}
        >
          新增顶级部门
        </Button>}
      </Space>
      <Tree
        treeData={toTreeData(tree)}
        defaultExpandedKeys={allKeys(tree)}
        selectedKeys={selected ? [String(selected.id)] : []}
        onSelect={(keys) => {
          const id = Number(keys[0])
          setSelected(id ? findNode(tree, id) : null)
        }}
      />
      {selected && (
        <Card style={{ marginTop: 16, background: 'var(--color-fill-1)' }} size="small">
          <Space>
            <Typography.Text bold>{selected.name}</Typography.Text>
            <Tag color={selected.status === 'ACTIVE' ? 'green' : 'red'}>{selected.status === 'ACTIVE' ? '启用' : '禁用'}</Tag>
            {canManage && <><Button
              size="mini"
              onClick={() => openCreate(selected.id)}
            >
              新增子部门
            </Button>
            <Button
              size="mini"
              onClick={() => {
                setEditing(selected)
                form.resetFields()
                form.setFieldsValue({ name: selected.name, sortNo: selected.sortNo })
                setEditOpen(true)
              }}
            >
              编辑
            </Button>
            <Popconfirm title={selected.status === 'ACTIVE' ? '禁用该部门？' : '启用该部门？'} onOk={() => toggle(selected)}>
              <Button size="mini" status={selected.status === 'ACTIVE' ? 'danger' : 'success'}>
                {selected.status === 'ACTIVE' ? '禁用' : '启用'}
              </Button>
            </Popconfirm>
            </>}
          </Space>
        </Card>
      )}

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
