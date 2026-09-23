import { Button, Card, Input, InputNumber, Select, Space, Switch, Table, Tag, Typography } from '@arco-design/web-react'
import PasswordInput from '../../../components/PasswordInput'
import {
  MAIL_ACTION_LABELS,
  NOTIFICATION_EVENT_ITEMS,
  type MailRecent,
  type MailStatus,
  type NotificationPolicy,
  type SmtpSettings,
  mailRecentSummary,
} from './systemConfigModel'

interface MailSettingsPanelProps {
  mail: MailStatus | null
  smtp: SmtpSettings
  smtpDraft: SmtpSettings
  smtpPassword: string
  smtpSaving: boolean
  smtpDirty: boolean
  smtpIdentityChanged: boolean
  notificationDraft: NotificationPolicy
  notificationSaving: boolean
  notificationDirty: boolean
  formatTime: (value?: string | null) => string
  onSmtpDraftChange: (update: (current: SmtpSettings) => SmtpSettings) => void
  onSmtpPasswordChange: (value: string) => void
  onNotificationDraftChange: (update: (current: NotificationPolicy) => NotificationPolicy) => void
  onCancelSmtp: () => void
  onCancelNotifications: () => void
  onSaveSmtp: () => void
  onSaveNotifications: () => void
  onViewLogs: () => void
}

