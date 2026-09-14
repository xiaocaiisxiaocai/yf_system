import { textLengthRule } from '../../utils/textRules'
import { useCallback, useEffect, useRef, useState } from 'react'
import {
  Button, Card, Form, Input, Message, Modal, Popconfirm, Select, Space, Table, Tag, Typography,
} from '@arco-design/web-react'
import { IconPlus } from '@arco-design/web-react/icon'
import { Link, useNavigate } from 'react-router-dom'
import http from '../../api/client'
import { actionSlots } from '../../components/ActionSlots'
import { useAuth } from '../../store/auth'
import { type PageResp, type Project, PROJECT_STATUS, fmtTime } from '../../api/types'
import { useCollaboration } from '../../store/collaboration'

interface SupplierOpt {
  id: number
  name: string
}

export default function ProjectList() {
  const revision = useCollaboration((state) => state.revision)
  const syncStatus = useCollaboration((state) => state.status)
  const [data, setData] = useState<PageResp<Project>>({ list: [], total: 0, page: 1, pageSize: 10 })
  const [loading, setLoading] = useState(true)
  const [reloadKey, setReloadKey] = useState(0)
  const [keyword, setKeyword] = useState('')
  const [status, setStatus] = useState<string>()
  const [supplierId, setSupplierId] = useState<number>()
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(10)
  const [loadError, setLoadError] = useState(false)
  const [modalOpen, setModalOpen] = useState(false)
  const [saving, setSaving] = useState(false)
  const [editing, setEditing] = useState<Project | null>(null)
  const [compactTable, setCompactTable] = useState(
    () => typeof window !== 'undefined' && window.matchMedia('(max-width: 1100px)').matches,
  )
  const [suppliers, setSuppliers] = useState<SupplierOpt[]>([])
  const [supplierOptionsLoading, setSupplierOptionsLoading] = useState(true)
  const [supplierOptionsError, setSupplierOptionsError] = useState(false)
  const supplierOptionsSeq = useRef(0)
  const statusUpdatesInFlight = useRef(new Set<number>())
  const [statusUpdatingIds, setStatusUpdatingIds] = useState<Set<number>>(() => new Set())
  const [form] = Form.useForm()
  const nav = useNavigate()
  const { hasPerm, user } = useAuth()
  const isInternal = user?.userType === 'INTERNAL'
  const canDelete = isInternal && hasPerm('project:delete')

  const fetchProjects = useCallback(async () => {
    const r = await http.get('/projects', { params: { page, pageSize, keyword: keyword || undefined, status, supplierId } })
    return r.data as PageResp<Project>
  }, [page, pageSize, keyword, status, supplierId])

  const load = useCallback(() => {
    setLoading(true)
    setLoadError(false)
    setReloadKey((value) => value + 1)
  }, [])

  useEffect(() => {
    if (syncStatus === 'error') return
    let active = true
    fetchProjects()
      .then((next) => {
        if (active) {
          setData(next)
          setLoadError(false)
          const lastPage = Math.max(1, Math.ceil(next.total / (next.pageSize || pageSize)))
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
  }, [fetchProjects, page, pageSize, reloadKey, revision, syncStatus])

  const retrySupplierOptions = () => {
    setSupplierOptionsLoading(true)
    setSupplierOptionsError(false)
    const seq = ++supplierOptionsSeq.current
    if (!isInternal) return
    http.get('/supplier-options')
      .then((r) => {
        if (seq === supplierOptionsSeq.current) setSuppliers(r.data)
      })
      .catch(() => {
        if (seq === supplierOptionsSeq.current) setSupplierOptionsError(true)
      })
      .finally(() => {
        if (seq === supplierOptionsSeq.current) setSupplierOptionsLoading(false)
      })
  }

  useEffect(() => {
    const seq = ++supplierOptionsSeq.current
    if (isInternal) {
      http.get('/supplier-options')
        .then((r) => {
          if (seq === supplierOptionsSeq.current) setSuppliers(r.data)
        })
        .catch(() => {
          if (seq === supplierOptionsSeq.current) setSupplierOptionsError(true)
        })
        .finally(() => {
          if (seq === supplierOptionsSeq.current) setSupplierOptionsLoading(false)
        })
    }
    return () => {
      supplierOptionsSeq.current += 1
    }
  }, [isInternal])

  useEffect(() => {
    if (typeof window === 'undefined') return undefined
    const query = window.matchMedia('(max-width: 1100px)')
    const sync = () => setCompactTable(query.matches)
    query.addEventListener('change', sync)
    return () => query.removeEventListener('change', sync)
  }, [])

  const openCreate = () => {
    if (supplierOptionsLoading || supplierOptionsError || suppliers.length === 0) return
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
    if (saving) return
    const v = await form.validate().catch(() => null)
    if (!v) return
    if (!editing && (supplierOptionsLoading || supplierOptionsError || !suppliers.some((supplier) => supplier.id === Number(v.supplierId)))) {
      Message.error('供应商选项尚未就绪，请重试加载')
      return
    }
    setSaving(true)
    try {
      if (editing) {
        await http.put(`/projects/${editing.id}`, v)
        Message.success('项目已更新')
      } else {
        await http.post('/projects', v)
        Message.success('项目已创建')
      }
      setModalOpen(false)
      load()
    } finally {
      setSaving(false)
    }
  }

  const changeStatus = async (p: Project, next: string) => {
    if (statusUpdatesInFlight.current.has(p.id)) return
    statusUpdatesInFlight.current.add(p.id)
    setStatusUpdatingIds(new Set(statusUpdatesInFlight.current))
    try {
      await http.put(`/projects/${p.id}/status`, { status: next })
      Message.success('状态已更新')
      load()
    } finally {
      statusUpdatesInFlight.current.delete(p.id)
      setStatusUpdatingIds(new Set(statusUpdatesInFlight.current))
    }
  }

  const remove = async (p: Project) => {
    await http.delete(`/projects/${p.id}`)
    Message.success('项目已删除')
    load()
  }

  const statusActions = (p: Project) => {
    const opts: { key: string; text: string }[] = []
    if (p.status === 'DRAFT') opts.push({ key: 'IN_PROGRESS', text: '开始' })
    if (p.status === 'IN_PROGRESS') opts.push({ key: 'TERMINATED', text: '终止' })
    if (p.status === 'TERMINATED') opts.push({ key: 'IN_PROGRESS', text: '重新开始' })
    return opts
  }

  // 不做 useMemo：列里的操作回调引用 load/changeStatus 等每次渲染更新的闭包，
  // memo 化会捕获过期闭包，导致状态变更后用旧筛选条件刷新列表
  const columns = [
      {
        title: '项目名称',
        dataIndex: 'name',
        width: compactTable ? 180 : 200,
        ellipsis: true,
        render: (v: string, r: Project) => (
          <Link to={`/projects/${r.id}`}>{v}</Link>
        ),
      },
      { title: '供应商', dataIndex: 'supplierName', width: compactTable ? 140 : 160, ellipsis: true },
      {
        title: '状态',
        dataIndex: 'status',
        width: 112,
        align: 'center' as const,
        render: (v: string) => <Tag color={PROJECT_STATUS[v]?.color}>
          {v === 'PENDING_CONFIRMATION'
            ? '待公司内部验收'
            : PROJECT_STATUS[v]?.text || v}
        </Tag>,
      },
      ...(!compactTable ? [
        { title: '创建人', dataIndex: 'createdByName', width: 100, align: 'center' as const, ellipsis: true },
        { title: '更新时间', dataIndex: 'updatedAt', width: 170, align: 'center' as const, render: fmtTime },
      ] : []),
      {
        title: '操作',
        width: compactTable ? 252 : 272,
        fixed: compactTable ? undefined : 'right' as const,
        align: 'center' as const,
        render: (_: unknown, r: Project) => {
          const nextStatuses = statusActions(r)
          return actionSlots([
            <Button key="enter" size="mini" type="text" onClick={() => nav(`/projects/${r.id}`)}>
              进入
            </Button>,
            isInternal && hasPerm('project:update') && ['DRAFT', 'IN_PROGRESS'].includes(r.status) && (
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
                disabled={statusUpdatingIds.has(r.id)}
                loading={statusUpdatingIds.has(r.id)}
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
            canDelete && ['DRAFT', 'TERMINATED'].includes(r.status) && (
              <Popconfirm key="delete" title="仅可删除没有文件和留言的项目，删除后不可恢复。确认？" onOk={() => remove(r)}>
                <Button size="mini" type="text" status="danger">删除</Button>
              </Popconfirm>
            ),
          ], 'project')
        },
      },
    ]

  return (
    <Card className="page-card page-card--table">
      <div className="page-heading">
        <div>
          <h1>项目协作</h1>
        </div>
      </div>

      <div className="page-toolbar responsive-toolbar">
        <Space>
          <Input.Search
            allowClear
            placeholder="项目名称"
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
          <Select
            allowClear
            placeholder="状态"
            style={{ width: 130 }}
            onChange={(v) => {
               setLoading(true); setLoadError(false); setReloadKey((value) => value + 1)
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
              loading={supplierOptionsLoading}
              disabled={supplierOptionsError}
              onChange={(v) => { setLoading(true); setLoadError(false); setReloadKey((value) => value + 1); setPage(1); setSupplierId(v as number | undefined) }}
            >
              {suppliers.map((s) => <Select.Option key={s.id} value={s.id}>{s.name}</Select.Option>)}
            </Select>
          )}
        </Space>
        <Space>
          {isInternal && supplierOptionsError && <Button size="small" onClick={retrySupplierOptions}>重试加载供应商</Button>}
          {isInternal && !supplierOptionsLoading && !supplierOptionsError && suppliers.length === 0 && (
            <Typography.Text type="warning">暂无启用的供应商</Typography.Text>
          )}
          {isInternal && hasPerm('project:create') && (
            <Button type="primary" icon={<IconPlus />} onClick={openCreate} disabled={supplierOptionsLoading || supplierOptionsError || suppliers.length === 0}>
              新建项目
            </Button>
          )}
        </Space>
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
          columns={columns}
          data={data.list}
          scroll={{ y: 'var(--page-table-scroll-y)' }}
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
        style={{ width: 640 }}
        title={editing ? '编辑项目' : '新建项目'}
        visible={modalOpen}
        onOk={submit}
        onCancel={() => { if (!saving) setModalOpen(false) }}
        confirmLoading={saving}
        closable={!saving}
        maskClosable={!saving}
        escToExit={!saving}
        cancelButtonProps={{ disabled: saving }}
        okText={editing ? '保存修改' : '创建项目'}
        cancelText="取消"
        autoFocus={false}
      >
        <Form className="form-grid" form={form} layout="vertical">
          <Form.Item label="项目名称" field="name" rules={[{ required: true, message: '请输入项目名称' }, textLengthRule('项目名称', 128)]}>
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
          <Form.Item className="form-grid-full" label="项目说明" field="description">
            <Input.TextArea rows={3} maxLength={500} showWordLimit placeholder="选填" />
          </Form.Item>
        </Form>
      </Modal>
    </Card>
  )
}
