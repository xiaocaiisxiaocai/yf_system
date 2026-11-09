import { useCallback, useEffect, useRef, useState } from 'react'
import { Alert, Button, DatePicker, Form, Input, Message, Modal, Select, Typography } from '@arco-design/web-react'
import { Link } from 'react-router-dom'
import http, { type QuietRequestConfig } from '../../api/client'
import { isVersionConflict } from '../../api/versionConflict'
import { useAuth } from '../../store/auth'
import type { ApiResponses, ProjectDictionaryOption, ProjectSummary, RobotPart, SupplierContactOption } from '../../api/types'
import { textLengthRule } from '../../utils/textRules'
import { normalizeList } from '../../utils/listValues'
import EnterSubmit from '../../components/EnterSubmit'

interface SupplierOpt { id: number; name: string }
interface ProjectFormValues {
  name?: string
  description?: string
  supplierId?: number
  workOrderNos?: string[]
  machineModel?: string
  robotPartId?: number
  priorityId?: number
  robotTypeId?: number
  robotOwnerContactIds?: number[]
  expectedCompletionDate?: string
}

const WORK_ORDER_LIMIT = 50
const ROBOT_OWNER_LIMIT = 10

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

function expectOptionList<T>(value: unknown): T[] {
  if (!Array.isArray(value)) throw new Error('选项接口返回格式错误')
  return value as T[]
}

interface Props {
  visible: boolean
  /** null 表示新建；新建成功即开始项目。 */
  project: ProjectSummary | null
  onCancel: () => void
  /** 保存成功；created 为 true 时是新建并已开始的项目。 */
  onSaved: (project: ProjectSummary, created: boolean) => void
  /** 编辑遇到版本冲突或项目已不可编辑时，让调用方刷新自己的数据。 */
  onStale?: () => void
}

