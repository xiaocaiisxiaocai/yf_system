import { useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { actionSlots } from '../../components/ActionSlots'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import {
  Alert, Button, Card, Descriptions, Empty, Input, Message, Modal, Popconfirm, Result, Space, Spin, Table, Typography,
} from '@arco-design/web-react'
import { IconDownload, IconEye, IconLeft } from '@arco-design/web-react/icon'
import { useNavigate, useParams } from 'react-router-dom'
import { downloadOemFile } from '../components/downloadOemFile'
import RecipientChoiceSelect from '../components/RecipientChoiceSelect'
import { useRecipientChoices } from '../components/useRecipientChoices'
import { fmtSize, fmtTime } from '../../api/types'
import { isVersionConflict } from '../../api/versionConflict'
import type { ApprovalInfo, ApprovalStatus, ApprovalTask, TransferDetail, TransferFile } from '../api/types'
import OemFilePreview from '../components/OemFilePreview'
import OemUploader, { type OemUploaderHandle } from '../components/OemUploader'
import { OEM_PREVIEW_MAX_BYTES, oemPreviewKind } from '../components/oemPreview'
import { ApprovalTag, LifecycleTag, PayloadTag, ValidationTag } from '../components/StatusTags'
import { transferClosedNotice } from '../components/statusLabels'
import Counterpart from '../components/Counterpart'
import { useCan, useOem } from '../OemContext'
import { ColumnResizeProvider, ColumnVisibilityPicker } from '../../components/columnResize'
import { type ColumnLayoutSpec } from '../../components/tableColumnLayout'
import { useAdjustableColumns } from '../../components/useAdjustableColumns'

const OEM_TRANSFER_FILE_COLUMNS: readonly ColumnLayoutSpec[] = [
  { key: 'name', title: '文件名', defaultWidth: 270, minWidth: 160, maxWidth: 2400, locked: true },
  { key: 'size', title: '大小', defaultWidth: 80, minWidth: 80 },
  { key: 'validation', title: '文件校验', defaultWidth: 140, minWidth: 120 },
  { key: 'payload', title: '存储', defaultWidth: 80, minWidth: 80 },
  { key: 'receipt', title: '接收 / 删除', defaultWidth: 210, minWidth: 140 },
  { key: 'actions', title: '操作', defaultWidth: 210, minWidth: 120, maxWidth: 320, locked: true },
]

const OEM_APPROVAL_COLUMN: ColumnLayoutSpec = { key: 'approval', title: '审批', defaultWidth: 150, minWidth: 120, maxWidth: 320 }

function promptText(title: string, placeholder: string, initial = ''): Promise<string | null> {
  return new Promise((resolve) => {
    let text = initial
    Modal.confirm({
      title,
      content: <Input.TextArea autoFocus defaultValue={initial} placeholder={placeholder} maxLength={512} onChange={(value) => { text = value }} />,
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

/**
 * The approver's decision buttons sit in the attachment card's header (next to the files being reviewed). The approval progress is
 * the last column of the attachment table; approvers are not reassigned from here.
 */
function ApprovalPanel({ detail, onChanged, toolbarHost }: { detail: TransferDetail; onChanged: (next: TransferDetail) => void; toolbarHost: HTMLElement | null }) {
  const { api, userId } = useOem()
  const approval = detail.approval as ApprovalInfo
  const [deciding, setDeciding] = useState<'approve' | 'reject' | null>(null)
  const decidingRef = useRef(false)

  const myTask = approval.nodes.flatMap((node) => node.tasks).find((task) => task.status === 'PENDING' && task.approverUserId === userId)

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
  const refreshAggregate = async () => {
    const latest = await api.transfer(detail.summary.id).catch(() => null)
    if (latest) onChanged(latest)
    return latest
  }
  const approve = (task: ApprovalTask) => decide('approve', async () => {
    try {
      onChanged(await api.approve(task.id, task.version))
    } catch (error) {
      if (isVersionConflict(error)) await refreshAggregate()
      throw error
    }
    Message.success('已审批通过')
    return true
  })
  const reject = (task: ApprovalTask) => decide('reject', async () => {
    let version = task.version
    let draft = ''
    for (;;) {
      const text = await promptText('驳回传递单', '请输入驳回原因（发送人将收到通知）', draft)
      if (!text) return false
      draft = text
      try {
        onChanged(await api.reject(task.id, version, text))
        Message.success('已驳回')
        return true
      } catch (error) {
        if (!isVersionConflict(error)) throw error
        const latest = await refreshAggregate()
        const fresh = latest?.approval?.nodes.flatMap((node) => node.tasks).find((item) => item.id === task.id)
        if (fresh) version = fresh.version
      }
    }
  })
  const decisionButtons = myTask && toolbarHost ? createPortal(
    <Space size={8}>
      <Button type="primary" size="small" loading={deciding === 'approve'} disabled={deciding !== null}
        onClick={() => void approve(myTask)}>审批通过</Button>
      <Button status="danger" size="small" loading={deciding === 'reject'} disabled={deciding !== null}
        onClick={() => void reject(myTask)}>驳回</Button>
    </Space>,
    toolbarHost,
  ) : null
  return (
    <>
    {decisionButtons}
    </>
  )
}

/** 审批列只留状态和时间：时间取最近一次审批处理的时间，还没处理过就不显示。 */
function ApprovalProgress({ approval, status }: { approval: ApprovalInfo; status: ApprovalStatus | null }) {
  const decidedAt = approval.nodes.flatMap((node) => node.tasks).map((task) => task.decidedAt)
    .filter((value): value is string => Boolean(value)).sort().pop()
  return (
    <Space direction="vertical" size={2} align="center">
      <ApprovalTag value={status} />
      {decidedAt && <Typography.Text type="secondary" style={{ fontSize: 12 }}>{fmtTime(decidedAt)}</Typography.Text>}
    </Space>
  )
}

function TransferFilesTable({ files, approval, approvalStatus, canEdit, toolbarHost, onDownload, onPreview, onRemove }: {
  files: TransferFile[]
  /** 内部账号看得到：审批进度放在附件表的最后一列（整张传递单一份，合并成一格）。 */
  approval?: ApprovalInfo | null
  approvalStatus?: ApprovalStatus | null
  canEdit: boolean
  /** 附件卡标题右侧的挂载点：“显示列”放在那里，不再单占一行。 */
  toolbarHost: HTMLElement | null
  onDownload: (file: TransferFile) => void
  onPreview: (file: TransferFile) => void
  onRemove: (file: TransferFile) => Promise<void>
}) {
  const { userId, realm } = useOem()
  const tableHostRef = useRef<HTMLDivElement>(null)
  const columns = [
    {
      key: 'name', title: '文件名', dataIndex: 'originalName', ellipsis: true,
      render: (value: string) => <span title={value}>{value}</span>,
    },
    { key: 'size', title: '大小', render: (_: unknown, file: TransferFile) => fmtSize(file.sizeBytes) },
    {
      key: 'validation', title: '文件校验',
      render: (_: unknown, file: TransferFile) => (
        <Space direction="vertical" size={0}>
          <ValidationTag value={file.validationStatus} />
          {file.validationStatus !== 'VALID' && file.validationMessage && (
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>{file.validationMessage}</Typography.Text>
          )}
        </Space>
      ),
    },
    { key: 'payload', title: '存储', render: (_: unknown, file: TransferFile) => <PayloadTag value={file.payloadStatus} /> },
    {
      key: 'receipt', title: '接收 / 删除',
      render: (_: unknown, file: TransferFile) => (
        <Space direction="vertical" size={0} style={{ fontSize: 12 }}>
          {file.firstRecipientDownloadAt && <span>首次接收：{fmtTime(file.firstRecipientDownloadAt)}</span>}
          {file.purgedAt ? <span>已删除：{fmtTime(file.purgedAt)}</span> : file.purgeDueAt && <span>计划删除：{fmtTime(file.purgeDueAt)}</span>}
        </Space>
      ),
    },
    {
      key: 'actions', title: '操作',
      // 只有可编辑时才有“移除”位：没有这个位就不占宽度，否则隐藏的空位会把可见按钮挤偏、和列头对不齐。
      render: (_: unknown, file: TransferFile) => actionSlots([
        file.downloadable && <Button key="download" size="small" icon={<IconDownload />} onClick={() => onDownload(file)}>下载</Button>,
        file.downloadable && oemPreviewKind(file.ext) && file.sizeBytes <= OEM_PREVIEW_MAX_BYTES && (
          <Button key="preview" size="small" icon={<IconEye />} onClick={() => onPreview(file)}>预览</Button>
        ),
        ...(canEdit ? [
          <Popconfirm key="remove" title="移除该附件？" onOk={() => onRemove(file)}><Button size="small" status="danger">移除</Button></Popconfirm>,
        ] : []),
      ], 'oem-file'),
    },
    ...(approval ? [{
      key: 'approval', title: '审批',
      onCell: (_: TransferFile, index: number) => ({ rowSpan: index === 0 ? files.length : 0 }),
      render: () => <ApprovalProgress approval={approval} status={approvalStatus ?? null} />,
    }] : []),
  ]
  const fileColumnSpecs = approval ? [...OEM_TRANSFER_FILE_COLUMNS, OEM_APPROVAL_COLUMN] : OEM_TRANSFER_FILE_COLUMNS
  const fileTableId = realm === 'oem' ? 'oem-portal-transfer-files' : 'oem-transfer-files'
  // 文件名常很长：屏幕有余量时多出的宽度都给文件名，其余列保持设定宽度。
  const table = useAdjustableColumns(fileTableId, userId, fileColumnSpecs, columns, 'name', tableHostRef, true, true)
  return (
    <>
      {toolbarHost && createPortal(
        <ColumnVisibilityPicker
          specs={fileColumnSpecs}
          hidden={table.hidden}
          onToggle={table.toggle}
          onShowAll={table.showAll}
          onHideOptional={table.hideOptional}
          onReset={table.reset}
        />,
        toolbarHost,
      )}
      <ColumnResizeProvider actions={table.actions}>
        <div ref={tableHostRef} className={`${table.hostClassName} page-table-host`}>
          <Table
            className={`page-table ${table.tableClassName}`}
            rowKey="id"
            data={files}
            pagination={false}
            columns={table.columns}
            components={table.components}
            scroll={{ x: table.scrollX, y: 240 }}
          />
        </div>
      </ColumnResizeProvider>
    </>
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
  const cancelLocked = useRef(false)
  const [uploadBusy, setUploadBusy] = useState(false)
  const [sending, setSending] = useState(false)
  const [cancelling, setCancelling] = useState(false)
  const [previewing, setPreviewing] = useState<TransferFile | null>(null)
  const [filesToolbar, setFilesToolbar] = useState<HTMLElement | null>(null)
  const [notifyUserIds, setNotifyUserIds] = useState<number[]>([])
  const [signerName, setSignerName] = useState('')
  const recipientChoices = useRecipientChoices()
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
    const message = transferClosedNotice(next.summary)
    if (!message) return
    Message.info(message)
    if (next.summary.lifecycleStatus === 'ABANDONED') navigate(`${base}/transfers`, { replace: true })
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
      // A vendor names the company staff to notify; required whenever anyone can be notified.
      const choosing = realm === 'oem' && latest.summary.direction === 'OEM_TO_INTERNAL'
      if (choosing && (recipientChoices.data?.length ?? 0) > 0 && notifyUserIds.length === 0) {
        Message.warning('请选择要通知的公司人员')
        return
      }
      if (choosing && !signerName.trim()) {
        Message.warning('请填写签名')
        return
      }
      try {
        const submitted = choosing
          ? await api.send(id, latest.summary.version, notifyUserIds, signerName)
          : await api.send(id, latest.summary.version)
        setDetail(submitted)
        Message.success(submitted.summary.lifecycleStatus === 'RELEASED'
          ? '文件已发送'
          : submitted.summary.direction === 'INTERNAL_TO_OEM' ? '已提交审批，审批完成后将自动发送' : '文件正在处理中')
      } catch (error) {
        const checked = await api.transfer(id)
        if (checked.summary.lifecycleStatus === 'DRAFT') {
          if (isVersionConflict(error)) setDetail(checked)
          throw error
        }
        handleNoLongerDraft(checked)
      }
    } finally {
      sendLocked.current = false
      setSending(false)
    }
  }
  const remove = async () => {
    try {
      await api.deleteDraft(id, summary.version)
    } catch (error) {
      if (isVersionConflict(error)) {
        const latest = await api.transfer(id).catch(() => null)
        if (latest) setDetail(latest)
      }
      throw error
    }
    Message.success('草稿已删除')
    navigate(`${base}/transfers`)
  }
  const cancel = async () => {
    if (cancelLocked.current) return
    cancelLocked.current = true
    setCancelling(true)
    let version = summary.version
    let draft = ''
    try {
      for (;;) {
        const reason = await promptText('终止传递单', '请输入终止原因（发送人将收到通知）', draft)
        if (!reason) return
        draft = reason
        try {
          const next = await api.cancel(id, reason, version)
          Message.success('传递已终止')
          if (recoveryScoped) {
            navigate(`${base}/approvals?tab=blocked`, { replace: true })
            return
          }
          setDetail(next)
          return
        } catch (error) {
          if (!isVersionConflict(error)) return
          const latest = await api.transfer(id).catch(() => null)
          if (latest) {
            version = latest.summary.version
            setDetail(latest)
          }
        }
      }
    } finally {
      cancelLocked.current = false
      setCancelling(false)
    }
  }
  const download = (file: TransferFile) => downloadOemFile(api, file.id)
  const preview = (file: TransferFile) => {
    // PDF, images, Excel, PPTX and Word (.docx) open in-page with the viewer watermark; other formats are download-only.
    if (oemPreviewKind(file.ext)) setPreviewing(file)
  }
  const removeFile = async (file: TransferFile) => {
    await api.removeFile(file.id)
    await load()
  }

  return (
    <div className="oem-transfer-detail">
      <Card
        title={<Space><Button type="text" icon={<IconLeft />} onClick={() => navigate(`${base}/transfers`)} />{summary.title}
          <Counterpart direction={summary.direction} companyName={summary.companyName} realm={realm} />
          <LifecycleTag value={summary.lifecycleStatus} />
          {summary.direction === 'INTERNAL_TO_OEM' && realm === 'internal' && <ApprovalTag value={summary.approvalStatus} />}</Space>}
        extra={
          <Space>
            {caps.canSend && realm === 'oem' && (recipientChoices.data?.length ?? 0) > 0 && (
              <RecipientChoiceSelect choices={recipientChoices.data ?? []} value={notifyUserIds} onChange={setNotifyUserIds}
                disabled={sending} style={{ width: 260 }} />
            )}
            {caps.canSend && realm === 'oem' && (
              <Input aria-label="签名" placeholder="签名（提供人姓名）" maxLength={32} value={signerName} onChange={setSignerName}
                disabled={sending} style={{ width: 180 }} />
            )}
            {caps.canSend && (
              <Button type="primary" loading={sending} disabled={detail.files.length === 0 || failedFiles || uploadBusy}
                onClick={() => void send().catch(() => undefined)}>{summary.direction === 'INTERNAL_TO_OEM' ? '提交审批' : '发送文件'}</Button>
            )}
            {caps.canDelete && (
              <Popconfirm title="删除草稿后附件将被清理，确定删除？" onOk={remove}>
                <Button status="danger">删除草稿</Button>
              </Popconfirm>
            )}
            {can.recover && summary.lifecycleStatus === 'SEALED' && <Button status="danger" loading={cancelling} onClick={() => void cancel()}>终止传递</Button>}
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
          <Alert type="warning" style={{ marginBottom: 12 }} content={`审批异常：${summary.approvalBlockedReason}`} />
        )}
        {detail.closedReason && <Alert type="warning" style={{ marginBottom: 12 }} content={`结束原因：${detail.closedReason}`} />}
        {/* 带边框的信息表：每行三组“标签 + 内容”，说明占两格；删除策略不在这里展示。 */}
        <Descriptions
          className="oem-transfer-meta"
          border
          size="small"
          column={{ xs: 1, md: 2, xl: 3 }}
          data={[
            {
              label: '发送人',
              value: summary.signerName ? `${summary.signerName}（${summary.sender.realName}）`
                : summary.sender.employeeNo ? `${summary.sender.realName}（${summary.sender.employeeNo}）` : summary.sender.realName,
            },
            { label: '提交时间', value: summary.sentAt ? fmtTime(summary.sentAt) : '-' },
            { label: '发送时间', value: summary.releasedAt ? fmtTime(summary.releasedAt) : '-' },
            { label: '最晚保留至', value: detail.expiresAt ? fmtTime(detail.expiresAt) : '-' },
            ...(detail.description ? [{ label: '说明', span: 2, value: detail.description }] : []),
          ]}
        />
      </Card>

      <Card className="oem-transfer-files-card" title="附件" extra={<span ref={setFilesToolbar} />}>
        {caps.canEdit && <div style={{ marginBottom: 16 }}><OemUploader ref={uploader} transferId={id} disabled={sending}
          onBusyChange={setUploadBusy} onUploaded={() => void load()} /></div>}
        {can.recover && !caps.canReadContent && detail.files.length === 0 ? (
          <Alert type="info" content="异常处置权限不包含附件内容。" />
        ) : detail.files.length === 0 ? <Empty description="暂无附件" /> : (
          <TransferFilesTable
            files={detail.files}
            approval={realm === 'internal' ? detail.approval : null}
            approvalStatus={summary.approvalStatus}
            canEdit={caps.canEdit}
            toolbarHost={filesToolbar}
            onDownload={(file) => void download(file).catch(() => undefined)}
            onPreview={(file) => void preview(file)}
            onRemove={removeFile}
          />
        )}
        {summary.direction === 'OEM_TO_INTERNAL' || realm === 'oem' ? null : summary.lifecycleStatus === 'SEALED' && !caps.canReadContent && (
          <Typography.Text type="secondary">附件在文件校验与审批完成、正式发布前仅发送人和审批人可查看。</Typography.Text>
        )}
      </Card>

      {detail.approval && realm === 'internal' && <ApprovalPanel detail={detail} onChanged={setDetail} toolbarHost={filesToolbar} />}
      <OemFilePreview file={previewing} onClose={() => setPreviewing(null)} />
    </div>
  )
}
