import { useCallback, useEffect, useRef, useState } from 'react'
import {
  Alert, Badge, Button, Card, DatePicker, Form, Input, Message, Modal, Popconfirm, Select, Space, Table, Tag, Tooltip, Typography,
} from '@arco-design/web-react'
import { IconPlus } from '@arco-design/web-react/icon'
import { Link, useNavigate } from 'react-router-dom'
import http, { type QuietRequestConfig } from '../../api/client'
import { actionSlots } from '../../components/ActionSlots'
import { useAuth } from '../../store/auth'
import {
  type PageResp, type ProjectDictionaryOption, type ProjectGroup, type RobotPart, PROJECT_STATUS,
} from '../../api/types'
import { textLengthRule } from '../../utils/textRules'
import { normalizeList } from '../../utils/listValues'
import { useCollaboration } from '../../store/collaboration'
import type { ApiResponses } from '../../api/types'

interface SupplierOpt { id: number; name: string }
interface ProjectGroupFormValues {
  name?: string
  description?: string
  supplierId?: number
  workOrderNos?: string[]
  machineModel?: string
  robotPartId?: number
  priorityId?: number
  expectedCompletionDate?: string
  subprojectNames?: string[]
}

const WORK_ORDER_LIMIT = 50
const SUBPROJECT_LIMIT = 50

