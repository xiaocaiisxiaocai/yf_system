import { useCallback, useState } from 'react'
import { Alert, Button, Card, Message, Pagination, Select, Space, Spin, Table, Tag, Typography } from '@arco-design/web-react'
import { useNavigate } from 'react-router-dom'
import http from '../../../api/client'
import type { ApiResponses } from '../../../api/generated/api-types'
import { useAuth } from '../../../store/auth'
import { mailReasonText } from '../../../utils/mailReasons'
import { useLatestRequest, useLoadEffect } from '../../../oem/useLoadEffect'
import {
  MAIL_LOG_RESULTS, mailActionTag, mailFailureCause, mailRecentSummary, normalizeMailStatus,
  type MailLogResult, type MailRecent, type MailStatus,
} from './systemConfigModel'

type Scope = 'collaboration' | 'oem' | 'all'
const LABELS = { collaboration: 'Robot 仿真协作', oem: 'ODM/OEMA', all: '全部业务' }
const QUEUE_LABELS = { collaboration: 'Robot 业务通知队列', oem: 'ODM/OEMA 业务通知队列', all: '全部业务通知队列' }
const PAGE_SIZE = 20

type LogPage = { key: string; list: MailRecent[]; total: number }

/** The newest failure while mail is still waiting and nothing was delivered after it: SMTP is not working right now. */
function currentFailure(data: MailStatus): MailRecent | null {
  const failure = data.latestFailure
  if (!failure || data.queue.pending + data.queue.sending === 0) return null
  if (data.latestSentAt && Date.parse(data.latestSentAt) >= Date.parse(failure.createdAt)) return null
  return failure
}

function MailLogDetail({ row, formatTime }: { row: MailRecent; formatTime: (value?: string | null) => string }) {
  const detail = row.detail || {}
  const items = [
    { label: '记录', value: `${row.targetType || '-'} #${row.targetId || '-'}` },
    { label: '事件', value: detail.eventType },
    { label: '收件人', value: detail.recipient },
    { label: '失败步骤', value: detail.failedStep },
    { label: '原因', value: detail.diagnosis || detail.error },
    { label: '处理', value: mailReasonText(detail.reason) },
    { label: '重试次数', value: detail.retryCount == null ? '' : String(detail.retryCount) },
    { label: '重连次数', value: detail.reconnects ? String(detail.reconnects) : '' },
    { label: '下次重试', value: detail.nextAttemptAt ? formatTime(detail.nextAttemptAt) : '' },
  ].filter(item => item.value)
  return <dl className="system-mail-log-detail">
    {items.map(item => <div key={item.label} className="system-mail-log-detail-item"><dt>{item.label}</dt><dd>{item.value}</dd></div>)}
  </dl>
}

