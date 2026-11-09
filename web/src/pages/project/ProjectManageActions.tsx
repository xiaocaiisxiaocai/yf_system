import { useEffect, useRef, useState } from 'react'
import { Button, Drawer, Form, Input, Message, Modal, Popconfirm, Select } from '@arco-design/web-react'
import { useNavigate } from 'react-router-dom'
import { isAxiosError } from 'axios'
import http from '../../api/client'
import { isVersionConflict } from '../../api/versionConflict'
import { createProjectCopyJob, unwrapCopyJob } from '../../api/copyJobs'
import ProjectCopyJobsPanel from '../../components/project-copy-jobs/ProjectCopyJobsPanel'
import EnterSubmit from '../../components/EnterSubmit'
import { useProjectCopyJobs } from '../../hooks/useProjectCopyJobs'
import { useAuth } from '../../store/auth'
import type { ApiResponses, Project, ProjectSummary } from '../../api/types'
import { textLengthRule } from '../../utils/textRules'
import ProjectFormModal from './ProjectFormModal'

type OwnerOption = ApiResponses['GET /project-owner-options'][number]

function suggestedCopyName(name: string) {
  const suffix = ' - 副本'
  return `${[...name.trim()].slice(0, 128 - [...suffix].length).join('').trimEnd()}${suffix}`
}

interface Props {
  project: Project
  /** 资料、负责人或状态变化后重新获取项目。 */
  onChanged: () => void
}

/**
 * 项目详情页头部的管理操作：编辑（负责人）、变更负责人（负责人、所属组织主管或管理员）、
 * 复制为自己的新项目、删除已终止项目。按钮是否出现以后端返回的 canManage/canTransfer/canDelete 为准。
 */
