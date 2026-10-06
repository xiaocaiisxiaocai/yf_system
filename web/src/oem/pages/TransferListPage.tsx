import { useCallback, useEffect, useRef, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Alert, Button, Card, Checkbox, Form, Input, Message, Modal, Radio, Select, Space, Table, Typography } from '@arco-design/web-react'
import { IconUpload } from '@arco-design/web-react/icon'
import { useNavigate } from 'react-router-dom'
import { fmtSize, fmtTime } from '../../api/types'
import type { CompanyOption, TransferSummary } from '../api/types'
import OemUploader, { type OemUploaderHandle } from '../components/OemUploader'
import { ApprovalTag, LifecycleTag, ValidationTag } from '../components/StatusTags'
import { DIRECTION_LABEL, LIFECYCLE_OPTIONS } from '../components/statusLabels'
import { useCan, useOem } from '../OemContext'

const NO_RECEIVING_ACCOUNT_REASON = '该厂商没有启用的登录账号，请先新增或启用账号后再发送'

function requestError(error: unknown, fallback = '操作失败，请重试'): string {
  return (error as { response?: { data?: { message?: string } } })?.response?.data?.message
    || (error instanceof Error ? error.message : '')
    || fallback
}

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

  const loadCompanyOptions = useCallback(async (): Promise<CompanyOption[]> => {
    // Keep the initial state update off the effect's synchronous call stack.
    await Promise.resolve()
    setCompanyOptionsLoading(true)
    setCompanyOptionsError(null)
    try {
      const options = await api.companyOptions()
      setCompanies(options)
      return options
    } catch (error) {
      const message = requestError(error, '目标 OEM 厂商加载失败，请稍后重试')
      setCompanies([])
      setCompanyOptionsError(message)
      throw new Error(message)
    } finally {
      setCompanyOptionsLoading(false)
    }
  }, [api])

  useEffect(() => {
    if (!visible || realm !== 'internal') return
    const timer = window.setTimeout(() => { void loadCompanyOptions().catch(() => undefined) }, 0)
    return () => window.clearTimeout(timer)
  }, [visible, realm, loadCompanyOptions])

  const finish = (id: number, message: string) => {
    void queryClient.invalidateQueries({ queryKey: ['oem', ...queryScope, 'transfers'] })
    Message.success(message)
    onClose()
    navigate(`${base}/transfers/${id}`)
  }

  const leaveFinishedDraft = (current: Awaited<ReturnType<typeof api.transfer>>): boolean => {
    const status = current.summary.lifecycleStatus
    if (status === 'DRAFT') return false
    void queryClient.invalidateQueries({ queryKey: ['oem', ...queryScope, 'transfers'] })
    const message = ({
      SEALED: current.summary.direction === 'INTERNAL_TO_OEM' ? '传递单已提交，正在等待审批或文件处理' : '文件正在处理中',
      RELEASED: '文件已发送',
      REJECTED: '传递单已被驳回',
      BLOCKED: '传递单因文件校验未通过而终止',
      CANCELLED: '传递单已终止',
      ABANDONED: '草稿已删除',
    } as const)[status]
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
        const latestCompanies = draftId !== null ? await loadCompanyOptions() : companies
        const company = latestCompanies.find((item) => item.id === oemCompanyId)
        if (!company?.canReceive) throw new Error(company?.unavailableReason || NO_RECEIVING_ACCOUNT_REASON)
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
      setSubmitError(requestError(error))
    } finally {
      submitLocked.current = false
      setSaving(false)
    }
  }

  const close = () => {
    if (saving) return
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
      onOk: () => {
        void queryClient.invalidateQueries({ queryKey: ['oem', ...queryScope, 'transfers'] })
        onClose()
        navigate(`${base}/transfers/${draftId}`)
      },
      onCancel: () => {
        void queryClient.invalidateQueries({ queryKey: ['oem', ...queryScope, 'transfers'] })
        onClose()
      },
    })
  }

  const actionText = realm === 'internal' ? '提交审批' : '发送文件'
  return (
    <Modal
      title="发送文件"
      visible={visible}
      onCancel={close}
      onOk={submit}
      okText={actionText}
      confirmLoading={saving}
      closable={!saving}
      maskClosable={!saving}
      escToExit={!saving}
      cancelButtonProps={{ disabled: saving }}
      okButtonProps={{ disabled: fileCount === 0 || createOutcomeUnknown
        || (realm === 'internal' && (companyOptionsLoading || !!companyOptionsError || !companies.some((company) => company.canReceive))) }}
      unmountOnExit
    >
      <Form form={form} layout="vertical">
        {realm === 'internal' && (
          <Form.Item field="oemCompanyId" label="目标 OEM 厂商" extra="至少需要一个启用的 OEM 登录账号才能发送。" rules={[{ required: true, message: '请选择厂商' }]}>
            <Select
              showSearch
              disabled={saving || draftId !== null || companyOptionsLoading || !!companyOptionsError}
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
          <Alert type="info" style={{ marginBottom: 12 }} content="正在加载可发送的 OEM 厂商…" />
        )}
        {realm === 'internal' && companyOptionsError && (
          <Alert type="error" style={{ marginBottom: 12 }} content={(
            <Space size="small">
              <span>目标 OEM 厂商加载失败：{companyOptionsError}</span>
              <Button type="text" size="small" loading={companyOptionsLoading} disabled={saving || companyOptionsLoading}
                onClick={() => void loadCompanyOptions().catch(() => undefined)}>刷新厂商</Button>
            </Space>
          )} />
        )}
        {realm === 'internal' && !companyOptionsLoading && !companyOptionsError && companies.length === 0 && (
          <Alert type="warning" style={{ marginBottom: 12 }} content={(
            <Space size="small">
              <span>暂无可发送的 OEM 厂商，请先启用厂商后再试。</span>
              <Button type="text" size="small" loading={companyOptionsLoading} disabled={saving || companyOptionsLoading}
                onClick={() => void loadCompanyOptions().catch(() => undefined)}>刷新厂商</Button>
            </Space>
          )} />
        )}
        {realm === 'internal' && !companyOptionsLoading && !companyOptionsError && companies.length > 0 && !companies.some((company) => company.canReceive) && (
          <Alert type="warning" style={{ marginBottom: 12 }} content={(
            <Space size="small">
              <span>当前没有可接收的 OEM 厂商，请先新增或启用至少一个 OEM 登录账号后再发送。</span>
              <Button type="text" size="small" loading={companyOptionsLoading} disabled={saving || companyOptionsLoading}
                onClick={() => void loadCompanyOptions().catch(() => undefined)}>刷新厂商</Button>
            </Space>
          )} />
        )}
        <Form.Item label="文件" required>
          <OemUploader ref={uploader} deferred disabled={saving} onQueueChange={setFileCount} />
        </Form.Item>
        {submitError && <Alert type="error" content={submitError} />}
        {createOutcomeUnknown && (
          <Alert type="warning" style={{ marginTop: 12 }}
            content="草稿创建结果未知。请关闭窗口并从列表确认，避免重复创建传递单。" />
        )}
        {draftId && !saving && (
          <Alert type="info" style={{ marginTop: 12 }} content="草稿已创建。再次提交只会继续失败或未完成的文件，不会重复上传成功文件。" />
        )}
      </Form>
    </Modal>
  )
}

