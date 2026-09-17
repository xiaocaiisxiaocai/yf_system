import { useCallback, useEffect, useRef, useState } from 'react'
import {
  Badge, Button, Card, Descriptions, Empty, Form, Input, Message, Modal, Popconfirm, Progress, Space, Spin, Table, Tag, Typography,
} from '@arco-design/web-react'
import { IconDown, IconPlus } from '@arco-design/web-react/icon'
import { useNavigate, useParams } from 'react-router-dom'
import { isAxiosError } from 'axios'
import http, { type QuietRequestConfig } from '../../api/client'
import { actionSlots } from '../../components/ActionSlots'
import { useAuth } from '../../store/auth'
import { useCollaboration } from '../../store/collaboration'
import {
  type ProjectSummary, type ProjectCopyResult, type ProjectGroupDetail as ProjectGroupDetailData, PROJECT_STATUS, fmtTime,
} from '../../api/types'
import { textLengthRule } from '../../utils/textRules'
import './ProjectDetail.css'

interface ChildFormValues { name?: string; description?: string }

function display(value?: string | null) { return value?.trim() || '-' }
function suggestedCopyName(name: string) {
  const suffix = ' - 副本'
  return `${[...name.trim()].slice(0, 128 - [...suffix].length).join('').trimEnd()}${suffix}`
}

export default function ProjectGroupDetail() {
  const { id } = useParams()
  // 同一路由切换主项目时重建全部局部状态，旧请求、弹窗和操作目标不能泄漏到新主项目。
  return <ProjectGroupDetailContent key={id} id={id} />
}

