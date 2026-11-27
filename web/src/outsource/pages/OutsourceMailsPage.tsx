import { useCallback, useEffect, useRef, useState } from 'react'
import { Button, Card, Descriptions, Empty, Message, Modal, Popover, Select, Space, Tag, Tooltip } from '@arco-design/web-react'
import type { TableColumnProps } from '@arco-design/web-react'
import { DEFAULT_PAGE_SIZE } from '../../components/tablePagination'
import http, { type QuietRequestConfig } from '../../api/client'
import { fmtTime, type ApiResponses } from '../../api/types'
import LoadError from '../../components/LoadError'
import { useAuth } from '../../store/auth'
import { OUTSOURCE_PERMISSIONS } from '../outsourceNavigation'
import './OutsourceMailsPage.css'
import ResizableTable from '../../components/ResizableTable'
import { saveBlob } from '../../utils/saveBlob'
import { actionSlots } from '../../components/ActionSlots'

type Mail = ApiResponses['GET /outsource/mails']['list'][number]
type Vendor = ApiResponses['GET /outsource/settings/vendors'][number]
type Recipient = Mail['recipients'][number]

const STATUS: Record<string, { label: string; color: string }> = {
  QUEUED: { label: '排队中', color: 'arcoblue' },
  SENDING: { label: '发送中', color: 'arcoblue' },
  SENT: { label: '已寄出', color: 'green' },
  PARTIAL: { label: '部分送达', color: 'orange' },
  FAILED: { label: '失败', color: 'red' },
}
const STATUS_OPTIONS = Object.entries(STATUS).filter(([key]) => key !== 'SENDING').map(([value, item]) => ({ value, label: item.label }))
const MAIL_KIND: Record<string, { label: string; detail: string; color: string }> = {
  NOTICE: { label: '外发', detail: '外发通知', color: 'arcoblue' },
  WITHDRAW: { label: '撤回', detail: '撤回通知', color: 'orange' },
  ELECTRICAL_HANDOFF: { label: '转迅得电控', detail: '转迅得电控', color: 'arcoblue' },
  ELECTRICAL_HANDOFF_WITHDRAW: { label: '撤回转迅得电控', detail: '撤回转迅得电控', color: 'orange' },
}
const POLL_MS = 5000

function Recipients({ list, kind }: { list: Recipient[]; kind: 'TO' | 'CC' }) {
  const rows = list.filter((recipient) => recipient.kind === kind)
  if (rows.length === 0) return <span className="outsource-muted">-</span>
  const rejected = rows.filter((recipient) => recipient.rejected).length
  return <Popover trigger={['hover', 'focus', 'click']} content={<ul className="outsource-mail-recipients">
    {rows.map((recipient) => <li key={recipient.email} className={recipient.rejected ? 'outsource-mail-rejected' : undefined}>
      {recipient.name ? `${recipient.name} <${recipient.email}>` : recipient.email}{recipient.rejected ? '（被拒收）' : ''}
    </li>)}
  </ul>}>
    <span className="outsource-mail-recipient-count" tabIndex={0}>{rows.length} 人{rejected > 0 && <span className="outsource-mail-rejected">（拒收 {rejected}）</span>}</span>
  </Popover>
}