export default function ProjectManageActions({ project, onChanged }: Props) {
  const navigate = useNavigate()
  const { user, hasPerm } = useAuth()
  const isInternal = user?.userType === 'INTERNAL'
  const canEdit = project.canManage && hasPerm('project:update') && project.status === 'IN_PROGRESS'
  const canTransfer = project.canTransfer && hasPerm('project:transfer')
  const canCopy = isInternal && hasPerm('project:create')

  const [editOpen, setEditOpen] = useState(false)

  const [transferOpen, setTransferOpen] = useState(false)
  const [ownerOptions, setOwnerOptions] = useState<OwnerOption[]>([])
  const [ownerOptionsLoading, setOwnerOptionsLoading] = useState(false)
  const [transferTarget, setTransferTarget] = useState<number | undefined>()
  const [transferring, setTransferring] = useState(false)
  const transferInFlight = useRef(false)
  const ownerOptionsSeq = useRef(0)

  const [copyOpen, setCopyOpen] = useState(false)
  const [copyJobsOpen, setCopyJobsOpen] = useState(false)
  const [copying, setCopying] = useState(false)
  const [copyOutcomeUnknown, setCopyOutcomeUnknown] = useState(false)
  const copyInFlight = useRef(false)
  const copyIdempotencyKey = useRef<string | null>(null)
  const knownFinishedJobs = useRef<Set<number> | null>(null)
  const [copyForm] = Form.useForm()
  const copyJobs = useProjectCopyJobs(canCopy, 0)

  // 只提示本页打开后才结束的复制任务；成功的副本可从任务面板进入。
  useEffect(() => {
    if (!copyJobs.ready) return
    if (!knownFinishedJobs.current) {
      knownFinishedJobs.current = new Set(copyJobs.jobs.filter((job) => job.status === 'succeeded' || job.status === 'failed').map((job) => job.jobId))
      return
    }
    copyJobs.jobs.forEach((job) => {
      if ((job.status !== 'succeeded' && job.status !== 'failed') || knownFinishedJobs.current!.has(job.jobId)) return
      knownFinishedJobs.current!.add(job.jobId)
      if (job.status === 'succeeded') Message.success(`项目“${job.targetName}”复制完成`)
      else Message.error(`项目“${job.targetName}”复制失败：${job.error || '请稍后重试'}`)
    })
  }, [copyJobs.jobs, copyJobs.ready])

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
      // 以页面当前展示的负责人为期望值：别处已转交时服务端返回 40903，不覆盖他人的转交结果。
      await http.put<ApiResponses['PUT /projects/{id}/responsible']>(`/projects/${project.id}/responsible`, {
        responsibleUserId: transferTarget,
        expectedResponsibleUserId: project.responsibleUserId,
      })
      setTransferOpen(false)
      Message.success('负责人已变更')
      onChanged()
    } catch (error: unknown) {
      /* 请求错误由统一拦截器提示，保留弹窗供重试；版本冲突时刷新负责人基线，不自动重放。 */
      if (isVersionConflict(error)) onChanged()
    } finally { transferInFlight.current = false; setTransferring(false) }
  }

  const openCopy = () => {
    if (!copyOutcomeUnknown) {
      copyIdempotencyKey.current = null
      copyForm.setFieldsValue({ name: suggestedCopyName(project.name) })
    }
    setCopyOpen(true)
  }
  const closeCopy = () => {
    if (copying) return
    setCopyOpen(false)
    if (!copyOutcomeUnknown) { copyForm.resetFields(); copyIdempotencyKey.current = null }
  }
  const submitCopy = async () => {
    if (copyInFlight.current) return
    copyInFlight.current = true; setCopying(true)
    let preserveIdempotencyKey = false
    try {
      const values = await copyForm.validate().catch(() => null) as { name?: string } | null
      if (!values) return
      const name = values.name?.trim() || ''
      const idempotencyKey = copyIdempotencyKey.current || (() => {
        const generated = typeof globalThis.crypto?.randomUUID === 'function'
          ? globalThis.crypto.randomUUID()
          : `copy-${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`
        copyIdempotencyKey.current = generated.slice(0, 64)
        return copyIdempotencyKey.current
      })()
      const response = await createProjectCopyJob(project.id, { name, idempotencyKey })
      copyJobs.upsert(unwrapCopyJob(response.data))
      Message.success('已开始复制，完成后可从复制任务进入新项目')
      setCopyOutcomeUnknown(false); setCopyOpen(false); setCopyJobsOpen(true)
      copyForm.resetFields(); copyIdempotencyKey.current = null
    } catch (error) {
      const status = isAxiosError(error) ? error.response?.status : undefined
      const knownBusinessRejection = typeof status === 'number' && status >= 400 && status < 500
      if (!knownBusinessRejection) {
        // 网络中断不代表服务器没有受理：保留幂等键，重试不会重复创建副本。
        preserveIdempotencyKey = true
        setCopyOutcomeUnknown(true)
        Message.warning('复制请求结果尚未确认，重试不会重复创建副本。')
        void copyJobs.refresh()
      } else setCopyOutcomeUnknown(false)
    } finally {
      if (!preserveIdempotencyKey) copyIdempotencyKey.current = null
      copyInFlight.current = false; setCopying(false)
    }
  }

  const remove = async () => {
    try {
      await http.delete<ApiResponses['DELETE /projects/{id}']>(`/projects/${project.id}`)
      Message.success('项目已删除')
      navigate('/projects')
    } catch { /* 请求错误由统一拦截器提示。 */ }
  }

  const responsible = project.responsibleUserName
    ? `${project.responsibleUserName}${project.responsibleUserEmployeeNo ? `（${project.responsibleUserEmployeeNo}）` : ''}`
    : '-'
  const showCopyJobs = canCopy && !copyJobs.unavailable && copyJobs.jobs.length > 0

  return (
    <>
      {canEdit && <Button size="small" onClick={() => setEditOpen(true)}>编辑</Button>}
      {canTransfer && <Button size="small" onClick={openTransfer}>变更负责人</Button>}
      {canCopy && <Button size="small" disabled={!copyJobs.ready && !copyOutcomeUnknown} onClick={openCopy}>复制</Button>}
      {showCopyJobs && <Button size="small" type="text" onClick={() => setCopyJobsOpen(true)}>复制任务</Button>}
      {project.canDelete && (
        <Popconfirm title={`确认删除项目“${project.name}”？其中全部文件、留言和上传记录将一并永久删除，无法恢复。`} onOk={remove}>
          <Button size="small" status="danger">删除</Button>
        </Popconfirm>
      )}

      <ProjectFormModal
        visible={editOpen}
        project={editOpen ? project as ProjectSummary : null}
        onCancel={() => setEditOpen(false)}
        onSaved={() => { setEditOpen(false); onChanged() }}
        onStale={onChanged}
      />

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
          <Form.Item label="当前负责人"><Input readOnly value={responsible} /></Form.Item>
          <Form.Item label="新负责人" required>
            <Select
              showSearch
              allowClear
              loading={ownerOptionsLoading}
              placeholder="搜索姓名或账号"
              notFoundContent={ownerOptionsLoading ? undefined : <div className="select-empty-note">没有可选的内部账号（需启用且拥有“项目列表”权限）</div>}
              value={transferTarget}
              onChange={(value?: number) => setTransferTarget(value)}
              filterOption={(input, option) => String(option?.props?.children ?? '').toLowerCase().includes(input.trim().toLowerCase())}
            >
              {ownerOptions.filter((owner) => owner.id !== project.responsibleUserId).map((owner) => (
                <Select.Option key={owner.id} value={owner.id}>
                  {`${owner.realName}（${owner.employeeNo}）${owner.sectionName ? ` · ${owner.sectionName}` : ''}`}
                </Select.Option>
              ))}
            </Select>
          </Form.Item>
        </Form>
      </Modal>

      <Modal
        className="form-dialog"
        title="复制项目"
        visible={copyOpen}
        onOk={submitCopy}
        onCancel={closeCopy}
        confirmLoading={copying}
        closable={!copying}
        maskClosable={!copying}
        escToExit={!copying}
        cancelText={copyOutcomeUnknown ? '暂时关闭' : '取消'}
        okText={copying ? '提交任务中…' : copyOutcomeUnknown ? '确认并重试' : '创建复制任务'}
        okButtonProps={{ disabled: !copyOutcomeUnknown && !copyJobs.ready }}
        unmountOnExit
      >
        <Form onSubmit={() => void submitCopy()} form={copyForm} layout="vertical">
          <Form.Item label="新项目名称" field="name" rules={[{ required: true, message: '请输入新项目名称' }, textLengthRule('项目名称', 128)]}>
            <Input autoFocus disabled={copyOutcomeUnknown} placeholder="新项目名称" />
          </Form.Item>
          <EnterSubmit />
        </Form>
        <div className="dialog-note">新项目由您负责并立即开始；复制公共资料和当前项目文件，不复制留言、资料确认结果、已读回执和通知。</div>
        {copyOutcomeUnknown && <div className="dialog-note" role="alert">提交结果尚未确认，重试不会重复创建副本。任务会在后台继续执行；可暂时关闭，稍后再次打开重试。</div>}
      </Modal>

      <Drawer
        width="min(560px, 100vw)"
        title="复制任务"
        visible={copyJobsOpen}
        onCancel={() => setCopyJobsOpen(false)}
        footer={null}
        unmountOnExit
      >
        <ProjectCopyJobsPanel
          jobs={copyJobs.jobs}
          loading={copyJobs.loading}
          error={copyJobs.error}
          onRetry={copyJobs.refresh}
          onOpenResult={(projectId) => { setCopyJobsOpen(false); navigate(`/projects/${projectId}?tab=files`) }}
        />
      </Drawer>
    </>
  )
}
