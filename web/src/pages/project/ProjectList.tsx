import { textLengthRule } from '../../utils/textRules'
import { useCallback, useEffect, useRef, useState } from 'react'
import {
  Button, Card, DatePicker, Form, Input, Message, Modal, Popconfirm, Select, Space, Table, Tag, Typography,
} from '@arco-design/web-react'
import { IconPlus } from '@arco-design/web-react/icon'
import { Link, useNavigate } from 'react-router-dom'
import http, { type QuietRequestConfig } from '../../api/client'
import { actionSlots } from '../../components/ActionSlots'
import { useAuth } from '../../store/auth'
import {
  type PageResp,
  type Project,
  type ProjectCopyResult,
  type ProjectDictionaryOption,
  type ProjectOwnerOption,
  PROJECT_STATUS,
  fmtTime,
} from '../../api/types'
import { useCollaboration } from '../../store/collaboration'

interface SupplierOpt {
  id: number
  name: string
}

interface ProjectFormValues {
  name?: string
  description?: string
  supplierId?: number
  workOrderNos?: string[]
  machineModel?: string
  robotVendorId?: number
  robotModelId?: number
  responsibleUserId?: number
  priorityId?: number
  expectedCompletionDate?: string
}

interface ProjectCopyFormValues {
  name?: string
}

const WORK_ORDER_LIMIT = 50

function suggestedCopyName(name: string) {
  const suffix = ' - 副本'
  const room = 128 - [...suffix].length
  return `${[...name.trim()].slice(0, room).join('').trimEnd()}${suffix}`
}

function optionFilter(input: string, option?: { props?: { children?: unknown } }) {
  return String(option?.props?.children ?? '').toLocaleLowerCase().includes(input.toLocaleLowerCase())
}

function normalizeWorkOrderNos(values?: string[]) {
  const unique = new Set<string>()
  for (const value of values ?? []) {
    const normalized = String(value).trim()
    if (normalized) unique.add(normalized)
  }
  return [...unique]
}

function workOrderNosRule(value: unknown, callback: (error?: string) => void) {
  const values = normalizeWorkOrderNos(Array.isArray(value) ? value.map(String) : [])
  if (!values.length) { callback('请至少填写一个工令号'); return }
  if (values.length > WORK_ORDER_LIMIT) {
    callback(`工令号最多填写 ${WORK_ORDER_LIMIT} 项`)
    return
  }
  if (values.some((item) => [...item].length > 128)) {
    callback('每个工令号最多 128 个字符')
    return
  }
  callback()
}

function nullableNumber(value: unknown): number | null {
  const parsed = Number(value)
  return Number.isSafeInteger(parsed) && parsed > 0 ? parsed : null
}

function displayText(value?: string | null) {
  return value?.trim() || '-'
}

