import { useCallback, useMemo, useRef, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Alert, Button, Card, Checkbox, DatePicker, Form, Input, Message, Modal, Radio, Select, Space, Table, Tabs, TreeSelect } from '@arco-design/web-react'
import { IconDownload, IconEye, IconSearch, IconUpload } from '@arco-design/web-react/icon'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { fmtSize, fmtTime } from '../../api/types'
import { oemRequestError } from '../api/requestError'
import type { CompanyOption, DepartmentNode, TransferFile, TransferSummary } from '../api/types'
import { downloadOemFile } from '../components/downloadOemFile'
import OemFilePreview from '../components/OemFilePreview'
import { OEM_PREVIEW_MAX_BYTES, oemPreviewKind } from '../components/oemPreview'
import OemUploader, { type OemUploaderHandle } from '../components/OemUploader'
import { actionSlots } from '../../components/ActionSlots'
import RecipientChoiceSelect from '../components/RecipientChoiceSelect'
import { useRecipientChoices } from '../components/useRecipientChoices'
import { ApprovalTag, LifecycleTag, ValidationTag } from '../components/StatusTags'
import { LIFECYCLE_OPTIONS, transferClosedNotice } from '../components/statusLabels'
import Counterpart from '../components/Counterpart'
import { useCan, useOem } from '../OemContext'
import { useLatestRequest, useLoadEffect } from '../useLoadEffect'
import { ColumnResizeProvider } from '../../components/columnResize'
import { type ColumnLayoutSpec } from '../../components/tableColumnLayout'
import { useAdjustableColumns } from '../../components/useAdjustableColumns'
import EnterSubmit from '../../components/EnterSubmit'
import ResizableTable from '../../components/ResizableTable'
import { TABLE_PAGINATION } from '../../components/tablePagination'

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
  const beginCompanyRequest = useLatestRequest()
  const recipientChoices = useRecipientChoices()
  const choices = recipientChoices.data ?? []

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
    // A draft created by this very submit is removed again when anything fails, so a failed attempt leaves nothing behind.
    let createdHere: number | null = null
    try {
      if (!uploader.current?.hasFiles()) {
        Message.warning('请先选择要发送的文件')
        return
      }
      let activeDraftId = draftId
      if (activeDraftId) {
        // A leftover from a failed clean-up: if it already went through, show it; otherwise remove it and start over.
        const current = await api.transfer(activeDraftId)
        if (leaveFinishedDraft(current)) return
        if (!(await rollbackNewDraft(activeDraftId))) {
          setSubmitError('操作失败，请稍后重试')
          return
        }
        activeDraftId = null
      }
      let oemCompanyId: number | undefined
      // A vendor names the company staff to notify; when nobody can be notified it may still send (no mail).
      let recipientUserIds: number[] | undefined
      let signerName: string | undefined
      if (realm === 'oem') {
        const values = await form.validate().catch(() => null) as { recipientUserIds?: number[]; signerName?: string } | null
        if (!values) return
        recipientUserIds = values.recipientUserIds ?? []
        signerName = values.signerName
      }
      if (realm === 'internal') {
        // 校验未通过时由表单自己标红，不当作失败提示。
        const values = await form.validate().catch(() => null) as { oemCompanyId: number } | null
        if (!values) return
        oemCompanyId = values.oemCompanyId
        // A draft can stay open while its recipient account is disabled. Refresh
        // the options before resuming uploads so an invalid recipient never gets
        // another upload attempt; the server still checks again when sending.
        // A failed refresh is already shown next to the vendor field, with a retry action.
        const company = companies.find((item) => item.id === oemCompanyId)
        if (!company?.canReceive) {
          setSubmitError(company?.unavailableReason || NO_RECEIVING_ACCOUNT_REASON)
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
        createdHere = activeDraftId
        setDraftId(activeDraftId)
      }
      const uploaded = await uploader.current.uploadAll(activeDraftId)
      if (!uploaded) {
        if (createdHere !== null) await rollbackNewDraft(createdHere)
        setSubmitError('文件上传失败，请修复失败项后重新提交。')
        return
      }

      // Upload merge updates the draft version. Always read the aggregate again before send.
      const latest = await api.transfer(activeDraftId)
      if (leaveFinishedDraft(latest)) return
      try {
        const submitted = recipientUserIds
          ? await api.send(activeDraftId, latest.summary.version, recipientUserIds, signerName)
          : await api.send(activeDraftId, latest.summary.version)
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
      const message = oemRequestError(error, '操作失败，请重试')
      if (createdHere !== null) await rollbackNewDraft(createdHere)
      setSubmitError(createdHere !== null ? `${message}，请修正后重新提交` : message)
    } finally {
      submitLocked.current = false
      setSaving(false)
    }
  }

  /** Deletes the draft this submit created (with its uploads) and resets the local queue. False when it could not be removed. */
  const rollbackNewDraft = async (id: number): Promise<boolean> => {
    try {
      const current = await api.transfer(id)
      if (transferClosedNotice(current.summary)) return false
      await api.deleteDraft(id, current.summary.version)
      void queryClient.invalidateQueries({ queryKey: ['oem', ...queryScope, 'transfers'] })
      uploader.current?.resetForNewDraft()
      setDraftId(null)
      return true
    } catch {
      return false
    }
  }

  const close = () => {
    if (saving) return
    void queryClient.invalidateQueries({ queryKey: ['oem', ...queryScope, 'transfers'] })
    onClose()
  }

  const actionText = realm === 'internal' ? '提交审批' : '发送文件'
  const busy = saving
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
      okButtonProps={{ disabled: fileCount === 0 || createOutcomeUnknown 
        || (realm === 'internal' && (companyOptionsLoading || !!companyOptionsError || !companies.some((company) => company.canReceive))) }}
      unmountOnExit
    >
      <Form onSubmit={() => void submit()} form={form} layout="vertical">
        {realm === 'internal' && (
          <Form.Item field="oemCompanyId" label="目标 ODM/OEMA 厂商" rules={[{ required: true, message: '请选择厂商' }]}>
            <Select
              showSearch
              loading={companyOptionsLoading}
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
        {realm === 'oem' && choices.length > 0 && (
          <Form.Item field="recipientUserIds" label="通知公司人员"
            rules={[{ required: true, type: 'array', minLength: 1, message: '请选择要通知的公司人员' }]}>
            <RecipientChoiceSelect choices={choices} disabled={busy} />
          </Form.Item>
        )}
        {realm === 'oem' && (
          <Form.Item field="signerName" label="签名" rules={[{ required: true, message: '请填写签名' }]}>
            <Input aria-label="签名" placeholder="签名（提供人姓名）" maxLength={32} disabled={busy} />
          </Form.Item>
        )}
        {realm === 'oem' && recipientChoices.isSuccess && choices.length === 0 && (
          <Alert type="info" style={{ marginBottom: 12 }} content="没有可通知的公司人员，发送后不会邮件提醒。" />
        )}
        {realm === 'oem' && recipientChoices.isError && (
          <Alert type="error" style={{ marginBottom: 12 }} content="可通知人员加载失败"
            action={<Button size="small" onClick={() => void recipientChoices.refetch()}>重试</Button>} />
        )}
        <Form.Item label="文件" required>
          <OemUploader ref={uploader} deferred disabled={busy} onQueueChange={setFileCount} />
        </Form.Item>
        {submitError && <Alert type="error" content={submitError} />}
        {createOutcomeUnknown && (
          <Alert type="warning" style={{ marginTop: 12 }}
            content="提交结果未知。请关闭窗口并从列表确认，避免重复发送。" />
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
      <ResizableTable<TransferFile>
        tableId="oem-download-picker"
        growKey="name"
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

// 停用的组织仍可选（用于查历史），但要标出来，和组织架构页的“停用”一致。
function organizationTree(nodes: DepartmentNode[]): { key: string; value: string; title: string; children: ReturnType<typeof organizationTree> }[] {
  return nodes.map(node => ({
    key: String(node.id),
    value: String(node.id),
    title: node.status === 'DISABLED' ? `${node.name}（已停用）` : node.name,
    children: organizationTree(node.children),
  }))
}

function hasDepartment(nodes: DepartmentNode[], id: number): boolean {
  return nodes.some(node => node.id === id || hasDepartment(node.children, id))
}

const EMPTY_SELECTION: (string | number)[] = []
// Arco 自动加在最左边的勾选列宽度，要计入表格总宽，否则中等屏幕会多出横向滚动条。
const SELECTION_COLUMN_WIDTH = 40

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
  const [busyAction, setBusyAction] = useState<'download' | 'preview'>('download')
  // 勾选只跨页保留；换了筛选条件后，上一个视图里的行不再可见，也不该被批量下载带上，
  // 所以勾选记着它属于哪一组筛选条件，条件变了就视为空。
  const filterKey = JSON.stringify([scope, direction, status, keyword, mine, companyId, sender, sentRange, departmentId])
  const [selection, setSelection] = useState<{ key: string; ids: (string | number)[] }>({ key: '', ids: [] })
  const selectedIds = selection.key === filterKey ? selection.ids : EMPTY_SELECTION
  const setSelectedIds = (ids: (string | number)[]) => setSelection({ key: filterKey, ids })
  const [batching, setBatching] = useState(false)
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
    setBusyAction(action)
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
  // Batch download: every downloadable file of the ticked transfers, one after another (a single archive of large engineering files would not fit in memory).
  const downloadSelected = useCallback(async () => {
    setBatching(true)
    try {
      const files: TransferFile[] = []
      for (const id of selectedIds) {
        const detail = await api.transfer(Number(id))
        files.push(...detail.files.filter((file) => file.downloadable))
      }
      if (files.length === 0) {
        Message.warning('所选传递单没有可下载的文件')
        return
      }
      for (const [index, file] of files.entries()) {
        if (index > 0) await new Promise((resolve) => setTimeout(resolve, 600))
        await downloadOemFile(api, file.id)
      }
      Message.success(`已开始下载 ${files.length} 个文件`)
      setSelection({ key: '', ids: [] })
    } catch {
      // The HTTP client already shows the reason.
    } finally {
      setBatching(false)
    }
  }, [api, selectedIds])
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
    // A vendor-sent transfer shows the contact who signed it, with the shared login in brackets (as in Robot).
    {
      key: 'sender', title: '发送人',
      render: (_: unknown, row: TransferSummary) => row.signerName ? `${row.signerName}（${row.sender.realName}）` : row.sender.realName,
      filteredValue: sender ? [sender] : [],
      filterIcon: <IconSearch />,
      filterDropdown: ({ confirm }: { confirm?: Function }) => (
        <div className="oem-sender-filter" style={{ padding: 8 }}>
          <Input.Search allowClear autoFocus aria-label="筛选发送人" placeholder="输入发送人姓名或账号" defaultValue={sender} style={{ width: 200 }}
            onSearch={(value) => { setSender(value.trim()); setPage(1); confirm?.() }}
            onClear={() => { setSender(''); setPage(1); confirm?.() }} />
        </div>
      ),
    },
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
      render: (_: unknown, row: TransferSummary) => (
        <div className="oem-row-actions" role="presentation" onClick={(event) => event.stopPropagation()}>
          {/* Every row shows both buttons; one that cannot be used is greyed out, so the column lines up. */}
          {actionSlots([
            <Button key="preview" size="mini" icon={<IconEye />} aria-label={`预览 ${row.title}`}
              loading={downloadingId === row.id && busyAction === 'preview'} disabled={row.previewableCount === 0 || downloadingId !== null}
              onClick={() => void openRow(row, 'preview')}>预览</Button>,
            <Button key="download" size="mini" icon={<IconDownload />} aria-label={`下载 ${row.title}`}
              loading={downloadingId === row.id && busyAction === 'download'}
              disabled={row.downloadableCount === 0 || (downloadingId !== null && downloadingId !== row.id)}
              onClick={() => void openRow(row, 'download')}>下载</Button>,
          ], 'oem-file')}
        </div>
      ),
    },
  ], [base, realm, downloadingId, busyAction, openRow, sender])
  const tableId = realm === 'oem' ? 'oem-portal-transfers' : 'oem-transfers'
  const columnSpecs = realm === 'internal' ? OEM_TRANSFER_COLUMNS : PORTAL_TRANSFER_COLUMNS
  const table = useAdjustableColumns(tableId, userId, columnSpecs, transferColumns, 'title', tableHostRef, true, false, SELECTION_COLUMN_WIDTH)

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
          <Button icon={<IconDownload />} disabled={selectedIds.length === 0} loading={batching} onClick={() => void downloadSelected()}>
            批量下载{selectedIds.length > 0 ? `（${selectedIds.length}）` : ''}
          </Button>
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
      <ColumnResizeProvider actions={table.actions} picker={table.picker}>
        <div ref={tableHostRef} className={`${table.hostClassName} page-table-host`}>
          <Table
            className={`page-table ${table.tableClassName}`}
            rowKey="id"
            scroll={{ x: table.scrollX + SELECTION_COLUMN_WIDTH, y: 'var(--page-table-scroll-y)' }}
            loading={transfers.isPending || transfers.isFetching}
            data={rows}
            columns={table.columns}
            components={table.components}
            rowSelection={{
              fixed: table.picker.entries.some((entry) => entry.pinned),
              type: 'checkbox', selectedRowKeys: selectedIds, preserveSelectedRowKeys: true, onChange: (keys) => setSelectedIds(keys),
              checkboxProps: (row) => ({ disabled: row.downloadableCount === 0 }),
            }}
            onRow={(row) => ({
              onClick: (event) => {
                // Ticking a row's checkbox must not open its detail page.
                if ((event.target as HTMLElement).closest('.arco-table-cell-check-box, .arco-checkbox')) return
                navigate(`${base}/transfers/${row.id}`)
              },
              style: { cursor: 'pointer' },
            })}
            pagination={{ ...TABLE_PAGINATION, current: page, total, pageSize, onChange: (nextPage, nextSize) => { setPage(nextPage); setPageSize(nextSize) } }}
          />
        </div>
      </ColumnResizeProvider>
      {creating && <CreateTransferModal visible onClose={() => setCreating(false)} />}
      {picking && <DownloadPicker title={picking.title} files={picking.files} onPreview={setPreviewing} onClose={() => setPicking(null)} />}
      <OemFilePreview file={previewing} onClose={() => setPreviewing(null)} />
    </Card>
  )
}
