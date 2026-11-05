import type { ReactNode } from 'react'
import BusinessMailLogPanel from './BusinessMailLogPanel'
import { Button, Card, Input, InputNumber, Select, Space, Switch, Radio, Tag, Typography } from '@arco-design/web-react'
import PasswordInput from '../../../components/PasswordInput'
import SmtpTestPanel from './SmtpTestPanel'
import {
  DUE_REMIND_DAYS,
  NOTIFICATION_EVENT_ITEMS,
  type MailStatus,
  type NotificationPolicy,
  type SmtpSettings,
} from './systemConfigModel'

interface MailSettingsPanelProps {
  rulesBusiness?: 'robot' | 'oem'
  onRulesBusinessChange?: (value: 'robot' | 'oem') => void
  oemRules?: ReactNode
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
  onTested?: () => Promise<void>
  onSaveNotifications: () => void
  onViewLogs: () => void
}

export function renderMailSettingsPanel({
  rulesBusiness = 'robot',
  onRulesBusinessChange,
  oemRules,
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
  onTested,
  onSaveNotifications,
  onViewLogs,
}: MailSettingsPanelProps, view: 'smtp' | 'rules' = 'smtp') {
  if (!mail) {
    return (
      <Card className="page-card system-mail-card" title="SMTP 配置与邮件提醒">
        <Typography.Text type="secondary">邮件配置暂不可用，请刷新页面。</Typography.Text>
      </Card>
    )
  }

  if (view === 'rules') return (
    <Card className="page-card system-mail-card system-reminder-card" title="邮件提醒规则"
      extra={oemRules && <Radio.Group type="button" aria-label="邮件提醒业务" value={rulesBusiness} onChange={onRulesBusinessChange}><Radio value="robot">Robot 仿真协作</Radio><Radio value="oem">ODM/OEMA</Radio></Radio.Group>}>
      <div className="system-rule-business-pane" hidden={Boolean(oemRules) && rulesBusiness === 'oem'}>
        <div className="system-notification-settings">
          <div className="system-notification-heading">
            <div>
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
            <div className="system-notification-side">
            <div className="system-notification-panel">
              <div className="system-notification-panel-title">通知对象</div>
              <div className="system-notification-row">
                <div><Typography.Text>内部员工</Typography.Text><Typography.Text type="secondary">公司内部项目负责人</Typography.Text></div>
                <Switch aria-label="内部员工邮件通知" checked={notificationDraft.internalEnabled} disabled={notificationSaving} onChange={(checked) => onNotificationDraftChange((current) => ({ ...current, internalEnabled: checked }))} />
              </div>
              <div className="system-notification-row">
                <div><Typography.Text>外部企业（供应商）</Typography.Text><Typography.Text type="secondary">厂商的启用联络人</Typography.Text></div>
                <Switch aria-label="外部企业邮件通知" checked={notificationDraft.supplierEnabled} disabled={notificationSaving} onChange={(checked) => onNotificationDraftChange((current) => ({ ...current, supplierEnabled: checked }))} />
              </div>
            </div>
            <div className="system-notification-panel">
              <div className="system-notification-panel-title">到期提醒</div>
              <div className="system-notification-row">
                <div><Typography.Text>提前天数</Typography.Text><Typography.Text type="secondary">需求完成时间前几天发送即将到期提醒</Typography.Text></div>
                <InputNumber
                  aria-label="到期提前提醒天数"
                  size="small"
                  style={{ width: 96 }}
                  min={DUE_REMIND_DAYS.min}
                  max={DUE_REMIND_DAYS.max}
                  precision={0}
                  suffix="天"
                  value={notificationDraft.due.remindDaysBefore}
                  disabled={notificationSaving}
                  onChange={(value) => onNotificationDraftChange((current) => ({
                    ...current,
                    due: { ...current.due, remindDaysBefore: typeof value === 'number' ? value : DUE_REMIND_DAYS.fallback },
                  }))}
                />
              </div>
              <div className="system-notification-row">
                <div><Typography.Text>负责人</Typography.Text><Typography.Text type="secondary">子项目负责人</Typography.Text></div>
                <Switch aria-label="到期提醒发给负责人" checked={notificationDraft.due.internalEnabled} disabled={notificationSaving} onChange={(checked) => onNotificationDraftChange((current) => ({ ...current, due: { ...current.due, internalEnabled: checked } }))} />
              </div>
              <div className="system-notification-row">
                <div><Typography.Text>厂商</Typography.Text><Typography.Text type="secondary">厂商全部启用联络人</Typography.Text></div>
                <Switch aria-label="到期提醒发给厂商" checked={notificationDraft.due.supplierEnabled} disabled={notificationSaving} onChange={(checked) => onNotificationDraftChange((current) => ({ ...current, due: { ...current.due, supplierEnabled: checked } }))} />
              </div>
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
            <Typography.Text type="secondary">关闭后不再创建对应邮件；已入队的邮件发送前会再次检查。</Typography.Text>
          </Space>
        </div>
      </div>
      {oemRules && <div className="system-rule-business-pane" hidden={rulesBusiness !== 'oem'}>{oemRules}</div>}
    </Card>
  )

  return (
    <div className="system-mail-workspace">
      <Card className="page-card system-mail-card system-mail-editor" title="邮件发送"
        extra={<Tag color={mail.configured ? 'green' : 'red'}>{mail.configured ? 'SMTP 已配置' : 'SMTP 未配置'}</Tag>}>
        <div className="system-mail-body">
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
          <Typography.Text type="secondary">授权码不回显</Typography.Text>
        </Space>
        <SmtpTestPanel configured={smtp.configured} dirty={smtpDirty} saving={smtpSaving} configuration={smtp} onTested={onTested} />
        </div>
      </Card>
      <BusinessMailLogPanel mail={mail} formatTime={formatTime} onRefresh={onTested} onViewLogs={onViewLogs} />
    </div>
  )
}
