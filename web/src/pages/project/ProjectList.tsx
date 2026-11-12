import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import {
  Badge, Button, Card, Checkbox, Input, Message, Popconfirm, Select, Space, Table, Tabs, Tag, Tooltip, Typography,
} from '@arco-design/web-react'
import { IconPlus } from '@arco-design/web-react/icon'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import http, { type QuietRequestConfig } from '../../api/client'
import { actionSlots } from '../../components/ActionSlots'
import { ColumnResizeProvider, ColumnVisibilityPicker, ResizableHeaderCell } from '../../components/columnResize'
import { columnLayoutStorageKey, measureTableWidth, withFlexibleColumn, type ColumnLayoutSpec } from '../../components/tableColumnLayout'
import { useColumnLayout } from '../../components/useColumnLayout'
import { useAuth } from '../../store/auth'
import { type PageResp, type ProjectSummary, MATERIAL_REVIEW, PROJECT_STATUS } from '../../api/types'
import { useCollaboration } from '../../store/collaboration'
import type { ApiResponses } from '../../api/types'
import LoadError from '../../components/LoadError'
import OverdueTag from '../../components/OverdueTag'
import ProjectFormModal from './ProjectFormModal'

interface SupplierOpt { id: number; name: string }

function displayText(value?: string | null) { return value?.trim() || '-' }

function cellText(value?: string | null) {
  const text = displayText(value)
  return <span className="table-cell-text" title={text}>{text}</span>
}

