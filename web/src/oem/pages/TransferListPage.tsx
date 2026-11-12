import { useCallback, useMemo, useRef, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Alert, Button, Card, Checkbox, DatePicker, Form, Input, Message, Modal, Radio, Select, Space, Table, Tabs, TreeSelect } from '@arco-design/web-react'
import { IconDownload, IconEye, IconUpload } from '@arco-design/web-react/icon'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { fmtSize, fmtTime } from '../../api/types'
import { isVersionConflict } from '../../api/versionConflict'
import { oemRequestError } from '../api/requestError'
import type { CompanyOption, DepartmentNode, TransferFile, TransferSummary } from '../api/types'
import { downloadOemFile } from '../components/downloadOemFile'
import OemFilePreview from '../components/OemFilePreview'
import { OEM_PREVIEW_MAX_BYTES, oemPreviewKind } from '../components/oemPreview'
import OemUploader, { type OemUploaderHandle } from '../components/OemUploader'
import { ApprovalTag, LifecycleTag, ValidationTag } from '../components/StatusTags'
import { LIFECYCLE_OPTIONS, transferClosedNotice } from '../components/statusLabels'
import Counterpart from '../components/Counterpart'
import { useCan, useOem } from '../OemContext'
import { useLatestRequest, useLoadEffect } from '../useLoadEffect'
import { ColumnResizeProvider, ColumnVisibilityPicker } from '../../components/columnResize'
import { type ColumnLayoutSpec } from '../../components/tableColumnLayout'
import { useAdjustableColumns } from '../../components/useAdjustableColumns'
import EnterSubmit from '../../components/EnterSubmit'

const OEM_TRANSFER_COLUMNS: readonly ColumnLayoutSpec[] = [
  { key: 'title', title: '文件', defaultWidth: 240, minWidth: 140, locked: true },
  // 往来对象：内部看到厂商名称，厂商门户看到“SAA”；箭头和动词表示发出或收到。
  { key: 'counterpart', title: '往来厂商', defaultWidth: 240, minWidth: 140 },
  { key: 'sender', title: '发送人', defaultWidth: 104, minWidth: 88 },
  { key: 'status', title: '状态', defaultWidth: 180, minWidth: 140 },
  { key: 'files', title: '附件', defaultWidth: 120, minWidth: 96 },
  { key: 'purgeDueAt', title: '到期时间', defaultWidth: 170, minWidth: 140 },
  { key: 'sentAt', title: '提交时间', defaultWidth: 170, minWidth: 140 },
  { key: 'actions', title: '操作', defaultWidth: 176, minWidth: 160, locked: true },
]
// 厂商门户里往来对象总是“SAA”，列名不写“厂商”。
const PORTAL_TRANSFER_COLUMNS: readonly ColumnLayoutSpec[] = OEM_TRANSFER_COLUMNS
  .map((spec) => spec.key === 'counterpart' ? { ...spec, title: '往来' } : spec)

const NO_RECEIVING_ACCOUNT_REASON = '该厂商没有启用的登录账号，请先新增或启用账号后再发送'

