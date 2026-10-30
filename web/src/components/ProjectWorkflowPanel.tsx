import { useRef, useState } from 'react'
import {
  Button, Form, Input, Message, Modal, Popconfirm, Space, Tag, Typography,
} from '@arco-design/web-react'
import { IconCheckCircle, IconCloseCircle, IconPlayArrow, IconStop, IconUndo } from '@arco-design/web-react/icon'
import http, { type QuietRequestConfig } from '../api/client'
import { isVersionConflict } from '../api/versionConflict'
import { type Project, PROJECT_STATUS } from '../api/types'
import { useAuth } from '../store/auth'
import type { ApiResponses } from '../api/types'
import ActiveUploadSessions from './ActiveUploadSessions'

type WorkflowAction = 'start' | 'terminate' | 'submit' | 'confirm' | 'reject' | 'withdraw'
  | 'materialApprove' | 'materialReturn'

const MATERIAL_REVIEW: Record<string, { text: string; color: string }> = {
  NO_MATERIAL: { text: '待回传资料', color: 'gray' },
  PENDING: { text: '资料待确认', color: 'orange' },
  APPROVED: { text: '资料已确认 OK', color: 'green' },
  RETURNED: { text: '资料已退回', color: 'red' },
}

interface Props {
  project: Project
  onChanged: () => void
  compact?: boolean
  /** 提交或终止失败后通知父组件刷新文件区的活动上传列表。 */
  onWriteFailed?: () => void
  uploadRefresh?: number
}