/** 新建或编辑项目的表单弹窗：创建人即负责人，课别按创建人带出。 */
export default function ProjectFormModal({ visible, project, onCancel, onSaved, onStale }: Props) {
  const { hasPerm, user } = useAuth()
  const [form] = Form.useForm()
  const [editing, setEditing] = useState<ProjectSummary | null>(project)
  const [editConflict, setEditConflict] = useState(false)
  const [saving, setSaving] = useState(false)
  const saveInFlight = useRef(false)
  const [suppliers, setSuppliers] = useState<SupplierOpt[]>([])
  const [supplierOptionsLoading, setSupplierOptionsLoading] = useState(true)
  const [supplierOptionsError, setSupplierOptionsError] = useState(false)
  const [priorities, setPriorities] = useState<ProjectDictionaryOption[]>([])
  const [robotTypes, setRobotTypes] = useState<ProjectDictionaryOption[]>([])
  const [metadataOptionsLoading, setMetadataOptionsLoading] = useState(true)
  const [metadataOptionsError, setMetadataOptionsError] = useState(false)
  const [robotParts, setRobotParts] = useState<RobotPart[]>([])
  const [robotPartsLoading, setRobotPartsLoading] = useState(false)
  const [robotPartsError, setRobotPartsError] = useState(false)
  const [contacts, setContacts] = useState<SupplierContactOption[]>([])
  const [contactsLoading, setContactsLoading] = useState(false)
  const [contactsError, setContactsError] = useState(false)
  const [selectedSupplierId, setSelectedSupplierId] = useState<number | null>(null)
  const [selectedRobotPartId, setSelectedRobotPartId] = useState<number | null>(null)
  const supplierOptionsSeq = useRef(0)
  const optionsSeq = useRef(0)
  const partSeq = useRef(0)
  const contactSeq = useRef(0)

  const loadSupplierOptions = useCallback(() => {
    const seq = ++supplierOptionsSeq.current
    setSupplierOptionsLoading(true); setSupplierOptionsError(false)
    http.get<ApiResponses['GET /supplier-options']>('/supplier-options', { quietNetworkError: true } as QuietRequestConfig)
      .then((response) => {
        if (seq === supplierOptionsSeq.current) setSuppliers(expectOptionList<SupplierOpt>(response.data))
      })
      .catch(() => { if (seq === supplierOptionsSeq.current) setSupplierOptionsError(true) })
      .finally(() => { if (seq === supplierOptionsSeq.current) setSupplierOptionsLoading(false) })
  }, [])

  const loadMetadataOptions = useCallback(() => {
    const seq = ++optionsSeq.current
    setMetadataOptionsLoading(true); setMetadataOptionsError(false)
    const dictionary = (type: 'PRIORITY' | 'ROBOT_TYPE') => http.get<ApiResponses['GET /project-dictionaries']>('/project-dictionaries', {
      params: { type, enabledOnly: true }, quietNetworkError: true,
    } as QuietRequestConfig)
    Promise.all([dictionary('PRIORITY'), dictionary('ROBOT_TYPE')]).then(([priorityResponse, robotTypeResponse]) => {
      if (seq !== optionsSeq.current) return
      setPriorities(expectOptionList<ProjectDictionaryOption>(priorityResponse.data))
      setRobotTypes(expectOptionList<ProjectDictionaryOption>(robotTypeResponse.data))
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

  const loadContacts = useCallback((supplierId: number) => {
    const seq = ++contactSeq.current
    setContacts([]); setContactsLoading(true); setContactsError(false)
    http.get<ApiResponses['GET /supplier-contacts']>('/supplier-contacts', {
      params: { supplierId }, quietNetworkError: true,
    } as QuietRequestConfig).then((response) => {
      if (seq === contactSeq.current) setContacts(expectOptionList<SupplierContactOption>(response.data))
    }).catch(() => { if (seq === contactSeq.current) setContactsError(true) })
      .finally(() => { if (seq === contactSeq.current) setContactsLoading(false) })
  }, [])

  // 每次打开都按当时的项目重新填表并加载选项；关闭时让迟到的选项响应失效。
  useEffect(() => {
    if (!visible) return
    let active = true
    void Promise.resolve().then(() => {
      if (!active) return
      setEditing(project); setEditConflict(false)
      setRobotParts([]); setContacts([])
      if (project) {
        form.setFieldsValue({
          name: project.name, description: project.description, supplierId: project.supplierId, workOrderNos: project.workOrderNos,
          machineModel: project.machineModel ?? undefined, robotPartId: project.robotPartId ?? undefined,
          priorityId: project.priorityId ?? undefined, robotTypeId: project.robotTypeId ?? undefined,
          robotOwnerContactIds: project.robotOwnerContactIds?.length ? project.robotOwnerContactIds : undefined,
          expectedCompletionDate: project.expectedCompletionDate ?? undefined,
        })
        setSelectedSupplierId(project.supplierId); setSelectedRobotPartId(project.robotPartId ?? null)
        loadRobotParts(project.supplierId); loadContacts(project.supplierId)
      } else {
        form.resetFields()
        setSelectedSupplierId(null); setSelectedRobotPartId(null)
      }
      loadSupplierOptions(); loadMetadataOptions()
    })
    return () => {
      active = false
      supplierOptionsSeq.current += 1; optionsSeq.current += 1; partSeq.current += 1; contactSeq.current += 1
    }
  }, [form, loadContacts, loadMetadataOptions, loadRobotParts, loadSupplierOptions, project, visible])

  const retryMetadataOptions = () => {
    if (metadataOptionsError) loadMetadataOptions()
    if (robotPartsError && selectedSupplierId) loadRobotParts(selectedSupplierId)
    if (contactsError && selectedSupplierId) loadContacts(selectedSupplierId)
  }

  const close = () => { if (!saving) onCancel() }

  const changeSupplier = (value?: number) => {
    const nextSupplierId = nullableNumber(value)
    partSeq.current += 1; contactSeq.current += 1
    setSelectedSupplierId(nextSupplierId); setSelectedRobotPartId(null)
    form.setFieldValue('robotPartId', undefined); setRobotParts([]); setRobotPartsError(false); setRobotPartsLoading(false)
    form.setFieldValue('robotOwnerContactIds', undefined); setContacts([]); setContactsError(false); setContactsLoading(false)
    if (nextSupplierId) { loadRobotParts(nextSupplierId); loadContacts(nextSupplierId) }
  }

  const partOptions = [...robotParts]
  if (editing?.robotPartId && !partOptions.some((item) => item.id === editing.robotPartId))
    partOptions.push({ id: editing.robotPartId, supplierId: editing.supplierId, supplierName: editing.supplierName ?? '', partNumber: editing.robotPartNumber || '历史料号', model: editing.robotModelName || '历史型号', sortNo: 0, enabled: false, inUse: true })
  const selectedPart = partOptions.find((item) => item.id === selectedRobotPartId)
  // 已停用的联络人只在原已选中时出现（不可新选）。
  const contactOptions = contacts.filter((item) => item.enabled || editing?.robotOwnerContactIds?.includes(item.id))
  const supplierFormOptions = [...suppliers]
  if (editing && !supplierFormOptions.some((item) => item.id === editing.supplierId))
    supplierFormOptions.push({ id: editing.supplierId, name: editing.supplierName || '历史 Robot 厂商' })
  const priorityOptions = [...priorities]
  if (editing?.priorityId && !priorityOptions.some((item) => item.id === editing.priorityId))
    priorityOptions.push({ id: editing.priorityId, type: 'PRIORITY', name: editing.priorityName || '历史优先级', sortNo: 0, enabled: false, parentId: null, parentName: null, inUse: true })
  const robotTypeOptions = [...robotTypes]
  if (editing?.robotTypeId && !robotTypeOptions.some((item) => item.id === editing.robotTypeId))
    robotTypeOptions.push({ id: editing.robotTypeId, type: 'ROBOT_TYPE', name: editing.robotTypeName || '历史 Robot 类型', sortNo: 0, enabled: false, parentId: null, parentName: null, inUse: true })

  const missingCreateOptions: { message: string; path: string; permission: string; action: string }[] = []
  if (!supplierOptionsLoading && !supplierOptionsError && suppliers.length === 0)
    missingCreateOptions.push({ message: '暂无启用的供应商，请先新增或启用供应商。', path: '/suppliers', permission: 'supplier:manage', action: '维护供应商' })
  if (!metadataOptionsLoading && !metadataOptionsError) {
    if (!robotTypes.length)
      missingCreateOptions.push({ message: '暂无启用的 Robot 类型，请在数据字典中维护。', path: '/system/dictionaries', permission: 'dict:manage', action: '维护 Robot 类型' })
    if (!priorities.length)
      missingCreateOptions.push({ message: '暂无启用的优先级，请在数据字典中维护。', path: '/system/dictionaries', permission: 'dict:manage', action: '维护优先级' })
  }
  if (selectedSupplierId && !robotPartsLoading && !robotPartsError && robotParts.length === 0 && !editing?.robotPartId)
    missingCreateOptions.push({ message: '所选 Robot 厂商暂无启用的料号，请先维护料号。', path: '/system/dictionaries', permission: 'dict:manage', action: '维护 Robot 料号' })
  if (selectedSupplierId && !contactsLoading && !contactsError && !contacts.some((item) => item.enabled))
    missingCreateOptions.push({ message: '所选 Robot 厂商暂无启用的联络人，请先在供应商管理中维护联络人。', path: '/suppliers', permission: 'supplier:manage', action: '维护联络人' })
  const robotPartOptionsRequired = !editing || selectedRobotPartId !== null
  const submitOptionsBlocked = metadataOptionsLoading || metadataOptionsError || contactsLoading || contactsError
    || robotPartOptionsRequired && (robotPartsLoading || robotPartsError)
    || (!editing && (supplierOptionsLoading || supplierOptionsError || missingCreateOptions.length > 0))
  const createOptionsBusy = supplierOptionsLoading || metadataOptionsLoading
  // 只在厂商加载失败或缺少必需基础数据时提供手动刷新（优先级/料号失败另有“重试加载选项”）；正常情况下不显示。
  const createOptionsNeedRefresh = supplierOptionsError || missingCreateOptions.length > 0
  const refreshCreateOptions = () => {
    loadSupplierOptions(); loadMetadataOptions()
    if (selectedSupplierId) loadRobotParts(selectedSupplierId)
  }

  const submit = async () => {
    if (saveInFlight.current || submitOptionsBlocked) return
    saveInFlight.current = true; setSaving(true)
    try {
      const values = await form.validate().catch(() => null) as ProjectFormValues | null
      if (!values) return
      const robotPartId = nullableNumber(values.robotPartId)
      if (robotPartId && !partOptions.some((part) => part.id === robotPartId && part.supplierId === Number(values.supplierId))) {
        Message.error('所选 Robot 料号与厂商不匹配，请重新选择'); return
      }
      const payload = {
        name: values.name, description: values.description, supplierId: Number(values.supplierId),
        workOrderNos: normalizeList(values.workOrderNos), machineModel: values.machineModel?.trim() || null,
        robotPartId, priorityId: nullableNumber(values.priorityId), robotTypeId: nullableNumber(values.robotTypeId),
        robotOwnerContactIds: (values.robotOwnerContactIds ?? []).map(Number),
        expectedCompletionDate: values.expectedCompletionDate || null,
      }
      if (editing) {
        const response = await http.put<ApiResponses['PUT /projects/{id}']>(`/projects/${editing.id}`, { ...payload, expectedUpdatedAt: editing.updatedAt })
        Message.success('项目资料已保存')
        setEditConflict(false)
        onSaved(response.data as ProjectSummary, false)
      } else {
        const response = await http.post<ApiResponses['POST /projects']>('/projects', payload)
        Message.success('项目已创建并开始')
        onSaved(response.data as ProjectSummary, true)
      }
    } catch (error) {
      // 40901 只由拦截器提示。40903 保留弹窗和已填内容，改用刷新后的最新 updatedAt。
      if (!editing || !isVersionConflict(error)) throw error
      onStale?.()
      try {
        const response = await http.get<ApiResponses['GET /projects/{id}']>(`/projects/${editing.id}`, { quietClientError: true } as QuietRequestConfig)
        setEditing(response.data as ProjectSummary)
        setEditConflict(true)
      } catch {
        setEditConflict(false); onCancel()
        Message.info('项目已不可编辑或已被删除，请刷新后查看')
      }
    } finally { saveInFlight.current = false; setSaving(false) }
  }

  return (
    <Modal className="form-dialog" style={{ width: 760 }} title={editing ? '编辑项目' : '新建项目'} visible={visible} onOk={submit} onCancel={close} confirmLoading={saving} closable={!saving} maskClosable={false} escToExit={!saving} cancelButtonProps={{ disabled: saving }} okButtonProps={{ disabled: submitOptionsBlocked }} okText={editing ? '保存' : '创建并开始'} unmountOnExit>
      <Form onSubmit={() => void submit()} className="form-grid" form={form} layout="vertical">
        {!editing && (createOptionsBusy || createOptionsNeedRefresh) && <div className="form-grid-full" style={{ display: 'grid', gap: 8 }}>
          {createOptionsBusy && <Typography.Text type="secondary">正在加载创建所需的基础数据…</Typography.Text>}
          {supplierOptionsError && <Alert type="error" content="Robot 厂商选项加载失败，请刷新基础数据后重试。" />}
          {missingCreateOptions.length > 0 && <Alert type="warning" title="创建前请补齐基础数据" content={<>
            <ul>{missingCreateOptions.map((item) => <li key={item.message}>{item.message}{hasPerm(item.permission)
              ? <> <Link to={item.path} target="_blank" rel="noopener noreferrer" style={{ whiteSpace: 'nowrap' }}>{item.action}（新窗口）</Link></>
              : ' 请联系具有维护权限的管理员。'}</li>)}</ul>
            <span>可先填写项目资料，补齐后点击“刷新基础数据”继续。</span>
          </>} />}
          {createOptionsNeedRefresh && <div><Button size="small" onClick={refreshCreateOptions} disabled={saving || supplierOptionsLoading || metadataOptionsLoading || robotPartsLoading}>刷新基础数据</Button></div>}
        </div>}
        {editing && editConflict && (
          <Alert className="form-grid-full" type="warning" content={`项目已被他人修改，已刷新为最新数据（当前名称：${editing.name}）。请核对后再次保存，保存将覆盖为本弹窗中的内容。`} />
        )}
        <Form.Item className="form-grid-full" label="项目名称" field="name" rules={[{ required: true, message: '请输入项目名称' }, textLengthRule('项目名称', 128)]}><Input autoFocus placeholder="项目名称" /></Form.Item>
        <Form.Item className="form-grid-full" label="工令号" field="workOrderNos" rules={[{ required: true, message: '请至少填写一个工令号' }, { validator: (value, callback) => listRule('工令号', WORK_ORDER_LIMIT, value, callback) }]}><Select mode="multiple" allowCreate allowClear showSearch maxTagCount={3} tokenSeparators={[',', '，', ';', '；', '\n']} placeholder="输入工令号后按回车，可填写多个" /></Form.Item>
        <Form.Item label="机型" field="machineModel" rules={[{ required: true, message: '请填写机型' }, textLengthRule('机型', 128)]}><Input maxLength={128} showWordLimit placeholder="请输入机型" /></Form.Item>
        <Form.Item label="Robot 厂商" field="supplierId" rules={[{ required: true, message: '请选择 Robot 厂商' }]}><Select showSearch placeholder="选择 Robot 厂商" filterOption={optionFilter} loading={supplierOptionsLoading} disabled={!!editing || supplierOptionsLoading || supplierOptionsError} onChange={(value) => changeSupplier(value as number | undefined)}>{supplierFormOptions.map((supplier) => <Select.Option key={supplier.id} value={supplier.id}>{supplier.name}</Select.Option>)}</Select></Form.Item>
        <Form.Item label="Robot 类型" field="robotTypeId" rules={[{ required: true, message: '请选择 Robot 类型' }]}><Select allowClear showSearch placeholder="选择 Robot 类型" loading={metadataOptionsLoading} disabled={metadataOptionsLoading || metadataOptionsError} filterOption={optionFilter}>{robotTypeOptions.map((item) => <Select.Option key={item.id} value={item.id} disabled={!item.enabled}>{item.name}{item.enabled ? '' : '（已停用）'}</Select.Option>)}</Select></Form.Item>
        <Form.Item label="Robot 料号" field="robotPartId" rules={editing && !editing.robotPartId ? [] : [{ required: true, message: '请选择 Robot 料号' }]} help={editing && !editing.robotPartId ? '历史项目可保留原型号；选择料号后将使用新料号对应型号。' : undefined}><Select allowClear showSearch placeholder={selectedSupplierId ? '选择 Robot 料号' : '请先选择 Robot 厂商'} loading={robotPartsLoading} disabled={!selectedSupplierId || robotPartsLoading || robotPartsError} filterOption={optionFilter} onChange={(value) => setSelectedRobotPartId(nullableNumber(value))}>{partOptions.map((item) => <Select.Option key={item.id} value={item.id} disabled={!item.enabled}>{item.partNumber}{item.enabled ? '' : '（已停用）'}</Select.Option>)}</Select></Form.Item>
        <Form.Item className="form-grid-full" label="Robot 型号（自动带出）"><Input.TextArea readOnly autoSize={{ minRows: 2, maxRows: 4 }} value={selectedPart?.model ?? (editing && !selectedRobotPartId ? editing.robotModelName ?? '' : '')} placeholder={selectedRobotPartId ? '所选料号未设置型号' : '选择 Robot 料号后自动带出'} /></Form.Item>
        <Form.Item label="负责人"><Input readOnly value={editing ? `${editing.responsibleUserName || '未设置'}${editing.responsibleUserEmployeeNo ? `（${editing.responsibleUserEmployeeNo}）` : ''}` : `${user?.realName || '当前用户'}${user?.employeeNo ? `（${user.employeeNo}）` : ''}`} /></Form.Item>
        <Form.Item label="Robot 负责人" field="robotOwnerContactIds" rules={[{ required: true, type: 'array', minLength: 1, message: '请选择 Robot 负责人' }, { type: 'array', maxLength: ROBOT_OWNER_LIMIT, message: `Robot 负责人最多选择 ${ROBOT_OWNER_LIMIT} 位` }]}><Select mode="multiple" allowClear showSearch maxTagCount={3} placeholder={selectedSupplierId ? '选择该厂商的联络人，可多选' : '请先选择 Robot 厂商'} loading={contactsLoading} disabled={!selectedSupplierId || contactsLoading || contactsError} filterOption={optionFilter}>{contactOptions.map((item) => <Select.Option key={item.id} value={item.id} disabled={!item.enabled}>{item.name}{item.enabled ? '' : '（已停用）'}</Select.Option>)}</Select></Form.Item>
        <Form.Item label="课别" help={editing ? undefined : '按创建人的课别记录，未设置也可创建'}><Input readOnly value={editing ? editing.sectionName || '未设置' : '自动带出'} /></Form.Item>
        <Form.Item label="优先级" field="priorityId" rules={[{ required: true, message: '请选择优先级' }]}><Select allowClear showSearch placeholder="选择优先级" loading={metadataOptionsLoading} disabled={metadataOptionsLoading || metadataOptionsError} filterOption={optionFilter}>{priorityOptions.map((item) => <Select.Option key={item.id} value={item.id} disabled={!item.enabled}>{item.name}{item.enabled ? '' : '（已停用）'}</Select.Option>)}</Select></Form.Item>
        <Form.Item label="需求完成时间" field="expectedCompletionDate" rules={[{ required: true, message: '请选择需求完成时间' }]}><DatePicker allowClear format="YYYY-MM-DD" placeholder="选择需求完成时间" style={{ width: '100%' }} /></Form.Item>
        <Form.Item className="form-grid-full" label="项目说明" field="description"><Input.TextArea rows={3} maxLength={500} showWordLimit wordLimitPosition="outside" placeholder="选填" /></Form.Item>
        <div className="form-grid-full dialog-note">所选 Robot 厂商的全部启用账号均可访问此项目。</div>
        {(metadataOptionsError || robotPartsError || contactsError) && <div className="form-grid-full dialog-note dialog-note--danger">
          <span>Robot 类型、优先级、Robot 料号或联络人选项加载失败，请重试。</span>
          <Button size="small" onClick={retryMetadataOptions}>重试加载选项</Button>
        </div>}
      <EnterSubmit /></Form>
    </Modal>
  )
}
