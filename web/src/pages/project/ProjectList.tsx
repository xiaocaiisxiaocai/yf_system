import { useCallback, useEffect, useState } from 'react'
import {
  Button, Card, Form, Input, Message, Modal, Select, Space, Table, Tag,
} from '@arco-design/web-react'
import { IconPlus } from '@arco-design/web-react/icon'
import { useNavigate } from 'react-router-dom'
import http from '../../api/client'
import { actionSlots } from '../../components/ActionSlots'
import { useAuth } from '../../store/auth'
import { type PageResp, type Project, PROJECT_STATUS, fmtTime } from '../../api/types'

interface SupplierOpt {
  id: number
  name: string
}

export default function ProjectList() {
  const [data, setData] = useState<PageResp<Project>>({ list: [], total: 0, page: 1, pageSize: 10 })
  const [loading, setLoading] = useState(true)
  const [reloadKey, setReloadKey] = useState(0)
  const [keyword, setKeyword] = useState('')
  const [status, setStatus] = useState<string>()
  const [supplierId, setSupplierId] = useState<number>()
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(10)
  const [modalOpen, setModalOpen] = useState(false)
  const [editing, setEditing] = useState<Project | null>(null)
  const [suppliers, setSuppliers] = useState<SupplierOpt[]>([])
  const [form] = Form.useForm()
  const nav = useNavigate()
  const { hasPerm, user } = useAuth()
  const isInternal = user?.userType === 'INTERNAL'

  const fetchProjects = useCallback(async () => {
    const r = await http.get('/projects', { params: { page, pageSize, keyword: keyword || undefined, status, supplierId } })
    return r.data as PageResp<Project>
  }, [page, pageSize, keyword, status, supplierId])

  const load = useCallback(async () => {
    setLoading(true)
    try {
      setData(await fetchProjects())
    } finally {
      setLoading(false)
    }
  }, [fetchProjects])

  useEffect(() => {
    let active = true
    fetchProjects()
      .then((next) => {
        if (active) setData(next)
      })
      .finally(() => {
        if (active) setLoading(false)
      })
    return () => {
      active = false
    }
  }, [fetchProjects, reloadKey])

  useEffect(() => {
    if (isInternal) {
      http.get('/supplier-options').then((r) => setSuppliers(r.data)).catch(() => setSuppliers([]))
    }
  }, [isInternal])

  const openCreate = () => {
    setEditing(null)
    form.resetFields()
    setModalOpen(true)
  }
  const openEdit = (p: Project) => {
    setEditing(p)
    form.setFieldsValue({ name: p.name, description: p.description, supplierId: p.supplierId })
    setModalOpen(true)
  }

  const submit = async () => {
    const v = await form.validate().catch(() => null)
    if (!v) return
    if (editing) {
      await http.put(`/projects/${editing.id}`, v)
      Message.success('项目已更新')
    } else {
      await http.post('/projects', v)
      Message.success('项目已创建')
    }
    setModalOpen(false)
    load()
  }

  const changeStatus = async (p: Project, next: string) => {
    await http.put(`/projects/${p.id}/status`, { status: next })
    Message.success('状态已更新')
    load()
  }

  const statusActions = (p: Project) => {
    const opts: { key: string; text: string }[] = []
    if (p.status === 'DRAFT') opts.push({ key: 'IN_PROGRESS', text: '开工' })
    if (p.status === 'IN_PROGRESS') {
      opts.push({ key: 'COMPLETED', text: '完成' })
      opts.push({ key: 'TERMINATED', text: '终止' })
    }
    return opts
  }

  // 不做 useMemo：列里的操作回调引用 load/changeStatus 等每次渲染更新的闭包，
  // memo 化会捕获过期闭包，导致状态变更后用旧筛选条件刷新列表
  const columns = [
      {
        title: '项目名称',
        dataIndex: 'name',
        width: 220,
        ellipsis: true,
        render: (v: string, r: Project) => (
          <a onClick={() => nav(`/projects/${r.id}`)}>{v}</a>
        ),
      },
      { title: '供应商', dataIndex: 'supplierName', width: 180, ellipsis: true },
      {
        title: '状态',
        dataIndex: 'status',
        width: 80,
        align: 'center' as const,
        render: (v: string) => <Tag color={PROJECT_STATUS[v]?.color}>{PROJECT_STATUS[v]?.text || v}</Tag>,
      },
      { title: '创建人', dataIndex: 'createdByName', width: 120, align: 'center' as const, ellipsis: true },
      { title: '更新时间', dataIndex: 'updatedAt', width: 180, align: 'center' as const, render: fmtTime },
      {
        title: '操作',
        width: 228,
        align: 'center' as const,
        render: (_: unknown, r: Project) => {
          const nextStatuses = statusActions(r)
          return actionSlots([
            <Button key="enter" size="mini" type="text" onClick={() => nav(`/projects/${r.id}`)}>
              进入
            </Button>,
            isInternal && hasPerm('project:update') && (
              <Button key="edit" size="mini" type="text" onClick={() => openEdit(r)}>
                编辑
              </Button>
            ),
            isInternal && hasPerm('project:status') && nextStatuses.length > 0 && (
              <Select
                key="status"
                size="mini"
                placeholder="状态"
                style={{ width: 92 }}
                value={undefined}
                onChange={(v) => changeStatus(r, v as string)}
                triggerProps={{ autoAlignPopupWidth: false }}
              >
                {nextStatuses.map((o) => (
                  <Select.Option key={o.key} value={o.key}>
                    {o.text}
                  </Select.Option>
                ))}
              </Select>
            ),
          ], 'project')
        },
      },
    ]

  return (
    <Card className="page-card page-card--table">
      <Space className="responsive-toolbar" style={{ marginBottom: 16, width: '100%', justifyContent: 'space-between' }}>
        <Space>
          <Input.Search
            allowClear
            placeholder="项目名称"
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
          <Select
            allowClear
            placeholder="状态"
            style={{ width: 130 }}
            onChange={(v) => {
              setLoading(true); setReloadKey((value) => value + 1)
              setPage(1)
              setStatus(v as string | undefined)
            }}
          >
            {Object.entries(PROJECT_STATUS).map(([k, v]) => (
              <Select.Option key={k} value={k}>
                {v.text}
              </Select.Option>
            ))}
          </Select>
          {isInternal && (
            <Select
              allowClear
              showSearch
              placeholder="供应商"
              style={{ width: 180 }}
              value={supplierId}
              onChange={(v) => { setLoading(true); setReloadKey((value) => value + 1); setPage(1); setSupplierId(v as number | undefined) }}
            >
              {suppliers.map((s) => <Select.Option key={s.id} value={s.id}>{s.name}</Select.Option>)}
            </Select>
          )}
        </Space>
        {isInternal && hasPerm('project:create') && (
          <Button type="primary" icon={<IconPlus />} onClick={openCreate}>
            新建项目
          </Button>
        )}
      </Space>
      <Table
        className="page-table"
        rowKey="id"
        loading={loading}
        columns={columns}
        data={data.list}
        scroll={{ x: 1028, y: 'var(--page-table-scroll-y)' }}
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

      <Modal
        title={editing ? '编辑项目' : '新建项目'}
        visible={modalOpen}
        onOk={submit}
        onCancel={() => setModalOpen(false)}
        autoFocus={false}
      >
        <Form form={form} layout="vertical">
          <Form.Item label="项目名称" field="name" rules={[{ required: true, message: '请输入项目名称' }]}>
            <Input placeholder="项目名称" />
          </Form.Item>
          <Form.Item label="关联供应商" field="supplierId" rules={[{ required: true, message: '请选择供应商' }]}>
            <Select
              placeholder="选择供应商"
              showSearch
              filterOption={(input, option) =>
                String(option?.props?.children ?? '').toLowerCase().includes(input.toLowerCase())
              }
              disabled={!!editing}
            >
              {suppliers.map((s) => (
                <Select.Option key={s.id} value={s.id}>
                  {s.name}
                </Select.Option>
              ))}
            </Select>
          </Form.Item>
          <Form.Item label="项目说明" field="description">
            <Input.TextArea rows={3} maxLength={500} showWordLimit />
          </Form.Item>
        </Form>
      </Modal>
    </Card>
  )
}
