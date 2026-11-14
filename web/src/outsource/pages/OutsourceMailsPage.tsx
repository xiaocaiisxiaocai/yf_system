import { useCallback, useEffect, useRef, useState } from 'react'
import { Button, Card, Empty, Message, Popover, Select, Space, Tag, Tooltip } from '@arco-design/web-react'
import type { TableColumnProps } from '@arco-design/web-react'
import http, { type QuietRequestConfig } from '../../api/client'
import { fmtTime, type ApiResponses } from '../../api/types'
import LoadError from '../../components/LoadError'
import { useAuth } from '../../store/auth'
import { OUTSOURCE_PERMISSIONS } from '../outsourceNavigation'
import './OutsourceMailsPage.css'
import ResizableTable from '../../components/ResizableTable'
import { saveBlob } from '../../utils/saveBlob'

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
const POLL_MS = 5000

function Recipients({ list, kind }: { list: Recipient[]; kind: 'TO' | 'CC' }) {
  const rows = list.filter((recipient) => recipient.kind === kind)
  if (rows.length === 0) return <span className="outsource-muted">—</span>
  const rejected = rows.filter((recipient) => recipient.rejected).length
  return <Popover trigger="hover" content={<ul className="outsource-mail-recipients">
    {rows.map((recipient) => <li key={recipient.email} className={recipient.rejected ? 'outsource-mail-rejected' : undefined}>
      {recipient.name ? `${recipient.name} <${recipient.email}>` : recipient.email}{recipient.rejected ? '（被拒收）' : ''}
    </li>)}
  </ul>}>
    <span className="outsource-mail-recipient-count" tabIndex={0}>{rows.length} 人{rejected > 0 && <span className="outsource-mail-rejected">（拒收 {rejected}）</span>}</span>
  </Popover>
}

/** Every vendor mail with its delivery state; failed mails can be sent again with the same content. */
export default function OutsourceMailsPage() {
  const canManage = useAuth((state) => state.permissions.includes(OUTSOURCE_PERMISSIONS.manage))
  const [mails, setMails] = useState<Mail[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [vendorId, setVendorId] = useState<number>()
  const [status, setStatus] = useState<string>()
  const [vendors, setVendors] = useState<Vendor[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(false)
  const [busy, setBusy] = useState<number | null>(null)
  const sequence = useRef(0)

  const load = useCallback(async (quiet = false) => {
    const seq = ++sequence.current
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
    const timer = window.setInterval(() => void load(true), POLL_MS)
    return () => window.clearInterval(timer)
  }, [pending, load])

  const retry = async (mail: Mail) => {
    setBusy(mail.id)
    try {
      await http.post<ApiResponses['POST /outsource/mails/{id}/retry']>(`/outsource/mails/${mail.id}/retry`)
      Message.success('已重新排入发送'); await load()
    } catch { /* 统一请求层提示 */ } finally { setBusy(null) }
  }

  const download = async (mail: Mail) => {
    setBusy(mail.id)
    try {
      const response = await http.get<ApiResponses['GET /outsource/mails/{id}/attachment']>(`/outsource/mails/${mail.id}/attachment`, { responseType: 'blob' })
      saveBlob(response.data as Blob, mail.attachmentName ?? '外发清单.xlsx')
    } catch { /* 统一请求层提示 */ } finally { setBusy(null) }
  }

  const columns: TableColumnProps<Mail>[] = [
    { title: '时间', width: 160, render: (_, mail) => fmtTime(mail.createdAt) },
    {
      title: '类型', width: 90, align: 'center',
      render: (_, mail) => <Tag color={mail.kind === 'WITHDRAW' ? 'orange' : 'arcoblue'}>{mail.kind === 'WITHDRAW' ? '撤回' : '外发'}</Tag>,
    },
    { title: '厂商', dataIndex: 'vendorName', width: 110, ellipsis: true },
    { title: '主旨', dataIndex: 'subject', width: 280, ellipsis: true },
    {
      title: '工令', width: 200, ellipsis: true,
      render: (_, mail) => <Tooltip content={mail.workOrderNos.join('、')}><span>{mail.workOrderNos.join('、')}</span></Tooltip>,
    },
    { title: '收件人', width: 110, render: (_, mail) => <Recipients list={mail.recipients} kind="TO" /> },
    { title: '抄送', width: 110, render: (_, mail) => <Recipients list={mail.recipients} kind="CC" /> },
    {
      title: '状态', width: 100, align: 'center',
      render: (_, mail) => <Tag color={STATUS[mail.status]?.color}>{STATUS[mail.status]?.label ?? mail.status}</Tag>,
    },
    { title: '说明', dataIndex: 'lastError', width: 220, ellipsis: true, render: (value: string | null) => value ?? '' },
    { title: '发送人', dataIndex: 'createdByName', width: 100, ellipsis: true },
    {
      title: '操作', width: 150, align: 'center', fixed: 'right',
      render: (_, mail) => <Space size={4}>
        {mail.attachmentName && <Button type="text" size="mini" loading={busy === mail.id} onClick={() => void download(mail)}>附件</Button>}
        {canManage && mail.status === 'FAILED' && <Button type="text" size="mini" disabled={busy === mail.id} onClick={() => void retry(mail)}>重新发送</Button>}
      </Space>,
    },
  ]

  return <Card className="page-card page-card--table outsource-mails-page">
    <h1 className="visually-hidden">发送记录</h1>
    <div className="page-toolbar responsive-toolbar">
      <Space wrap>
        <Select aria-label="厂商" placeholder="全部厂商" allowClear style={{ width: 160 }} value={vendorId}
          onChange={(value) => { setVendorId(value); setPage(1) }} options={vendors.map((vendor) => ({ label: vendor.name, value: vendor.id }))} />
        <Select aria-label="状态" placeholder="全部状态" allowClear style={{ width: 140 }} value={status}
          onChange={(value) => { setStatus(value); setPage(1) }} options={STATUS_OPTIONS} />
      </Space>
    </div>
    {error ? <LoadError message="发送记录加载失败" onRetry={() => void load()} />
      : <ResizableTable<Mail> tableId="outsource-mails" className="page-table" rowKey="id" loading={loading} data={mails} columns={columns}
        noDataElement={loading ? <span /> : <Empty description="暂无发送记录" />} scroll={{ x: 1630, y: 'var(--page-table-scroll-y)' }}
        pagination={{ current: page, pageSize, total, onChange: (next, size) => { setPage(size === pageSize ? next : 1); setPageSize(size) } }} />}
  </Card>
}
