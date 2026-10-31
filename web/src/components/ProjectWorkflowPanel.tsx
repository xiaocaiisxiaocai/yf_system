import { useRef, useState } from 'react'
import {
  Button, Form, Input, Message, Modal, Popconfirm, Space, Spin, Tag, Typography,
} from '@arco-design/web-react'
import { IconCheckCircle, IconCloseCircle, IconPlayArrow, IconStop, IconUndo } from '@arco-design/web-react/icon'
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
  const canStatus = isInternal && hasPerm('project:status')
  const isInProgress = project.status === 'IN_PROGRESS'
  const materialReview = isInProgress ? project.materialReviewStatus ?? null : null
  const isOwner = isInternal && project.responsibleUserId === user?.id
  const canApproveMaterial = isOwner && (materialReview === 'PENDING' || materialReview === 'RETURNED')
  const canReturnMaterial = isOwner && materialReview === 'PENDING'
  const canReopen = isOwner && project.status === 'COMPLETED'

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
      Message.success('资料已确认 OK，子项目已完成')
      closeApproval()
      onChanged()
    } catch (error) {
      if (!isVersionConflict(error)) throw error
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
      if (!isVersionConflict(error)) throw error
      // 厂商期间又回传了资料：关闭弹窗并刷新，由负责人查看新资料后重新决定。
      Message.info('厂商回传资料已更新，请查看新资料后再操作')
      closeReturn()
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
      Message.success('子项目已重开，资料已退回厂商')
      closeReopen()
      onChanged()
    } catch (error) {
      if (!isVersionConflict(error)) throw error
      Message.info('子项目已被他人修改，请刷新后再操作')
      closeReopen()
      onChanged()
    } finally {
      finishAction()
    }
  }

  const status = PROJECT_STATUS[project.status]
  const showActions = (canStatus && ['DRAFT', 'IN_PROGRESS', 'TERMINATED'].includes(project.status))
    || canApproveMaterial || canReturnMaterial || canReopen
  const materialTag = materialReview ? MATERIAL_REVIEW[materialReview] : undefined

  const uploadHint = (
    <ActiveUploadSessions
      compact
      projectId={project.id}
      refreshToken={uploadRefresh + localUploadRefresh}
    />
  )
  if (compact && !showActions && !materialTag) return uploadHint

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
            {uploadHint}
          </div>
        </div>
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
          <Typography.Text>确认后子项目将完成并冻结，不能再上传文件或留言。</Typography.Text>
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
        title="重开子项目"
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
