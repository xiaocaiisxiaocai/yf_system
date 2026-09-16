import { useCallback, useEffect, useRef, useState } from 'react'
import {
  Badge, Button, Card, DatePicker, Form, Input, Message, Modal, Popconfirm, Select, Space, Table, Tag, Tooltip, Typography,
} from '@arco-design/web-react'
import { IconPlus } from '@arco-design/web-react/icon'
import { Link, useNavigate } from 'react-router-dom'
import http, { type QuietRequestConfig } from '../../api/client'
import { actionSlots } from '../../components/ActionSlots'
import { useAuth } from '../../store/auth'
import {
  type PageResp, type ProjectDictionaryOption, type ProjectGroup, type ProjectOwnerOption, PROJECT_STATUS,
} from '../../api/types'
import { textLengthRule } from '../../utils/textRules'
import { useCollaboration } from '../../store/collaboration'

interface SupplierOpt { id: number; name: string }
interface ProjectGroupFormValues {
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
  subprojectNames?: string[]
}

const WORK_ORDER_LIMIT = 50
const SUBPROJECT_LIMIT = 50

function optionFilter(input: string, option?: { props?: { children?: unknown } }) {
  return String(option?.props?.children ?? '').toLocaleLowerCase().includes(input.toLocaleLowerCase())
}

function normalizeList(values?: string[]) {
  const unique = new Set<string>()
  for (const value of values ?? []) {
    const normalized = String(value).trim()
    if (normalized) unique.add(normalized)
  }
  return [...unique]
}

function listRule(label: string, limit: number, values: unknown, callback: (error?: string) => void) {
  const normalized = normalizeList(Array.isArray(values) ? values.map(String) : [])
  if (!normalized.length) { callback(`请至少填写一个${label}`); return }
  if (normalized.length > limit) { callback(`${label}最多填写 ${limit} 项`); return }
  if (normalized.some((item) => [...item].length > 128)) { callback(`每个${label}最多 128 个字符`); return }
  callback()
}

function nullableNumber(value: unknown): number | null {
  const parsed = Number(value)
  return Number.isSafeInteger(parsed) && parsed > 0 ? parsed : null
}

function displayText(value?: string | null) { return value?.trim() || '-' }
function expectOptionList<T>(value: unknown): T[] {
  if (!Array.isArray(value)) throw new Error('选项接口返回格式错误')
  return value as T[]
}

