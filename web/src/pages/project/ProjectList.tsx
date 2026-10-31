import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import {
  Alert, Badge, Button, Card, Checkbox, DatePicker, Form, Input, Message, Modal, Popconfirm, Select, Space, Table, Tag, Tooltip, Typography,
} from '@arco-design/web-react'
import { IconPlus } from '@arco-design/web-react/icon'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import http, { type QuietRequestConfig } from '../../api/client'
import { isVersionConflict } from '../../api/versionConflict'
import { actionSlots } from '../../components/ActionSlots'
import { ColumnResizeProvider, ColumnVisibilityPicker, ResizableHeaderCell } from '../../components/columnResize'
import { columnLayoutStorageKey, measureTableWidth, withFlexibleColumn, type ColumnLayoutSpec } from '../../components/tableColumnLayout'
import { useColumnLayout } from '../../components/useColumnLayout'
import { useAuth } from '../../store/auth'
import {
  type PageResp, type ProjectDictionaryOption, type ProjectGroup, type RobotPart, type SupplierContactOption, MATERIAL_REVIEW, PROJECT_STATUS,
} from '../../api/types'
import { textLengthRule } from '../../utils/textRules'
import { normalizeList } from '../../utils/listValues'
import { useCollaboration } from '../../store/collaboration'
import type { ApiResponses } from '../../api/types'
import LoadError from '../../components/LoadError'
import EnterSubmit from '../../components/EnterSubmit'
import OverdueTag from '../../components/OverdueTag'

interface SupplierOpt { id: number; name: string }
interface ProjectGroupFormValues {
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
  subprojectNames?: string[]
}

const WORK_ORDER_LIMIT = 50
const SUBPROJECT_LIMIT = 50
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

function displayText(value?: string | null) { return value?.trim() || '-' }

function cellText(value?: string | null) {
  const text = displayText(value)
  return <span className="table-cell-text" title={text}>{text}</span>
}

// 默认列宽合计 1572，1920 视口下状态和未读仍在首屏。用户可隐藏列或拖动列宽，偏好按账号记在本机。
// 容器还有剩余宽度时主项目名称保持设定宽度，其余列平分，避免名称列被拉得很宽。
const PROJECT_LIST_COLUMNS: readonly ColumnLayoutSpec[] = [
  { key: 'name', title: '主项目名称', defaultWidth: 120, minWidth: 96, locked: true },
  { key: 'workOrderNos', title: '工令号', defaultWidth: 96, minWidth: 72 },
  { key: 'machineModel', title: '机型', defaultWidth: 80, minWidth: 64 },
  { key: 'supplierName', title: 'Robot 厂商', defaultWidth: 112, minWidth: 104 },
  { key: 'robotTypeName', title: 'Robot 类型', defaultWidth: 112, minWidth: 104 },
  { key: 'robotPartNumber', title: 'Robot 料号', defaultWidth: 128, minWidth: 104 },
  { key: 'robotModelName', title: 'Robot 型号', defaultWidth: 168, minWidth: 104 },
  { key: 'responsibleUserName', title: '负责人', defaultWidth: 84, minWidth: 72 },
  { key: 'robotOwnerName', title: 'Robot 负责人', defaultWidth: 124, minWidth: 116 },
  { key: 'sectionName', title: '课别', defaultWidth: 72, minWidth: 64 },
  { key: 'priorityName', title: '优先级', defaultWidth: 76, minWidth: 72 },
  { key: 'expectedCompletionDate', title: '需求完成时间', defaultWidth: 116, minWidth: 108 },
  { key: 'progress', title: '子项目进度', defaultWidth: 112, minWidth: 96 },
  { key: 'status', title: '状态', defaultWidth: 96, minWidth: 88 },
  { key: 'unreadMessages', title: '未读留言', defaultWidth: 100, minWidth: 96 },
  { key: 'actions', title: '操作', defaultWidth: 140, minWidth: 108, maxWidth: 280, locked: true },
]

function expectOptionList<T>(value: unknown): T[] {
  if (!Array.isArray(value)) throw new Error('选项接口返回格式错误')
  return value as T[]
}

