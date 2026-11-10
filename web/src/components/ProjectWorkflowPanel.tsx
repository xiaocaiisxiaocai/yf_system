import { useEffect, useRef, useState } from 'react'
import {
  Button, Form, Input, Message, Modal, Popconfirm, Space, Spin, Tag, Typography,
} from '@arco-design/web-react'
import { IconCheckCircle, IconCloseCircle, IconStop, IconUndo } from '@arco-design/web-react/icon'
import http, { type QuietRequestConfig } from '../api/client'
import { isVersionConflict } from '../api/versionConflict'
import { MATERIAL_REVIEW, type Project, PROJECT_STATUS } from '../api/types'
import { useAuth } from '../store/auth'
import type { ApiResponses } from '../api/types'
import ActiveUploadSessions from './ActiveUploadSessions'

type WorkflowAction = 'start' | 'terminate' | 'materialApprove' | 'materialReturn' | 'reopen'

/** 确认时涵盖的厂商回传资料：确认对话框打开时读取，帮助负责人核对看到的是哪一批。 */
interface ApprovalScope {
  supplierFileId: number
  loading: boolean
  fileCount?: number
  latestName?: string
}

interface Props {
  project: Project
  onChanged: () => void
  compact?: boolean
  /** 终止失败后通知父组件刷新文件区的活动上传列表。 */
  onWriteFailed?: () => void
  uploadRefresh?: number
}