function optionFilter(input: string, option?: { props?: { children?: unknown } }) {
  return String(option?.props?.children ?? '').toLocaleLowerCase().includes(input.toLocaleLowerCase())
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
  const [priorities, setPriorities] = useState<ProjectDictionaryOption[]>([])
  const [metadataOptionsLoading, setMetadataOptionsLoading] = useState(false)
  const [metadataOptionsError, setMetadataOptionsError] = useState(false)
  const [robotParts, setRobotParts] = useState<RobotPart[]>([])
  const [robotPartsLoading, setRobotPartsLoading] = useState(false)
  const [robotPartsError, setRobotPartsError] = useState(false)
  const [selectedSupplierId, setSelectedSupplierId] = useState<number | null>(null)
  const [selectedRobotPartId, setSelectedRobotPartId] = useState<number | null>(null)
  const optionsSeq = useRef(0)
  const partSeq = useRef(0)
  const supplierOptionsSeq = useRef(0)
  const [form] = Form.useForm()
  const { hasPerm, user } = useAuth()
  const isInternal = user?.userType === 'INTERNAL'
  const canDelete = isInternal && hasPerm('project:delete')

  const fetchGroups = useCallback(async () => {
    const response = await http.get<ApiResponses['GET /project-groups']>('/project-groups', {
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
    const seq = ++supplierOptionsSeq.current
    if (!isInternal) return
    setSupplierOptionsLoading(true); setSupplierOptionsError(false)
    http.get<ApiResponses['GET /supplier-options']>('/supplier-options', { quietNetworkError: true } as QuietRequestConfig)
      .then((response) => {
        if (seq === supplierOptionsSeq.current) setSuppliers(expectOptionList<SupplierOpt>(response.data))
      })
      .catch(() => { if (seq === supplierOptionsSeq.current) setSupplierOptionsError(true) })
      .finally(() => { if (seq === supplierOptionsSeq.current) setSupplierOptionsLoading(false) })
  }, [isInternal])

  useEffect(() => {
    let active = true
    void Promise.resolve().then(() => { if (active) loadSupplierOptions() })
    return () => {
      active = false
      supplierOptionsSeq.current += 1
    }
  }, [loadSupplierOptions])

  const loadMetadataOptions = useCallback(() => {
    const seq = ++optionsSeq.current
    setMetadataOptionsLoading(true); setMetadataOptionsError(false)
    http.get<ApiResponses['GET /project-dictionaries']>('/project-dictionaries', {
      params: { type: 'PRIORITY', enabledOnly: true }, quietNetworkError: true,
    } as QuietRequestConfig).then((priorityResponse) => {
      if (seq !== optionsSeq.current) return
      setPriorities(expectOptionList<ProjectDictionaryOption>(priorityResponse.data))
    }).catch(() => { if (seq === optionsSeq.current) setMetadataOptionsError(true) })
      .finally(() => { if (seq === optionsSeq.current) setMetadataOptionsLoading(false) })
  }, [])

  const loadRobotParts = useCallback((supplierId: number) => {
    const seq = ++partSeq.current
    setRobotParts([]); setRobotPartsLoading(true); setRobotPartsError(false)
    http.get<ApiResponses['GET /robot-parts']>('/robot-parts', {
      params: { supplierId, enabledOnly: true }, quietNetworkError: true,
    } as QuietRequestConfig).then((response) => {
      if (seq === partSeq.current) setRobotParts(expectOptionList<RobotPart>(response.data).filter((part) => part.supplierId === supplierId))
    }).catch(() => { if (seq === partSeq.current) setRobotPartsError(true) })
      .finally(() => { if (seq === partSeq.current) setRobotPartsLoading(false) })
  }, [])

  const retryMetadataOptions = () => {
    if (metadataOptionsError) loadMetadataOptions()
    if (robotPartsError && selectedSupplierId) loadRobotParts(selectedSupplierId)
  }

  const openCreate = () => {
    setEditing(null); form.resetFields(); form.setFieldValue('subprojectNames', [])
    setSelectedSupplierId(null); setSelectedRobotPartId(null); setRobotParts([])
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
      machineModel: group.machineModel ?? undefined, robotPartId: group.robotPartId ?? undefined,
      priorityId: group.priorityId ?? undefined, expectedCompletionDate: group.expectedCompletionDate ?? undefined,
    })
    setSelectedSupplierId(group.supplierId); setSelectedRobotPartId(group.robotPartId ?? null); loadMetadataOptions()
    loadRobotParts(group.supplierId)
    setModalOpen(true)
  }

  const closeModal = () => {
    if (saving) return
    optionsSeq.current += 1; partSeq.current += 1; setModalOpen(false)
  }

  const changeSupplier = (value?: number) => {
    const nextSupplierId = nullableNumber(value)
    partSeq.current += 1
    setSelectedSupplierId(nextSupplierId); setSelectedRobotPartId(null)
    form.setFieldValue('robotPartId', undefined); setRobotParts([]); setRobotPartsError(false); setRobotPartsLoading(false)
    if (nextSupplierId) loadRobotParts(nextSupplierId)
  }

  const partOptions = [...robotParts]
  if (editing?.robotPartId && !partOptions.some((item) => item.id === editing.robotPartId))
    partOptions.push({ id: editing.robotPartId, supplierId: editing.supplierId, supplierName: editing.supplierName ?? '', partNumber: editing.robotPartNumber || '历史料号', model: editing.robotModelName || '历史型号', sortNo: 0, enabled: false, inUse: true })
  const selectedPart = partOptions.find((item) => item.id === selectedRobotPartId)
  const supplierFormOptions = [...suppliers]
  if (editing && !supplierFormOptions.some((item) => item.id === editing.supplierId))
    supplierFormOptions.push({ id: editing.supplierId, name: editing.supplierName || '历史 Robot 厂商' })
  const priorityOptions = [...priorities]
  if (editing?.priorityId && !priorityOptions.some((item) => item.id === editing.priorityId))
    priorityOptions.push({ id: editing.priorityId, type: 'PRIORITY', name: editing.priorityName || '历史优先级', sortNo: 0, enabled: false, parentId: null, parentName: null, inUse: true })

  const missingCreateOptions: { message: string; path: string; permission: string; action: string }[] = []
  if (!supplierOptionsLoading && !supplierOptionsError && suppliers.length === 0)
    missingCreateOptions.push({ message: '暂无启用的供应商，请先新增或启用供应商。', path: '/suppliers', permission: 'supplier:manage', action: '维护供应商' })
  if (!metadataOptionsLoading && !metadataOptionsError) {
    if (!priorities.length)
      missingCreateOptions.push({ message: '暂无启用的优先级，请在数据字典中维护。', path: '/system/dictionaries', permission: 'config:manage', action: '维护优先级' })
  }
  if (selectedSupplierId && !robotPartsLoading && !robotPartsError && robotParts.length === 0 && !editing?.robotPartId)
    missingCreateOptions.push({ message: '所选 Robot 厂商暂无启用的料号，请先维护料号。', path: '/system/dictionaries', permission: 'config:manage', action: '维护 Robot 料号' })
  const robotPartOptionsRequired = !editing || selectedRobotPartId !== null
  const submitOptionsBlocked = metadataOptionsLoading || metadataOptionsError || robotPartOptionsRequired && (robotPartsLoading || robotPartsError)
    || (!editing && (supplierOptionsLoading || supplierOptionsError || missingCreateOptions.length > 0))
  const refreshCreateOptions = () => {
    loadSupplierOptions(); loadMetadataOptions()
    if (selectedSupplierId) loadRobotParts(selectedSupplierId)
  }

  const submit = async () => {
    if (saveInFlight.current || submitOptionsBlocked) return
    saveInFlight.current = true; setSaving(true)
    try {
      const values = await form.validate().catch(() => null) as ProjectGroupFormValues | null
      if (!values) return
      const robotPartId = nullableNumber(values.robotPartId)
      if (robotPartId && !partOptions.some((part) => part.id === robotPartId && part.supplierId === Number(values.supplierId))) {
        Message.error('所选 Robot 料号与厂商不匹配，请重新选择'); return
      }
      const payload = {
        name: values.name, description: values.description, supplierId: Number(values.supplierId),
        workOrderNos: normalizeList(values.workOrderNos), machineModel: values.machineModel?.trim() || null,
        robotPartId, priorityId: nullableNumber(values.priorityId),
        expectedCompletionDate: values.expectedCompletionDate || null,
        subprojectNames: editing ? undefined : normalizeList(values.subprojectNames),
      }
      if (editing) {
        await http.put<ApiResponses['PUT /project-groups/{id}']>(`/project-groups/${editing.id}`, payload)
        Message.success('主项目资料已同步到全部子项目')
      } else {
        await http.post<ApiResponses['POST /project-groups']>('/project-groups', payload)
        Message.success('主项目及子项目已创建')
      }
      setModalOpen(false); load()
    } finally { saveInFlight.current = false; setSaving(false) }
  }

  const remove = async (group: ProjectGroup) => {
    await http.delete<ApiResponses['DELETE /project-groups/{id}']>(`/project-groups/${group.id}`); Message.success('主项目已删除'); load()
  }

  const columns = [
    { title: '主项目名称', dataIndex: 'name', width: 145, fixed: 'left' as const, ellipsis: true, render: (value: string, row: ProjectGroup) => <Link to={`/project-groups/${row.id}`}>{value}</Link> },
    { title: '工令号', dataIndex: 'workOrderNos', width: 190, ellipsis: true, render: (values?: string[]) => displayText(values?.join('、')) },
    { title: '机型', dataIndex: 'machineModel', width: 100, ellipsis: true, render: displayText },
    { title: 'Robot 厂商', dataIndex: 'supplierName', width: 110, ellipsis: true, render: displayText },
    { title: 'Robot 料号', dataIndex: 'robotPartNumber', width: 120, ellipsis: true, render: displayText },
    { title: 'Robot 型号', dataIndex: 'robotModelName', width: 110, ellipsis: true, render: (value?: string | null) => <Tooltip content={displayText(value)}><span>{displayText(value)}</span></Tooltip> },
    { title: '负责人', dataIndex: 'responsibleUserName', width: 88, ellipsis: true, render: displayText },
    { title: '课别', dataIndex: 'sectionName', width: 88, ellipsis: true, render: displayText },
    { title: '优先级', dataIndex: 'priorityName', width: 76, align: 'center' as const, render: displayText },
    { title: '需求完成时间', dataIndex: 'expectedCompletionDate', width: 120, align: 'center' as const, render: displayText },
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
          {isInternal && <Select allowClear showSearch placeholder="Robot 厂商" style={{ width: 180 }} value={supplierId} loading={supplierOptionsLoading} disabled={supplierOptionsError} onChange={(value) => { setPage(1); setSupplierId(value as number | undefined); load() }}>{suppliers.map((supplier) => <Select.Option key={supplier.id} value={supplier.id}>{supplier.name}</Select.Option>)}</Select>}
        </Space>
        <Space>
          {supplierOptionsError && <Button size="small" onClick={loadSupplierOptions}>重试加载供应商</Button>}
          {isInternal && hasPerm('project:create') && <Button type="primary" icon={<IconPlus />} onClick={openCreate}>新建主项目</Button>}
        </Space>
      </div>
      {loadError ? <div className="page-load-error"><Typography.Text type="error">加载失败</Typography.Text><Button size="small" onClick={load}>重试</Button></div> :
        <Table className="page-table" rowKey="id" loading={loading} columns={columns} data={data.list} scroll={{ x: columns.reduce((sum, column) => sum + column.width, 0), y: 'var(--page-table-scroll-y)' }} pagination={{ total: data.total, current: page, pageSize, showTotal: true, sizeCanChange: true, onChange: (nextPage, nextSize) => { setPage(nextPage); setPageSize(nextSize); load() } }} />}

      <Modal className="form-dialog" style={{ width: 760 }} title={editing ? '编辑主项目' : '新建主项目'} visible={modalOpen} onOk={submit} onCancel={closeModal} confirmLoading={saving} closable={!saving} maskClosable={!saving} escToExit={!saving} cancelButtonProps={{ disabled: saving }} okButtonProps={{ disabled: submitOptionsBlocked }} okText={editing ? '保存并同步' : '创建主项目'} unmountOnExit>
        <Form className="form-grid" form={form} layout="vertical">
          {!editing && <div className="form-grid-full" style={{ display: 'grid', gap: 8 }}>
            {(supplierOptionsLoading || metadataOptionsLoading) && <Typography.Text type="secondary">正在加载创建所需的基础数据…</Typography.Text>}
            {supplierOptionsError && <Alert type="error" content="Robot 厂商选项加载失败，请刷新基础数据后重试。" />}
            {missingCreateOptions.length > 0 && <Alert type="warning" title="创建前请补齐基础数据" content={<>
              <ul>{missingCreateOptions.map((item) => <li key={item.message}>{item.message}{hasPerm(item.permission)
                ? <> <Link to={item.path} target="_blank" rel="noopener noreferrer" style={{ whiteSpace: 'nowrap' }}>{item.action}（新窗口）</Link></>
                : ' 请联系具有维护权限的管理员。'}</li>)}</ul>
              <span>可先填写项目资料，补齐后点击“刷新基础数据”继续。</span>
            </>} />}
            <div><Button size="small" onClick={refreshCreateOptions} disabled={saving || supplierOptionsLoading || metadataOptionsLoading || robotPartsLoading}>刷新基础数据</Button></div>
          </div>}
          <Form.Item className="form-grid-full" label="主项目名称" field="name" rules={[{ required: true, message: '请输入主项目名称' }, textLengthRule('主项目名称', 128)]}><Input placeholder="主项目名称" /></Form.Item>
          {!editing && <Form.Item className="form-grid-full" label="子项目" field="subprojectNames" rules={[{ required: true, message: '请至少创建一个子项目' }, { validator: (value, callback) => listRule('子项目', SUBPROJECT_LIMIT, value, callback) }]}><Select mode="multiple" allowCreate allowClear showSearch maxTagCount={3} tokenSeparators={[',', '，', ';', '；', '\n']} placeholder="输入子项目名称后按回车，可一次创建多个" /></Form.Item>}
          <Form.Item className="form-grid-full" label="工令号" field="workOrderNos" rules={[{ required: true, message: '请至少填写一个工令号' }, { validator: (value, callback) => listRule('工令号', WORK_ORDER_LIMIT, value, callback) }]}><Select mode="multiple" allowCreate allowClear showSearch maxTagCount={3} tokenSeparators={[',', '，', ';', '；', '\n']} placeholder="输入工令号后按回车，可填写多个" /></Form.Item>
          <Form.Item label="机型" field="machineModel" rules={[{ required: true, message: '请填写机型' }, textLengthRule('机型', 128)]}><Input maxLength={128} showWordLimit placeholder="请输入机型" /></Form.Item>
          <Form.Item label="Robot 厂商" field="supplierId" rules={[{ required: true, message: '请选择 Robot 厂商' }]}><Select showSearch placeholder="选择 Robot 厂商" filterOption={optionFilter} loading={supplierOptionsLoading} disabled={!!editing || supplierOptionsLoading || supplierOptionsError} onChange={(value) => changeSupplier(value as number | undefined)}>{supplierFormOptions.map((supplier) => <Select.Option key={supplier.id} value={supplier.id}>{supplier.name}</Select.Option>)}</Select></Form.Item>
          <Form.Item label="Robot 料号" field="robotPartId" rules={editing && !editing.robotPartId ? [] : [{ required: true, message: '请选择 Robot 料号' }]} help={editing && !editing.robotPartId ? '历史项目可保留原型号；选择料号后将使用新料号对应型号。' : undefined}><Select allowClear showSearch placeholder={selectedSupplierId ? '选择 Robot 料号' : '请先选择 Robot 厂商'} loading={robotPartsLoading} disabled={!selectedSupplierId || robotPartsLoading || robotPartsError} filterOption={optionFilter} onChange={(value) => setSelectedRobotPartId(nullableNumber(value))}>{partOptions.map((item) => <Select.Option key={item.id} value={item.id} disabled={!item.enabled}>{item.partNumber}{item.enabled ? '' : '（已停用）'}</Select.Option>)}</Select></Form.Item>
          <Form.Item label="Robot 型号（自动带出）"><Input.TextArea readOnly autoSize={{ minRows: 2, maxRows: 4 }} value={selectedPart?.model ?? (editing && !selectedRobotPartId ? editing.robotModelName ?? '' : '')} placeholder={selectedRobotPartId ? '所选料号未设置型号' : '选择 Robot 料号后自动带出'} /></Form.Item>
          <Form.Item label="负责人"><Input readOnly value={editing ? `${editing.responsibleUserName || '未设置'}${editing.responsibleUserEmployeeNo ? `（${editing.responsibleUserEmployeeNo}）` : ''}` : `${user?.realName || '当前用户'}${user?.employeeNo ? `（${user.employeeNo}）` : ''}`} /></Form.Item>
          <Form.Item label="课别" help={editing ? undefined : '按创建人的课别记录，未设置也可创建'}><Input readOnly value={editing ? editing.sectionName || '未设置' : '自动带出'} /></Form.Item>
          <Form.Item label="优先级" field="priorityId" rules={[{ required: true, message: '请选择优先级' }]}><Select allowClear showSearch placeholder="选择优先级" loading={metadataOptionsLoading} disabled={metadataOptionsLoading || metadataOptionsError} filterOption={optionFilter}>{priorityOptions.map((item) => <Select.Option key={item.id} value={item.id} disabled={!item.enabled}>{item.name}{item.enabled ? '' : '（已停用）'}</Select.Option>)}</Select></Form.Item>
          <Form.Item label="需求完成时间" field="expectedCompletionDate" rules={[{ required: true, message: '请选择需求完成时间' }]}><DatePicker allowClear format="YYYY-MM-DD" placeholder="选择需求完成时间" style={{ width: '100%' }} /></Form.Item>
          <Form.Item className="form-grid-full" label="主项目说明" field="description"><Input.TextArea rows={3} maxLength={500} showWordLimit wordLimitPosition="outside" placeholder="选填" /></Form.Item>
          <div className="form-grid-full dialog-note">所选 Robot 厂商的全部启用账号均可访问此主项目及其子项目。</div>
          {(metadataOptionsError || robotPartsError) && <div className="form-grid-full dialog-note dialog-note--danger">
            <span>优先级或 Robot 料号选项加载失败，请重试。</span>
            <Button size="small" onClick={retryMetadataOptions}>重试加载选项</Button>
          </div>}
        </Form>
      </Modal>
    </Card>
  )
}