function ProjectGroupDetailContent({ id }: { id?: string }) {
  const groupId = Number(id)
  const validId = Number.isSafeInteger(groupId) && groupId > 0
  const navigate = useNavigate()
  const revision = useCollaboration((state) => state.revision)
  const syncStatus = useCollaboration((state) => state.status)
  const [data, setData] = useState<ProjectGroupDetailData | null>(null)
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState(false)
  const [summaryExpanded, setSummaryExpanded] = useState(false)
  const [reloadKey, setReloadKey] = useState(0)
  const [editing, setEditing] = useState<ProjectSummary | null>(null)
  const [childModalOpen, setChildModalOpen] = useState(false)
  const [saving, setSaving] = useState(false)
  const saveInFlight = useRef(false)
  const [copySource, setCopySource] = useState<ProjectSummary | null>(null)
  const [copying, setCopying] = useState(false)
  const copyInFlight = useRef(false)
  const statusInFlight = useRef(new Set<number>())
  const [statusUpdating, setStatusUpdating] = useState<Set<number>>(() => new Set())
  const [form] = Form.useForm()
  const [copyForm] = Form.useForm()
  const { user, hasPerm } = useAuth()
  const isInternal = user?.userType === 'INTERNAL'
  const canWrite = isInternal && data && ['DRAFT', 'IN_PROGRESS'].includes(data.group.status)

  const load = useCallback(() => {
    setLoading(true); setLoadError(false); setReloadKey((value) => value + 1)
  }, [])

  useEffect(() => {
    if (!validId) return
    let active = true
    const controller = new AbortController()
    http.get(`/project-groups/${groupId}`, { signal: controller.signal, quietNetworkError: true } as QuietRequestConfig)
      .then((response) => { if (active) { setData(response.data as ProjectGroupDetailData); setLoadError(false) } })
      .catch((error: unknown) => {
        if (!active) return
        setLoadError(true)
        if (isAxiosError(error) && (error.response?.status === 403 || error.response?.status === 404)) {
          setData(null)
          setEditing(null)
          setChildModalOpen(false)
          setCopySource(null)
        }
      })
      .finally(() => { if (active) setLoading(false) })
    return () => { active = false; controller.abort() }
  }, [groupId, reloadKey, revision, syncStatus, validId])

  if (!validId) return <div className="project-group-load-state"><Empty description="主项目地址无效" /><Button type="primary" onClick={() => navigate('/projects')}>返回项目列表</Button></div>
  if (loading && !data) return <div className="project-group-load-state"><Spin size={36} /></div>
  if (!data) return <div className="project-group-load-state"><Empty description="主项目加载失败或没有访问权限" /><Button type="primary" onClick={load}>重试</Button></div>

  const group = data.group
  const openCreate = () => { setEditing(null); form.resetFields(); setChildModalOpen(true) }
  const openEdit = (project: ProjectSummary) => {
    setEditing(project); form.setFieldsValue({ name: project.name, description: project.description }); setChildModalOpen(true)
  }
  const closeChildModal = () => { if (!saving) setChildModalOpen(false) }
  const submitChild = async () => {
    if (saveInFlight.current) return
    saveInFlight.current = true; setSaving(true)
    try {
      const values = await form.validate().catch(() => null) as ChildFormValues | null
      if (!values) return
      if (editing) {
        await http.put(`/projects/${editing.id}`, values); Message.success('子项目已更新')
      } else {
        await http.post(`/project-groups/${groupId}/projects`, values); Message.success('子项目已创建并继承主项目资料')
      }
      setChildModalOpen(false); load()
    } catch {
      /* 请求错误由统一拦截器提示，保留弹窗内容供重试。 */
    } finally { saveInFlight.current = false; setSaving(false) }
  }
  const openCopy = (project: ProjectSummary) => {
    setCopySource(project); copyForm.setFieldsValue({ name: suggestedCopyName(project.name) })
  }
  const submitCopy = async () => {
    if (!copySource || copyInFlight.current) return
    copyInFlight.current = true; setCopying(true)
    try {
      const values = await copyForm.validate().catch(() => null) as ChildFormValues | null
      if (!values) return
      const response = await http.post(`/projects/${copySource.id}/copy`, { name: values.name?.trim() })
      const result = response.data as ProjectCopyResult
      Message.success(`子项目已复制，包含 ${result.copy.fileCount} 个文件`)
      setCopySource(null); copyForm.resetFields(); load()
    } catch {
      /* 请求错误由统一拦截器提示，保留名称和源项目供重试。 */
    } finally { copyInFlight.current = false; setCopying(false) }
  }
  const changeStatus = async (project: ProjectSummary, next: string) => {
    if (statusInFlight.current.has(project.id)) return
    statusInFlight.current.add(project.id); setStatusUpdating(new Set(statusInFlight.current))
    try { await http.put(`/projects/${project.id}/status`, { status: next }); Message.success('子项目状态已更新'); load() }
    catch { /* 请求错误由统一拦截器提示，解除当前行锁后可重试。 */ }
    finally { statusInFlight.current.delete(project.id); setStatusUpdating(new Set(statusInFlight.current)) }
  }
  const remove = async (project: ProjectSummary) => {
    try { await http.delete(`/projects/${project.id}`); Message.success('子项目已删除'); load() }
    catch { /* 请求错误由统一拦截器提示。 */ }
  }
  const statusActions = (project: ProjectSummary) => {
    if (project.status === 'DRAFT') return [{ key: 'IN_PROGRESS', text: '开始' }]
    if (project.status === 'IN_PROGRESS') return [{ key: 'TERMINATED', text: '终止' }]
    if (project.status === 'TERMINATED') return [{ key: 'IN_PROGRESS', text: '重新开始' }]
    return []
  }
  const columns = [
    { title: '子项目', dataIndex: 'name', width: 180, ellipsis: true, render: (value: string, project: ProjectSummary) => <Button type="text" size="small" onClick={() => navigate(`/projects/${project.id}`)}>{value}</Button> },
    { title: '状态', dataIndex: 'status', width: 110, align: 'center' as const, render: (value: string) => <Tag color={PROJECT_STATUS[value]?.color}>{PROJECT_STATUS[value]?.text || value}</Tag> },
    { title: '未读留言', dataIndex: 'unreadMessages', width: 100, align: 'center' as const, render: (value?: number) => value ? <Badge count={value} /> : '-' },
    { title: '项目说明', dataIndex: 'description', ellipsis: true, render: display },
    { title: '操作', width: 290, align: 'center' as const, fixed: 'right' as const, render: (_: unknown, project: ProjectSummary) => {
      const nextStatuses = statusActions(project)
      return actionSlots([
        <Button key="enter" size="mini" type="text" onClick={() => navigate(`/projects/${project.id}`)}>进入协作</Button>,
        canWrite && hasPerm('project:create') && <Button key="copy" size="mini" type="text" onClick={() => openCopy(project)}>复制</Button>,
        canWrite && hasPerm('project:update') && ['DRAFT', 'IN_PROGRESS'].includes(project.status) && <Button key="edit" size="mini" type="text" onClick={() => openEdit(project)}>编辑</Button>,
        isInternal && hasPerm('project:status') && nextStatuses[0] && <Button key={nextStatuses[0].key} size="mini" type="text" status={nextStatuses[0].key === 'TERMINATED' ? 'danger' : undefined} loading={statusUpdating.has(project.id)} disabled={statusUpdating.has(project.id)} onClick={() => changeStatus(project, nextStatuses[0].key)}>{nextStatuses[0].text}</Button>,
        isInternal && hasPerm('project:delete') && ['DRAFT', 'TERMINATED'].includes(project.status) && <Popconfirm key="delete" title={`确认删除子项目“${project.name}”？仅草稿或已终止且无文件、留言、上传和复制履历时可删除。`} onOk={() => remove(project)}><Button size="mini" type="text" status="danger">删除</Button></Popconfirm>,
      ], 'subproject')
    } },
  ]
  const progress = group.subprojectCount ? Math.round(group.completedCount / group.subprojectCount * 100) : 0
  const responsible = group.responsibleUserName
    ? `${group.responsibleUserName}${group.responsibleUserEmployeeNo ? `（${group.responsibleUserEmployeeNo}）` : ''}`
    : '-'
  const supplier = display(group.supplierName)
  const expectedCompletionDate = display(group.expectedCompletionDate)

  return (
    <div className="project-group-detail-page">
      {loadError && <div className="page-load-error"><Typography.Text type="warning">刷新失败，当前显示上次数据。</Typography.Text><Button size="small" onClick={load}>重试</Button></div>}
      <Card className="page-card project-group-summary-card">
        <div className="project-group-summary-heading">
          <div className="project-group-summary-title">
            <Typography.Text type="secondary">主项目</Typography.Text>
            <h1>{group.name}</h1>
            <div className="project-group-summary-facts">
              <span><b>供应商</b>{supplier}</span>
              <span><b>负责人</b>{responsible}</span>
              <span><b>预计完成</b>{expectedCompletionDate}</span>
            </div>
          </div>
          <Space className="project-group-summary-actions">
            <Tag color={PROJECT_STATUS[group.status]?.color}>{PROJECT_STATUS[group.status]?.text}</Tag>
            <Button
              type="text"
              size="small"
              aria-expanded={summaryExpanded}
              aria-controls="project-group-extra-info"
              onClick={() => setSummaryExpanded((value) => !value)}
            >
              {summaryExpanded ? '收起资料' : '查看资料'}<IconDown className={summaryExpanded ? 'is-expanded' : undefined} />
            </Button>
            <Button onClick={() => navigate('/projects')}>返回项目列表</Button>
          </Space>
        </div>
        {summaryExpanded && <div id="project-group-extra-info" className="project-group-extra-info">
          <Descriptions className="project-metadata" column={{ xs: 1, sm: 2, md: 3, lg: 4 }} data={[
            { label: '工令号', value: display(group.workOrderNos?.join('、')) }, { label: '机型', value: display(group.machineModel) },
            { label: 'Robot 厂商', value: display(group.robotVendorName) }, { label: 'Robot 型号', value: display(group.robotModelName) },
            { label: '负责人', value: responsible }, { label: '课别', value: display(group.sectionName) },
            { label: '优先级', value: display(group.priorityName) }, { label: '预计完成日期', value: expectedCompletionDate },
            { label: '供应商', value: supplier },
            ...(group.completedAt ? [{ label: '自动验收时间', value: fmtTime(group.completedAt) }] : []),
          ]} />
          <div className="project-summary-description">
            <span className="project-summary-description-label">访问范围</span>
            <Typography.Text>该供应商的全部启用账号均可访问此主项目及其子项目。</Typography.Text>
          </div>
          {group.description && <div className="project-summary-description"><span className="project-summary-description-label">主项目说明</span><Typography.Text>{group.description}</Typography.Text></div>}
        </div>}
        <div className="project-group-overview">
          <div className="project-group-progress-main"><span>总体验收进度</span><Progress percent={progress} showText /></div>
          <div className="project-group-metrics"><span>子项目 {group.subprojectCount}</span><span>已验收 {group.completedCount}</span><span>待验收 {group.pendingCount}</span><span>已终止 {group.terminatedCount}</span></div>
        </div>
      </Card>

      <Card className="page-card project-group-children-card">
        <div className="project-group-section-heading"><div><h2>子项目</h2><Typography.Text type="secondary">文件、留言、动态与验收相互独立</Typography.Text></div>{canWrite && hasPerm('project:create') && <Button type="primary" icon={<IconPlus />} onClick={openCreate}>新增子项目</Button>}</div>
        <Table className="page-table" rowKey="id" columns={columns} data={data.projects} pagination={false} scroll={{ x: 900, y: 'var(--page-table-scroll-y)' }} noDataElement={<Empty description="暂无子项目" />} />
      </Card>

      <Modal className="form-dialog" title={editing ? '编辑子项目' : '新增子项目'} visible={childModalOpen} onOk={submitChild} onCancel={closeChildModal} confirmLoading={saving} closable={!saving} maskClosable={!saving} escToExit={!saving} okText={editing ? '保存子项目' : '创建子项目'} unmountOnExit>
        <Form form={form} layout="vertical">
          <Form.Item label="子项目名称" field="name" rules={[{ required: true, message: '请输入子项目名称' }, textLengthRule('子项目名称', 128)]}><Input autoFocus placeholder="子项目名称" /></Form.Item>
          <Form.Item label="子项目说明" field="description"><Input.TextArea rows={3} maxLength={500} showWordLimit placeholder="选填" /></Form.Item>
          {!editing && <div className="dialog-note">供应商、工令号、机型、Robot 信息、负责人、课别、优先级和预计完成日期将从主项目继承。</div>}
        </Form>
      </Modal>

      <Modal className="form-dialog" title="复制子项目" visible={!!copySource} onOk={submitCopy} onCancel={() => { if (!copying) setCopySource(null) }} confirmLoading={copying} closable={!copying} maskClosable={!copying} escToExit={!copying} okText="复制子项目" unmountOnExit>
        <Form form={copyForm} layout="vertical"><Form.Item label="新子项目名称" field="name" rules={[{ required: true, message: '请输入新子项目名称' }, textLengthRule('子项目名称', 128)]}><Input autoFocus placeholder="新子项目名称" /></Form.Item></Form>
        <div className="dialog-note">生成独立复制件，复制公共资料和当前项目文件；不复制留言、验收状态、已读回执和通知。</div>
      </Modal>
    </div>
  )
}