/** 项目级流程操作；所有按钮只表达当前状态允许的命令，最终权限仍由后端校验。 */
export default function ProjectWorkflowPanel({
  project, onChanged, compact = false, onWriteFailed, uploadRefresh = 0,
}: Props) {
  const { hasPerm, user } = useAuth()
  // 只让被点击的按钮转圈，其余流程按钮在请求期间禁用。
  const [busyAction, setBusyAction] = useState<WorkflowAction | null>(null)
  const busy = busyAction !== null
  const busyRef = useRef(false)
  const [approval, setApproval] = useState<ApprovalScope | null>(null)
  const approvalSeq = useRef(0)
  const [returnFileId, setReturnFileId] = useState<number | null>(null)
  const [returnForm] = Form.useForm()
  const [reopenVersion, setReopenVersion] = useState<string | null>(null)
  const [reopenForm] = Form.useForm()
  const [localUploadRefresh, setLocalUploadRefresh] = useState(0)
  // 40903 后对话框保持打开：记下冲突时的项目快照，等刷新后的新项目到达再把期望版本换成新基线。
  const returnConflictFrom = useRef<Project | null>(null)
  const reopenConflictFrom = useRef<Project | null>(null)

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
  const isInternal = user?.userType === 'INTERNAL'
  // 内部账号只能操作自己负责的项目（以后端返回的 canManage 为准）。
  const isOwner = isInternal && project.canManage
  const canStatus = isOwner && hasPerm('project:status')
  const isInProgress = project.status === 'IN_PROGRESS'
  const materialReview = isInProgress ? project.materialReviewStatus ?? null : null
  const canApproveMaterial = isOwner && (materialReview === 'PENDING' || materialReview === 'RETURNED')
  const canReturnMaterial = isOwner && materialReview === 'PENDING'
  const canReopen = isOwner && project.status === 'COMPLETED'

  useEffect(() => {
    const returnFrom = returnConflictFrom.current
    if (returnFrom && returnFrom !== project) {
      returnConflictFrom.current = null
      if (canReturnMaterial && project.latestSupplierFileId) {
        // 以刷新后的资料版本为新基线；原因文字保留，由负责人重新确认后再次提交。
        // oxlint-disable-next-line react/set-state-in-effect
        setReturnFileId(project.latestSupplierFileId)
      } else {
        // 项目已不允许退回（状态或资料已变），继续停留没有意义。
        returnForm.resetFields()
        // oxlint-disable-next-line react/set-state-in-effect
        setReturnFileId(null)
      }
    }
    const reopenFrom = reopenConflictFrom.current
    if (reopenFrom && reopenFrom !== project) {
      reopenConflictFrom.current = null
      if (canReopen) {
        // oxlint-disable-next-line react/set-state-in-effect
        setReopenVersion(project.updatedAt)
      } else {
        reopenForm.resetFields()
        // oxlint-disable-next-line react/set-state-in-effect
        setReopenVersion(null)
      }
    }
  }, [project, canReturnMaterial, canReopen, returnForm, reopenForm])

  const changeStatus = async (status: 'IN_PROGRESS' | 'TERMINATED') => {
    if (!beginAction(status === 'TERMINATED' ? 'terminate' : 'start')) return
    try {
      await http.put<ApiResponses['PUT /projects/{id}/status']>(`/projects/${project.id}/status`, {
        status,
        // 以页面当前展示的状态为期望值：别处已改变状态时由后端以 40903 拒绝，而不是按过期画面执行。
        expectedStatus: project.status,
      })
      Message.success(status === 'IN_PROGRESS' ? '项目已重新开始' : '项目已终止')
      onChanged()
    } catch (error) {
      // 终止失败时刷新活动上传。只有 40903 才刷新项目；40901 保持当前画面以便重试。
      if (status === 'TERMINATED') {
        setLocalUploadRefresh((value) => value + 1)
        onWriteFailed?.()
      }
      if (isVersionConflict(error)) onChanged()
    } finally {
      finishAction()
    }
  }

  const openApproval = async () => {
    const supplierFileId = project.latestSupplierFileId
    if (!supplierFileId) {
      Message.error('回传资料版本缺失，请刷新项目后重试')
      return
    }
    const seq = ++approvalSeq.current
    setApproval({ supplierFileId, loading: true })
    try {
      const response = await http.get<ApiResponses['GET /projects/{projectId}/files']>(`/projects/${project.id}/files`, {
        params: { page: 1, pageSize: 1, direction: 'S2C' },
        quietNetworkError: true,
      } as QuietRequestConfig)
      if (seq !== approvalSeq.current) return
      setApproval({
        supplierFileId,
        loading: false,
        fileCount: response.data.total,
        latestName: response.data.list[0]?.originalName,
      })
    } catch {
      // 文件概况只用于核对；读取失败时仍可按当前版本确认，后端会拒绝过期版本。
      if (seq === approvalSeq.current) setApproval({ supplierFileId, loading: false })
    }
  }

  const closeApproval = () => {
    approvalSeq.current += 1
    setApproval(null)
  }

  const approveMaterial = async () => {
    if (!approval) return
    if (!beginAction('materialApprove')) return
    try {
      await http.post<ApiResponses['POST /projects/{id}/materials/approve']>(`/projects/${project.id}/materials/approve`, {
        expectedSupplierFileId: approval.supplierFileId,
      })
      Message.success('资料已确认 OK，项目已完成')
      closeApproval()
      onChanged()
    } catch (error) {
      // 其他错误已由拦截器提示；保留弹窗以便重试，不再抛给 Modal 的 onOk。
      if (!isVersionConflict(error)) return
      Message.info('厂商回传资料已更新，请查看新资料后再确认')
      closeApproval()
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
      if (!isVersionConflict(error)) return
      // 厂商期间又回传了资料：保留弹窗和已填原因，刷新项目基线后由负责人查看新资料并重新确认。
      Message.info('厂商回传资料已更新，请查看新资料后再确认退回')
      returnConflictFrom.current = project
      onChanged()
    } finally {
      finishAction()
    }
  }

  const closeReopen = () => {
    reopenForm.resetFields()
    setReopenVersion(null)
  }

  const reopen = async () => {
    if (!reopenVersion) return
    if (!beginAction('reopen')) return
    try {
      const values = await reopenForm.validate().catch(() => null)
      if (!values) return
      await http.post<ApiResponses['POST /projects/{id}/reopen']>(`/projects/${project.id}/reopen`, {
        reason: values.reason.trim(),
        expectedUpdatedAt: reopenVersion,
      })
      Message.success('项目已重开，资料已退回厂商')
      closeReopen()
      onChanged()
    } catch (error) {
      if (!isVersionConflict(error)) return
      // 保留弹窗和已填原因，刷新项目基线后由负责人重新确认再提交。
      Message.info('项目已被他人修改，请确认最新状态后再重开')
      reopenConflictFrom.current = project
      onChanged()
    } finally {
      finishAction()
    }
  }

  const status = PROJECT_STATUS[project.status]
  const showActions = (canStatus && ['IN_PROGRESS', 'TERMINATED'].includes(project.status))
    || canApproveMaterial || canReturnMaterial || canReopen
  const materialTag = materialReview ? MATERIAL_REVIEW[materialReview] : undefined

  const uploadHint = (
    <ActiveUploadSessions
      compact
      projectId={project.id}
      refreshToken={uploadRefresh + localUploadRefresh}
    />
  )
  // 紧凑模式（项目详情标题栏）的资料状态和退回原因由标题显示，这里只放上传提示和操作按钮。
  if (compact && !showActions) return uploadHint

  return (
    <div className={`project-workflow${compact ? ' project-workflow--compact' : ''}`} aria-label="项目流程操作">
      <div className="project-workflow-summary">
        <div>
          {!compact && <Typography.Text type="secondary">项目流程</Typography.Text>}
          <div className="project-workflow-status">
            {!compact && <Tag color={status?.color}>{status?.text || project.status}</Tag>}
            {!compact && materialTag && <Tag color={materialTag.color}>{materialTag.text}</Tag>}
            {!compact && materialReview === 'RETURNED' && project.materialReviewReason && (
              <Typography.Text type="secondary" title={project.materialReviewReason}>退回原因：{project.materialReviewReason}</Typography.Text>
            )}
            {uploadHint}
          </div>
        </div>
      </div>

      {showActions && (
        <Space className="project-workflow-actions" wrap>
          {canStatus && project.status === 'TERMINATED' && (
            <Button type="primary" icon={<IconUndo />} {...actionProps('start')} onClick={() => changeStatus('IN_PROGRESS')}>
              重新开始
            </Button>
          )}
          {canApproveMaterial && (
            <Button type="primary" status="success" icon={<IconCheckCircle />} {...actionProps('materialApprove')} onClick={openApproval}>
              资料确认 OK
            </Button>
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
          {canReopen && (
            <Button icon={<IconUndo />} {...actionProps('reopen')} onClick={() => setReopenVersion(project.updatedAt)}>
              重开
            </Button>
          )}
          {canStatus && project.status === 'IN_PROGRESS' && (
            <Popconfirm title="终止项目后需要重新开始才能继续，确认终止？" onOk={() => changeStatus('TERMINATED')}>
              <Button status="danger" icon={<IconStop />} {...actionProps('terminate')}>终止</Button>
            </Popconfirm>
          )}
        </Space>
      )}

      <Modal
        className="form-dialog"
        title="资料确认 OK"
        visible={approval !== null}
        confirmLoading={busy}
        closable={!busy}
        maskClosable={!busy}
        escToExit={!busy}
        cancelButtonProps={{ disabled: busy }}
        okButtonProps={{ disabled: approval?.loading }}
        onOk={approveMaterial}
        onCancel={() => {
          if (busyRef.current) return
          closeApproval()
        }}
        okText="确认并完成"
        cancelText="取消"
      >
        <Space direction="vertical" size={8}>
          <Typography.Text>确认后项目将完成并冻结，不能再上传文件或留言。</Typography.Text>
          {approval?.loading ? <Spin size={16} /> : approval?.fileCount !== undefined && (
            <Typography.Text type="secondary">
              厂商回传文件 {approval.fileCount} 个{approval.latestName ? `，最新：${approval.latestName}` : ''}
            </Typography.Text>
          )}
        </Space>
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

      <Modal
        className="form-dialog"
        title="重开项目"
        visible={reopenVersion !== null}
        confirmLoading={busy}
        closable={!busy}
        maskClosable={!busy}
        escToExit={!busy}
        cancelButtonProps={{ disabled: busy }}
        onOk={reopen}
        onCancel={() => {
          if (busyRef.current) return
          closeReopen()
        }}
        okText="确认重开"
        cancelText="取消"
        okButtonProps={{ status: 'warning' }}
      >
        <Form form={reopenForm} layout="vertical">
          <Form.Item label="重开原因（必填）" field="reason" rules={[{ required: true, message: '请填写重开原因' }]}>
            <Input.TextArea rows={4} maxLength={500} showWordLimit wordLimitPosition="outside" placeholder="请说明需要厂商返工的内容" />
          </Form.Item>
        </Form>
      </Modal>
    </div>
  )
}