function CreateTransferModal({ visible, onClose }: { visible: boolean; onClose: () => void }) {
  const { api, realm, base, queryScope } = useOem()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [form] = Form.useForm()
  const uploader = useRef<OemUploaderHandle>(null)
  const submitLocked = useRef(false)
  const [companies, setCompanies] = useState<CompanyOption[]>([])
  const [companyOptionsLoading, setCompanyOptionsLoading] = useState(false)
  const [companyOptionsError, setCompanyOptionsError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [fileCount, setFileCount] = useState(0)
  const [draftId, setDraftId] = useState<number | null>(null)
  const [submitError, setSubmitError] = useState<string | null>(null)
  const [createOutcomeUnknown, setCreateOutcomeUnknown] = useState(false)
  /** Set when an existing draft's recipient can no longer receive files. */
  const [recipientBlocked, setRecipientBlocked] = useState<string | null>(null)
  const [discarding, setDiscarding] = useState(false)
  const beginCompanyRequest = useLatestRequest()

  const loadCompanyOptions = useCallback(async (): Promise<CompanyOption[]> => {
    const isLatest = beginCompanyRequest()
    setCompanyOptionsLoading(true)
    setCompanyOptionsError(null)
    try {
      const options = await api.companyOptions()
      if (isLatest()) setCompanies(options)
      return options
    } catch (error) {
      const message = oemRequestError(error, '目标 ODM/OEMA 厂商加载失败，请稍后重试')
      // Keep the previous options so the chosen vendor still shows its name, not a bare id.
      if (isLatest()) setCompanyOptionsError(message)
      throw new Error(message)
    } finally {
      if (isLatest()) setCompanyOptionsLoading(false)
    }
  }, [api, beginCompanyRequest])

  const loadInitialCompanies = useCallback(
    () => (visible && realm === 'internal' ? loadCompanyOptions() : undefined),
    [visible, realm, loadCompanyOptions],
  )
  useLoadEffect(loadInitialCompanies)

  const finish = (id: number, message: string) => {
    void queryClient.invalidateQueries({ queryKey: ['oem', ...queryScope, 'transfers'] })
    Message.success(message)
    onClose()
    navigate(`${base}/transfers/${id}`)
  }

  const leaveFinishedDraft = (current: Awaited<ReturnType<typeof api.transfer>>): boolean => {
    const message = transferClosedNotice(current.summary)
    if (!message) return false
    void queryClient.invalidateQueries({ queryKey: ['oem', ...queryScope, 'transfers'] })
    const status = current.summary.lifecycleStatus
    Message.info(message)
    onClose()
    navigate(status === 'ABANDONED' ? `${base}/transfers` : `${base}/transfers/${current.summary.id}`)
    return true
  }

  const submit = async () => {
    if (submitLocked.current || createOutcomeUnknown) return
    submitLocked.current = true
    setSaving(true)
    setSubmitError(null)
    setRecipientBlocked(null)
    try {
      if (!uploader.current?.hasFiles()) {
        Message.warning('请先选择要发送的文件')
        return
      }
      let activeDraftId = draftId
      if (activeDraftId) {
        // A previous send response may have been lost. Confirm the draft is still writable
        // before applying the current recipient-account check or resuming uploads.
        const current = await api.transfer(activeDraftId)
        if (leaveFinishedDraft(current)) return
      }
      let oemCompanyId: number | undefined
      if (realm === 'internal') {
        oemCompanyId = (await form.validate() as { oemCompanyId: number }).oemCompanyId
        // A draft can stay open while its recipient account is disabled. Refresh
        // the options before resuming uploads so an invalid recipient never gets
        // another upload attempt; the server still checks again when sending.
        // A failed refresh is already shown next to the vendor field, with a retry action.
        const latestCompanies = draftId !== null ? await loadCompanyOptions().catch(() => null) : companies
        if (!latestCompanies) return
        const company = latestCompanies.find((item) => item.id === oemCompanyId)
        if (!company?.canReceive) {
          const reason = company?.unavailableReason || NO_RECEIVING_ACCOUNT_REASON
          // The draft is bound to its vendor: offer a way out instead of repeating the same error.
          if (draftId !== null) setRecipientBlocked(reason)
          else setSubmitError(reason)
          return
        }
      }
      if (!activeDraftId) {
        let created
        try {
          created = await api.createTransfer(realm === 'internal' ? { oemCompanyId } : {})
        } catch (error) {
          const status = (error as { response?: { status?: number } })?.response?.status
          // A transport/5xx failure may have happened after the non-idempotent create committed.
          // Do not create another draft until the sender has checked the list.
          if (!status || status >= 500) setCreateOutcomeUnknown(true)
          throw error
        }
        activeDraftId = created.summary.id
        setDraftId(activeDraftId)
      }
      const uploaded = await uploader.current.uploadAll(activeDraftId)
      if (!uploaded) {
        setSubmitError('部分文件上传失败。已上传成功的文件不会重复，请修复失败项后再次提交。')
        return
      }

      // Upload merge updates the draft version. Always read the aggregate again before send.
      const latest = await api.transfer(activeDraftId)
      if (leaveFinishedDraft(latest)) return
      try {
        const submitted = await api.send(activeDraftId, latest.summary.version)
        const lifecycle = submitted.summary.lifecycleStatus
        if (lifecycle !== 'SEALED' && lifecycle !== 'RELEASED') {
          leaveFinishedDraft(submitted)
          return
        }
        finish(activeDraftId, lifecycle === 'RELEASED'
          ? '文件已发送'
          : realm === 'internal' ? '已提交审批，审批完成后将自动发送' : '已提交，文件处理完成后将自动发送')
        return
      } catch (error) {
        // A response can be lost after the server commits. Read state before allowing a retry,
        // so a non-draft transfer is never submitted twice.
        const checked = await api.transfer(activeDraftId)
        if (leaveFinishedDraft(checked)) return
        throw error
      }
    } catch (error) {
      setSubmitError(oemRequestError(error, '操作失败，请重试'))
    } finally {
      submitLocked.current = false
      setSaving(false)
    }
  }

  const openDraft = (id: number) => {
    void queryClient.invalidateQueries({ queryKey: ['oem', ...queryScope, 'transfers'] })
    onClose()
    navigate(`${base}/transfers/${id}`)
  }

  /** Deletes a draft bound to an unavailable vendor so the sender can choose another one. */
  const discardDraft = async () => {
    const id = draftId
    if (!id || submitLocked.current) return
    submitLocked.current = true
    setDiscarding(true)
    try {
      const current = await api.transfer(id)
      if (leaveFinishedDraft(current)) return
      await api.deleteDraft(id, current.summary.version)
      void queryClient.invalidateQueries({ queryKey: ['oem', ...queryScope, 'transfers'] })
      // The deleted draft took its uploads with it: the local files start over on the next draft.
      uploader.current?.resetForNewDraft()
      setDraftId(null)
      setRecipientBlocked(null)
      setSubmitError(null)
      form.resetFields(['oemCompanyId'])
      Message.success('草稿已删除，请重新选择厂商后提交')
    } catch (error) {
      if (isVersionConflict(error)) {
        const checked = await api.transfer(id).catch(() => null)
        if (checked && leaveFinishedDraft(checked)) return
      }
      setSubmitError(`删除草稿失败：${oemRequestError(error, '操作失败，请重试')}`)
    } finally {
      submitLocked.current = false
      setDiscarding(false)
    }
  }

  const close = () => {
    if (saving || discarding) return
    if (createOutcomeUnknown) {
      void queryClient.invalidateQueries({ queryKey: ['oem', ...queryScope, 'transfers'] })
      onClose()
      return
    }
    if (!draftId) {
      onClose()
      return
    }
    Modal.confirm({
      title: '草稿已保留',
      content: '已创建的草稿和成功上传的文件会保留，不会自动删除。你可以现在打开草稿继续，或稍后从列表进入。',
      okText: '打开草稿',
      cancelText: '留在列表',
      onOk: () => openDraft(draftId),
      onCancel: () => {
        void queryClient.invalidateQueries({ queryKey: ['oem', ...queryScope, 'transfers'] })
        onClose()
      },
    })
  }

  const actionText = realm === 'internal' ? '提交审批' : '发送文件'
  const busy = saving || discarding
  return (
    <Modal
      title="发送文件"
      visible={visible}
      onCancel={close}
      onOk={submit}
      okText={actionText}
      confirmLoading={saving}
      closable={!busy}
      maskClosable={!busy}
      escToExit={!busy}
      cancelButtonProps={{ disabled: busy }}
      okButtonProps={{ disabled: fileCount === 0 || createOutcomeUnknown || discarding || recipientBlocked !== null
        || (realm === 'internal' && (companyOptionsLoading || !!companyOptionsError || !companies.some((company) => company.canReceive))) }}
      unmountOnExit
    >
      <Form onSubmit={() => void submit()} form={form} layout="vertical">
        {realm === 'internal' && (
          <Form.Item field="oemCompanyId" label="目标 ODM/OEMA 厂商" extra="至少需要一个启用的 ODM/OEMA 登录账号才能发送。" rules={[{ required: true, message: '请选择厂商' }]}>
            <Select
              showSearch
              disabled={busy || draftId !== null || companyOptionsLoading || !!companyOptionsError}
              placeholder="选择厂商"
              options={companies.map((company) => ({
                value: company.id,
                label: company.canReceive ? company.name : `${company.name}（${company.unavailableReason || NO_RECEIVING_ACCOUNT_REASON}）`,
                disabled: !company.canReceive,
              }))}
              filterOption={(input, option) => String(option?.props?.children ?? '').includes(input)}
            />
          </Form.Item>
        )}
        {realm === 'internal' && companyOptionsLoading && (
          <Alert type="info" style={{ marginBottom: 12 }} content="正在加载可发送的 ODM/OEMA 厂商…" />
        )}
        {realm === 'internal' && companyOptionsError && (
          <Alert type="error" style={{ marginBottom: 12 }} content={(
            <Space size="small">
              <span>目标 ODM/OEMA 厂商加载失败：{companyOptionsError}</span>
              <Button type="text" size="small" loading={companyOptionsLoading} disabled={saving || companyOptionsLoading}
                onClick={() => void loadCompanyOptions().catch(() => undefined)}>刷新厂商</Button>
            </Space>
          )} />
        )}
        {realm === 'internal' && !companyOptionsLoading && !companyOptionsError && companies.length === 0 && (
          <Alert type="warning" style={{ marginBottom: 12 }} content={(
            <Space size="small">
              <span>暂无可发送的 ODM/OEMA 厂商，请先启用厂商后再试。</span>
              <Button type="text" size="small" loading={companyOptionsLoading} disabled={saving || companyOptionsLoading}
                onClick={() => void loadCompanyOptions().catch(() => undefined)}>刷新厂商</Button>
            </Space>
          )} />
        )}
        {realm === 'internal' && !companyOptionsLoading && !companyOptionsError && companies.length > 0 && !companies.some((company) => company.canReceive) && (
          <Alert type="warning" style={{ marginBottom: 12 }} content={(
            <Space size="small">
              <span>当前没有可接收的 ODM/OEMA 厂商，请先新增或启用至少一个 ODM/OEMA 登录账号后再发送。</span>
              <Button type="text" size="small" loading={companyOptionsLoading} disabled={saving || companyOptionsLoading}
                onClick={() => void loadCompanyOptions().catch(() => undefined)}>刷新厂商</Button>
            </Space>
          )} />
        )}
        <Form.Item label="文件" required>
          <OemUploader ref={uploader} deferred disabled={busy} onQueueChange={setFileCount} />
        </Form.Item>
        {recipientBlocked && draftId && (
          <Alert type="warning" style={{ marginBottom: 12 }} content={(
            <Space direction="vertical" size="small">
              <span>草稿的目标厂商当前无法接收文件：{recipientBlocked}</span>
              <span>草稿已绑定该厂商。可在厂商启用账号后打开草稿继续，或删除草稿后重新选择厂商。</span>
              <Space size="small">
                <Button size="small" disabled={busy} onClick={() => openDraft(draftId)}>打开草稿</Button>
                <Button size="small" status="danger" loading={discarding} disabled={saving}
                  onClick={() => void discardDraft()}>删除草稿并重新选择</Button>
              </Space>
            </Space>
          )} />
        )}
        {submitError && <Alert type="error" content={submitError} />}
        {createOutcomeUnknown && (
          <Alert type="warning" style={{ marginTop: 12 }}
            content="草稿创建结果未知。请关闭窗口并从列表确认，避免重复创建传递单。" />
        )}
        {draftId && !busy && !recipientBlocked && (
          <Alert type="info" style={{ marginTop: 12 }} content="草稿已创建。再次提交只会继续失败或未完成的文件，不会重复上传成功文件。" />
        )}
      <EnterSubmit /></Form>
    </Modal>
  )
}

const canPreview = (file: TransferFile) => Boolean(oemPreviewKind(file.ext)) && file.sizeBytes <= OEM_PREVIEW_MAX_BYTES

/** Lists a transfer's readable files when there is more than one: each previews or starts its own browser download. */
function DownloadPicker({ title, files, onPreview, onClose }: {
  title: string; files: TransferFile[]; onPreview: (file: TransferFile) => void; onClose: () => void
}) {
  const { api } = useOem()
  const [busyId, setBusyId] = useState<number | null>(null)
  const download = async (file: TransferFile) => {
    setBusyId(file.id)
    try {
      await downloadOemFile(api, file.id)
    } catch {
      // The HTTP client already shows the reason.
    } finally {
      setBusyId(null)
    }
  }
  return (
    <Modal title={title} visible footer={null} onCancel={onClose} unmountOnExit style={{ width: 640 }}>
      <Table
        rowKey="id"
        size="small"
        pagination={false}
        data={files}
        columns={[
          { key: 'name', title: '文件名', dataIndex: 'originalName', ellipsis: true },
          { key: 'size', title: '大小', width: 96, render: (_: unknown, file: TransferFile) => fmtSize(file.sizeBytes) },
          {
            key: 'actions', title: '操作', width: 176,
            render: (_: unknown, file: TransferFile) => (
              <Space size={8}>
                {canPreview(file) && (
                  <Button size="small" icon={<IconEye />} aria-label={`预览 ${file.originalName}`} onClick={() => onPreview(file)}>预览</Button>
                )}
                <Button size="small" icon={<IconDownload />} aria-label={`下载 ${file.originalName}`}
                  loading={busyId === file.id} disabled={busyId !== null && busyId !== file.id}
                  onClick={() => void download(file)}>下载</Button>
              </Space>
            ),
          },
        ]}
      />
    </Modal>
  )
}

/** 日期选择器给出本地日期（YYYY-MM-DD）；转成本地当天 0 点的时刻，止日取次日 0 点（不含）。 */
function dayStart(day: string, offsetDays = 0) {
  const date = new Date(`${day}T00:00:00`)
  date.setDate(date.getDate() + offsetDays)
  return date.toISOString()
}

// 停用的组织仍可选（用于查历史），但要标出来，和组织架构页的“禁用”一致。
function organizationTree(nodes: DepartmentNode[]): { key: string; value: string; title: string; children: ReturnType<typeof organizationTree> }[] {
  return nodes.map(node => ({
    key: String(node.id),
    value: String(node.id),
    title: node.status === 'DISABLED' ? `${node.name}（已禁用）` : node.name,
    children: organizationTree(node.children),
  }))
}

function hasDepartment(nodes: DepartmentNode[], id: number): boolean {
  return nodes.some(node => node.id === id || hasDepartment(node.children, id))
}

export default function TransferListPage() {
  const { api, realm, base, queryScope, userId } = useOem()
  const can = useCan()
  const navigate = useNavigate()
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [direction, setDirection] = useState('')
  // 从“厂商与账号”页的“传递记录”进入时带 ?companyId=，列表只显示与该厂商的往来。
  const [searchParams, setSearchParams] = useSearchParams()
  const companyParam = Number(searchParams.get('companyId'))
  const companyId = realm === 'internal' && Number.isSafeInteger(companyParam) && companyParam > 0 ? companyParam : undefined
  const departmentParam = Number(searchParams.get('departmentId'))
  const departmentId = realm === 'internal' && Number.isSafeInteger(departmentParam) && departmentParam > 0 ? departmentParam : undefined
  const changeDepartment = (value: string | undefined) => {
    setSearchParams((current) => {
      const next = new URLSearchParams(current)
      if (value) next.set('departmentId', value); else next.delete('departmentId')
      return next
    }, { replace: true })
    setPage(1)
  }
  const changeCompany = (value: number | undefined) => {
    setSearchParams((current) => {
      const next = new URLSearchParams(current)
      if (value) next.set('companyId', String(value)); else next.delete('companyId')
      return next
    }, { replace: true })
    setPage(1)
  }
  const [status, setStatus] = useState<string | undefined>()
  const [keyword, setKeyword] = useState('')
  const [mine, setMine] = useState(false)
  const [scope, setScope] = useState<'latest' | 'purged'>('latest')
  const [sender, setSender] = useState('')
  const [sentRange, setSentRange] = useState<[string, string] | undefined>()
  const [creating, setCreating] = useState(false)
  const [downloadingId, setDownloadingId] = useState<number | null>(null)
  const [picking, setPicking] = useState<{ title: string; files: TransferFile[] } | null>(null)
  const [previewing, setPreviewing] = useState<TransferFile | null>(null)
  const tableHostRef = useRef<HTMLDivElement>(null)

  const transfers = useQuery({
    queryKey: ['oem', ...queryScope, 'transfers', { page, pageSize, direction, status, keyword, mine, companyId, scope, sender, sentRange, departmentId }],
    queryFn: () => api.listTransfers({
      page, pageSize, direction: direction || undefined, status, keyword: keyword || undefined, mine, companyId, scope,
      sender: sender || undefined,
      departmentId,
      sentFrom: sentRange ? dayStart(sentRange[0]) : undefined,
      sentTo: sentRange ? dayStart(sentRange[1], 1) : undefined,
    }),
  })
  const companyOptions = useQuery({
    queryKey: ['oem', ...queryScope, 'company-options'],
    queryFn: () => api.companyOptions(),
    enabled: realm === 'internal',
    retry: false,
  })
  const departments = useQuery({
    queryKey: ['oem', ...queryScope, 'department-options'],
    queryFn: () => api.departments(),
    enabled: realm === 'internal',
    retry: false,
  })
  // 地址里的组织已不存在时补一个说明项，避免下拉框只显示一串编号（列表此时为空）。
  const departmentOptions = useMemo(() => {
    const tree = organizationTree(departments.data ?? [])
    if (!departmentId || !departments.data || hasDepartment(departments.data, departmentId)) return tree
    return [...tree, { key: String(departmentId), value: String(departmentId), title: `组织 #${departmentId}（已不存在）`, children: [] }]
  }, [departments.data, departmentId])
  const rows: TransferSummary[] = transfers.data?.list ?? []
  // 选项只含启用的厂商；从已停用厂商的“传递记录”进入时，用列表里的厂商名补一个选项，避免只显示编号。
  const companyFilterOptions = useMemo(() => {
    const options = companyOptions.data ?? []
    if (!companyId || options.some((company) => company.id === companyId)) return options
    const name = transfers.data?.list.find((row) => row.companyId === companyId)?.companyName ?? `厂商 #${companyId}`
    return [...options, { id: companyId, name, canReceive: false }]
  }, [companyId, companyOptions.data, transfers.data])
  const total = transfers.data?.total ?? 0
  // The row count can be stale; the detail's per-file flags decide what is offered. One file acts at once;
  // several open a picker with preview and download per file.
  const openRow = useCallback(async (row: TransferSummary, action: 'download' | 'preview') => {
    setDownloadingId(row.id)
    try {
      const detail = await api.transfer(row.id)
      const files = detail.files.filter((file) => file.downloadable)
      if (files.length === 0) {
        Message.warning('文件已不可下载')
        void transfers.refetch()
      } else if (files.length > 1) {
        setPicking({ title: detail.summary.title, files })
      } else if (action === 'download') {
        await downloadOemFile(api, files[0].id)
      } else if (canPreview(files[0])) {
        setPreviewing(files[0])
      } else {
        Message.info('该文件类型不支持在线预览，请下载后查看')
      }
    } catch {
      // The HTTP client already shows the reason.
    } finally {
      setDownloadingId(null)
    }
  }, [api, transfers])
  const transferColumns = useMemo(() => [
    {
      key: 'title', title: '文件', dataIndex: 'title', ellipsis: true,
      render: (value: string, row: TransferSummary) => (
        <Link className="table-link" to={`${base}/transfers/${row.id}`} title={value} onClick={(event) => event.stopPropagation()}>{value}</Link>
      ),
    },
    {
      key: 'counterpart', title: realm === 'internal' ? '往来厂商' : '往来',
      render: (_: unknown, row: TransferSummary) => <Counterpart direction={row.direction} companyName={row.companyName} realm={realm} />,
    },
    { key: 'sender', title: '发送人', render: (_: unknown, row: TransferSummary) => row.sender.realName },
    {
      key: 'status', title: '状态',
      render: (_: unknown, row: TransferSummary) => (
        <Space size={4} wrap>
          <LifecycleTag value={row.lifecycleStatus} />
          {row.direction === 'INTERNAL_TO_OEM' && row.lifecycleStatus === 'SEALED' && <ApprovalTag value={row.approvalStatus} />}
          {row.lifecycleStatus === 'SEALED' && row.validationSummary !== 'VALID' && <ValidationTag value={row.validationSummary} />}
        </Space>
      ),
    },
    { key: 'files', title: '附件', render: (_: unknown, row: TransferSummary) => `${row.fileCount} 个 / ${fmtSize(row.totalBytes)}` },
    { key: 'purgeDueAt', title: '到期时间', render: (_: unknown, row: TransferSummary) => (row.purgeDueAt ? fmtTime(row.purgeDueAt) : '-') },
    { key: 'sentAt', title: '提交时间', render: (_: unknown, row: TransferSummary) => (row.sentAt ? fmtTime(row.sentAt) : '-') },
    {
      key: 'actions', title: '操作',
      render: (_: unknown, row: TransferSummary) => row.downloadableCount > 0 && (
        <span className="oem-row-actions" role="presentation" onClick={(event) => event.stopPropagation()}><Space size={8}>
          {row.previewableCount > 0 && (
            <Button size="small" icon={<IconEye />} aria-label={`预览 ${row.title}`}
              disabled={downloadingId !== null} onClick={() => void openRow(row, 'preview')}>预览</Button>
          )}
          <Button size="small" icon={<IconDownload />} aria-label={`下载 ${row.title}`}
            loading={downloadingId === row.id} disabled={downloadingId !== null && downloadingId !== row.id}
            onClick={() => void openRow(row, 'download')}>下载</Button>
        </Space></span>
      ),
    },
  ], [base, realm, downloadingId, openRow])
  const tableId = realm === 'oem' ? 'oem-portal-transfers' : 'oem-transfers'
  const columnSpecs = realm === 'internal' ? OEM_TRANSFER_COLUMNS : PORTAL_TRANSFER_COLUMNS
  const table = useAdjustableColumns(tableId, userId, columnSpecs, transferColumns, 'title', tableHostRef)

  return (
    <Card
      className="page-card page-card--table"
      title={<h1 className="visually-hidden">文件传递单</h1>}
    >
      {realm === 'internal' && (
        // 一个厂商一个页签；“全部”包含所有厂商（含已停用厂商的历史记录）。
        <Tabs className="oem-vendor-tabs" aria-label="往来厂商" activeTab={companyId ? String(companyId) : 'all'}
          onChange={(key) => changeCompany(key === 'all' ? undefined : Number(key))}>
          <Tabs.TabPane key="all" title="全部" />
          {companyFilterOptions.map((company) => <Tabs.TabPane key={String(company.id)} title={company.name} />)}
        </Tabs>
      )}
      <div className="oem-scope-bar">
        <Tabs className="oem-scope-tabs" type="capsule" aria-label="传递单范围" activeTab={scope}
          onChange={(key) => { setScope(key as 'latest' | 'purged'); setPage(1) }}>
          <Tabs.TabPane key="latest" title="最新" />
          <Tabs.TabPane key="purged" title="历史（已删除）" />
        </Tabs>
        {can.createTransfer && <Button type="primary" icon={<IconUpload />} onClick={() => setCreating(true)}>发送文件</Button>}
      </div>
      <div className="page-toolbar responsive-toolbar">
        <Space wrap>
          {realm === 'internal' && (
            <Radio.Group type="button" value={direction} onChange={(value) => { setDirection(value); setPage(1) }}>
              <Radio value="">全部</Radio>
              <Radio value="INTERNAL_TO_OEM">SAA发出</Radio>
              <Radio value="OEM_TO_INTERNAL">ODM/OEMA 发来</Radio>
            </Radio.Group>
          )}
          <Select allowClear placeholder="状态" style={{ width: 140 }} value={status} options={LIFECYCLE_OPTIONS}
            onChange={(value) => { setStatus(value); setPage(1) }} />
          <Input.Search allowClear placeholder={realm === 'internal' ? '搜索文件或厂商' : '搜索传递记录'} style={{ width: 220 }} onSearch={(value) => { setKeyword(value); setPage(1) }} />
          <Input.Search allowClear aria-label="发送人" placeholder="发送人" style={{ width: 150 }} onSearch={(value) => { setSender(value.trim()); setPage(1) }} />
          {realm === 'internal' && <TreeSelect
            allowClear showSearch aria-label="发送人组织" placeholder="发送人组织" style={{ width: 190 }}
            treeData={departmentOptions} loading={departments.isFetching}
            filterTreeNode={(input, node) => String(node.props.title ?? '').toLowerCase().includes(input.trim().toLowerCase())}
            value={departmentId ? String(departmentId) : undefined}
            onChange={changeDepartment}
          />}
          <DatePicker.RangePicker aria-label="提交时间" style={{ width: 240 }} placeholder={['提交时间起', '提交时间止']}
            onChange={(value) => { setSentRange(value?.length === 2 ? [value[0], value[1]] : undefined); setPage(1) }} />
          <Checkbox checked={mine} onChange={(value) => { setMine(value); setPage(1) }}>只看我发送的</Checkbox>
          <ColumnVisibilityPicker
            specs={columnSpecs}
            hidden={table.hidden}
            onToggle={table.toggle}
            onShowAll={table.showAll}
            onHideOptional={table.hideOptional}
            onReset={table.reset}
          />
        </Space>
      </div>
      {realm === 'internal' && departments.isError && (
        <Alert type="error" style={{ marginBottom: 12 }} content="发送人组织加载失败，请重试"
          action={<Button size="small" loading={departments.isFetching} onClick={() => void departments.refetch()}>重新加载组织</Button>} />
      )}
      {transfers.isError && (
        <Alert type="error" style={{ marginBottom: 12 }}
          content={oemRequestError(transfers.error, '传递单列表加载失败，请重试')}
          action={<Button size="small" loading={transfers.isFetching} onClick={() => void transfers.refetch()}>重试</Button>} />
      )}
      <ColumnResizeProvider actions={table.actions}>
        <div ref={tableHostRef} className={`${table.hostClassName} page-table-host`}>
          <Table
            className={`page-table ${table.tableClassName}`}
            rowKey="id"
            scroll={{ x: table.scrollX, y: 'var(--page-table-scroll-y)' }}
            loading={transfers.isPending || transfers.isFetching}
            data={rows}
            columns={table.columns}
            components={table.components}
            onRow={(row) => ({ onClick: () => navigate(`${base}/transfers/${row.id}`), style: { cursor: 'pointer' } })}
            pagination={{ current: page, total, pageSize, showTotal: true, sizeCanChange: true, onChange: (nextPage, nextSize) => { setPage(nextPage); setPageSize(nextSize) } }}
          />
        </div>
      </ColumnResizeProvider>
      {creating && <CreateTransferModal visible onClose={() => setCreating(false)} />}
      {picking && <DownloadPicker title={picking.title} files={picking.files} onPreview={setPreviewing} onClose={() => setPicking(null)} />}
      <OemFilePreview file={previewing} onClose={() => setPreviewing(null)} />
    </Card>
  )
}
