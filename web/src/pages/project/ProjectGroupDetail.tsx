import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  Badge, Button, Card, Descriptions, Drawer, Empty, Form, Input, Message, Modal, Popconfirm, Progress, Select, Space, Spin, Tag, Typography,
} from '@arco-design/web-react'
import { IconDown, IconPlus, IconRefresh } from '@arco-design/web-react/icon'
import { useNavigate, useParams } from 'react-router-dom'
import { isAxiosError } from 'axios'
import http, { type QuietRequestConfig } from '../../api/client'
import { createProjectCopyJob, unwrapCopyJob } from '../../api/copyJobs'
import ProjectCopyJobsPanel from '../../components/project-copy-jobs/ProjectCopyJobsPanel'
import { isCopyJobActive, useProjectCopyJobs } from '../../hooks/useProjectCopyJobs'
import { projectAccessLostStatus, useProjectAccessLost } from '../../hooks/useProjectAccessLost'
import { useAuth } from '../../store/auth'
import { useCollaboration } from '../../store/collaboration'
import {
  type ProjectSummary, type ProjectGroupDetail as ProjectGroupDetailData, PROJECT_STATUS, fmtTime,
} from '../../api/types'
import { textLengthRule } from '../../utils/textRules'
import SubprojectDock, { type SubprojectDockHandle } from './SubprojectDock'
import type { SubprojectDockContextValue } from './subprojectDockContext'
import './ProjectDetail.css'
import type { ApiResponses } from '../../api/types'