// 名称列固定在左侧、操作列固定在右侧，横向滚动时两者始终可见。用户可隐藏列或拖动列宽，偏好按账号记在本机。
// 容器还有剩余宽度时项目名称保持设定宽度，其余列平分，避免名称列被拉得很宽。
const PROJECT_LIST_COLUMNS: readonly ColumnLayoutSpec[] = [
  { key: 'name', title: '项目名称', defaultWidth: 120, minWidth: 96, locked: true },
  { key: 'workOrderNos', title: '工令号', defaultWidth: 96, minWidth: 72 },
  { key: 'machineModel', title: '机型', defaultWidth: 96, minWidth: 64 },
  { key: 'supplierName', title: 'Robot 厂商', defaultWidth: 112, minWidth: 104 },
  { key: 'robotTypeName', title: 'Robot 类型', defaultWidth: 112, minWidth: 104 },
  { key: 'robotPartNumber', title: 'Robot 料号', defaultWidth: 120, minWidth: 104 },
  { key: 'robotModelName', title: 'Robot 型号', defaultWidth: 140, minWidth: 104 },
  { key: 'responsibleUserName', title: '负责人', defaultWidth: 104, minWidth: 72 },
  { key: 'robotOwnerName', title: 'Robot 负责人', defaultWidth: 124, minWidth: 116 },
  { key: 'sectionName', title: '课别', defaultWidth: 72, minWidth: 64 },
  { key: 'priorityName', title: '优先级', defaultWidth: 76, minWidth: 72 },
  { key: 'expectedCompletionDate', title: '需求完成时间', defaultWidth: 116, minWidth: 108 },
  { key: 'materialReviewStatus', title: '资料状态', defaultWidth: 112, minWidth: 96 },
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
  const [data, setData] = useState<PageResp<ProjectSummary>>({ list: [], total: 0, page: 1, pageSize: 10 })
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState(false)
  const [listStale, setListStale] = useState(false)
  const loadedQueryKey = useRef<string | null>(null)
  const [reloadKey, setReloadKey] = useState(0)
  // 筛选和分页放在地址栏里：进入项目再返回时还原列表位置。用 replace，不在历史里逐页留痕。
  const [searchParams, setSearchParams] = useSearchParams()
  const keyword = searchParams.get('q') ?? ''
  const status = parseStatusParam(searchParams.get('status'))
  const supplierParam = parsePositiveIntParam(searchParams.get('supplier'))
  const materialState = parseMaterialParam(searchParams.get('material'))
  const overdue = searchParams.get('overdue') === '1'
  const mine = searchParams.get('mine') === '1'
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
  const [editing, setEditing] = useState<ProjectSummary | null>(null)
  const [suppliers, setSuppliers] = useState<SupplierOpt[]>([])
  const [supplierOptionsLoading, setSupplierOptionsLoading] = useState(true)
  const [supplierOptionsError, setSupplierOptionsError] = useState(false)
  const supplierOptionsSeq = useRef(0)
  const { hasPerm, user } = useAuth()
  const columnLayout = useColumnLayout(columnLayoutStorageKey('projects', user?.id), PROJECT_LIST_COLUMNS)
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
  // 供应商账号看不到厂商筛选，地址栏里的 supplier 参数对其无效。
  const supplierId = isInternal ? supplierParam : undefined

  const fetchProjects = useCallback(async () => {
    const response = await http.get<ApiResponses['GET /projects']>('/projects', {
      params: {
        page, pageSize, keyword: keyword || undefined, status, supplierId, materialState,
        overdue: overdue || undefined, mine: (isInternal && mine) || undefined,
      },
      quietNetworkError: true,
    } as QuietRequestConfig)
    return response.data as PageResp<ProjectSummary>
  }, [isInternal, keyword, materialState, mine, overdue, page, pageSize, status, supplierId])

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
    const queryKey = `${page}|${pageSize}|${keyword}|${status ?? ''}|${supplierId ?? ''}|${materialState ?? ''}|${overdue}|${mine}`
    const background = loadedQueryKey.current === queryKey
    fetchProjects().then((next) => {
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
  }, [fetchProjects, keyword, materialState, mine, overdue, page, pageSize, reloadKey, revision, setPage, status, supplierId])

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

  const openCreate = () => { setEditing(null); setModalOpen(true) }
  const openEdit = (project: ProjectSummary) => { setEditing(project); setModalOpen(true) }

  const saved = (project: ProjectSummary, created: boolean) => {
    setModalOpen(false)
    // 新建即开始：直接进入项目的文件页上传资料。
    if (created) navigate(`/projects/${project.id}?tab=files`)
    else load()
  }

  const remove = async (project: ProjectSummary) => {
    await http.delete<ApiResponses['DELETE /projects/{id}']>(`/projects/${project.id}`); Message.success('项目已删除'); load()
  }

  const columns = withFlexibleColumn(columnLayout.apply([
    { key: 'name', title: '项目名称', dataIndex: 'name', fixed: 'left' as const, ellipsis: true, render: (value: string, row: ProjectSummary) => <Link className="table-link" to={`/projects/${row.id}`} title={value}>{value}</Link> },
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
    { key: 'expectedCompletionDate', title: '需求完成时间', dataIndex: 'expectedCompletionDate', align: 'center' as const, render: (value: string | null | undefined, row: ProjectSummary) => (
      <span className="project-due-cell">{displayText(value)}<OverdueTag date={value} status={row.status} /></span>
    ) },
    { key: 'materialReviewStatus', title: '资料状态', dataIndex: 'materialReviewStatus', align: 'center' as const, render: (value: ProjectSummary['materialReviewStatus']) =>
      value ? <Tag size="small" color={MATERIAL_REVIEW[value].color}>{MATERIAL_REVIEW[value].text}</Tag> : '-' },
    { key: 'status', title: '状态', dataIndex: 'status', align: 'center' as const, render: (value: string) => <Tag color={PROJECT_STATUS[value]?.color}>{PROJECT_STATUS[value]?.text || value}</Tag> },
    { key: 'unreadMessages', title: '未读留言', dataIndex: 'unreadMessages', align: 'center' as const, render: (value: number) => value > 0 ? <Badge count={value} /> : '-' },
    { key: 'actions', title: '操作', fixed: 'right' as const, render: (_: unknown, row: ProjectSummary) => actionSlots([
      <Button key="enter" size="mini" type="text" onClick={() => navigate(`/projects/${row.id}`)}>进入</Button>,
      row.canManage && hasPerm('project:update') && row.status === 'IN_PROGRESS'
        && <Button key="edit" size="mini" type="text" onClick={() => openEdit(row)}>编辑</Button>,
      row.canDelete && (
        <Popconfirm key="delete" title={`确认删除项目“${row.name}”？其中全部文件、留言和上传记录将一并永久删除，无法恢复。`} onOk={() => remove(row)}>
          <Button size="mini" type="text" status="danger">删除</Button>
        </Popconfirm>),
    ], 'project-group') },
  ]), tableWidth, 'name')

  return (
    <Card className="page-card page-card--table">
      <h1 className="visually-hidden">Robot仿真协作</h1>
      <Tabs className="project-status-tabs" activeTab={status ?? 'ALL'}
        onChange={(key) => applyQuery({ status: key === 'ALL' ? undefined : key, page: 1 })}>
        <Tabs.TabPane key="ALL" title="总表" />
        {['IN_PROGRESS', 'COMPLETED', 'TERMINATED'].map((key) => <Tabs.TabPane key={key} title={PROJECT_STATUS[key]?.text ?? key} />)}
      </Tabs>
      <div className="page-toolbar responsive-toolbar">
        <Space wrap>
          <Input.Search allowClear placeholder="项目名称 / 工令号 / 机型 / 料号" style={{ width: 280 }} defaultValue={keyword} onSearch={(value) => applyQuery({ q: value.trim(), page: 1 })} onClear={() => applyQuery({ q: undefined, page: 1 })} />
          {isInternal && <Select allowClear showSearch placeholder="Robot 厂商" style={{ width: 180 }} value={supplierId} loading={supplierOptionsLoading} disabled={supplierOptionsError} onChange={(value) => applyQuery({ supplier: value as number | undefined, page: 1 })}>{suppliers.map((supplier) => <Select.Option key={supplier.id} value={supplier.id}>{supplier.name}</Select.Option>)}</Select>}
          <Select allowClear placeholder="资料状态" style={{ width: 130 }} value={materialState} onChange={(value) => applyQuery({ material: value as string | undefined, page: 1 })}>
            {(['NO_MATERIAL', 'PENDING', 'RETURNED'] as const).map((key) => <Select.Option key={key} value={key}>{MATERIAL_REVIEW[key].text}</Select.Option>)}
          </Select>
          {isInternal && <Checkbox checked={mine} onChange={(checked) => applyQuery({ mine: checked ? 1 : undefined, page: 1 })}>只看我负责的</Checkbox>}
          <Checkbox checked={overdue} onChange={(checked) => applyQuery({ overdue: checked ? 1 : undefined, page: 1 })}>仅看逾期</Checkbox>
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
          {isInternal && hasPerm('project:create') && <Button type="primary" icon={<IconPlus />} onClick={openCreate}>新建项目</Button>}
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

      <ProjectFormModal visible={modalOpen} project={editing} onCancel={() => setModalOpen(false)} onSaved={saved} onStale={load} />
    </Card>
  )
}
