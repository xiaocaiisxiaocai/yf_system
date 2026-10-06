import { useRef, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import {
  Alert, Button, Card, Descriptions, Empty, Input, Message, Modal, Popconfirm, Result, Select, Space, Spin, Steps, Table, Tag, Typography,
} from '@arco-design/web-react'
import { IconDownload, IconEye, IconLeft } from '@arco-design/web-react/icon'
import { useNavigate, useParams } from 'react-router-dom'
import { fmtSize, fmtTime } from '../../api/types'
import type { ApprovalInfo, ApprovalTask, TransferDetail, TransferFile } from '../api/types'
import OemUploader, { type OemUploaderHandle } from '../components/OemUploader'
import { ApprovalTag, LifecycleTag, PayloadTag, ValidationTag } from '../components/StatusTags'
import { DIRECTION_LABEL } from '../components/statusLabels'
import { useCan, useOem } from '../OemContext'

const PREVIEWABLE = new Set(['pdf', 'png', 'jpg', 'jpeg'])

const SOURCE_LABEL: Record<string, string> = {
  SECTION_LEADER: '课别主管', DEPARTMENT_LEADER: '部门主管', DIVISION_LEADER: '事业部主管', SPECIFIED_USERS: '指定人员',
}
const TASK_LABEL: Record<string, string> = {
  WAITING: '等待', PENDING: '待审批', APPROVED: '已通过', REJECTED: '已驳回', NOT_NEEDED: '无需处理', SUPERSEDED: '已改派', CANCELLED: '已取消',
}

function promptText(title: string, placeholder: string): Promise<string | null> {
  return new Promise((resolve) => {
    let text = ''
    Modal.confirm({
      title,
      content: <Input.TextArea autoFocus placeholder={placeholder} maxLength={512} onChange={(value) => { text = value }} />,
      onOk: () => {
        if (!text.trim()) {
          Message.warning('请填写原因')
          return Promise.reject(new Error('empty'))
        }
        resolve(text.trim())
        return Promise.resolve()
      },
      onCancel: () => resolve(null),
    })
  })
}

function ApprovalPanel({ detail, onChanged }: { detail: TransferDetail; onChanged: (next: TransferDetail) => void }) {
  const { api, userId, base, realm, permissions } = useOem()
  const can = useCan()
  const navigate = useNavigate()
  const recoveryScoped = realm === 'internal' && can.recover && !permissions.has('oem:transfer_view')
  const approval = detail.approval as ApprovalInfo
  const [reassigning, setReassigning] = useState<ApprovalTask | null>(null)
  const [candidates, setCandidates] = useState<{ id: number; employeeNo: string; realName: string }[]>([])
  const [target, setTarget] = useState<number | undefined>()
  const [reason, setReason] = useState('')
  const [deciding, setDeciding] = useState<'approve' | 'reject' | null>(null)
  const decidingRef = useRef(false)

  const myTask = approval.nodes.flatMap((node) => node.tasks).find((task) => task.status === 'PENDING' && task.approverUserId === userId)
  const open = ['WAITING_FILES', 'IN_PROGRESS', 'APPROVAL_BLOCKED'].includes(approval.status)

  /** One decision at a time; failures are already reported by the HTTP layer. */
  const decide = async (kind: 'approve' | 'reject', run: () => Promise<boolean>) => {
    if (decidingRef.current) return
    decidingRef.current = true
    setDeciding(kind)
    try {
      await run()
    } catch {
      // Reported by the request layer; the task stays pending so the approver can retry.
    } finally {
      decidingRef.current = false
      setDeciding(null)
    }
  }
  const approve = (task: ApprovalTask) => decide('approve', async () => {
    onChanged(await api.approve(task.id, task.version))
    Message.success('已审批通过')
    return true
  })
  const reject = (task: ApprovalTask) => decide('reject', async () => {
    const text = await promptText('驳回传递单', '请输入驳回原因（发送人将收到通知）')
    if (!text) return false
    onChanged(await api.reject(task.id, task.version, text))
    Message.success('已驳回')
    return true
  })
  const searchCandidates = async (keyword: string) => setCandidates(await api.approverOptions(keyword))
  const submitReassign = async () => {
    if (!reassigning || !target || !reason.trim()) {
      Message.warning('请选择新审批人并填写改派原因')
      return
    }
    const next = await api.reassign(reassigning.id, target, reason.trim(), reassigning.version)
    setReassigning(null)
    Message.success('已改派')
    if (recoveryScoped) {
      navigate(`${base}/recovery`, { replace: true })
      return
    }
    onChanged(next)
  }

  const current = approval.nodes.findIndex((node) => node.status === 'PENDING' || node.status === 'WAITING')
  return (
    <Card title={`审批流程（${approval.templateName ?? '-'}）`} style={{ marginTop: 16 }}>
      {approval.status === 'APPROVAL_BLOCKED' && (
        <Alert type="warning" style={{ marginBottom: 12 }} content={`审批阻断：${approval.blockedReason ?? ''}。需要具有异常处置权限的管理员改派审批人或终止传递。`} />
      )}
      {myTask && (
        <Alert type="info" style={{ marginBottom: 12 }} content={
          <Space>
            <span>请审阅附件后处理此审批任务。</span>
            <Button type="primary" size="small" loading={deciding === 'approve'} disabled={deciding !== null}
              onClick={() => void approve(myTask)}>审批通过</Button>
            <Button status="danger" size="small" loading={deciding === 'reject'} disabled={deciding !== null}
              onClick={() => void reject(myTask)}>驳回</Button>
          </Space>
        } />
      )}
      <Steps direction="vertical" current={current < 0 ? approval.nodes.length + 1 : current + 1} size="small">
        {approval.nodes.map((node) => (
          <Steps.Step
            key={node.sortNo}
            title={<Space>{node.name}<Tag size="small">{SOURCE_LABEL[node.approverSource] ?? node.approverSource}</Tag>
              {node.approvalMode === 'ALL' && <Tag size="small" color="purple">会签</Tag>}
              {node.approvalMode === 'ANY' && <Tag size="small" color="blue">或签</Tag>}
              {node.usedFallback && <Tag size="small" color="orange">备用审批人</Tag>}</Space>}
            description={node.status === 'SKIPPED' ? '发送人即本节点审批人，按策略免审' : (
              <Space direction="vertical" size={2}>
                {node.tasks.map((task) => (
                  <Space key={task.id} size={6}>
                    <span>{task.approverName}（{task.approverEmployeeNo}）</span>
                    <Tag size="small">{TASK_LABEL[task.status] ?? task.status}</Tag>
                    {task.decidedAt && <Typography.Text type="secondary">{fmtTime(task.decidedAt)}</Typography.Text>}
                    {task.reason && <Typography.Text type="secondary">原因：{task.reason}</Typography.Text>}
                    {task.reassignReason && <Typography.Text type="secondary">改派原因：{task.reassignReason}</Typography.Text>}
                    {can.recover && open && ['WAITING', 'PENDING'].includes(task.status) && (
                      <Button size="mini" onClick={() => { setReassigning(task); setTarget(undefined); setReason(''); void searchCandidates('') }}>改派</Button>
                    )}
                  </Space>
                ))}
              </Space>
            )}
          />
        ))}
      </Steps>
      <Modal title="改派审批人" visible={Boolean(reassigning)} onCancel={() => setReassigning(null)} onOk={submitReassign} unmountOnExit>
        <Space direction="vertical" style={{ width: '100%' }}>
          <Select showSearch placeholder="搜索具有 OEM 审批权限的员工" value={target} onChange={setTarget} filterOption={false}
            onSearch={(value) => void searchCandidates(value)}
            options={candidates.map((item) => ({ value: item.id, label: `${item.realName}（${item.employeeNo}）` }))} />
          <Input.TextArea placeholder="改派原因（必填）" value={reason} onChange={setReason} maxLength={512} />
          <Typography.Text type="secondary">改派不会替原审批人做决定；原任务保留为“已改派”记录。</Typography.Text>
        </Space>
      </Modal>
    </Card>
  )
}

export default function TransferDetailPage() {
  const { api, base, realm, permissions, queryScope } = useOem()
  const can = useCan()
  const navigate = useNavigate()
  const id = Number(useParams().id)
  const queryClient = useQueryClient()
  const uploader = useRef<OemUploaderHandle>(null)
  const sendLocked = useRef(false)
  const [uploadBusy, setUploadBusy] = useState(false)
  const [sending, setSending] = useState(false)
  const recoveryScoped = realm === 'internal' && can.recover && !permissions.has('oem:transfer_view')
  const detailKey = ['oem', ...queryScope, 'transfer', id] as const
  const detailQuery = useQuery({
    queryKey: detailKey,
    queryFn: () => api.transfer(id),
    enabled: Number.isSafeInteger(id) && id > 0,
    // Validation, approval completion and final release are asynchronous.
    refetchInterval: (query) => {
      const current = query.state.data
      if (!current) return false
      const filesSettling = current.files.some((file) => ['PENDING', 'VALIDATING'].includes(file.validationStatus) || file.payloadStatus === 'PROMOTING')
      if (current.summary.lifecycleStatus === 'DRAFT') return filesSettling ? 5000 : false
      if (current.summary.lifecycleStatus !== 'SEALED') return false
      const approvalSettling = current.summary.direction === 'INTERNAL_TO_OEM'
        && ['WAITING_FILES', 'PENDING', 'APPROVED', 'SKIPPED'].includes(current.summary.approvalStatus ?? '')
      return filesSettling || approvalSettling ? 5000 : false
    },
  })
  const detail = detailQuery.data ?? null
  const setDetail = (next: TransferDetail) => queryClient.setQueryData(detailKey, next)
  const load = async () => { await detailQuery.refetch() }
  const missing = (detailQuery.error as { response?: { status?: number } } | null)?.response?.status === 404

  if (missing || !Number.isSafeInteger(id) || id <= 0) return <Result status="404" title="传递单不存在或无权查看" extra={<Button onClick={() => navigate(`${base}/transfers`)}>返回列表</Button>} />
  if (!detail && detailQuery.isError) {
    return (
      <Result status="error" title="传递单加载失败" subTitle="网络或服务暂时不可用，请稍后重试。" extra={(
        <Space>
          <Button type="primary" loading={detailQuery.isFetching} onClick={() => void load()}>重试</Button>
          <Button onClick={() => navigate(`${base}/transfers`)}>返回列表</Button>
        </Space>
      )} />
    )
  }
  if (!detail) return <Spin style={{ display: 'block', marginTop: 80 }} />

  const summary = detail.summary
  const caps = detail.capabilities
  const failedFiles = detail.files.some((file) => ['INVALID', 'ERROR'].includes(file.validationStatus))

  const handleNoLongerDraft = (next: TransferDetail) => {
    setDetail(next)
    const status = next.summary.lifecycleStatus
    if (status === 'DRAFT') return
    const message = ({
      SEALED: next.summary.direction === 'INTERNAL_TO_OEM' ? '传递单已提交，正在等待审批或文件处理' : '文件正在处理中',
      RELEASED: '文件已发送',
      REJECTED: '传递单已被驳回',
      BLOCKED: '传递单因文件校验未通过而终止',
      CANCELLED: '传递单已终止',
      ABANDONED: '草稿已删除',
    } as const)[status]
    Message.info(message)
    if (status === 'ABANDONED') navigate(`${base}/transfers`, { replace: true })
  }

  const send = async () => {
    if (sendLocked.current || uploader.current?.isBusy()) return
    sendLocked.current = true
    setSending(true)
    try {
      const latest = await api.transfer(id)
      if (latest.summary.lifecycleStatus !== 'DRAFT') {
        handleNoLongerDraft(latest)
        return
      }
      try {
        const submitted = await api.send(id, latest.summary.version)
        setDetail(submitted)
        Message.success(submitted.summary.lifecycleStatus === 'RELEASED'
          ? '文件已发送'
          : submitted.summary.direction === 'INTERNAL_TO_OEM' ? '已提交审批，审批完成后将自动发送' : '文件正在处理中')
      } catch (error) {
        const checked = await api.transfer(id)
        if (checked.summary.lifecycleStatus === 'DRAFT') throw error
        handleNoLongerDraft(checked)
      }
    } finally {
      sendLocked.current = false
      setSending(false)
    }
  }
  const remove = async () => {
    await api.deleteDraft(id, summary.version)
    Message.success('草稿已删除')
    navigate(`${base}/transfers`)
  }
  const cancel = async () => {
    const reason = await promptText('终止传递单', '请输入终止原因（发送人将收到通知）')
    if (!reason) return
    const next = await api.cancel(id, reason, summary.version)
    Message.success('传递已终止')
    if (recoveryScoped) {
      navigate(`${base}/recovery`, { replace: true })
      return
    }
    setDetail(next)
  }
  const download = async (file: TransferFile) => {
    const session = await api.startDownload(file.id)
    // The grant cookie is path-scoped and HttpOnly; a plain navigation lets the browser stream the file to disk.
    window.location.href = session.url
  }
  const preview = async (file: TransferFile) => {
    // Open the tab inside the click gesture so popup blockers allow it, then point it at the blob.
    // 'noopener' would make window.open return null, so the opener link is cut by hand instead.
    const target = window.open('', '_blank')
    if (!target) {
      Message.warning('浏览器阻止了新窗口，请允许本站弹出窗口后重试')
      return
    }
    target.opener = null
    try {
      const blob = await api.previewBlob(file.id)
      const url = URL.createObjectURL(blob)
      target.location.href = url
      setTimeout(() => URL.revokeObjectURL(url), 60_000)
    } catch {
      // The HTTP layer already reported the failure.
      target.close()
    }
  }
  const removeFile = async (file: TransferFile) => {
    await api.removeFile(file.id)
    await load()
  }

  return (
    <div>
      <Card
        title={<Space><Button type="text" icon={<IconLeft />} onClick={() => navigate(`${base}/transfers`)} />{summary.title}
          <LifecycleTag value={summary.lifecycleStatus} />
          {summary.direction === 'INTERNAL_TO_OEM' && realm === 'internal' && <ApprovalTag value={summary.approvalStatus} />}</Space>}
        extra={
          <Space>
            {caps.canSend && (
              <Button type="primary" loading={sending} disabled={detail.files.length === 0 || failedFiles || uploadBusy}
                onClick={() => void send().catch(() => undefined)}>{summary.direction === 'INTERNAL_TO_OEM' ? '提交审批' : '发送文件'}</Button>
            )}
            {caps.canDelete && (
              <Popconfirm title="删除草稿后附件将被清理，确定删除？" onOk={remove}>
                <Button status="danger">删除草稿</Button>
              </Popconfirm>
            )}
            {can.recover && summary.lifecycleStatus === 'SEALED' && <Button status="warning" onClick={() => void cancel().catch(() => undefined)}>终止传递</Button>}
          </Space>
        }
      >
        {summary.lifecycleStatus === 'DRAFT' && failedFiles && (
          <Alert type="error" style={{ marginBottom: 12 }} content="有附件未通过文件校验，请移除后再发送。" />
        )}
        {summary.lifecycleStatus === 'DRAFT' && uploadBusy && (
          <Alert type="info" style={{ marginBottom: 12 }} content="附件仍在上传，完成后才能提交。" />
        )}
        {summary.approvalBlockedReason && realm === 'internal' && (
          <Alert type="warning" style={{ marginBottom: 12 }} content={`审批阻断：${summary.approvalBlockedReason}`} />
        )}
        {detail.closedReason && <Alert type="warning" style={{ marginBottom: 12 }} content={`结束原因：${detail.closedReason}`} />}
        <Descriptions column={2} data={[
          { label: '方向', value: DIRECTION_LABEL[summary.direction] },
          { label: 'OEM 厂商', value: summary.companyName },
          { label: '发送人', value: `${summary.sender.realName}（${summary.sender.employeeNo}）` },
          { label: '删除策略', value: detail.retention.summary ?? '-' },
          { label: '提交时间', value: summary.sentAt ? fmtTime(summary.sentAt) : '-' },
          { label: '发送时间', value: summary.releasedAt ? fmtTime(summary.releasedAt) : '-' },
          { label: '最晚保留至', value: detail.expiresAt ? fmtTime(detail.expiresAt) : '-' },
          { label: '说明', value: detail.description ?? '-' },
        ]} />
      </Card>

      <Card title="附件" style={{ marginTop: 16 }}>
        {caps.canEdit && <div style={{ marginBottom: 16 }}><OemUploader ref={uploader} transferId={id} disabled={sending}
          onBusyChange={setUploadBusy} onUploaded={() => void load()} /></div>}
        {can.recover && !caps.canReadContent && detail.files.length === 0 ? (
          <Alert type="info" content="异常处置权限不包含附件内容；此页仅显示完成改派或终止所需的审批任务信息。" />
        ) : detail.files.length === 0 ? <Empty description="暂无附件" /> : (
          <Table rowKey="id" data={detail.files} pagination={false} scroll={{ x: 900 }} columns={[
            { title: '文件名', dataIndex: 'originalName' },
            { title: '大小', width: 110, render: (_: unknown, file: TransferFile) => fmtSize(file.sizeBytes) },
            {
              title: '文件校验', width: 200,
              render: (_: unknown, file: TransferFile) => (
                <Space direction="vertical" size={0}>
                  <ValidationTag value={file.validationStatus} />
                  {file.validationStatus !== 'VALID' && file.validationMessage && (
                    <Typography.Text type="secondary" style={{ fontSize: 12 }}>{file.validationMessage}</Typography.Text>
                  )}
                </Space>
              ),
            },
            { title: '存储', width: 110, render: (_: unknown, file: TransferFile) => <PayloadTag value={file.payloadStatus} /> },
            {
              title: '接收 / 删除', width: 220,
              render: (_: unknown, file: TransferFile) => (
                <Space direction="vertical" size={0} style={{ fontSize: 12 }}>
                  {file.firstRecipientDownloadAt && <span>首次接收：{fmtTime(file.firstRecipientDownloadAt)}</span>}
                  {file.purgedAt ? <span>已删除：{fmtTime(file.purgedAt)}</span> : file.purgeDueAt && <span>计划删除：{fmtTime(file.purgeDueAt)}</span>}
                </Space>
              ),
            },
            {
              title: '操作', width: 200,
              render: (_: unknown, file: TransferFile) => (
                <Space>
                  {file.downloadable && <Button size="small" icon={<IconDownload />} onClick={() => void download(file).catch(() => undefined)}>下载</Button>}
                  {file.downloadable && PREVIEWABLE.has(file.ext) && file.sizeBytes <= 50 * 1024 * 1024 && (
                    <Button size="small" icon={<IconEye />} onClick={() => void preview(file)}>预览</Button>
                  )}
                  {caps.canEdit && (
                    <Popconfirm title="移除该附件？" onOk={() => removeFile(file)}><Button size="small" status="danger">移除</Button></Popconfirm>
                  )}
                </Space>
              ),
            },
          ]} />
        )}
        {summary.direction === 'OEM_TO_INTERNAL' || realm === 'oem' ? null : summary.lifecycleStatus === 'SEALED' && !caps.canReadContent && (
          <Typography.Text type="secondary">附件在文件校验与审批完成、正式发布前仅发送人和当前审批人可查看。</Typography.Text>
        )}
      </Card>

      {detail.approval && realm === 'internal' && <ApprovalPanel detail={detail} onChanged={setDetail} />}
    </div>
  )
}