function expectOptionList<T>(value: unknown): T[] {
  if (!Array.isArray(value)) throw new Error('选项接口返回格式错误')
  return value as T[]
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
  const saveInFlight = useRef(false)
  const [copySource, setCopySource] = useState<Project | null>(null)
  const [copying, setCopying] = useState(false)
  const copyInFlight = useRef(false)
  const [editing, setEditing] = useState<Project | null>(null)
  const [suppliers, setSuppliers] = useState<SupplierOpt[]>([])
  const [supplierOptionsLoading, setSupplierOptionsLoading] = useState(true)
  const [supplierOptionsError, setSupplierOptionsError] = useState(false)
  const supplierOptionsSeq = useRef(0)
  const [robotVendors, setRobotVendors] = useState<ProjectDictionaryOption[]>([])
  const [priorities, setPriorities] = useState<ProjectDictionaryOption[]>([])
  const [owners, setOwners] = useState<ProjectOwnerOption[]>([])
  const [metadataOptionsLoading, setMetadataOptionsLoading] = useState(false)
  const [metadataOptionsError, setMetadataOptionsError] = useState(false)
  const metadataOptionsSeq = useRef(0)
  const [robotModels, setRobotModels] = useState<ProjectDictionaryOption[]>([])
  const [robotModelsLoading, setRobotModelsLoading] = useState(false)
  const [robotModelsError, setRobotModelsError] = useState(false)
  const robotModelsSeq = useRef(0)
  const [selectedRobotVendorId, setSelectedRobotVendorId] = useState<number | null>(null)
  const [selectedResponsibleUserId, setSelectedResponsibleUserId] = useState<number | null>(null)
  const [sectionName, setSectionName] = useState<string | null>(null)
  const statusUpdatesInFlight = useRef(new Set<number>())
  const [statusUpdatingIds, setStatusUpdatingIds] = useState<Set<number>>(() => new Set())
  const [form] = Form.useForm()
  const [copyForm] = Form.useForm()
  const nav = useNavigate()
  const { hasPerm, user } = useAuth()
  const isInternal = user?.userType === 'INTERNAL'
  const canDelete = isInternal && hasPerm('project:delete')

  const fetchProjects = useCallback(async () => {
    const r = await http.get('/projects', { params: { page, pageSize, keyword: keyword || undefined, status, supplierId }, quietNetworkError: true } as QuietRequestConfig)
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
    http.get('/supplier-options', { quietNetworkError: true } as QuietRequestConfig)
      .then((r) => {
        if (seq === supplierOptionsSeq.current) setSuppliers(expectOptionList<SupplierOpt>(r.data))
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
      http.get('/supplier-options', { quietNetworkError: true } as QuietRequestConfig)
        .then((r) => {
          if (seq === supplierOptionsSeq.current) setSuppliers(expectOptionList<SupplierOpt>(r.data))
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

  const loadMetadataOptions = useCallback(() => {
    const seq = ++metadataOptionsSeq.current
    setMetadataOptionsLoading(true)
    setMetadataOptionsError(false)
    Promise.all([
      http.get('/project-dictionaries', { params: { type: 'ROBOT_VENDOR', enabledOnly: true }, quietNetworkError: true } as QuietRequestConfig),
      http.get('/project-dictionaries', { params: { type: 'PRIORITY', enabledOnly: true }, quietNetworkError: true } as QuietRequestConfig),
      http.get('/project-owner-options', { quietNetworkError: true } as QuietRequestConfig),
    ])
      .then(([vendorResponse, priorityResponse, ownerResponse]) => {
        if (seq !== metadataOptionsSeq.current) return
        setRobotVendors(expectOptionList<ProjectDictionaryOption>(vendorResponse.data))
        setPriorities(expectOptionList<ProjectDictionaryOption>(priorityResponse.data))
        setOwners(expectOptionList<ProjectOwnerOption>(ownerResponse.data))
      })
      .catch(() => {
        if (seq === metadataOptionsSeq.current) setMetadataOptionsError(true)
      })
      .finally(() => {
        if (seq === metadataOptionsSeq.current) setMetadataOptionsLoading(false)
      })
  }, [])

  const loadRobotModels = useCallback((vendorId: number) => {
    const seq = ++robotModelsSeq.current
    setRobotModels([])
    setRobotModelsLoading(true)
    setRobotModelsError(false)
    http.get('/project-dictionaries', {
      params: { type: 'ROBOT_MODEL', enabledOnly: true, parentId: vendorId },
      quietNetworkError: true,
    } as QuietRequestConfig)
      .then((response) => {
        if (seq === robotModelsSeq.current) setRobotModels(expectOptionList<ProjectDictionaryOption>(response.data))
      })
      .catch(() => {
        if (seq === robotModelsSeq.current) setRobotModelsError(true)
      })
      .finally(() => {
        if (seq === robotModelsSeq.current) setRobotModelsLoading(false)
      })
  }, [])

  useEffect(() => () => {
    metadataOptionsSeq.current += 1
    robotModelsSeq.current += 1
  }, [])

  const openCreate = () => {
    if (supplierOptionsLoading || supplierOptionsError || suppliers.length === 0) return
    setEditing(null)
    form.resetFields()
    setSectionName(null)
    setSelectedRobotVendorId(null)
    setSelectedResponsibleUserId(null)
    setRobotModels([])
    setRobotModelsLoading(false)
    setRobotModelsError(false)
    robotModelsSeq.current += 1
    loadMetadataOptions()
    setModalOpen(true)
  }
  const openEdit = (p: Project) => {
    setEditing(p)
    form.setFieldsValue({
      name: p.name,
      description: p.description,
      supplierId: p.supplierId,
      workOrderNos: p.workOrderNos ?? [],
      machineModel: p.machineModel ?? undefined,
      robotVendorId: p.robotVendorId ?? undefined,
      robotModelId: p.robotModelId ?? undefined,
      responsibleUserId: p.responsibleUserId ?? undefined,
      priorityId: p.priorityId ?? undefined,
      expectedCompletionDate: p.expectedCompletionDate ?? undefined,
    })
    setSectionName(p.sectionName ?? null)
    setSelectedRobotVendorId(p.robotVendorId ?? null)
    setSelectedResponsibleUserId(p.responsibleUserId ?? null)
    loadMetadataOptions()
    if (p.robotVendorId) loadRobotModels(p.robotVendorId)
    else {
      robotModelsSeq.current += 1
      setRobotModels([])
      setRobotModelsLoading(false)
      setRobotModelsError(false)
    }
    setModalOpen(true)
  }

  const closeModal = () => {
    if (saving) return
    metadataOptionsSeq.current += 1
    robotModelsSeq.current += 1
    setMetadataOptionsLoading(false)
    setRobotModelsLoading(false)
    setModalOpen(false)
  }

  const openCopy = (project: Project) => {
    setCopySource(project)
    copyForm.setFieldsValue({ name: suggestedCopyName(project.name) })
  }

  const closeCopy = () => {
    if (copyInFlight.current) return
    setCopySource(null)
    copyForm.resetFields()
  }

  const submitCopy = async () => {
    if (!copySource || copyInFlight.current) return
    copyInFlight.current = true
    setCopying(true)
    try {
      const values = await copyForm.validate().catch(() => null) as ProjectCopyFormValues | null
      if (!values) return
      const name = values.name?.trim() ?? ''
      if (name === copySource.name.trim()) {
        copyForm.setFieldValue('name', name)
        Message.error('新项目名称不能与原项目相同')
        return
      }
      const response = await http.post(`/projects/${copySource.id}/copy`, { name })
      const result = response.data as ProjectCopyResult
      setCopySource(null)
      copyForm.resetFields()
      setCopying(false)
      copyInFlight.current = false
      if (Number.isSafeInteger(result?.project?.id) && result.project.id > 0) {
        Message.success(`项目已复制，包含 ${result.copy?.fileCount ?? 0} 个文件`)
        nav(`/projects/${result.project.id}`)
      } else {
        Message.warning('项目已复制，返回信息不完整，请在列表中查看')
        load()
      }
      return
    } catch {
      // 请求失败时保留名称与源项目，便于用户修正后重试。
    } finally {
      if (copyInFlight.current) {
        setCopying(false)
        copyInFlight.current = false
      }
    }
  }

  const submit = async () => {
    if (saveInFlight.current) return
    saveInFlight.current = true
    setSaving(true)
    try {
      const v = await form.validate().catch(() => null) as ProjectFormValues | null
      if (!v) return
      if (!sectionName) {
        Message.error('负责人未关联课别，请先在用户管理中设置其所属课别')
        return
      }
      if (!editing && (supplierOptionsLoading || supplierOptionsError || !suppliers.some((supplier) => supplier.id === Number(v.supplierId)))) {
        Message.error('供应商选项尚未就绪，请重试加载')
        return
      }
      const robotVendorId = nullableNumber(v.robotVendorId)
      const payload = {
        name: v.name,
        description: v.description,
        supplierId: Number(v.supplierId),
        workOrderNos: normalizeWorkOrderNos(v.workOrderNos),
        machineModel: v.machineModel?.trim() || null,
        robotVendorId,
        robotModelId: robotVendorId ? nullableNumber(v.robotModelId) : null,
        responsibleUserId: nullableNumber(v.responsibleUserId),
        priorityId: nullableNumber(v.priorityId),
        expectedCompletionDate: v.expectedCompletionDate || null,
      }
      if (editing) {
        await http.put(`/projects/${editing.id}`, payload)
        Message.success('项目已更新')
      } else {
        await http.post('/projects', payload)
        Message.success('项目已创建')
      }
      setModalOpen(false)
      load()
    } catch {
      // Keep the form when a dictionary or owner changed before the save reached the server.
    } finally {
      setSaving(false)
      saveInFlight.current = false
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
    } catch {
      // 请求层已展示错误；保持当前列表状态并恢复操作入口供用户重试。
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

  const vendorOptions = [...robotVendors]
  if (editing?.robotVendorId && !vendorOptions.some((option) => option.id === editing.robotVendorId)) {
    vendorOptions.push({
      id: editing.robotVendorId,
      type: 'ROBOT_VENDOR',
      code: editing.robotVendorCode ?? '',
      name: editing.robotVendorName || '历史厂商',
      parentId: null,
      sortNo: 0,
      enabled: false,
    })
  }
  const modelOptions = [...robotModels]
  if (editing?.robotModelId && selectedRobotVendorId === editing.robotVendorId
      && !modelOptions.some((option) => option.id === editing.robotModelId)) {
    modelOptions.push({
      id: editing.robotModelId,
      type: 'ROBOT_MODEL',
      code: editing.robotModelCode ?? '',
      name: editing.robotModelName || '历史型号',
      parentId: editing.robotVendorId ?? null,
      sortNo: 0,
      enabled: false,
    })
  }
  const priorityOptions = [...priorities]
  if (editing?.priorityId && !priorityOptions.some((option) => option.id === editing.priorityId)) {
    priorityOptions.push({
      id: editing.priorityId,
      type: 'PRIORITY',
      code: editing.priorityCode ?? '',
      name: editing.priorityName || '历史优先级',
      parentId: null,
      sortNo: 0,
      enabled: false,
    })
  }
  const ownerOptions = [...owners]
  if (editing?.responsibleUserId && !ownerOptions.some((option) => option.id === editing.responsibleUserId)) {
    ownerOptions.push({
      id: editing.responsibleUserId,
      employeeNo: editing.responsibleUserEmployeeNo ?? '',
      realName: editing.responsibleUserName || '历史负责人',
      sectionId: editing.sectionId ?? null,
      sectionName: editing.sectionName ?? null,
    })
  }

  const changeRobotVendor = (value?: number) => {
    const vendorId = nullableNumber(value)
    setSelectedRobotVendorId(vendorId)
    form.setFieldValue('robotModelId', undefined)
    robotModelsSeq.current += 1
    setRobotModels([])
    setRobotModelsLoading(false)
    setRobotModelsError(false)
    if (vendorId) loadRobotModels(vendorId)
  }

  const changeResponsibleUser = (value?: number) => {
    const ownerId = nullableNumber(value)
    setSelectedResponsibleUserId(ownerId)
    setSelectedResponsibleUserId(ownerId)
    const owner = ownerOptions.find((option) => option.id === ownerId)
    setSectionName(owner?.sectionName ?? null)
  }

  const retryRobotModels = () => {
    if (selectedRobotVendorId) loadRobotModels(selectedRobotVendorId)
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
      width: 150,
      fixed: 'left' as const,
      ellipsis: true,
      render: (v: string, r: Project) => <Link to={`/projects/${r.id}`}>{v}</Link>,
    },
    {
      title: '工令号',
      dataIndex: 'workOrderNos',
      width: 200,
      ellipsis: true,
      render: (values?: string[]) => displayText(values?.join('、')),
    },
    { title: '机型', dataIndex: 'machineModel', width: 110, ellipsis: true, render: displayText },
    { title: 'Robot 厂商', dataIndex: 'robotVendorName', width: 110, ellipsis: true, render: displayText },
    { title: 'Robot 型号', dataIndex: 'robotModelName', width: 110, ellipsis: true, render: displayText },
    { title: '负责人', dataIndex: 'responsibleUserName', width: 88, ellipsis: true, render: displayText },
    { title: '课别', dataIndex: 'sectionName', width: 96, ellipsis: true, render: displayText },
    { title: '优先级', dataIndex: 'priorityName', width: 80, align: 'center' as const, ellipsis: true, render: displayText },
    { title: '预计完成日期', dataIndex: 'expectedCompletionDate', width: 120, align: 'center' as const, render: displayText },
    { title: '供应商', dataIndex: 'supplierName', width: 120, ellipsis: true, render: displayText },
    {
      title: '状态',
      dataIndex: 'status',
      width: 96,
      align: 'center' as const,
      render: (v: string) => <Tag color={PROJECT_STATUS[v]?.color}>{PROJECT_STATUS[v]?.text || v}</Tag>,
    },
    { title: '创建人', dataIndex: 'createdByName', width: 88, align: 'center' as const, ellipsis: true, render: displayText },
    { title: '更新时间', dataIndex: 'updatedAt', width: 164, align: 'center' as const, render: fmtTime },
    {
      title: '操作',
      width: 256,
      fixed: 'right' as const,
      align: 'center' as const,
      render: (_: unknown, r: Project) => {
        const nextStatuses = statusActions(r)
        return actionSlots([
          <Button key="enter" size="mini" type="text" onClick={() => nav(`/projects/${r.id}`)}>
            进入
          </Button>,
          isInternal && hasPerm('project:create') && (
            <Button key="copy" size="mini" type="text" onClick={() => openCopy(r)}>
              复制
            </Button>
          ),
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
          scroll={{ x: columns.reduce((width, column) => width + column.width, 0), y: 'var(--page-table-scroll-y)' }}
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
        style={{ width: 760 }}
        title={editing ? '编辑项目' : '新建项目'}
        visible={modalOpen}
        onOk={submit}
        onCancel={closeModal}
        confirmLoading={saving}
        closable={!saving}
        maskClosable={!saving}
        escToExit={!saving}
        cancelButtonProps={{ disabled: saving }}
        okText={editing ? '保存修改' : '创建项目'}
        cancelText="取消"
      >
        <Form className="form-grid" form={form} layout="vertical">
          <Form.Item className="form-grid-full" label="项目名称" field="name" rules={[{ required: true, message: '请输入项目名称' }, textLengthRule('项目名称', 128)]}>
            <Input placeholder="项目名称" />
          </Form.Item>
          <Form.Item className="form-grid-full" label="工令号" field="workOrderNos" rules={[{ required: true, message: '请至少填写一个工令号' }, { validator: workOrderNosRule }]}>
            <Select
              mode="multiple"
              allowCreate
              allowClear
              showSearch
              maxTagCount={3}
              tokenSeparators={[',', '，', ';', '；', '\n']}
              placeholder="输入工令号后按回车，可填写多个"
            />
          </Form.Item>
          <Form.Item label="机型" field="machineModel" rules={[{ required: true, message: '请填写机型' }, { validator: (value, callback) => callback(value?.trim() ? undefined : '请填写机型') }]}>
            <Input maxLength={128} showWordLimit placeholder="请输入机型" />
          </Form.Item>
          <Form.Item label="Robot 厂商" field="robotVendorId" rules={[{ required: true, message: '请选择 Robot 厂商' }]}>
            <Select
              allowClear
              showSearch
              placeholder="选择Robot 厂商"
              loading={metadataOptionsLoading}
              disabled={metadataOptionsLoading || metadataOptionsError}
              filterOption={optionFilter}
              onChange={(value) => changeRobotVendor(value as number | undefined)}
            >
              {vendorOptions.map((option) => (
                <Select.Option key={option.id} value={option.id} disabled={!option.enabled}>
                  {option.name}{option.enabled ? '' : '（已停用）'}
                </Select.Option>
              ))}
            </Select>
          </Form.Item>
          <Form.Item label="Robot 型号" field="robotModelId" rules={[{ required: true, message: '请选择 Robot 型号' }]}>
            <Select
              allowClear
              showSearch
              placeholder={selectedRobotVendorId ? '选择Robot 型号' : '请先选择Robot 厂商'}
              loading={robotModelsLoading}
              disabled={!selectedRobotVendorId || robotModelsLoading || robotModelsError}
              filterOption={optionFilter}
            >
              {modelOptions.map((option) => (
                <Select.Option key={option.id} value={option.id} disabled={!option.enabled}>
                  {option.name}{option.enabled ? '' : '（已停用）'}
                </Select.Option>
              ))}
            </Select>
          </Form.Item>
          <Form.Item label="负责人" field="responsibleUserId" rules={[{ required: true, message: '请选择负责人' }]}>
            <Select
              allowClear
              showSearch
              placeholder="选择负责人"
              loading={metadataOptionsLoading}
              disabled={metadataOptionsLoading || metadataOptionsError}
              filterOption={optionFilter}
              onChange={(value) => changeResponsibleUser(value as number | undefined)}
            >
              {ownerOptions.map((option) => (
                <Select.Option key={option.id} value={option.id}>
                  {option.employeeNo ? `${option.realName}（${option.employeeNo}）` : option.realName}
                </Select.Option>
              ))}
            </Select>
          </Form.Item>
          <Form.Item label="课别（自动带出）" required validateStatus={selectedResponsibleUserId && !sectionName ? 'error' : undefined} help={selectedResponsibleUserId && !sectionName ? '负责人未关联课别，请先在用户管理中设置' : undefined}>
            <Input
              readOnly
              value={sectionName ?? ''}
              placeholder={selectedResponsibleUserId ? '该负责人无直属课别' : '选择负责人后自动带出'}
            />
          </Form.Item>
          <Form.Item label="优先级" field="priorityId" rules={[{ required: true, message: '请选择优先级' }]}>
            <Select
              allowClear
              showSearch
              placeholder="选择优先级"
              loading={metadataOptionsLoading}
              disabled={metadataOptionsLoading || metadataOptionsError}
              filterOption={optionFilter}
            >
              {priorityOptions.map((option) => (
                <Select.Option key={option.id} value={option.id} disabled={!option.enabled}>
                  {option.name}{option.enabled ? '' : '（已停用）'}
                </Select.Option>
              ))}
            </Select>
          </Form.Item>
          <Form.Item label="预计完成日期" field="expectedCompletionDate" rules={[{ required: true, message: '请选择预计完成日期' }]}>
            <DatePicker allowClear format="YYYY-MM-DD" placeholder="选择预计完成日期" style={{ width: '100%' }} />
          </Form.Item>
          <Form.Item label="关联供应商" field="supplierId" rules={[{ required: true, message: '请选择供应商' }]}>
            <Select
              placeholder="选择供应商"
              showSearch
              filterOption={optionFilter}
              disabled={!!editing}
            >
              {suppliers.map((s) => (
                <Select.Option key={s.id} value={s.id}>
                  {s.name}
                </Select.Option>
              ))}
            </Select>
          </Form.Item>
          {metadataOptionsError && (
            <div className="form-grid-full" role="status" style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 16 }}>
              <Typography.Text type="error">项目字典或负责人选项加载失败</Typography.Text>
              <Button size="small" onClick={loadMetadataOptions}>重试加载选项</Button>
            </div>
          )}
          {robotModelsError && selectedRobotVendorId && (
            <div className="form-grid-full" role="status" style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 16 }}>
              <Typography.Text type="error">Robot 型号加载失败</Typography.Text>
              <Button size="small" onClick={retryRobotModels}>重试加载型号</Button>
            </div>
          )}
          <Form.Item className="form-grid-full" label="项目说明" field="description">
            <Input.TextArea rows={3} maxLength={500} showWordLimit wordLimitPosition="outside" placeholder="选填" />
          </Form.Item>
        </Form>
      </Modal>

      {copySource && <Modal
        className="form-dialog project-copy-dialog"
        title="复制项目"
        visible
        onOk={submitCopy}
        onCancel={closeCopy}
        confirmLoading={copying}
        closable={!copying}
        maskClosable={!copying}
        escToExit={!copying}
        cancelButtonProps={{ disabled: copying }}
        okText="复制项目"
        cancelText="取消"
        unmountOnExit
      >
        <div className="project-copy-dialog-content">
          <div className="project-copy-source">
            <Typography.Text type="secondary">原项目</Typography.Text>
            <Typography.Text bold ellipsis={{ showTooltip: true }}>{copySource.name}</Typography.Text>
          </div>
          <Form form={copyForm} layout="vertical">
            <Form.Item
              label="新项目名称"
              field="name"
              rules={[
                { required: true, message: '请输入新项目名称' },
                textLengthRule('项目名称', 128),
                {
                  validator: (value, callback) => {
                    const name = String(value ?? '').trim()
                    if (!name) callback('请输入新项目名称')
                    else if (name === copySource.name.trim()) callback('新项目名称不能与原项目相同')
                    else callback()
                  },
                },
              ]}
            >
              <Input autoFocus disabled={copying} placeholder="请输入与原项目不同的名称" />
            </Form.Item>
          </Form>
          <div className="project-copy-scope" aria-label="项目复制范围">
            <div>
              <Typography.Text bold>将复制</Typography.Text>
              <span>项目资料、工令号、关联供应商和项目文件</span>
            </div>
            <div>
              <Typography.Text bold>不复制</Typography.Text>
              <span>留言、流程及验收状态、已读回执和通知</span>
            </div>
            <div>
              <Typography.Text bold>新项目状态</Typography.Text>
              <Tag color="gray">草稿</Tag>
            </div>
          </div>
        </div>
      </Modal>}
    </Card>
  )
}