const DEFAULT_PAGE_SIZE = 10

function parsePositiveIntParam(value: string | null) {
  const parsed = value ? Number(value) : NaN
  return Number.isSafeInteger(parsed) && parsed > 0 ? parsed : undefined
}

function parsePageSizeParam(value: string | null) {
  const parsed = parsePositiveIntParam(value)
  return parsed && parsed <= 100 ? parsed : DEFAULT_PAGE_SIZE
}

function parseStatusParam(value: string | null) {
  return value && value in PROJECT_STATUS ? value : undefined
}

function parseMaterialParam(value: string | null) {
  return value && value in MATERIAL_REVIEW ? value : undefined
}

export default function ProjectList() {
  const navigate = useNavigate()
  const revision = useCollaboration((state) => state.revision)
  const [data, setData] = useState<PageResp<ProjectGroup>>({ list: [], total: 0, page: 1, pageSize: 10 })
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState(false)
  const [listStale, setListStale] = useState(false)
  const loadedQueryKey = useRef<string | null>(null)
  const [reloadKey, setReloadKey] = useState(0)
  // 筛选和分页放在地址栏里：进入主项目再返回时还原列表位置。用 replace，不在历史里逐页留痕。
  const [searchParams, setSearchParams] = useSearchParams()
  const keyword = searchParams.get('q') ?? ''
  const status = parseStatusParam(searchParams.get('status'))
  const supplierParam = parsePositiveIntParam(searchParams.get('supplier'))
  const materialState = parseMaterialParam(searchParams.get('material'))
  const overdue = searchParams.get('overdue') === '1'
  const page = parsePositiveIntParam(searchParams.get('page')) ?? 1
  const pageSize = parsePageSizeParam(searchParams.get('pageSize'))
  /** 写入地址栏；返回条件是否真的变化（未变化时调用方需自行重新加载）。 */
  const updateQuery = useCallback((changes: Record<string, string | number | undefined>) => {
    const next = new URLSearchParams(searchParams)
    Object.entries(changes).forEach(([key, value]) => {
      const isDefault = (key === 'page' && value === 1) || (key === 'pageSize' && value === DEFAULT_PAGE_SIZE)
      if (value === undefined || value === '' || isDefault) next.delete(key)
      else next.set(key, String(value))
    })
    if (next.toString() === searchParams.toString()) return false
    setSearchParams(next, { replace: true })
    return true
  }, [searchParams, setSearchParams])
  const setPage = useCallback((value: number) => updateQuery({ page: value }), [updateQuery])
  const [modalOpen, setModalOpen] = useState(false)
  const [editConflict, setEditConflict] = useState(false)
  const [saving, setSaving] = useState(false)
  const saveInFlight = useRef(false)
  const [editing, setEditing] = useState<ProjectGroup | null>(null)
  const [suppliers, setSuppliers] = useState<SupplierOpt[]>([])
  const [supplierOptionsLoading, setSupplierOptionsLoading] = useState(true)
  const [supplierOptionsError, setSupplierOptionsError] = useState(false)
  const [priorities, setPriorities] = useState<ProjectDictionaryOption[]>([])
  const [robotTypes, setRobotTypes] = useState<ProjectDictionaryOption[]>([])
  const [metadataOptionsLoading, setMetadataOptionsLoading] = useState(false)
  const [metadataOptionsError, setMetadataOptionsError] = useState(false)
  const [robotParts, setRobotParts] = useState<RobotPart[]>([])
  const [robotPartsLoading, setRobotPartsLoading] = useState(false)
  const [robotPartsError, setRobotPartsError] = useState(false)
  const [contacts, setContacts] = useState<SupplierContactOption[]>([])
  const [contactsLoading, setContactsLoading] = useState(false)
  const [contactsError, setContactsError] = useState(false)
  const contactSeq = useRef(0)
  const [selectedSupplierId, setSelectedSupplierId] = useState<number | null>(null)
  const [selectedRobotPartId, setSelectedRobotPartId] = useState<number | null>(null)
  const optionsSeq = useRef(0)
  const partSeq = useRef(0)
  const supplierOptionsSeq = useRef(0)
  const [form] = Form.useForm()
  const { hasPerm, user } = useAuth()
  const columnLayout = useColumnLayout(columnLayoutStorageKey('project-groups', user?.id), PROJECT_LIST_COLUMNS)
  const tableComponents = useMemo(() => ({ header: { cell: ResizableHeaderCell } }), [])
  const tableHostRef = useRef<HTMLDivElement>(null)
  const [tableWidth, setTableWidth] = useState(0)
  useLayoutEffect(() => {
    const host = tableHostRef.current
    if (!host) return
    let frame = 0
    const measure = () => {
      const next = measureTableWidth(host)
      setTableWidth((current) => (current === next ? current : next))
    }
    measure()
    const observer = new ResizeObserver(() => {
      cancelAnimationFrame(frame)
      frame = requestAnimationFrame(measure)
    })
    observer.observe(host)
    const container = host.querySelector('.arco-table-container')
    if (container) observer.observe(container)
    return () => {
      cancelAnimationFrame(frame)
      observer.disconnect()
    }
  }, [loadError])
  const isInternal = user?.userType === 'INTERNAL'
  const canDelete = isInternal && hasPerm('project:delete')
  // 供应商账号看不到厂商筛选，地址栏里的 supplier 参数对其无效。
  const supplierId = isInternal ? supplierParam : undefined

  const fetchGroups = useCallback(async () => {
    const response = await http.get<ApiResponses['GET /project-groups']>('/project-groups', {
      params: { page, pageSize, keyword: keyword || undefined, status, supplierId, materialState, overdue: overdue || undefined },
      quietNetworkError: true,
    } as QuietRequestConfig)
    return response.data as PageResp<ProjectGroup>
  }, [keyword, materialState, overdue, page, pageSize, status, supplierId])

  const load = useCallback(() => {
    setLoading(true); setLoadError(false); setListStale(false); setReloadKey((value) => value + 1)
  }, [])
  // 条件变化时由地址栏驱动重新请求；条件相同（例如重复搜索同一关键字）才手动重新加载。
  const applyQuery = (changes: Record<string, string | number | undefined>) => {
    if (!updateQuery(changes)) { load(); return }
    setLoading(true); setLoadError(false); setListStale(false)
  }

  useEffect(() => {
    let active = true
    const queryKey = `${page}|${pageSize}|${keyword}|${status ?? ''}|${supplierId ?? ''}|${materialState ?? ''}|${overdue}`
    const background = loadedQueryKey.current === queryKey
    fetchGroups().then((next) => {
      if (!active) return
      loadedQueryKey.current = queryKey
      setData(next); setLoadError(false); setListStale(false)
      const lastPage = Math.max(1, Math.ceil(next.total / (next.pageSize || pageSize)))
      if (page > lastPage) setPage(lastPage)
    }).catch(() => {
      if (!active) return
      if (background) setListStale(true)
      else { setLoadError(true); setListStale(false) }
    }).finally(() => { if (active) setLoading(false) })
    return () => { active = false }
  }, [fetchGroups, keyword, materialState, overdue, page, pageSize, reloadKey, revision, setPage, status, supplierId])

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

  const retryMetadataOptions = () => {
    if (metadataOptionsError) loadMetadataOptions()
    if (robotPartsError && selectedSupplierId) loadRobotParts(selectedSupplierId)
    if (contactsError && selectedSupplierId) loadContacts(selectedSupplierId)
  }

  const openCreate = () => {
    setEditing(null); setEditConflict(false); form.resetFields(); form.setFieldValue('subprojectNames', [])
    setSelectedSupplierId(null); setSelectedRobotPartId(null); setRobotParts([]); setContacts([])
    loadMetadataOptions(); setModalOpen(true)
  }

  const openEdit = (group: ProjectGroup) => {
    setEditing(group); setEditConflict(false)
    form.setFieldsValue({
      name: group.name, description: group.description, supplierId: group.supplierId, workOrderNos: group.workOrderNos,
      machineModel: group.machineModel ?? undefined, robotPartId: group.robotPartId ?? undefined,
      priorityId: group.priorityId ?? undefined, robotTypeId: group.robotTypeId ?? undefined,
      robotOwnerContactIds: group.robotOwnerContactIds?.length ? group.robotOwnerContactIds : undefined,
      expectedCompletionDate: group.expectedCompletionDate ?? undefined,
    })
    setSelectedSupplierId(group.supplierId); setSelectedRobotPartId(group.robotPartId ?? null); loadMetadataOptions()
    loadRobotParts(group.supplierId); loadContacts(group.supplierId)
    setModalOpen(true)
  }

  const closeModal = () => {
    if (saving) return
    optionsSeq.current += 1; partSeq.current += 1; contactSeq.current += 1; setModalOpen(false)
  }

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
      const values = await form.validate().catch(() => null) as ProjectGroupFormValues | null
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
        subprojectNames: editing ? undefined : normalizeList(values.subprojectNames),
      }
      if (editing) {
        await http.put<ApiResponses['PUT /project-groups/{id}']>(`/project-groups/${editing.id}`, { ...payload, expectedUpdatedAt: editing.updatedAt })
        Message.success('主项目资料已保存，并同步到未完成的子项目')
      } else {
        await http.post<ApiResponses['POST /project-groups']>('/project-groups', payload)
        Message.success('主项目及子项目已创建')
      }
      setEditConflict(false); setModalOpen(false); load()
    } catch (error) {
      // 40901 只由拦截器提示。40903 保留弹窗和已填内容，等列表刷新后再改用最新 updatedAt。
      if (!editing || !isVersionConflict(error)) throw error
      load()
      try {
        const response = await http.get<ApiResponses['GET /project-groups/{id}']>(`/project-groups/${editing.id}`, { quietClientError: true } as QuietRequestConfig)
        setEditing(response.data.group as ProjectGroup)
        setEditConflict(true)
      } catch {
        setEditConflict(false); setModalOpen(false)
        Message.info('主项目已不可编辑或已被删除，请刷新后查看')
      }
    } finally { saveInFlight.current = false; setSaving(false) }
  }

  const remove = async (group: ProjectGroup) => {
    await http.delete<ApiResponses['DELETE /project-groups/{id}']>(`/project-groups/${group.id}`); Message.success('主项目已删除'); load()
  }

  const columns = withFlexibleColumn(columnLayout.apply([
    { key: 'name', title: '主项目名称', dataIndex: 'name', fixed: 'left' as const, ellipsis: true, render: (value: string, row: ProjectGroup) => <Link className="table-link" to={`/project-groups/${row.id}`} title={value}>{value}</Link> },
    { key: 'workOrderNos', title: '工令号', dataIndex: 'workOrderNos', ellipsis: true, render: (values?: string[]) => cellText(values?.join('、')) },
    { key: 'machineModel', title: '机型', dataIndex: 'machineModel', ellipsis: true, render: cellText },
    { key: 'supplierName', title: 'Robot 厂商', dataIndex: 'supplierName', ellipsis: true, render: cellText },
    { key: 'robotTypeName', title: 'Robot 类型', dataIndex: 'robotTypeName', ellipsis: true, render: cellText },
    { key: 'robotPartNumber', title: 'Robot 料号', dataIndex: 'robotPartNumber', ellipsis: true, render: cellText },
    { key: 'robotModelName', title: 'Robot 型号', dataIndex: 'robotModelName', ellipsis: true, render: (value?: string | null) => <Tooltip content={displayText(value)}><span className="table-cell-text">{displayText(value)}</span></Tooltip> },
    { key: 'responsibleUserName', title: '负责人', dataIndex: 'responsibleUserName', ellipsis: true, render: cellText },
    { key: 'robotOwnerName', title: 'Robot 负责人', dataIndex: 'robotOwnerName', ellipsis: true, render: cellText },
    { key: 'sectionName', title: '课别', dataIndex: 'sectionName', ellipsis: true, render: cellText },
    { key: 'priorityName', title: '优先级', dataIndex: 'priorityName', align: 'center' as const, render: cellText },
    { key: 'expectedCompletionDate', title: '需求完成时间', dataIndex: 'expectedCompletionDate', align: 'center' as const, render: (value: string | null | undefined, row: ProjectGroup) => (
      <span className="project-due-cell">{displayText(value)}<OverdueTag date={value} status={row.status} /></span>
    ) },
    { key: 'progress', title: '子项目进度', align: 'center' as const, render: (_: unknown, row: ProjectGroup) => (
      <div className="project-group-progress" title={`已完成 ${row.completedCount} / ${row.subprojectCount}`}>
        <span>{row.completedCount}/{row.subprojectCount}</span>
        {row.materialPendingCount > 0 && <Tag size="small" color="orange">资料待确认 {row.materialPendingCount}</Tag>}
      </div>
    ) },
    { key: 'status', title: '状态', dataIndex: 'status', align: 'center' as const, render: (value: string) => <Tag color={PROJECT_STATUS[value]?.color}>{PROJECT_STATUS[value]?.text || value}</Tag> },
    { key: 'unreadMessages', title: '未读留言', dataIndex: 'unreadMessages', align: 'center' as const, render: (value: number) => value > 0 ? <Badge count={value} /> : '-' },
    { key: 'actions', title: '操作', render: (_: unknown, row: ProjectGroup) => actionSlots([
      <Button key="enter" size="mini" type="text" onClick={() => navigate(`/project-groups/${row.id}`)}>进入</Button>,
      isInternal && hasPerm('project:update') && ['DRAFT', 'IN_PROGRESS'].includes(row.status)
        && <Button key="edit" size="mini" type="text" onClick={() => openEdit(row)}>编辑</Button>,
      canDelete && row.subprojectCount === 0 && <Popconfirm key="delete" title={`确认删除空主项目“${row.name}”？`} onOk={() => remove(row)}><Button size="mini" type="text" status="danger">删除</Button></Popconfirm>,
    ], 'project-group') },
  ]), tableWidth, 'name')

  return (
    <Card className="page-card page-card--table">
      <h1 className="visually-hidden">Robot仿真协作</h1>
      <div className="page-toolbar responsive-toolbar">
        <Space wrap>
          <Input.Search allowClear placeholder="主项目名称" style={{ width: 240 }} defaultValue={keyword} onSearch={(value) => applyQuery({ q: value.trim(), page: 1 })} onClear={() => applyQuery({ q: undefined, page: 1 })} />
          <Select allowClear placeholder="状态" style={{ width: 130 }} value={status} onChange={(value) => applyQuery({ status: value as string | undefined, page: 1 })}>
            {['DRAFT', 'IN_PROGRESS', 'COMPLETED', 'TERMINATED'].map((key) => <Select.Option key={key} value={key}>{PROJECT_STATUS[key]?.text ?? key}</Select.Option>)}
          </Select>
          <Select allowClear placeholder="资料状态" style={{ width: 130 }} value={materialState} onChange={(value) => applyQuery({ material: value as string | undefined, page: 1 })}>
            {(['NO_MATERIAL', 'PENDING', 'RETURNED'] as const).map((key) => <Select.Option key={key} value={key}>{MATERIAL_REVIEW[key].text}</Select.Option>)}
          </Select>
          <Checkbox checked={overdue} onChange={(checked) => applyQuery({ overdue: checked ? 1 : undefined, page: 1 })}>仅看逾期</Checkbox>
          {isInternal && <Select allowClear showSearch placeholder="Robot 厂商" style={{ width: 180 }} value={supplierId} loading={supplierOptionsLoading} disabled={supplierOptionsError} onChange={(value) => applyQuery({ supplier: value as number | undefined, page: 1 })}>{suppliers.map((supplier) => <Select.Option key={supplier.id} value={supplier.id}>{supplier.name}</Select.Option>)}</Select>}
        </Space>
        <Space>
          <ColumnVisibilityPicker
            specs={PROJECT_LIST_COLUMNS}
            hidden={columnLayout.hidden}
            onToggle={columnLayout.toggle}
            onShowAll={columnLayout.showAll}
            onHideOptional={columnLayout.hideOptional}
            onReset={columnLayout.reset}
          />
          {supplierOptionsError && <Button size="small" onClick={loadSupplierOptions}>重试加载供应商</Button>}
          {isInternal && hasPerm('project:create') && <Button type="primary" icon={<IconPlus />} onClick={openCreate}>新建主项目</Button>}
        </Space>
      </div>
      {loadError ? <LoadError onRetry={load} /> :
        <>
        {listStale && <div role="status" style={{ marginBottom: 12 }}>
          <Typography.Text type="warning">列表更新失败，当前显示上次获取的数据。</Typography.Text>
          <Button type="text" size="small" onClick={load}>重新获取</Button>
        </div>}
        <ColumnResizeProvider actions={columnLayout.actions}>
          <div ref={tableHostRef} className="page-table-host">
            <Table className="page-table page-table--resizable" rowKey="id" loading={loading} columns={columns} components={tableComponents} data={data.list} scroll={{ x: columns.reduce((sum, column) => sum + column.width, 0), y: 'var(--page-table-scroll-y)' }} pagination={{ total: data.total, current: page, pageSize, showTotal: true, sizeCanChange: true, onChange: (nextPage, nextSize) => applyQuery({ page: nextPage, pageSize: nextSize }) }} />
          </div>
        </ColumnResizeProvider>
        </>}

      <Modal className="form-dialog" style={{ width: 760 }} title={editing ? '编辑主项目' : '新建主项目'} visible={modalOpen} onOk={submit} onCancel={closeModal} confirmLoading={saving} closable={!saving} maskClosable={!saving} escToExit={!saving} cancelButtonProps={{ disabled: saving }} okButtonProps={{ disabled: submitOptionsBlocked }} okText={editing ? '保存并同步' : '创建主项目'} unmountOnExit>
        <Form onSubmit={() => void submit()} className="form-grid" form={form} layout="vertical">
          {!editing && (createOptionsBusy || createOptionsNeedRefresh) && <div className="form-grid-full" style={{ display: 'grid', gap: 8 }}>
            {(supplierOptionsLoading || metadataOptionsLoading) && <Typography.Text type="secondary">正在加载创建所需的基础数据…</Typography.Text>}
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
            <Alert className="form-grid-full" type="warning" content={`主项目已被他人修改，已刷新为最新数据（当前名称：${editing.name}）。请核对后再次保存，保存将覆盖为本弹窗中的内容。`} />
          )}
          <Form.Item className="form-grid-full" label="主项目名称" field="name" rules={[{ required: true, message: '请输入主项目名称' }, textLengthRule('主项目名称', 128)]}><Input autoFocus placeholder="主项目名称" /></Form.Item>
          {!editing && <Form.Item className="form-grid-full" label="子项目" field="subprojectNames" rules={[{ required: true, message: '请至少创建一个子项目' }, { validator: (value, callback) => listRule('子项目', SUBPROJECT_LIMIT, value, callback) }]}><Select mode="multiple" allowCreate allowClear showSearch maxTagCount={3} tokenSeparators={[',', '，', ';', '；', '\n']} placeholder="输入子项目名称后按回车，可一次创建多个" /></Form.Item>}
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
          <Form.Item className="form-grid-full" label="主项目说明" field="description"><Input.TextArea rows={3} maxLength={500} showWordLimit wordLimitPosition="outside" placeholder="选填" /></Form.Item>
          <div className="form-grid-full dialog-note">所选 Robot 厂商的全部启用账号均可访问此主项目及其子项目。</div>
          {(metadataOptionsError || robotPartsError || contactsError) && <div className="form-grid-full dialog-note dialog-note--danger">
            <span>Robot 类型、优先级、Robot 料号或联络人选项加载失败，请重试。</span>
            <Button size="small" onClick={retryMetadataOptions}>重试加载选项</Button>
          </div>}
        <EnterSubmit /></Form>
      </Modal>
    </Card>
  )
}