export default function BusinessMailLogPanel({ mail, formatTime, onRefresh, onViewLogs }: {
  mail: MailStatus; formatTime: (value?: string | null) => string; onRefresh?: () => Promise<void>; onViewLogs: () => void
}) {
  const navigate = useNavigate()
  const auth = useAuth()
  const canManageOem = auth.hasPerm('oem:notify_manage')
  const canViewOemLogs = auth.hasPerm('oem:audit_view')
  const [scope, setScope] = useState<Scope>('collaboration')
  const [snapshot, setSnapshot] = useState<{ scope: Scope; data: MailStatus; source: MailStatus } | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState('')
  const [result, setResult] = useState<MailLogResult>('all')
  const [page, setPage] = useState(1)
  const [logs, setLogs] = useState<LogPage | null>(null)
  const [logsError, setLogsError] = useState('')
  const beginRequest = useLatestRequest()
  const beginLogsRequest = useLatestRequest()
  const visibleScope = canManageOem ? scope : 'collaboration'
  const data = visibleScope === 'collaboration' ? mail : snapshot?.scope === visibleScope && snapshot.source === mail ? snapshot.data : null
  const logsKey = `${visibleScope}|${result}|${page}`

  const load = useCallback(async () => {
    const isLatest = beginRequest()
    if (scope === 'collaboration' || !canManageOem) { setLoading(false); return }
    setLoading(true)
    setError('')
    try {
      const response = await http.get<ApiResponses['GET /admin/system/mail-status']>('/admin/system/mail-status', { params: { scope } })
      if (isLatest()) setSnapshot({ scope, data: normalizeMailStatus(response.data), source: mail })
    } catch { if (isLatest()) setError('邮件状态加载失败，请重试') }
    finally { if (isLatest()) setLoading(false) }
  }, [scope, canManageOem, mail, beginRequest])
  useLoadEffect(load)

  // Reloads with the status, so a refresh or a finished test also refreshes the visible page.
  const loadLogs = useCallback(async () => {
    const isLatest = beginLogsRequest()
    if (!data) return
    setLogsError('')
    try {
      const response = await http.get<ApiResponses['GET /admin/system/mail-logs']>('/admin/system/mail-logs', {
        params: { scope: visibleScope, result, page, pageSize: PAGE_SIZE },
      })
      if (isLatest()) setLogs({ key: logsKey, list: (response.data.list ?? []) as MailRecent[], total: Number(response.data.total ?? 0) })
    } catch { if (isLatest()) setLogsError('邮件日志加载失败，请重试') }
  }, [beginLogsRequest, data, visibleScope, result, page, logsKey])
  useLoadEffect(loadLogs)

  const [retrying, setRetrying] = useState(false)
  const refresh = () => (visibleScope === 'collaboration' ? onRefresh?.() : load())
  const retryNow = async () => {
    setRetrying(true)
    try {
      const response = await http.post<ApiResponses['POST /admin/system/mail-retry']>('/admin/system/mail-retry')
      const count = Number(response.data.rescheduled ?? 0)
      Message.success(count > 0 ? `已恢复发送，${count} 封邮件正在重新发送` : '已恢复发送，待发送邮件正在重新发送')
      await refresh()
    } catch { Message.error('恢复发送失败，请重试') }
    finally { setRetrying(false) }
  }

  const failure = data ? currentFailure(data) : null
  const pageLogs = logs?.key === logsKey ? logs : null
  return <Card className="page-card system-mail-card system-mail-log" title="邮件日志" extra={<Space wrap>
    {canManageOem && <Select aria-label="邮件日志业务" style={{ width: 145 }} value={scope} onChange={(value: Scope) => { setScope(value); setSnapshot(null); setError(''); setPage(1) }}>
      {Object.entries(LABELS).map(([key, label]) => <Select.Option key={key} value={key}>{label}</Select.Option>)}
    </Select>}
    <Button size="small" loading={loading} onClick={() => void refresh()?.catch(() => undefined)}>刷新</Button>
    {visibleScope !== 'oem' && <Button size="small" onClick={onViewLogs}>{visibleScope === 'all' ? 'Robot 日志' : '全部日志'}</Button>}
    {visibleScope !== 'collaboration' && canViewOemLogs && <Button size="small" onClick={() => navigate('/oem/admin/audit')}>ODM/OEMA 日志</Button>}
  </Space>}>
    {error ? <Alert type="error" content={error} action={<Button size="small" onClick={() => void load()}>重试</Button>} /> : loading || !data ? <Spin loading style={{ width: '100%', minHeight: 100 }} /> : <div className="system-mail-body">
      <Space wrap size={20}>
        <Typography.Text bold>{QUEUE_LABELS[visibleScope]}</Typography.Text>
        <Typography.Text type="secondary">SMTP {data.configured ? `${data.host || '-'}:${data.port || '-'}` : '未配置'}</Typography.Text>
        {data.configured && <Typography.Text type="secondary">发件人 {data.from || '-'}</Typography.Text>}
        <Typography.Text>待发送 <strong>{data.queue.pending}</strong></Typography.Text>
        <Typography.Text>发送中 <strong>{data.queue.sending}</strong></Typography.Text>
        <Typography.Text type="success">已发送 <strong>{data.queue.sent}</strong></Typography.Text>
        <Typography.Text type={data.queue.failed ? 'error' : 'secondary'}>失败 <strong>{data.queue.failed}</strong></Typography.Text>
        <Typography.Text type="secondary">已取消 <strong>{data.queue.cancelled}</strong></Typography.Text>
      </Space>
      <Space wrap size={20}><Typography.Text type="secondary">最近成功：{formatTime(data.latestSentAt)}</Typography.Text><Typography.Text type={failure ? 'error' : 'secondary'}>最近失败：{formatTime(data.latestFailedAt)}</Typography.Text></Space>
      {failure && <Alert type="error" className="system-mail-failure" content={`${formatTime(failure.createdAt)} 发送失败：${mailFailureCause(failure)}。${data.queue.pending + data.queue.sending} 封待发送，恢复后自动补发`}
        action={<Button size="mini" type="primary" status="danger" loading={retrying} onClick={() => void retryNow()}>立即重试</Button>} />}
      {visibleScope !== 'oem' && data.missingEmailCount > 0 && <Typography.Text type="warning">{data.missingEmailCount} 个启用内部账号未填写邮箱，相关通知不会入队。示例：{data.missingEmailAccounts.map(account => `${account.realName}（${account.employeeNo}）`).join('、')}</Typography.Text>}
      {visibleScope !== 'collaboration' && (data.missingOemContactCompanyCount ?? 0) > 0 && <Space wrap><Typography.Text type="warning">{data.missingOemContactCompanyCount} 家启用的 ODM/OEMA 厂商没有启用联络人邮箱，无法接收厂商邮件。</Typography.Text>{auth.hasPerm('oem:company_manage') && <Button size="mini" type="text" onClick={() => navigate('/oem/admin/companies')}>维护联络人</Button>}</Space>}
      <div className="system-mail-log-toolbar">
        <Select aria-label="邮件日志结果" size="small" style={{ width: 130 }} value={result} onChange={(value: MailLogResult) => { setResult(value); setPage(1) }}>
          {MAIL_LOG_RESULTS.map(item => <Select.Option key={item.value} value={item.value}>{item.label}</Select.Option>)}
        </Select>
        {pageLogs && pageLogs.total > PAGE_SIZE && <Pagination size="small" simple current={page} pageSize={PAGE_SIZE} total={pageLogs.total} onChange={setPage} />}
      </div>
      {logsError ? <Alert type="error" content={logsError} action={<Button size="small" onClick={() => void loadLogs()}>重试</Button>} /> : <div className="system-mail-log-table">
        <Table rowKey="id" size="small" pagination={false} loading={!pageLogs} noDataElement="暂无邮件记录" data={pageLogs?.list ?? []} scroll={{ x: 620 }}
          expandedRowRender={(row: MailRecent) => <MailLogDetail row={row} formatTime={formatTime} />}
          columns={[
            { title: '时间', dataIndex: 'createdAt', width: 160, render: formatTime },
            { title: '结果', dataIndex: 'action', width: 150, render: (_action: string, row: MailRecent) => { const tag = mailActionTag(row); return <Tag color={tag.color}>{tag.label}</Tag> } },
            { title: '说明', width: 300, render: (_value: unknown, row: MailRecent) => <span className="system-mail-log-summary">{mailRecentSummary(row, formatTime)}</span> },
          ]} />
      </div>}
    </div>}
  </Card>
}