export default function TransferListPage() {
  const { api, realm, base, queryScope } = useOem()
  const can = useCan()
  const navigate = useNavigate()
  const [page, setPage] = useState(1)
  const [direction, setDirection] = useState('')
  const [status, setStatus] = useState<string | undefined>()
  const [keyword, setKeyword] = useState('')
  const [mine, setMine] = useState(false)
  const [creating, setCreating] = useState(false)

  const transfers = useQuery({
    queryKey: ['oem', ...queryScope, 'transfers', { page, direction, status, keyword, mine }],
    queryFn: () => api.listTransfers({ page, pageSize: 20, direction: direction || undefined, status, keyword: keyword || undefined, mine }),
  })
  const rows: TransferSummary[] = transfers.data?.list ?? []
  const total = transfers.data?.total ?? 0

  return (
    <Card
      title="文件传递单"
      extra={can.createTransfer && <Button type="primary" icon={<IconUpload />} onClick={() => setCreating(true)}>发送文件</Button>}
    >
      <Space wrap style={{ marginBottom: 16 }}>
        {realm === 'internal' && (
          <Radio.Group type="button" value={direction} onChange={(value) => { setDirection(value); setPage(1) }}>
            <Radio value="">全部</Radio>
            <Radio value="INTERNAL_TO_OEM">公司发出</Radio>
            <Radio value="OEM_TO_INTERNAL">OEM 发来</Radio>
          </Radio.Group>
        )}
        <Select allowClear placeholder="状态" style={{ width: 140 }} value={status} options={LIFECYCLE_OPTIONS}
          onChange={(value) => { setStatus(value); setPage(1) }} />
        <Input.Search allowClear placeholder="搜索传递记录" style={{ width: 220 }} onSearch={(value) => { setKeyword(value); setPage(1) }} />
        <Checkbox checked={mine} onChange={(value) => { setMine(value); setPage(1) }}>只看我发送的</Checkbox>
      </Space>
      <Table
        rowKey="id"
        scroll={{ x: 1170 }}
        loading={transfers.isPending || transfers.isFetching}
        data={rows}
        onRow={(row) => ({ onClick: () => navigate(`${base}/transfers/${row.id}`), style: { cursor: 'pointer' } })}
        pagination={{ current: page, total, pageSize: 20, onChange: setPage }}
        columns={[
          { title: '文件', dataIndex: 'title', width: 260, ellipsis: true, render: (value: string) => <Typography.Text bold title={value}>{value}</Typography.Text> },
          { title: '方向', dataIndex: 'direction', width: 120, render: (value: TransferSummary['direction']) => DIRECTION_LABEL[value] },
          { title: '厂商', dataIndex: 'companyName', width: 160 },
          { title: '发送人', width: 120, render: (_: unknown, row: TransferSummary) => row.sender.realName },
          {
            title: '状态', width: 220,
            render: (_: unknown, row: TransferSummary) => (
              <Space size={4} wrap>
                <LifecycleTag value={row.lifecycleStatus} />
                {row.direction === 'INTERNAL_TO_OEM' && row.lifecycleStatus === 'SEALED' && <ApprovalTag value={row.approvalStatus} />}
                {row.lifecycleStatus === 'SEALED' && row.validationSummary !== 'VALID' && <ValidationTag value={row.validationSummary} />}
              </Space>
            ),
          },
          { title: '附件', width: 120, render: (_: unknown, row: TransferSummary) => `${row.fileCount} 个 / ${fmtSize(row.totalBytes)}` },
          { title: '提交时间', width: 170, render: (_: unknown, row: TransferSummary) => (row.sentAt ? fmtTime(row.sentAt) : '-') },
        ]}
      />
      {creating && <CreateTransferModal visible onClose={() => setCreating(false)} />}
    </Card>
  )
}