export default function ProjectList() {
  const navigate = useNavigate()
  const revision = useCollaboration((state) => state.revision)
  const syncStatus = useCollaboration((state) => state.status)
  const [data, setData] = useState<PageResp<ProjectGroup>>({ list: [], total: 0, page: 1, pageSize: 10 })
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState(false)
  const [reloadKey, setReloadKey] = useState(0)
  const [keyword, setKeyword] = useState('')
  const [status, setStatus] = useState<string>()
  const [supplierId, setSupplierId] = useState<number>()
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(10)
  const [modalOpen, setModalOpen] = useState(false)
  const [saving, setSaving] = useState(false)
  const saveInFlight = useRef(false)
  const [editing, setEditing] = useState<ProjectGroup | null>(null)
  const [suppliers, setSuppliers] = useState<SupplierOpt[]>([])
  const [supplierOptionsLoading, setSupplierOptionsLoading] = useState(true)
  const [supplierOptionsError, setSupplierOptionsError] = useState(false)
  const [robotVendors, setRobotVendors] = useState<ProjectDictionaryOption[]>([])
  const [priorities, setPriorities] = useState<ProjectDictionaryOption[]>([])
  const [owners, setOwners] = useState<ProjectOwnerOption[]>([])
  const [metadataOptionsLoading, setMetadataOptionsLoading] = useState(false)
  const [metadataOptionsError, setMetadataOptionsError] = useState(false)
  const [robotModels, setRobotModels] = useState<ProjectDictionaryOption[]>([])
  const [robotModelsLoading, setRobotModelsLoading] = useState(false)
  const [robotModelsError, setRobotModelsError] = useState(false)
  const [selectedRobotVendorId, setSelectedRobotVendorId] = useState<number | null>(null)
  const [selectedResponsibleUserId, setSelectedResponsibleUserId] = useState<number | null>(null)
  const [sectionName, setSectionName] = useState<string | null>(null)
  const optionsSeq = useRef(0)
  const modelSeq = useRef(0)
  const [form] = Form.useForm()
  const { hasPerm, user } = useAuth()
  const isInternal = user?.userType === 'INTERNAL'
  const canDelete = isInternal && hasPerm('project:delete')

  const fetchGroups = useCallback(async () => {
    const response = await http.get('/project-groups', {
      params: { page, pageSize, keyword: keyword || undefined, status, supplierId }, quietNetworkError: true,
    } as QuietRequestConfig)
    return response.data as PageResp<ProjectGroup>
  }, [keyword, page, pageSize, status, supplierId])

  const load = useCallback(() => {
    setLoading(true); setLoadError(false); setReloadKey((value) => value + 1)
  }, [])

  useEffect(() => {
    let active = true
    fetchGroups().then((next) => {
      if (!active) return
      setData(next); setLoadError(false)
      const lastPage = Math.max(1, Math.ceil(next.total / (next.pageSize || pageSize)))
      if (page > lastPage) setPage(lastPage)
    }).catch(() => { if (active) setLoadError(true) })
      .finally(() => { if (active) setLoading(false) })
    return () => { active = false }
  }, [fetchGroups, page, pageSize, reloadKey, revision, syncStatus])

  const loadSupplierOptions = useCallback(() => {
    if (!isInternal) return
    setSupplierOptionsLoading(true); setSupplierOptionsError(false)
    http.get('/supplier-options', { quietNetworkError: true } as QuietRequestConfig)
      .then((response) => setSuppliers(expectOptionList<SupplierOpt>(response.data)))
      .catch(() => setSupplierOptionsError(true))
      .finally(() => setSupplierOptionsLoading(false))
  }, [isInternal])

  useEffect(() => { loadSupplierOptions() }, [loadSupplierOptions])

  const loadMetadataOptions = useCallback(() => {
    const seq = ++optionsSeq.current
    setMetadataOptionsLoading(true); setMetadataOptionsError(false)
    Promise.all([
      http.get('/project-dictionaries', { params: { type: 'ROBOT_VENDOR', enabledOnly: true }, quietNetworkError: true } as QuietRequestConfig),
      http.get('/project-dictionaries', { params: { type: 'PRIORITY', enabledOnly: true }, quietNetworkError: true } as QuietRequestConfig),
      http.get('/project-owner-options', { quietNetworkError: true } as QuietRequestConfig),
    ]).then(([vendorResponse, priorityResponse, ownerResponse]) => {
      if (seq !== optionsSeq.current) return
      setRobotVendors(expectOptionList<ProjectDictionaryOption>(vendorResponse.data))
      setPriorities(expectOptionList<ProjectDictionaryOption>(priorityResponse.data))
      setOwners(expectOptionList<ProjectOwnerOption>(ownerResponse.data))
    }).catch(() => { if (seq === optionsSeq.current) setMetadataOptionsError(true) })
      .finally(() => { if (seq === optionsSeq.current) setMetadataOptionsLoading(false) })
  }, [])

  const loadRobotModels = useCallback((vendorId: number) => {
    const seq = ++modelSeq.current
    setRobotModels([]); setRobotModelsLoading(true); setRobotModelsError(false)
    http.get('/project-dictionaries', {
      params: { type: 'ROBOT_MODEL', enabledOnly: true, parentId: vendorId }, quietNetworkError: true,
    } as QuietRequestConfig).then((response) => {
      if (seq === modelSeq.current) setRobotModels(expectOptionList<ProjectDictionaryOption>(response.data))
    }).catch(() => { if (seq === modelSeq.current) setRobotModelsError(true) })
      .finally(() => { if (seq === modelSeq.current) setRobotModelsLoading(false) })
  }, [])

  const openCreate = () => {
    if (supplierOptionsLoading || supplierOptionsError || suppliers.length === 0) return
    setEditing(null); form.resetFields(); form.setFieldValue('subprojectNames', [])
    setSectionName(null); setSelectedRobotVendorId(null); setSelectedResponsibleUserId(null); setRobotModels([])
    loadMetadataOptions(); setModalOpen(true)
  }

  const openEdit = (group: ProjectGroup) => {
    if (group.pendingCount > 0) {
      Message.info('存在待公司验收的子项目，验收处理完成后才能编辑主项目资料')
      return
    }
    setEditing(group)
    form.setFieldsValue({
      name: group.name, description: group.description, supplierId: group.supplierId, workOrderNos: group.workOrderNos,
      machineModel: group.machineModel ?? undefined, robotVendorId: group.robotVendorId ?? undefined,
      robotModelId: group.robotModelId ?? undefined, responsibleUserId: group.responsibleUserId ?? undefined,
      priorityId: group.priorityId ?? undefined, expectedCompletionDate: group.expectedCompletionDate ?? undefined,
    })
    setSectionName(group.sectionName ?? null); setSelectedRobotVendorId(group.robotVendorId ?? null)
    setSelectedResponsibleUserId(group.responsibleUserId ?? null); loadMetadataOptions()
    if (group.robotVendorId) loadRobotModels(group.robotVendorId)
    setModalOpen(true)
  }

  const closeModal = () => {
    if (saving) return
    optionsSeq.current += 1; modelSeq.current += 1; setModalOpen(false)
  }

  const changeRobotVendor = (value?: number) => {
    const vendorId = nullableNumber(value)
    setSelectedRobotVendorId(vendorId); form.setFieldValue('robotModelId', undefined); setRobotModels([])
    if (vendorId) loadRobotModels(vendorId)
  }

  const ownerOptions = [...owners]
  if (editing?.responsibleUserId && !ownerOptions.some((item) => item.id === editing.responsibleUserId))
    ownerOptions.push({ id: editing.responsibleUserId, employeeNo: editing.responsibleUserEmployeeNo ?? '', realName: editing.responsibleUserName || '历史负责人', sectionId: editing.sectionId, sectionName: editing.sectionName })
  const vendorOptions = [...robotVendors]
  if (editing?.robotVendorId && !vendorOptions.some((item) => item.id === editing.robotVendorId))
    vendorOptions.push({ id: editing.robotVendorId, type: 'ROBOT_VENDOR', name: editing.robotVendorName || '历史厂商', sortNo: 0, enabled: false })
  const modelOptions = [...robotModels]
  if (editing?.robotModelId && !modelOptions.some((item) => item.id === editing.robotModelId))
    modelOptions.push({ id: editing.robotModelId, type: 'ROBOT_MODEL', name: editing.robotModelName || '历史型号', parentId: editing.robotVendorId, sortNo: 0, enabled: false })
  const priorityOptions = [...priorities]
  if (editing?.priorityId && !priorityOptions.some((item) => item.id === editing.priorityId))
    priorityOptions.push({ id: editing.priorityId, type: 'PRIORITY', name: editing.priorityName || '历史优先级', sortNo: 0, enabled: false })

  const submit = async () => {
    if (saveInFlight.current) return
    saveInFlight.current = true; setSaving(true)
    try {
      const values = await form.validate().catch(() => null) as ProjectGroupFormValues | null
      if (!values) return
      if (!sectionName) { Message.error('负责人未关联课别，请先在用户管理中设置其所属课别'); return }
      const robotVendorId = nullableNumber(values.robotVendorId)
      const payload = {
        name: values.name, description: values.description, supplierId: Number(values.supplierId),
        workOrderNos: normalizeList(values.workOrderNos), machineModel: values.machineModel?.trim() || null,
        robotVendorId, robotModelId: robotVendorId ? nullableNumber(values.robotModelId) : null,
        responsibleUserId: nullableNumber(values.responsibleUserId), priorityId: nullableNumber(values.priorityId),
        expectedCompletionDate: values.expectedCompletionDate || null,
        subprojectNames: editing ? undefined : normalizeList(values.subprojectNames),
      }
      if (editing) {
        await http.put(`/project-groups/${editing.id}`, payload)
        Message.success('主项目资料已同步到全部子项目')
      } else {
        await http.post('/project-groups', payload)
        Message.success('主项目及子项目已创建')
      }
      setModalOpen(false); load()
    } finally { saveInFlight.current = false; setSaving(false) }
  }

  const remove = async (group: ProjectGroup) => {
    await http.delete(`/project-groups/${group.id}`); Message.success('主项目已删除'); load()
  }

  const columns = [
    { title: '主项目名称', dataIndex: 'name', width: 145, fixed: 'left' as const, ellipsis: true, render: (value: string, row: ProjectGroup) => <Link to={`/project-groups/${row.id}`}>{value}</Link> },
    { title: '工令号', dataIndex: 'workOrderNos', width: 190, ellipsis: true, render: (values?: string[]) => displayText(values?.join('、')) },
    { title: '机型', dataIndex: 'machineModel', width: 100, ellipsis: true, render: displayText },
    { title: 'Robot 厂商', dataIndex: 'robotVendorName', width: 105, ellipsis: true, render: displayText },
    { title: 'Robot 型号', dataIndex: 'robotModelName', width: 105, ellipsis: true, render: displayText },
    { title: '负责人', dataIndex: 'responsibleUserName', width: 88, ellipsis: true, render: displayText },
    { title: '课别', dataIndex: 'sectionName', width: 88, ellipsis: true, render: displayText },
    { title: '优先级', dataIndex: 'priorityName', width: 76, align: 'center' as const, render: displayText },
    { title: '预计完成', dataIndex: 'expectedCompletionDate', width: 108, align: 'center' as const, render: displayText },
    { title: '供应商', dataIndex: 'supplierName', width: 110, ellipsis: true, render: displayText },
    { title: '子项目进度', width: 142, align: 'center' as const, render: (_: unknown, row: ProjectGroup) => <div className="project-group-progress"><span>{row.completedCount}/{row.subprojectCount} 已验收</span>{row.pendingCount > 0 && <Tag size="small" color="orange">待验收 {row.pendingCount}</Tag>}</div> },
    { title: '状态', dataIndex: 'status', width: 90, align: 'center' as const, render: (value: string) => <Tag color={PROJECT_STATUS[value]?.color}>{PROJECT_STATUS[value]?.text || value}</Tag> },
    { title: '未读留言', dataIndex: 'unreadMessages', width: 88, align: 'center' as const, render: (value: number) => value > 0 ? <Badge count={value} /> : '-' },
    { title: '操作', width: 190, fixed: 'right' as const, align: 'center' as const, render: (_: unknown, row: ProjectGroup) => actionSlots([
      <Button key="enter" size="mini" type="text" onClick={() => navigate(`/project-groups/${row.id}`)}>进入</Button>,
      isInternal && hasPerm('project:update') && ['DRAFT', 'IN_PROGRESS'].includes(row.status) && (row.pendingCount > 0 ? (
        <Tooltip key="edit" content={`有 ${row.pendingCount} 个子项目待公司验收，暂不能编辑主项目资料`}>
          <span><Button size="mini" type="text" disabled>编辑</Button></span>
        </Tooltip>
      ) : <Button key="edit" size="mini" type="text" onClick={() => openEdit(row)}>编辑</Button>),
      canDelete && row.subprojectCount === 0 && <Popconfirm key="delete" title={`确认删除空主项目“${row.name}”？`} onOk={() => remove(row)}><Button size="mini" type="text" status="danger">删除</Button></Popconfirm>,
    ], 'project-group') },
  ]

  return (
    <Card className="page-card page-card--table">
      <div className="page-heading"><div><h1>项目协作</h1><p>按主项目归集资料，各子项目独立协作与验收</p></div></div>
      <div className="page-toolbar responsive-toolbar">
        <Space wrap>
          <Input.Search allowClear placeholder="主项目名称" style={{ width: 240 }} onSearch={(value) => { setPage(1); setKeyword(value); load() }} onClear={() => { setPage(1); setKeyword(''); load() }} />
          <Select allowClear placeholder="状态" style={{ width: 130 }} value={status} onChange={(value) => { setPage(1); setStatus(value as string | undefined); load() }}>
            {['DRAFT', 'IN_PROGRESS', 'COMPLETED', 'TERMINATED'].map((key) => <Select.Option key={key} value={key}>{PROJECT_STATUS[key]?.text ?? key}</Select.Option>)}
          </Select>
          {isInternal && <Select allowClear showSearch placeholder="供应商" style={{ width: 180 }} value={supplierId} loading={supplierOptionsLoading} disabled={supplierOptionsError} onChange={(value) => { setPage(1); setSupplierId(value as number | undefined); load() }}>{suppliers.map((supplier) => <Select.Option key={supplier.id} value={supplier.id}>{supplier.name}</Select.Option>)}</Select>}
        </Space>
        <Space>
          {supplierOptionsError && <Button size="small" onClick={loadSupplierOptions}>重试加载供应商</Button>}
          {isInternal && hasPerm('project:create') && <Button type="primary" icon={<IconPlus />} onClick={openCreate} disabled={supplierOptionsLoading || supplierOptionsError || suppliers.length === 0}>新建主项目</Button>}
        </Space>
      </div>
      {loadError ? <div className="page-load-error"><Typography.Text type="error">加载失败</Typography.Text><Button size="small" onClick={load}>重试</Button></div> :
        <Table className="page-table" rowKey="id" loading={loading} columns={columns} data={data.list} scroll={{ x: columns.reduce((sum, column) => sum + column.width, 0), y: 'var(--page-table-scroll-y)' }} pagination={{ total: data.total, current: page, pageSize, showTotal: true, sizeCanChange: true, onChange: (nextPage, nextSize) => { setPage(nextPage); setPageSize(nextSize); load() } }} />}

      <Modal className="form-dialog" style={{ width: 760 }} title={editing ? '编辑主项目' : '新建主项目'} visible={modalOpen} onOk={submit} onCancel={closeModal} confirmLoading={saving} closable={!saving} maskClosable={!saving} escToExit={!saving} cancelButtonProps={{ disabled: saving }} okText={editing ? '保存并同步' : '创建主项目'} unmountOnExit>
        <Form className="form-grid" form={form} layout="vertical">
          <Form.Item className="form-grid-full" label="主项目名称" field="name" rules={[{ required: true, message: '请输入主项目名称' }, textLengthRule('主项目名称', 128)]}><Input placeholder="主项目名称" /></Form.Item>
          {!editing && <Form.Item className="form-grid-full" label="子项目" field="subprojectNames" rules={[{ required: true, message: '请至少创建一个子项目' }, { validator: (value, callback) => listRule('子项目', SUBPROJECT_LIMIT, value, callback) }]}><Select mode="multiple" allowCreate allowClear showSearch maxTagCount={3} tokenSeparators={[',', '，', ';', '；', '\n']} placeholder="输入子项目名称后按回车，可一次创建多个" /></Form.Item>}
          <Form.Item className="form-grid-full" label="工令号" field="workOrderNos" rules={[{ required: true, message: '请至少填写一个工令号' }, { validator: (value, callback) => listRule('工令号', WORK_ORDER_LIMIT, value, callback) }]}><Select mode="multiple" allowCreate allowClear showSearch maxTagCount={3} tokenSeparators={[',', '，', ';', '；', '\n']} placeholder="输入工令号后按回车，可填写多个" /></Form.Item>
          <Form.Item label="机型" field="machineModel" rules={[{ required: true, message: '请填写机型' }]}><Input maxLength={128} showWordLimit placeholder="请输入机型" /></Form.Item>
          <Form.Item label="Robot 厂商" field="robotVendorId" rules={[{ required: true, message: '请选择 Robot 厂商' }]}><Select allowClear showSearch placeholder="选择 Robot 厂商" loading={metadataOptionsLoading} disabled={metadataOptionsLoading || metadataOptionsError} filterOption={optionFilter} onChange={(value) => changeRobotVendor(value as number | undefined)}>{vendorOptions.map((item) => <Select.Option key={item.id} value={item.id} disabled={!item.enabled}>{item.name}{item.enabled ? '' : '（已停用）'}</Select.Option>)}</Select></Form.Item>
          <Form.Item label="Robot 型号" field="robotModelId" rules={[{ required: true, message: '请选择 Robot 型号' }]}><Select allowClear showSearch placeholder={selectedRobotVendorId ? '选择 Robot 型号' : '请先选择 Robot 厂商'} loading={robotModelsLoading} disabled={!selectedRobotVendorId || robotModelsLoading || robotModelsError} filterOption={optionFilter}>{modelOptions.map((item) => <Select.Option key={item.id} value={item.id} disabled={!item.enabled}>{item.name}{item.enabled ? '' : '（已停用）'}</Select.Option>)}</Select></Form.Item>
          <Form.Item label="负责人" field="responsibleUserId" rules={[{ required: true, message: '请选择负责人' }]}><Select allowClear showSearch placeholder="选择负责人" loading={metadataOptionsLoading} disabled={metadataOptionsLoading || metadataOptionsError} filterOption={optionFilter} onChange={(value) => { const ownerId = nullableNumber(value); setSelectedResponsibleUserId(ownerId); setSectionName(ownerOptions.find((item) => item.id === ownerId)?.sectionName ?? null) }}>{ownerOptions.map((item) => <Select.Option key={item.id} value={item.id}>{item.realName}（{item.employeeNo}）</Select.Option>)}</Select></Form.Item>
          <Form.Item label="课别（自动带出）" required validateStatus={selectedResponsibleUserId && !sectionName ? 'error' : undefined} help={selectedResponsibleUserId && !sectionName ? '负责人未关联课别，请先在用户管理中设置' : undefined}><Input readOnly value={sectionName ?? ''} placeholder={selectedResponsibleUserId ? '该负责人无直属课别' : '选择负责人后自动带出'} /></Form.Item>
          <Form.Item label="优先级" field="priorityId" rules={[{ required: true, message: '请选择优先级' }]}><Select allowClear showSearch placeholder="选择优先级" loading={metadataOptionsLoading} disabled={metadataOptionsLoading || metadataOptionsError} filterOption={optionFilter}>{priorityOptions.map((item) => <Select.Option key={item.id} value={item.id} disabled={!item.enabled}>{item.name}{item.enabled ? '' : '（已停用）'}</Select.Option>)}</Select></Form.Item>
          <Form.Item label="预计完成日期" field="expectedCompletionDate" rules={[{ required: true, message: '请选择预计完成日期' }]}><DatePicker allowClear format="YYYY-MM-DD" placeholder="选择预计完成日期" style={{ width: '100%' }} /></Form.Item>
          <Form.Item label="关联供应商" field="supplierId" rules={[{ required: true, message: '请选择供应商' }]}><Select showSearch placeholder="选择供应商" filterOption={optionFilter} disabled={!!editing}>{suppliers.map((supplier) => <Select.Option key={supplier.id} value={supplier.id}>{supplier.name}</Select.Option>)}</Select></Form.Item>
          <Form.Item className="form-grid-full" label="主项目说明" field="description"><Input.TextArea rows={3} maxLength={500} showWordLimit wordLimitPosition="outside" placeholder="选填" /></Form.Item>
          <div className="form-grid-full dialog-note">关联后，该供应商的全部启用账号均可访问此主项目及其子项目。</div>
          {(metadataOptionsError || robotModelsError) && <div className="form-grid-full dialog-note dialog-note--danger">项目字典或负责人选项加载失败，请重试。</div>}
        </Form>
      </Modal>
    </Card>
  )
}