interface ChildFormValues { name?: string; description?: string }
type OwnerOption = ApiResponses['GET /project-owner-options'][number]

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
  const [data, setData] = useState<ProjectGroupDetailData | null>(null)
  const projectIdsKey = useMemo(() => (data?.projects ?? []).map((project) => project.id).join(','), [data?.projects])
  // 只对本主项目相关的信号重新拉取：本组子项目的实时变更、重连、新出现的项目（可能是新增到本组的子项目），
  // 以及实时断开时的全局轮询指纹。其他项目的实时事件和协作快照的加载状态不触发重拉。
  const groupSignal = useCollaboration((state) => {
    const revisions = state.activityRevisions ?? {}
    // 只拼接已有实时变更的子项目，首次加载得到子项目列表时信号不变，避免重复拉取。
    const live = projectIdsKey
      ? projectIdsKey.split(',').filter((projectId) => revisions[Number(projectId)]).map((projectId) => `${projectId}:${revisions[Number(projectId)]}`).join(',')
      : ''
    const fallback = state.realtimeStatus === 'connected' ? 'live' : state.revision
    return `${live}|${Object.keys(revisions).length}|${state.reconnectRevision ?? 0}|${fallback}`
  })
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState(false)
  const [summaryExpanded, setSummaryExpanded] = useState(false)
  const [reloadKey, setReloadKey] = useState(0)
  const [groupRefreshKey, setGroupRefreshKey] = useState(0)
  const [copyJobsOpen, setCopyJobsOpen] = useState(false)
  const dockRef = useRef<SubprojectDockHandle>(null)
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
  const [transferOpen, setTransferOpen] = useState(false)
  const [ownerOptions, setOwnerOptions] = useState<OwnerOption[]>([])
  const [ownerOptionsLoading, setOwnerOptionsLoading] = useState(false)
  const [transferTarget, setTransferTarget] = useState<number | undefined>()
  const [transferring, setTransferring] = useState(false)
  const transferInFlight = useRef(false)
  const ownerOptionsSeq = useRef(0)
  // 曾成功展示过主项目后，刷新得到 403/404 说明负责人已被他人变更或主项目已删除：提示后回到列表。
  const everLoaded = useRef(false)
  const handleAccessLost = useProjectAccessLost('主项目')
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
  // 面板内的流程、文件和留言操作只需刷新主项目进度与子项目状态，不必重置复制任务。
  const refreshGroup = useCallback(() => setGroupRefreshKey((value) => value + 1), [])

  useEffect(() => {
    if (!validId) return
    let active = true
    const controller = new AbortController()
    // 已加载后的 403/404 由本页统一提示并跳转，不再叠加拦截器的逐条错误提示。
    const quietClientError = everLoaded.current
    http.get<ApiResponses['GET /project-groups/{id}']>(`/project-groups/${groupId}`, { signal: controller.signal, quietNetworkError: true, quietClientError } as QuietRequestConfig)
      .then((response) => { if (active) { everLoaded.current = true; setData(response.data as ProjectGroupDetailData); setLoadError(false) } })
      .catch((error: unknown) => {
        if (!active || controller.signal.aborted) return
        const lost = projectAccessLostStatus(error)
        if (lost && everLoaded.current && handleAccessLost(lost)) return
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
  }, [groupId, groupRefreshKey, groupSignal, handleAccessLost, reloadKey, validId])

  useEffect(() => {
    if (!canReadCopyJobs || copyJobs.unavailable) {
      knownSucceededCopyJobs.current.clear()
      copyJobsInitialized.current = false
      copyIdempotencyKey.current = null
      pendingUnknownCopies.current.clear()
      // Copy-job access comes from the server; losing it must close the copy dialog this component owns.
      // eslint-disable-next-line react/set-state-in-effect
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
    // 新副本只影响子项目列表；刷新主项目即可，不重置正在显示的复制任务列表。
    if (newSuccess) refreshGroup()
  }, [canReadCopyJobs, copyJobs.jobs, copyJobs.loading, copyJobs.ready, copyJobs.unavailable, refreshGroup])

  const projects = data?.projects
  const projectMap = useMemo(() => new Map((projects ?? []).map((project) => [project.id, project])), [projects])
  const canCreateChild = Boolean(canWrite && hasPerm('project:create'))
  const canUpdateChild = Boolean(canWrite && hasPerm('project:update'))
  const canDeleteChild = Boolean(isInternal && hasPerm('project:delete'))
  const copyReady = copyJobs.ready && !copyJobs.loading
  const copyLoading = copyJobs.loading
  // 面板中的管理按钮复用本页的弹窗、复制幂等键和防重状态；这些处理函数不依赖主项目数据，可在加载前定义。
  const openEdit = useCallback((project: ProjectSummary) => {
    setEditing(project); form.setFieldsValue({ name: project.name, description: project.description }); setChildModalOpen(true)
  }, [form])
  const openCopy = useCallback((project: ProjectSummary) => {
    if (!copyReady) return
    const pending = pendingUnknownCopies.current.get(project.id)
    setCopyOutcomeUnknown(Boolean(pending))
    copyIdempotencyKey.current = pending?.idempotencyKey || null
    setCopySource(project); copyForm.setFieldsValue({ name: suggestedCopyName(project.name) })
    if (pending) copyForm.setFieldsValue({ name: pending.name })
  }, [copyForm, copyReady])
  const remove = useCallback(async (project: ProjectSummary) => {
    // 删除只需刷新主项目；load() 会递增复制任务的 generation，导致任务列表清空闪烁。
    try { await http.delete<ApiResponses['DELETE /projects/{id}']>(`/projects/${project.id}`); Message.success('子项目已删除'); refreshGroup() }
    catch { /* 请求错误由统一拦截器提示。 */ }
  }, [refreshGroup])
  const dockContext = useMemo<SubprojectDockContextValue>(() => ({
    projects: projectMap,
    onGroupChanged: refreshGroup,
    renderManageActions: (project) => (
      <>
        {canCreateChild && <Button size="mini" type="text" disabled={!copyReady} loading={copyLoading} onClick={() => openCopy(project)}>复制</Button>}
        {canUpdateChild && ['DRAFT', 'IN_PROGRESS'].includes(project.status) && <Button size="mini" type="text" onClick={() => openEdit(project)}>编辑</Button>}
        {canDeleteChild && ['DRAFT', 'TERMINATED'].includes(project.status) && (
          <Popconfirm title={`确认删除子项目“${project.name}”？仅草稿或已终止且无文件、留言、上传和复制履历时可删除。`} onOk={() => remove(project)}>
            <Button size="mini" type="text" status="danger">删除</Button>
          </Popconfirm>
        )}
      </>
    ),
  }), [canCreateChild, canDeleteChild, canUpdateChild, copyLoading, copyReady, openCopy, openEdit, projectMap, refreshGroup, remove])

  if (!validId) return <div className="project-group-load-state"><Empty description="主项目地址无效" /><Button type="primary" onClick={() => navigate('/projects')}>返回项目列表</Button></div>
  if (loading && !data) return <div className="project-group-load-state"><Spin size={36} /></div>
  if (!data) return <div className="project-group-load-state"><Empty description="主项目加载失败或没有访问权限" /><Button type="primary" onClick={load}>重试</Button></div>

  const group = data.group
  const openCreate = () => { setEditing(null); form.resetFields(); setChildModalOpen(true) }
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
      setChildModalOpen(false); refreshGroup()
    } catch {
      /* 请求错误由统一拦截器提示，保留弹窗内容供重试。 */
    } finally { saveInFlight.current = false; setSaving(false) }
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
      setCopyJobsOpen(true)
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
  const canTransfer = isInternal && hasPerm('project:transfer')
  const openTransfer = () => {
    // 快速关闭再打开时，只接受最后一次打开的候选人响应。
    const seq = ++ownerOptionsSeq.current
    setTransferTarget(undefined); setTransferOpen(true); setOwnerOptionsLoading(true)
    http.get<ApiResponses['GET /project-owner-options']>('/project-owner-options')
      .then((response) => { if (seq === ownerOptionsSeq.current) setOwnerOptions(response.data) })
      .catch(() => { if (seq === ownerOptionsSeq.current) setOwnerOptions([]) /* 请求错误由统一拦截器提示。 */ })
      .finally(() => { if (seq === ownerOptionsSeq.current) setOwnerOptionsLoading(false) })
  }
  const closeTransfer = () => { if (!transferring) { ownerOptionsSeq.current += 1; setOwnerOptionsLoading(false); setTransferOpen(false) } }
  const submitTransfer = async () => {
    if (!transferTarget || transferInFlight.current) return
    transferInFlight.current = true; setTransferring(true)
    try {
      await http.put<ApiResponses['PUT /project-groups/{id}/responsible']>(`/project-groups/${groupId}/responsible`, { responsibleUserId: transferTarget })
      setTransferOpen(false)
      // 转交后仍可访问：主项目创建人或具备查看全部项目权限；否则回到列表，避免停留在无权页面。
      if (hasPerm('project:view_all') || data.group.createdBy === user?.id) { Message.success('负责人已变更'); load() }
      else { Message.success('负责人已变更，您已不再负责该主项目'); navigate('/projects') }
    } catch {
      /* 请求错误由统一拦截器提示，保留弹窗供重试。 */
    } finally { transferInFlight.current = false; setTransferring(false) }
  }

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

  const activeCopyJobs = copyJobs.jobs.filter(isCopyJobActive).length
  const showCopyJobs = canReadCopyJobs && !copyJobs.unavailable

  return (
    <div className="project-group-detail-page">
      {loadError && <div className="page-load-error"><Typography.Text type="warning">刷新失败，当前显示上次数据。</Typography.Text><Button size="small" onClick={load}>重试</Button></div>}
      <Card className="page-card project-group-summary-card">
        <div className="project-group-summary-heading">
          <div className="project-group-summary-title">
            <div className="project-group-summary-name">
              <Typography.Text type="secondary">主项目</Typography.Text>
              <h1>{group.name}</h1>
              <Tag color={PROJECT_STATUS[group.status]?.color}>{PROJECT_STATUS[group.status]?.text}</Tag>
            </div>
            <div className="project-group-summary-facts">
              <span><b>Robot 厂商</b>{supplier}</span>
              <span><b>负责人</b>{responsible}</span>
              <span><b>需求完成时间</b>{expectedCompletionDate}</span>
              <span className="project-group-inline-progress"><b>验收进度</b><Progress percent={progress} size="small" showText={false} /><em>{group.completedCount}/{group.subprojectCount}</em></span>
              <span className="project-group-inline-metrics">待验收 {group.pendingCount} · 已终止 {group.terminatedCount}</span>
            </div>
          </div>
          <Space className="project-group-summary-actions" wrap size={8}>
            {canCreateChild && <Button type="primary" size="small" icon={<IconPlus />} onClick={openCreate}>新增子项目</Button>}
            {showCopyJobs && (
              <Badge count={activeCopyJobs} dot={false}>
                <Button size="small" onClick={() => setCopyJobsOpen(true)}>复制任务</Button>
              </Badge>
            )}
            {canTransfer && <Button size="small" disabled={group.pendingCount > 0} title={group.pendingCount > 0 ? '存在待验收子项目，暂不能变更负责人' : undefined} onClick={openTransfer}>变更负责人</Button>}
            {data.projects.length > 1 && <Button size="small" icon={<IconRefresh />} title="恢复默认的子项目面板布局" onClick={() => dockRef.current?.resetLayout()}>重置布局</Button>}
            <Button
              type="text"
              size="small"
              aria-expanded={summaryExpanded}
              aria-controls="project-group-extra-info"
              onClick={() => setSummaryExpanded((value) => !value)}
            >
              {summaryExpanded ? '收起资料' : '查看资料'}<IconDown className={summaryExpanded ? 'is-expanded' : undefined} />
            </Button>
            <Button size="small" onClick={() => navigate('/projects')}>返回项目列表</Button>
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
            <Typography.Text>该 Robot 厂商的全部启用账号均可访问此主项目及其子项目；公司内部由负责人、主项目创建人及具备查看全部项目权限的账号访问。</Typography.Text>
          </div>
          {group.description && <div className="project-summary-description"><span className="project-summary-description-label">主项目说明</span><Typography.Text>{group.description}</Typography.Text></div>}
          <div className="project-summary-description"><span className="project-summary-description-label">面板布局</span><Typography.Text type="secondary">拖动子项目标签可停靠到任意位置或合并为标签组，拖动分隔条调整大小，双击标签最大化；布局按账号保存在本浏览器。</Typography.Text></div>
        </div>}
      </Card>

      {data.projects.length ? (
        <SubprojectDock ref={dockRef} groupId={groupId} userId={user?.id} projects={data.projects} context={dockContext} />
      ) : (
        <div className="project-group-dock-empty">
          <Empty description={canCreateChild ? '暂无子项目，新增后会在此并列显示子项目工作区' : '暂无子项目'} />
        </div>
      )}

      {showCopyJobs && (
        <Drawer
          className="project-copy-jobs-drawer"
          width="min(560px, 100vw)"
          title="复制任务"
          visible={copyJobsOpen}
          onCancel={() => setCopyJobsOpen(false)}
          footer={null}
        >
          <ProjectCopyJobsPanel
            jobs={copyJobs.jobs}
            loading={copyJobs.loading}
            error={copyJobs.error}
            onRetry={copyJobs.refresh}
            onOpenResult={(projectId) => {
              // 副本属于当前主项目时直接切到它的面板，否则进入独立页面。
              if (dockRef.current?.focusProject(projectId)) setCopyJobsOpen(false)
              else navigate(`/projects/${projectId}`)
            }}
          />
        </Drawer>
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
        title="变更负责人"
        visible={transferOpen}
        onOk={submitTransfer}
        onCancel={closeTransfer}
        confirmLoading={transferring}
        closable={!transferring}
        maskClosable={!transferring}
        escToExit={!transferring}
        okText="确认变更"
        okButtonProps={{ disabled: !transferTarget }}
        unmountOnExit
      >
        <Form layout="vertical">
          <Form.Item label="当前负责人"><Typography.Text>{responsible}</Typography.Text></Form.Item>
          <Form.Item label="新负责人" required>
            <Select
              showSearch
              allowClear
              loading={ownerOptionsLoading}
              placeholder="搜索姓名或工号"
              value={transferTarget}
              onChange={(value?: number) => setTransferTarget(value)}
              filterOption={(input, option) => String(option?.props?.children ?? '').toLowerCase().includes(input.trim().toLowerCase())}
            >
              {ownerOptions.filter((owner) => owner.id !== group.responsibleUserId).map((owner) => (
                <Select.Option key={owner.id} value={owner.id}>
                  {`${owner.realName}（${owner.employeeNo}）${owner.sectionName ? ` · ${owner.sectionName}` : ''}`}
                </Select.Option>
              ))}
            </Select>
          </Form.Item>
        </Form>
        <div className="dialog-note">负责人和课别会同步到全部子项目。原负责人如不是主项目创建人且没有查看全部项目权限，将不再能访问该主项目。存在待验收子项目时不能变更。</div>
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