/** 项目级流程操作；所有按钮只表达当前状态允许的命令，最终权限仍由后端校验。 */
export default function ProjectWorkflowPanel({
  project, onChanged, compact = false, onWriteFailed, uploadRefresh = 0,
}: Props) {
  const { hasPerm, user } = useAuth()
  const [rejectOpen, setRejectOpen] = useState(false)
  // 只让被点击的按钮转圈，其余流程按钮在请求期间禁用。
  const [busyAction, setBusyAction] = useState<WorkflowAction | null>(null)
  const busy = busyAction !== null
  const busyRef = useRef(false)
  const confirmSubmissionRef = useRef<number | null>(project.latestSubmissionId ?? null)
  const withdrawSubmissionRef = useRef<number | null>(project.latestSubmissionId ?? null)
  const [rejectSubmissionId, setRejectSubmissionId] = useState<number | null>(null)
  const [rejectForm] = Form.useForm()
  const materialFileRef = useRef<number | null>(project.latestSupplierFileId ?? null)
  const [returnFileId, setReturnFileId] = useState<number | null>(null)
  const [returnForm] = Form.useForm()
  const [localUploadRefresh, setLocalUploadRefresh] = useState(0)

  const beginAction = (action: WorkflowAction) => {
    if (busyRef.current) return false
    busyRef.current = true
    setBusyAction(action)
    return true
  }
  const finishAction = () => {
    busyRef.current = false
    setBusyAction(null)
  }
  const actionProps = (action: WorkflowAction) => ({
    loading: busyAction === action,
    disabled: busy && busyAction !== action,
    size: compact ? 'small' as const : undefined,
  })
  const noteWriteFailure = () => {
    setLocalUploadRefresh((value) => value + 1)
    onWriteFailed?.()
  }
  const isInternal = user?.userType === 'INTERNAL'
  const isSupplier = user?.userType === 'SUPPLIER'
  const canStatus = isInternal && hasPerm('project:status')
  const canSubmit = isSupplier && hasPerm('project:submit')
  const canConfirm = isInternal && hasPerm('project:confirm') && project.confirmSide === 'COMPANY'
  const canWithdraw = isSupplier && hasPerm('project:withdraw') && project.latestSubmitterId === user?.id
  const isInProgress = project.status === 'IN_PROGRESS'
  const materialReview = isInProgress ? project.materialReviewStatus ?? null : null
  const canReviewMaterial = isInternal && project.responsibleUserId === user?.id && isInProgress
  const canApproveMaterial = canReviewMaterial && (materialReview === 'PENDING' || materialReview === 'RETURNED')
  const canReturnMaterial = canReviewMaterial && (materialReview === 'PENDING' || materialReview === 'APPROVED')

  const changeStatus = async (status: 'IN_PROGRESS' | 'TERMINATED') => {
    if (!beginAction(status === 'TERMINATED' ? 'terminate' : 'start')) return
    try {
      await http.put<ApiResponses['PUT /projects/{id}/status']>(`/projects/${project.id}/status`, { status })
      Message.success(status === 'IN_PROGRESS'
        ? (project.status === 'TERMINATED' ? '项目已重新开始' : '项目已开始')
        : '项目已终止')
      onChanged()
    } catch (error) {
      // 终止失败时刷新活动上传。只有 40903 才刷新项目；40901 保持当前画面以便重试。
      if (status === 'TERMINATED') noteWriteFailure()
      if (isVersionConflict(error)) onChanged()
    } finally {
      finishAction()
    }
  }

  const submitConfirmation = async () => {
    if (!beginAction('submit')) return
    try {
      await http.post<ApiResponses['POST /projects/{id}/submit']>(`/projects/${project.id}/submit`, { confirmSide: 'COMPANY' })
      Message.success('项目已提交公司验收')
      onChanged()
    } catch (error) {
      noteWriteFailure()
      if (isVersionConflict(error)) onChanged()
    } finally {
      finishAction()
    }
  }

  const approveMaterial = async () => {
    const expectedSupplierFileId = materialFileRef.current
    if (!expectedSupplierFileId) {
      Message.error('回传资料版本缺失，请刷新项目后重试')
      return
    }
    if (!beginAction('materialApprove')) return
    try {
      await http.post<ApiResponses['POST /projects/{id}/materials/approve']>(`/projects/${project.id}/materials/approve`, { expectedSupplierFileId })
      Message.success('已确认回传资料 OK')
      onChanged()
    } catch (error) {
      if (!isVersionConflict(error)) throw error
      onChanged()
    } finally {
      finishAction()
    }
  }

  const closeReturn = () => {
    returnForm.resetFields()
    setReturnFileId(null)
  }

  const returnMaterial = async () => {
    if (!returnFileId) {
      Message.error('回传资料版本缺失，请关闭弹窗并刷新项目后重试')
      return
    }
    if (!beginAction('materialReturn')) return
    try {
      const values = await returnForm.validate().catch(() => null)
      if (!values) return
      await http.post<ApiResponses['POST /projects/{id}/materials/return']>(`/projects/${project.id}/materials/return`, {
        reason: values.reason.trim(),
        expectedSupplierFileId: returnFileId,
      })
      Message.success('回传资料已退回')
      closeReturn()
      onChanged()
    } catch (error) {
      if (!isVersionConflict(error)) throw error
      // 厂商期间又回传了资料：关闭弹窗并刷新，由负责人查看新资料后重新决定。
      Message.info('厂商回传资料已更新，请查看新资料后再操作')
      closeReturn()
      onChanged()
    } finally {
      finishAction()
    }
  }

  const confirmProject = async () => {
    const expectedSubmissionId = confirmSubmissionRef.current
    if (!expectedSubmissionId) {
      Message.error('验收申请版本缺失，请刷新项目后重试')
      return
    }
    if (!beginAction('confirm')) return
    try {
      await http.post<ApiResponses['POST /projects/{id}/confirm']>(`/projects/${project.id}/confirm`, { expectedSubmissionId })
      Message.success('公司验收已通过')
      onChanged()
    } catch (error) {
      if (!isVersionConflict(error)) throw error
      onChanged()
    } finally {
      finishAction()
    }
  }

  const rejectProject = async () => {
    if (!rejectSubmissionId) {
      Message.error('验收申请版本缺失，请关闭弹窗并刷新项目后重试')
      return
    }
    if (!beginAction('reject')) return
    try {
      const values = await rejectForm.validate().catch(() => null)
      if (!values) return
      await http.post<ApiResponses['POST /projects/{id}/reject']>(`/projects/${project.id}/reject`, {
        reason: values.reason.trim(),
        expectedSubmissionId: rejectSubmissionId,
      })
      Message.success('公司验收已驳回')
      rejectForm.resetFields()
      setRejectSubmissionId(null)
      setRejectOpen(false)
      onChanged()
    } catch (error) {
      if (!isVersionConflict(error)) throw error
      // 保留驳回原因：仍待验收则改用最新的申请编号；已不在待验收则关闭弹窗。
      onChanged()
      try {
        const response = await http.get<ApiResponses['GET /projects/{id}']>(`/projects/${project.id}`, { quietClientError: true } as QuietRequestConfig)
        const latest = response.data
        if (latest.status === 'PENDING_CONFIRMATION' && latest.latestSubmissionId) {
          setRejectSubmissionId(latest.latestSubmissionId)
          Message.info('验收申请已更新，请确认原因后再次提交')
          return
        }
      } catch {
        // 读取失败时按已不可驳回处理。
      }
      rejectForm.resetFields()
      setRejectSubmissionId(null)
      setRejectOpen(false)
    } finally {
      finishAction()
    }
  }

  const withdraw = async () => {
    const expectedSubmissionId = withdrawSubmissionRef.current
    if (!expectedSubmissionId) {
      Message.error('验收申请版本缺失，请刷新项目后重试')
      return
    }
    if (!beginAction('withdraw')) return
    try {
      await http.post<ApiResponses['POST /projects/{id}/withdraw']>(`/projects/${project.id}/withdraw`, { expectedSubmissionId })
      Message.success('已撤回验收申请')
      onChanged()
    } catch (error) {
      if (!isVersionConflict(error)) throw error
      onChanged()
    } finally {
      finishAction()
    }
  }

  const status = PROJECT_STATUS[project.status]
  const isPending = project.status === 'PENDING_CONFIRMATION'
  const showActions = (canStatus && ['DRAFT', 'IN_PROGRESS', 'TERMINATED'].includes(project.status))
    || (canSubmit && isInProgress)
    || canApproveMaterial || canReturnMaterial
    || (isPending && (canConfirm || canWithdraw))
  const materialTag = materialReview ? MATERIAL_REVIEW[materialReview] : undefined

  const uploadHint = (
    <ActiveUploadSessions
      compact
      projectId={project.id}
      refreshToken={uploadRefresh + localUploadRefresh}
    />
  )
  if (compact && !showActions && !isPending && !materialTag && !(isInProgress && project.rejectReason)) return uploadHint

  return (
    <div className={`project-workflow${compact ? ' project-workflow--compact' : ''}`} aria-label="项目流程操作">
      <div className="project-workflow-summary">
        <div>
          {!compact && <Typography.Text type="secondary">项目流程</Typography.Text>}
          <div className="project-workflow-status">
            {!compact && <Tag color={status?.color}>{status?.text || project.status}</Tag>}
            {materialTag && <Tag color={materialTag.color}>{materialTag.text}</Tag>}
            {materialReview === 'RETURNED' && project.materialReviewReason && (
              <Typography.Text type="secondary" title={project.materialReviewReason}>退回原因：{project.materialReviewReason}</Typography.Text>
            )}
            {isInProgress && project.rejectReason && (
              <Typography.Text type="secondary" title={project.rejectReason}>上次驳回：{project.rejectReason}</Typography.Text>
            )}
            {uploadHint}
          </div>
        </div>
        {isPending && (
          <Typography.Text type="secondary" className="project-workflow-confirm-side">
            验收方：公司
          </Typography.Text>
        )}
      </div>

      {showActions && (
        <Space className="project-workflow-actions" wrap>
          {canStatus && project.status === 'DRAFT' && (
            <Button type="primary" icon={<IconPlayArrow />} {...actionProps('start')} onClick={() => changeStatus('IN_PROGRESS')}>
              开始
            </Button>
          )}
          {canStatus && project.status === 'TERMINATED' && (
            <Button type="primary" icon={<IconUndo />} {...actionProps('start')} onClick={() => changeStatus('IN_PROGRESS')}>
              重新开始
            </Button>
          )}
          {canApproveMaterial && (
            <Popconfirm
              title="确认厂商回传资料 OK？确认后厂商可提交公司验收。"
              onVisibleChange={(visible) => {
                if (visible) materialFileRef.current = project.latestSupplierFileId ?? null
              }}
              onOk={approveMaterial}
            >
              <Button type="primary" status="success" icon={<IconCheckCircle />} {...actionProps('materialApprove')}>资料确认 OK</Button>
            </Popconfirm>
          )}
          {canReturnMaterial && (
            <Button
              status="warning"
              icon={<IconCloseCircle />}
              {...actionProps('materialReturn')}
              onClick={() => setReturnFileId(project.latestSupplierFileId ?? null)}
            >
              退回资料
            </Button>
          )}
          {canSubmit && isInProgress && (materialReview === 'APPROVED' ? (
            <Popconfirm
              title="提交公司验收？"
              onOk={submitConfirmation}
            >
              <Button type="primary" {...actionProps('submit')}>提交公司验收</Button>
            </Popconfirm>
          ) : (
            <Button type="primary" disabled size={compact ? 'small' : undefined}>提交公司验收</Button>
          ))}
          {canStatus && project.status === 'IN_PROGRESS' && (
            <Popconfirm title="终止项目后需要重新开始才能继续，确认终止？" onOk={() => changeStatus('TERMINATED')}>
              <Button status="danger" icon={<IconStop />} {...actionProps('terminate')}>终止</Button>
            </Popconfirm>
          )}
          {isPending && canConfirm && (
            <Popconfirm
              title="确认通过公司验收？项目将进入已完成状态。"
              onVisibleChange={(visible) => {
                if (visible) confirmSubmissionRef.current = project.latestSubmissionId ?? null
              }}
              onOk={confirmProject}
            >
              <Button type="primary" status="success" icon={<IconCheckCircle />} {...actionProps('confirm')}>验收通过</Button>
            </Popconfirm>
          )}
          {isPending && canConfirm && (
            <Button
              status="danger"
              icon={<IconCloseCircle />}
              {...actionProps('reject')}
              onClick={() => {
                setRejectSubmissionId(project.latestSubmissionId ?? null)
                setRejectOpen(true)
              }}
            >
              验收驳回
            </Button>
          )}
          {isPending && canWithdraw && (
            <Popconfirm
              title="撤回验收申请后项目将回到进行中，确认撤回？"
              onVisibleChange={(visible) => {
                if (visible) withdrawSubmissionRef.current = project.latestSubmissionId ?? null
              }}
              onOk={withdraw}
            >
              <Button icon={<IconUndo />} {...actionProps('withdraw')}>撤回</Button>
            </Popconfirm>
          )}
        </Space>
      )}

      <Modal
        className="form-dialog"
        title="公司验收驳回"
        visible={rejectOpen}
        confirmLoading={busy}
        closable={!busy}
        maskClosable={!busy}
        escToExit={!busy}
        cancelButtonProps={{ disabled: busy }}
        onOk={rejectProject}
        onCancel={() => {
          if (busyRef.current) return
          rejectForm.resetFields()
          setRejectSubmissionId(null)
          setRejectOpen(false)
        }}
        okText="确认驳回"
        cancelText="取消"
        okButtonProps={{ status: 'danger' }}
      >
        <Form form={rejectForm} layout="vertical">
          <Form.Item label="驳回原因（必填）" field="reason" rules={[{ required: true, message: '请填写驳回原因' }]}>
            <Input.TextArea rows={4} maxLength={500} showWordLimit wordLimitPosition="outside" placeholder="请填写驳回原因" />
          </Form.Item>
        </Form>
      </Modal>

      <Modal
        className="form-dialog"
        title="退回回传资料"
        visible={returnFileId !== null}
        confirmLoading={busy}
        closable={!busy}
        maskClosable={!busy}
        escToExit={!busy}
        cancelButtonProps={{ disabled: busy }}
        onOk={returnMaterial}
        onCancel={() => {
          if (busyRef.current) return
          closeReturn()
        }}
        okText="确认退回"
        cancelText="取消"
        okButtonProps={{ status: 'warning' }}
      >
        <Form form={returnForm} layout="vertical">
          <Form.Item label="退回原因（必填）" field="reason" rules={[{ required: true, message: '请填写退回原因' }]}>
            <Input.TextArea rows={4} maxLength={500} showWordLimit wordLimitPosition="outside" placeholder="请说明需要厂商补充或修改的资料" />
          </Form.Item>
        </Form>
      </Modal>
    </div>
  )
}
