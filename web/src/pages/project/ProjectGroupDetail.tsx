import { useCallback, useEffect, useRef, useState } from 'react'
import {
  Badge, Button, Card, Descriptions, Empty, Form, Input, Message, Modal, Popconfirm, Progress, Space, Spin, Table, Tag, Typography,
} from '@arco-design/web-react'
import { IconDown, IconPlus } from '@arco-design/web-react/icon'
import { useNavigate, useParams } from 'react-router-dom'
import { isAxiosError } from 'axios'
import http, { type QuietRequestConfig } from '../../api/client'
import { createProjectCopyJob, unwrapCopyJob } from '../../api/copyJobs'
import { actionSlots } from '../../components/ActionSlots'
import ProjectCopyJobsPanel from '../../components/project-copy-jobs/ProjectCopyJobsPanel'
import { useProjectCopyJobs } from '../../hooks/useProjectCopyJobs'
import { useAuth } from '../../store/auth'
import { useCollaboration } from '../../store/collaboration'
import {
  type ProjectSummary, type ProjectGroupDetail as ProjectGroupDetailData, PROJECT_STATUS, fmtTime,
} from '../../api/types'
import { textLengthRule } from '../../utils/textRules'
import './ProjectDetail.css'
import type { ApiResponses } from '../../api/types'

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
  const [copyOutcomeUnknown, setCopyOutcomeUnknown] = useState(false)
  const copyInFlight = useRef(false)
  const copyIdempotencyKey = useRef<string | null>(null)
  const pendingUnknownCopies = useRef(new Map<number, { idempotencyKey: string; name: string }>())
  const knownSucceededCopyJobs = useRef<Set<number>>(new Set())
  const copyJobsInitialized = useRef(false)
  const statusInFlight = useRef(new Set<number>())
  const [statusUpdating, setStatusUpdating] = useState<Set<number>>(() => new Set())
  const [form] = Form.useForm()
  const [copyForm] = Form.useForm()
  const { user, hasPerm } = useAuth()
  const isInternal = user?.userType === 'INTERNAL'
  const canWrite = isInternal && data && ['DRAFT', 'IN_PROGRESS'].includes(data.group.status)
  const canReadCopyJobs = Boolean(validId && data && isInternal && hasPerm('project:create'))
  const copyJobs = useProjectCopyJobs(groupId, canReadCopyJobs, reloadKey)

  const load = useCallback(() => {
    setLoading(true); setLoadError(false); setReloadKey((value) => value + 1)
  }, [])

  useEffect(() => {
    if (!validId) return
    let active = true
    const controller = new AbortController()
    http.get<ApiResponses['GET /project-groups/{id}']>(`/project-groups/${groupId}`, { signal: controller.signal, quietNetworkError: true } as QuietRequestConfig)
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

  useEffect(() => {
    if (!canReadCopyJobs || copyJobs.unavailable) {
      knownSucceededCopyJobs.current.clear()
      copyJobsInitialized.current = false
      copyIdempotencyKey.current = null
      pendingUnknownCopies.current.clear()
      setCopyOutcomeUnknown(false)
      setCopySource(null)
      return
    }
    if (!copyJobs.ready || copyJobs.loading) return
    if (!copyJobsInitialized.current) {
      copyJobs.jobs.forEach((job) => {
        if (job.status === 'succeeded') knownSucceededCopyJobs.current.add(job.jobId)
      })
      copyJobsInitialized.current = true
      return
    }
    let newSuccess = false
    copyJobs.jobs.forEach((job) => {
      if (job.status === 'succeeded' && !knownSucceededCopyJobs.current.has(job.jobId)) {
        knownSucceededCopyJobs.current.add(job.jobId)
        newSuccess = true
      }
    })
    if (newSuccess) load()
  }, [canReadCopyJobs, copyJobs.jobs, copyJobs.loading, copyJobs.ready, copyJobs.unavailable, load])

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
        await http.put<ApiResponses['PUT /projects/{id}']>(`/projects/${editing.id}`, values); Message.success('子项目已更新')
      } else {
        await http.post<ApiResponses['POST /project-groups/{id}/projects']>(`/project-groups/${groupId}/projects`, values); Message.success('子项目已创建并继承主项目资料')
      }
      setChildModalOpen(false); load()
    } catch {
      /* 请求错误由统一拦截器提示，保留弹窗内容供重试。 */
    } finally { saveInFlight.current = false; setSaving(false) }
  }
  const openCopy = (project: ProjectSummary) => {
    if (!copyJobs.ready || copyJobs.loading) return
    const pending = pendingUnknownCopies.current.get(project.id)
    setCopyOutcomeUnknown(Boolean(pending))
    copyIdempotencyKey.current = pending?.idempotencyKey || null
    setCopySource(project); copyForm.setFieldsValue({ name: suggestedCopyName(project.name) })
    if (pending) copyForm.setFieldsValue({ name: pending.name })
  }
  const submitCopy = async () => {
    if (!copySource || copyInFlight.current) return
    if (!copyOutcomeUnknown && (!copyJobs.ready || copyJobs.loading)) return
    copyInFlight.current = true; setCopying(true)
    let preserveIdempotencyKey = false
    try {
      const values = await copyForm.validate().catch(() => null) as ChildFormValues | null
      if (!values) return
      const name = values.name?.trim() || ''
      const idempotencyKey = copyIdempotencyKey.current || (() => {
        const generated = typeof globalThis.crypto?.randomUUID === 'function'
          ? globalThis.crypto.randomUUID()
          : `copy-${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`
        copyIdempotencyKey.current = generated.slice(0, 64)
        return copyIdempotencyKey.current
      })()
      const response = await createProjectCopyJob(copySource.id, { name, idempotencyKey })
      const job = unwrapCopyJob(response.data)
      copyJobs.upsert(job)
      Message.success('复制任务已创建，可在复制任务中查看进度')
      pendingUnknownCopies.current.delete(copySource.id)
      setCopyOutcomeUnknown(false)
      setCopySource(null); copyForm.resetFields(); copyIdempotencyKey.current = null
    } catch (error) {
      const status = isAxiosError(error) ? error.response?.status : undefined
      const knownBusinessRejection = typeof status === 'number' && status >= 400 && status < 500
      if (!knownBusinessRejection) {
        preserveIdempotencyKey = true
        pendingUnknownCopies.current.set(copySource.id, { idempotencyKey: copyIdempotencyKey.current || '', name: copyForm.getFieldValue('name') || '' })
        setCopyOutcomeUnknown(true)
        Message.warning('复制请求结果尚未确认，重试不会重复创建副本。')
        void copyJobs.refresh()
      } else {
        pendingUnknownCopies.current.delete(copySource.id)
        setCopyOutcomeUnknown(false)
      }
      /* 明确的业务错误由统一拦截器提示；网络中断不代表服务器复制失败。 */
    } finally {
      if (!preserveIdempotencyKey) copyIdempotencyKey.current = null
      copyInFlight.current = false; setCopying(false)
    }
  }
  const changeStatus = async (project: ProjectSummary, next: string) => {
    if (statusInFlight.current.has(project.id)) return
    statusInFlight.current.add(project.id); setStatusUpdating(new Set(statusInFlight.current))
    try { await http.put<ApiResponses['PUT /projects/{id}/status']>(`/projects/${project.id}/status`, { status: next }); Message.success('子项目状态已更新'); load() }
    catch { /* 请求错误由统一拦截器提示，解除当前行锁后可重试。 */ }
    finally { statusInFlight.current.delete(project.id); setStatusUpdating(new Set(statusInFlight.current)) }
  }
  const remove = async (project: ProjectSummary) => {
    try { await http.delete<ApiResponses['DELETE /projects/{id}']>(`/projects/${project.id}`); Message.success('子项目已删除'); load() }
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
        canWrite && hasPerm('project:create') && <Button key="copy" size="mini" type="text" disabled={!copyJobs.ready || copyJobs.loading} loading={copyJobs.loading} onClick={() => openCopy(project)}>复制</Button>,
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
  const copyModalVisible = Boolean(copySource) && canReadCopyJobs && !copyJobs.unavailable
  const closeCopyModal = () => {
    if (copying) return
    setCopySource(null)
    if (!copyOutcomeUnknown) {
      copyForm.resetFields()
      copyIdempotencyKey.current = null
    }
  }

  return (
    <div className="project-group-detail-page">
      {loadError && <div className="page-load-error"><Typography.Text type="warning">刷新失败，当前显示上次数据。</Typography.Text><Button size="small" onClick={load}>重试</Button></div>}
      <Card className="page-card project-group-summary-card">
        <div className="project-group-summary-heading">
          <div className="project-group-summary-title">
            <Typography.Text type="secondary">主项目</Typography.Text>
            <h1>{group.name}</h1>
            <div className="project-group-summary-facts">
              <span><b>Robot 厂商</b>{supplier}</span>
              <span><b>负责人</b>{responsible}</span>
              <span><b>需求完成时间</b>{expectedCompletionDate}</span>
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
            { label: 'Robot 厂商', value: supplier }, { label: 'Robot 料号', value: display(group.robotPartNumber) }, { label: 'Robot 型号', value: display(group.robotModelName) },
            { label: '负责人', value: responsible }, { label: '课别', value: display(group.sectionName) },
            { label: '优先级', value: display(group.priorityName) }, { label: '需求完成时间', value: expectedCompletionDate },
            ...(group.completedAt ? [{ label: '自动验收时间', value: fmtTime(group.completedAt) }] : []),
          ]} />
          <div className="project-summary-description">
            <span className="project-summary-description-label">访问范围</span>
            <Typography.Text>该 Robot 厂商的全部启用账号均可访问此主项目及其子项目。</Typography.Text>
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

      {canReadCopyJobs && !copyJobs.unavailable && (
        <ProjectCopyJobsPanel
          jobs={copyJobs.jobs}
          loading={copyJobs.loading}
          error={copyJobs.error}
          onRetry={copyJobs.refresh}
          onOpenResult={(projectId) => navigate(`/projects/${projectId}`)}
        />
      )}

      <Modal className="form-dialog" title={editing ? '编辑子项目' : '新增子项目'} visible={childModalOpen} onOk={submitChild} onCancel={closeChildModal} confirmLoading={saving} closable={!saving} maskClosable={!saving} escToExit={!saving} okText={editing ? '保存子项目' : '创建子项目'} unmountOnExit>
        <Form form={form} layout="vertical">
          <Form.Item label="子项目名称" field="name" rules={[{ required: true, message: '请输入子项目名称' }, textLengthRule('子项目名称', 128)]}><Input autoFocus placeholder="子项目名称" /></Form.Item>
          <Form.Item label="子项目说明" field="description"><Input.TextArea rows={3} maxLength={500} showWordLimit placeholder="选填" /></Form.Item>
          {!editing && <div className="dialog-note">Robot 厂商、工令号、机型、Robot 料号与型号、负责人、课别、优先级和需求完成时间将从主项目继承。</div>}
        </Form>
      </Modal>

      <Modal
        className="form-dialog"
        title="复制子项目"
        visible={copyModalVisible}
        onOk={submitCopy}
        onCancel={closeCopyModal}
        confirmLoading={copying}
        closable={!copying}
        maskClosable={!copying}
        escToExit={!copying}
        cancelText={copyOutcomeUnknown ? '暂时关闭' : '取消'}
        okText={copying ? '提交任务中…' : copyOutcomeUnknown ? '确认并重试' : '创建复制任务'}
        okButtonProps={{ disabled: !copyOutcomeUnknown && (!copyJobs.ready || copyJobs.loading) }}
        unmountOnExit
      >
        <Form form={copyForm} layout="vertical"><Form.Item label="新子项目名称" field="name" rules={[{ required: true, message: '请输入新子项目名称' }, textLengthRule('子项目名称', 128)]}><Input autoFocus disabled={copyOutcomeUnknown} placeholder="新子项目名称" /></Form.Item></Form>
        <div className="dialog-note">提交后台复制任务后可关闭弹窗；复制公共资料和当前项目文件，不复制留言、验收状态、已读回执和通知。</div>
        {copying && <div className="dialog-note" role="status">正在创建复制任务，请稍候。</div>}
        {copyOutcomeUnknown && <div className="dialog-note" role="alert">提交结果尚未确认，重试不会重复创建副本。任务会在后台继续执行；可暂时关闭，稍后从同一来源恢复。</div>}
      </Modal>
    </div>
  )
}