/** Every vendor mail with its delivery state; failed mails can be sent again with the same content. */
export default function OutsourceMailsPage() {
  const canSend = useAuth((state) => state.permissions.includes(OUTSOURCE_PERMISSIONS.send))
  const [mails, setMails] = useState<Mail[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(DEFAULT_PAGE_SIZE)
  const [vendorId, setVendorId] = useState<number>()
  const [status, setStatus] = useState<string>()
  const [vendors, setVendors] = useState<Vendor[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(false)
  const [busy, setBusy] = useState<{ id: number; kind: 'download' | 'retry' } | null>(null)
  const [viewing, setViewing] = useState<Mail | null>(null)
  const sequence = useRef(0)
  const inFlight = useRef(0)

  const load = useCallback(async (quiet = false) => {
    if (quiet && inFlight.current > 0) return
    const seq = ++sequence.current
    inFlight.current += 1
    if (!quiet) { setLoading(true); setError(false) }
    try {
      const response = await http.get<ApiResponses['GET /outsource/mails']>('/outsource/mails', {
        params: { page, pageSize, vendorId, status }, quietNetworkError: quiet,
      } as QuietRequestConfig)
      if (seq !== sequence.current) return
      setMails(response.data.list); setTotal(Number(response.data.total))
    } catch {
      if (seq === sequence.current && !quiet) setError(true)
    } finally {
      inFlight.current = Math.max(0, inFlight.current - 1)
      if (seq === sequence.current && !quiet) setLoading(false)
    }
  }, [page, pageSize, vendorId, status])

  useEffect(() => {
    let active = true
    void Promise.resolve().then(async () => {
      if (!active) return
      try {
        const response = await http.get<ApiResponses['GET /outsource/settings/vendors']>('/outsource/settings/vendors')
        if (active) setVendors(response.data)
      } catch { /* 筛选项缺失不影响列表 */ }
    })
    return () => { active = false }
  }, [])

  useEffect(() => {
    let active = true
    void Promise.resolve().then(() => { if (active) void load() })
    return () => { active = false; sequence.current += 1 }
  }, [load])

  // Queued mails change state in the background; refresh quietly until they settle.
  const pending = mails.some((mail) => mail.status === 'QUEUED' || mail.status === 'SENDING')
  useEffect(() => {
    if (!pending) return
    let active = true
    let polling = false
    let timer: number | undefined
    const isHidden = () => document.hidden || document.visibilityState === 'hidden'
    const clearTimer = () => {
      if (timer !== undefined) {
        window.clearTimeout(timer)
        timer = undefined
      }
    }
    const schedule = () => {
      clearTimer()
      if (!active || isHidden() || polling) return
      timer = window.setTimeout(() => {
        timer = undefined
        void tick()
      }, POLL_MS)
    }
    const tick = async () => {
      if (!active || isHidden() || polling) return
      polling = true
      try {
        await load(true)
      } finally {
        polling = false
        if (active && !isHidden()) schedule()
      }
    }
    const onVisibility = () => {
      if (isHidden()) {
        clearTimer()
        return
      }
      if (!polling) {
        clearTimer()
        void tick()
      }
    }
    schedule()
    document.addEventListener('visibilitychange', onVisibility)
    return () => {
      active = false
      clearTimer()
      document.removeEventListener('visibilitychange', onVisibility)
    }
  }, [pending, load])

  const retry = async (mail: Mail) => {
    setBusy({ id: mail.id, kind: 'retry' })
    try {
      await http.post<ApiResponses['POST /outsource/mails/{id}/retry']>(`/outsource/mails/${mail.id}/retry`)
      Message.success('已重新排入发送'); await load()
    } catch { /* 统一请求层提示 */ } finally {
      setBusy((current) => (current?.id === mail.id && current.kind === 'retry' ? null : current))
    }
  }

  const download = async (mail: Mail) => {
    setBusy({ id: mail.id, kind: 'download' })
    try {
      const response = await http.get<ApiResponses['GET /outsource/mails/{id}/attachment']>(`/outsource/mails/${mail.id}/attachment`, { responseType: 'blob' })
      saveBlob(response.data as Blob, mail.attachmentName ?? '外发清单.xlsx')
    } catch { /* 统一请求层提示 */ } finally {
      setBusy((current) => (current?.id === mail.id && current.kind === 'download' ? null : current))
    }
  }

  const columns: TableColumnProps<Mail>[] = [
    { title: '时间', width: 176, render: (_, mail) => fmtTime(mail.createdAt) },
    {
      title: '类型', width: 136, align: 'center',
      render: (_, mail) => <Tag color={MAIL_KIND[mail.kind]?.color}>{MAIL_KIND[mail.kind]?.label ?? mail.kind}</Tag>,
    },
    { title: '厂商', dataIndex: 'vendorName', width: 90, align: 'left', ellipsis: true },
    { title: '主旨', dataIndex: 'subject', width: 190, align: 'left', ellipsis: true },
    {
      title: '工令', width: 120, align: 'left', ellipsis: true,
      render: (_, mail) => mail.workOrderNos.length > 0
        ? <Tooltip content={mail.workOrderNos.join('、')}><span className="table-cell-text">{mail.workOrderNos.join('、')}</span></Tooltip>
        : '-',
    },
    { title: '收件人', width: 88, render: (_, mail) => <Recipients list={mail.recipients} kind="TO" /> },
    { title: '抄送', width: 80, render: (_, mail) => <Recipients list={mail.recipients} kind="CC" /> },
    {
      title: '状态', width: 88, align: 'center',
      render: (_, mail) => <Tag color={STATUS[mail.status]?.color}>{STATUS[mail.status]?.label ?? mail.status}</Tag>,
    },
    { title: '说明', dataIndex: 'lastError', width: 180, align: 'left', ellipsis: true, render: (value: string | null) => value || '-' },
    {
      title: '发送人', width: 140, align: 'left', ellipsis: true,
      render: (_, mail) => mail.createdByName
        ? <Tooltip content={mail.createdByName}><span className="table-cell-text">{mail.createdByName}</span></Tooltip> : '-',
    },
    {
      title: '操作', width: 192, align: 'center', fixed: 'right',
      render: (_, mail) => actionSlots([
        <Button key="detail" type="text" size="mini" onClick={() => setViewing(mail)}>详情</Button>,
        <Button key="attachment" type="text" size="mini"
          loading={busy?.id === mail.id && busy.kind === 'download'}
          disabled={!mail.attachmentName || (busy?.id === mail.id && busy.kind === 'retry')}
          onClick={() => void download(mail)}>附件</Button>,
        canSend && <Button key="retry" type="text" size="mini"
          loading={busy?.id === mail.id && busy.kind === 'retry'}
          disabled={mail.status !== 'FAILED' || (busy?.id === mail.id && busy.kind === 'download')}
          onClick={() => void retry(mail)}>重新发送</Button>,
      ], 'outsource-mail'),
    },
  ]

  const hasActiveFilters = vendorId !== undefined || status !== undefined

  return <Card className="page-card page-card--table outsource-mails-page">
    <h1 className="visually-hidden">发送记录</h1>
    <div className="page-toolbar responsive-toolbar">
      <Space wrap>
        <Select aria-label="厂商" placeholder="全部厂商" allowClear showSearch style={{ width: 160 }} value={vendorId}
          onChange={(value) => { setVendorId(value); setPage(1) }} options={vendors.map((vendor) => ({ label: vendor.name, value: vendor.id }))} />
        <Select aria-label="状态" placeholder="全部状态" allowClear style={{ width: 140 }} value={status}
          onChange={(value) => { setStatus(value); setPage(1) }} options={STATUS_OPTIONS} />
        {hasActiveFilters && <Button onClick={() => { setVendorId(undefined); setStatus(undefined); setPage(1) }}>重置筛选</Button>}
      </Space>
    </div>
    {error ? <LoadError message="发送记录加载失败" onRetry={() => void load()} />
      : <ResizableTable<Mail> tableId="outsource-mails" className="page-table" rowKey="id" loading={loading} data={mails} columns={columns}
        noDataElement={loading ? <span /> : <Empty description="暂无发送记录" />} scroll={{ x: 1480, y: 'var(--page-table-scroll-y)' }}
        pagination={{ current: page, pageSize, total, onChange: (next, size) => { setPage(size === pageSize ? next : 1); setPageSize(size) } }} />}
    <Modal title="邮件发送详情" visible={viewing !== null} footer={null} onCancel={() => setViewing(null)} unmountOnExit>
      {viewing && (
        <Descriptions
          column={1}
          border
          size="small"
          data={[
            { label: '发送时间', value: fmtTime(viewing.createdAt) },
            { label: '邮件类型', value: MAIL_KIND[viewing.kind]?.detail ?? viewing.kind },
            { label: '外发厂商', value: viewing.vendorName },
            { label: '邮件主旨', value: viewing.subject },
            { label: '关联工令', value: viewing.workOrderNos.join('、') || '-' },
            {
              label: '收件人',
              value: viewing.recipients.filter((r) => r.kind === 'TO')
                .map((r) => `${r.name ? `${r.name} <${r.email}>` : r.email}${r.rejected ? '（被拒收）' : ''}`).join('；') || '-',
            },
            {
              label: '抄送',
              value: viewing.recipients.filter((r) => r.kind === 'CC')
                .map((r) => `${r.name ? `${r.name} <${r.email}>` : r.email}${r.rejected ? '（被拒收）' : ''}`).join('；') || '-',
            },
            { label: '发送状态', value: STATUS[viewing.status]?.label ?? viewing.status },
            ...(viewing.lastError ? [{ label: '失败原因', value: viewing.lastError }] : []),
            { label: '发送人', value: viewing.createdByName || '-' },
            { label: '附件名称', value: viewing.attachmentName || '无附件' },
          ]}
        />
      )}
    </Modal>
  </Card>
}
