import { useCallback, useState } from 'react'
import { Alert, Button, Card, Select, Space, Spin, Table, Tag, Typography } from '@arco-design/web-react'
import { useNavigate } from 'react-router-dom'
import http from '../../../api/client'
import type { ApiResponses } from '../../../api/generated/api-types'
import { useAuth } from '../../../store/auth'
import { useLatestRequest, useLoadEffect } from '../../../oem/useLoadEffect'
import { MAIL_ACTION_LABELS, mailRecentSummary, normalizeMailStatus, type MailStatus, type MailRecent } from './systemConfigModel'

type Scope = 'collaboration' | 'oem' | 'all'
const LABELS = { collaboration: 'Robot 仿真协作', oem: 'ODM/OEMA', all: '全部业务' }
const QUEUE_LABELS = { collaboration: 'Robot 业务通知队列', oem: 'ODM/OEMA 业务通知队列', all: '全部业务通知队列' }

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
  const beginRequest = useLatestRequest()
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
  const visibleScope = canManageOem ? scope : 'collaboration'
  const data = visibleScope === 'collaboration' ? mail : snapshot?.scope === visibleScope && snapshot.source === mail ? snapshot.data : null
  return <Card className="page-card system-mail-card system-mail-log" title="邮件日志" extra={<Space wrap>
    {canManageOem && <Select aria-label="邮件日志业务" style={{ width: 145 }} value={scope} onChange={(value: Scope) => { setScope(value); setSnapshot(null); setError('') }}>
      {Object.entries(LABELS).map(([key, label]) => <Select.Option key={key} value={key}>{label}</Select.Option>)}
    </Select>}
    <Button size="small" loading={loading} onClick={() => void (visibleScope === 'collaboration' ? onRefresh?.() : load())?.catch(() => undefined)}>刷新</Button>
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
      <Space wrap size={20}><Typography.Text type="secondary">最近成功：{formatTime(data.latestSentAt)}</Typography.Text><Typography.Text type="secondary">最近失败：{formatTime(data.latestFailedAt)}</Typography.Text></Space>
      {visibleScope !== 'oem' && data.missingEmailCount > 0 && <Typography.Text type="warning">{data.missingEmailCount} 个启用内部账号未填写邮箱，相关通知不会入队。示例：{data.missingEmailAccounts.map(account => `${account.realName}（${account.employeeNo}）`).join('、')}</Typography.Text>}
      {visibleScope !== 'collaboration' && (data.missingOemContactCompanyCount ?? 0) > 0 && <Space wrap><Typography.Text type="warning">{data.missingOemContactCompanyCount} 家启用的 ODM/OEMA 厂商没有启用联络人邮箱，无法接收厂商邮件。</Typography.Text>{auth.hasPerm('oem:company_manage') && <Button size="mini" type="text" onClick={() => navigate('/oem/admin/companies')}>维护联络人</Button>}</Space>}
      <Table rowKey="id" size="small" pagination={false} noDataElement="暂无邮件记录" data={data.recent} scroll={{ x: 620 }} columns={[
        { title: '时间', dataIndex: 'createdAt', width: 160, render: formatTime },
        { title: '结果', dataIndex: 'action', width: 150, render: (action: string) => <Tag color={action.endsWith('_SENT') ? 'green' : action.endsWith('_FAILED') ? 'red' : action.endsWith('_CANCELLED_STALE') ? 'gray' : 'orange'}>{MAIL_ACTION_LABELS[action] || `未知动作：${action}`}</Tag> },
        { title: '说明', width: 300, render: (_value: unknown, row: MailRecent) => mailRecentSummary(row) },
      ]} />
    </div>}
  </Card>
}