export function renderMailSettingsPanel({
  mail,
  smtp,
  smtpDraft,
  smtpPassword,
  smtpSaving,
  smtpDirty,
  smtpIdentityChanged,
  notificationDraft,
  notificationSaving,
  notificationDirty,
  formatTime,
  onSmtpDraftChange,
  onSmtpPasswordChange,
  onNotificationDraftChange,
  onCancelSmtp,
  onCancelNotifications,
  onSaveSmtp,
  onSaveNotifications,
  onViewLogs,
}: MailSettingsPanelProps) {
  if (!mail) {
    return (
      <Card className="page-card system-mail-card" title="SMTP 配置与邮件提醒">
        <Typography.Text type="secondary">邮件配置暂不可用，请刷新页面。</Typography.Text>
      </Card>
    )
  }

  return (
    <Card
      className="page-card system-mail-card"
      title="邮件发送"
      extra={
        <Space size={8}>
          <Tag color={mail.configured ? 'green' : 'red'}>{mail.configured ? 'SMTP 已配置' : 'SMTP 未配置'}</Tag>
          <Tag color={mail.notificationsEnabled ? 'arcoblue' : 'gray'}>{mail.notificationsEnabled ? '邮件通知已启用' : '邮件通知已关闭'}</Tag>
        </Space>
      }
    >
      <div className="system-mail-body">
        <Typography.Text type="secondary">配置系统通知的发件邮箱，保存后用于后续邮件发送。邮箱服务商要求授权码时，请填写授权码。</Typography.Text>
        {smtp.passwordNeedsUpdate && <Typography.Text type="warning">已保存的授权码无法读取，请重新填写并保存。</Typography.Text>}
        <div className="system-smtp-grid">
          <label>SMTP 服务器<Input aria-label="SMTP 服务器" placeholder="例如 smtp.example.com" value={smtpDraft.host} maxLength={253} disabled={smtpSaving} onChange={(host) => onSmtpDraftChange((current) => ({ ...current, host }))} /></label>
          <label>SMTP 端口<InputNumber aria-label="SMTP 端口" min={1} max={65535} precision={0} value={smtpDraft.port} disabled={smtpSaving} onChange={(port) => onSmtpDraftChange((current) => ({ ...current, port: typeof port === 'number' ? port : 0 }))} /></label>
          <label>SMTP 登录账号<Input aria-label="SMTP 登录账号" placeholder="通常为完整邮箱地址" autoComplete="off" value={smtpDraft.username} maxLength={320} disabled={smtpSaving} onChange={(username) => onSmtpDraftChange((current) => ({ ...current, username }))} /></label>
          <label>发件邮箱<Input aria-label="发件邮箱" placeholder="例如 notice@example.com" value={smtpDraft.from} maxLength={320} disabled={smtpSaving} onChange={(from) => onSmtpDraftChange((current) => ({ ...current, from }))} /></label>
          <label>邮箱密码 / 授权码<PasswordInput aria-label="邮箱密码或授权码" autoComplete="new-password" placeholder={smtp.hasPassword && !smtpIdentityChanged && !smtp.passwordNeedsUpdate ? '已设置，留空保留原授权码' : '请输入邮箱密码或授权码'} value={smtpPassword} maxLength={1024} disabled={smtpSaving} onChange={onSmtpPasswordChange} /></label>
          <div className="system-smtp-field"><span>连接加密</span><Select aria-label="SMTP 连接加密" value={smtpDraft.security} disabled={smtpSaving} onChange={(security) => onSmtpDraftChange((current) => ({ ...current, security: security as SmtpSettings['security'] }))}
            triggerProps={{ autoAlignPopupWidth: false, autoAlignPopupMinWidth: true, className: 'smtp-security-popup', popupStyle: { maxWidth: 'calc(100vw - 32px)' } }}>
            <Select.Option value="Auto">自动（465 使用 TLS，其他端口使用 STARTTLS）</Select.Option>
            <Select.Option value="SslOnConnect">TLS / SSL（通常为 465）</Select.Option>
            <Select.Option value="StartTls">STARTTLS（通常为 587）</Select.Option>
          </Select></div>
        </div>
        <Space wrap>
          <Button type="primary" disabled={!smtpDirty} loading={smtpSaving} onClick={onSaveSmtp}>保存邮箱设置</Button>
          <Button disabled={!smtpDirty || smtpSaving} onClick={onCancelSmtp}>取消修改</Button>
          <Typography.Text type="secondary">授权码不回显；邮件是否发送由下方“邮件通知”开关控制。</Typography.Text>
        </Space>
        <div className="system-notification-settings">
          <div className="system-notification-heading">
            <div>
              <Typography.Text bold>邮件提醒规则</Typography.Text>
              <Typography.Text type="secondary">分别控制通知对象和需要发送邮件的协作消息。</Typography.Text>
            </div>
            <Space size={8}>
              <Typography.Text>邮件通知总开关</Typography.Text>
              <Switch
                aria-label="邮件通知总开关"
                checked={notificationDraft.globalEnabled}
                disabled={notificationSaving}
                onChange={(checked) => onNotificationDraftChange((current) => ({ ...current, globalEnabled: checked }))}
              />
            </Space>
          </div>
          <div className="system-notification-grid">
            <div className="system-notification-panel">
              <div className="system-notification-panel-title">通知对象</div>
              <div className="system-notification-row">
                <div><Typography.Text>内部员工</Typography.Text><Typography.Text type="secondary">公司内部负责人和验收人员</Typography.Text></div>
                <Switch aria-label="内部员工邮件通知" checked={notificationDraft.internalEnabled} disabled={notificationSaving} onChange={(checked) => onNotificationDraftChange((current) => ({ ...current, internalEnabled: checked }))} />
              </div>
              <div className="system-notification-row">
                <div><Typography.Text>外部企业（供应商）</Typography.Text><Typography.Text type="secondary">项目关联供应商的启用账号</Typography.Text></div>
                <Switch aria-label="外部企业邮件通知" checked={notificationDraft.supplierEnabled} disabled={notificationSaving} onChange={(checked) => onNotificationDraftChange((current) => ({ ...current, supplierEnabled: checked }))} />
              </div>
            </div>
            <div className="system-notification-panel system-notification-panel--events">
              <div className="system-notification-panel-title">提醒消息</div>
              {NOTIFICATION_EVENT_ITEMS.map((item) => (
                <div className="system-notification-row" key={item.key}>
                  <div><Typography.Text>{item.title}</Typography.Text><Typography.Text type="secondary">{item.description}</Typography.Text></div>
                  <Switch aria-label={`${item.title}邮件提醒`} checked={notificationDraft.events[item.key]} disabled={notificationSaving} onChange={(checked) => onNotificationDraftChange((current) => ({ ...current, events: { ...current.events, [item.key]: checked } }))} />
                </div>
              ))}
            </div>
          </div>
          <Space wrap>
            <Button type="primary" size="small" loading={notificationSaving} disabled={!notificationDirty || notificationSaving} onClick={onSaveNotifications}>保存邮件提醒</Button>
            <Button size="small" disabled={!notificationDirty || notificationSaving} onClick={onCancelNotifications}>取消修改</Button>
            <Typography.Text type="secondary">关闭后不会创建新的对应邮件；已入队邮件会在发送前再次按规则检查。</Typography.Text>
          </Space>
        </div>
        <Space wrap size={20}>
          <Typography.Text type="secondary">
            SMTP {mail.configured ? `${mail.host || '-'}:${mail.port || '-'}` : '未配置'}
          </Typography.Text>
          {mail.configured && <Typography.Text type="secondary">发件人 {mail.from || '-'}</Typography.Text>}
          <Typography.Text>待发送 <strong>{mail.queue.pending}</strong></Typography.Text>
          <Typography.Text>发送中 <strong>{mail.queue.sending}</strong></Typography.Text>
          <Typography.Text type="success">已发送 <strong>{mail.queue.sent}</strong></Typography.Text>
          <Typography.Text type={mail.queue.failed > 0 ? 'error' : 'secondary'}>失败 <strong>{mail.queue.failed}</strong></Typography.Text>
          <Typography.Text type="secondary">已取消 <strong>{mail.queue.cancelled}</strong></Typography.Text>
        </Space>
        <Space wrap size={20}>
          <Typography.Text type="secondary">最近成功：{formatTime(mail.latestSentAt)}</Typography.Text>
          <Typography.Text type="secondary">最近失败：{formatTime(mail.latestFailedAt)}</Typography.Text>
        </Space>
        {mail.missingEmailCount > 0 && (
          <div>
            <Typography.Text type="warning">
              {mail.missingEmailCount} 个启用账号未填写邮箱，相关通知不会入队。示例：{mail.missingEmailAccounts.map((account) => `${account.realName}（${account.employeeNo}）`).join('、') || '请查看操作日志'}
            </Typography.Text>
            <Button type="text" size="small" onClick={onViewLogs}>查看操作日志</Button>
          </div>
        )}
        {mail.recent.length > 0 && (
          <Table
            rowKey="id"
            size="small"
            pagination={false}
            data={mail.recent}
            scroll={{ x: 620 }}
            columns={[
              { title: '时间', dataIndex: 'createdAt', width: 160, render: formatTime },
              { title: '结果', dataIndex: 'action', width: 150, render: (action: string) => <Tag color={action === 'EMAIL_SENT' ? 'green' : action === 'EMAIL_FAILED' ? 'red' : action === 'EMAIL_CANCELLED_STALE' ? 'gray' : 'orange'}>{MAIL_ACTION_LABELS[action] || `未知动作：${action}`}</Tag> },
              { title: '说明', width: 300, render: (_value: unknown, row: MailRecent) => mailRecentSummary(row) },
            ]}
          />
        )}
      </div>
    </Card>
  )
}
