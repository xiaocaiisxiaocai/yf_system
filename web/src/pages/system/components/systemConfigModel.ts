export const MB = 1024 * 1024

export type NotificationEventKey = 'messageCreated' | 'fileUploaded' | 'projectSubmitted' | 'projectConfirmed' | 'projectRejected' | 'projectWithdrawn'

export interface NotificationPolicy {
  globalEnabled: boolean
  internalEnabled: boolean
  supplierEnabled: boolean
  events: Record<NotificationEventKey, boolean>
}

export const EMPTY_NOTIFICATION_POLICY: NotificationPolicy = {
  globalEnabled: true,
  internalEnabled: true,
  supplierEnabled: true,
  events: {
    messageCreated: true,
    fileUploaded: true,
    projectSubmitted: true,
    projectConfirmed: true,
    projectRejected: true,
    projectWithdrawn: true,
  },
}

export const NOTIFICATION_CONFIG_KEYS = new Set([
  'notify.enabled',
  'notify.internal.enabled',
  'notify.supplier.enabled',
  'notify.event.message_created',
  'notify.event.file_uploaded',
  'notify.event.project_submitted',
  'notify.event.project_confirmed',
  'notify.event.project_rejected',
  'notify.event.project_withdrawn',
])

export const NOTIFICATION_EVENT_ITEMS: Array<{ key: NotificationEventKey; title: string; description: string }> = [
  { key: 'messageCreated', title: '新留言', description: '对方在项目中发表留言时提醒' },
  { key: 'fileUploaded', title: '新文件', description: '对方在项目中上传文件时提醒' },
  { key: 'projectSubmitted', title: '提交验收', description: '供应商提交验收申请时提醒内部员工' },
  { key: 'projectConfirmed', title: '验收通过', description: '验收完成时提醒最近提交人' },
  { key: 'projectRejected', title: '验收驳回', description: '验收被驳回时提醒最近提交人' },
  { key: 'projectWithdrawn', title: '撤回验收', description: '验收申请撤回时提醒相关参与人' },
]

export interface SmtpSettings {
  host: string
  port: number
  username: string
  from: string
  security: 'Auto' | 'SslOnConnect' | 'StartTls'
  hasPassword: boolean
  configured: boolean
  passwordNeedsUpdate: boolean
}

export const EMPTY_SMTP: SmtpSettings = {
  host: '',
  port: 465,
  username: '',
  from: '',
  security: 'Auto',
  hasPassword: false,
  configured: false,
  passwordNeedsUpdate: false,
}

export const CONFIG_META: Record<string, { name: string; description: string; hint?: string }> = {
  'upload.allowed_exts': { name: '允许上传类型', description: '允许上传的文件扩展名', hint: '使用英文逗号分隔' },
  'upload.chunk_size': { name: '上传分片大小', description: '每个上传分片的大小' },
  'upload.max_file_size': { name: '单文件大小上限', description: '单个文件允许的最大大小' },
}

export interface Cfg {
  key: string
  value: string
  description?: string
  updatedAt?: string
}

export interface MailDetail {
  eventType?: string
  status?: string
  reason?: string
  employeeNo?: string
  realName?: string
  recipient?: string
  retryCount?: number
  error?: string
}

export interface MailRecent {
  id: number
  action: 'EMAIL_SENT' | 'EMAIL_FAILED' | 'EMAIL_RETRY' | 'EMAIL_SKIPPED_MISSING_EMAIL' | 'EMAIL_CANCELLED_STALE' | string
  targetType?: string | null
  targetId?: string | null
  detail?: MailDetail | null
  createdAt: string
}

export interface MailStatus {
  configured: boolean
  host?: string | null
  port?: number | null
  from?: string | null
  notificationsEnabled: boolean
  notificationPolicy: NotificationPolicy
  queue: { pending: number; sending: number; sent: number; failed: number; cancelled: number }
  latestSentAt?: string | null
  latestFailedAt?: string | null
  missingEmailCount: number
  missingEmailAccounts: Array<{
    userId: number
    employeeNo: string
    realName: string
    userType: string
    status: string
  }>
  recent: MailRecent[]
}

export const EMPTY_MAIL_STATUS: MailStatus = {
  configured: false,
  notificationsEnabled: true,
  notificationPolicy: EMPTY_NOTIFICATION_POLICY,
  queue: { pending: 0, sending: 0, sent: 0, failed: 0, cancelled: 0 },
  missingEmailCount: 0,
  missingEmailAccounts: [],
  recent: [],
}

export function normalizeNotificationPolicy(value: unknown): NotificationPolicy {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return EMPTY_NOTIFICATION_POLICY
  const raw = value as Partial<NotificationPolicy>
  const events = raw.events && typeof raw.events === 'object' && !Array.isArray(raw.events)
    ? raw.events as Partial<Record<NotificationEventKey, boolean>>
    : {}
  return {
    globalEnabled: raw.globalEnabled !== false,
    internalEnabled: raw.internalEnabled !== false,
    supplierEnabled: raw.supplierEnabled !== false,
    events: {
      messageCreated: events.messageCreated !== false,
      fileUploaded: events.fileUploaded !== false,
      projectSubmitted: events.projectSubmitted !== false,
      projectConfirmed: events.projectConfirmed !== false,
      projectRejected: events.projectRejected !== false,
      projectWithdrawn: events.projectWithdrawn !== false,
    },
  }
}

export function normalizeMailStatus(value: unknown): MailStatus {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return EMPTY_MAIL_STATUS
  const raw = value as Partial<MailStatus>
  return {
    ...EMPTY_MAIL_STATUS,
    ...raw,
    notificationPolicy: normalizeNotificationPolicy(raw.notificationPolicy),
    queue: { ...EMPTY_MAIL_STATUS.queue, ...(raw.queue || {}) },
    missingEmailAccounts: Array.isArray(raw.missingEmailAccounts) ? raw.missingEmailAccounts : [],
    recent: Array.isArray(raw.recent) ? raw.recent : [],
  }
}

export const MAIL_ACTION_LABELS: Record<string, string> = {
  EMAIL_SENT: '发送成功',
  EMAIL_FAILED: '发送失败',
  EMAIL_RETRY: '发送重试',
  EMAIL_SKIPPED_MISSING_EMAIL: '未入队：缺少邮箱',
  EMAIL_CANCELLED_STALE: '已取消：事件失效',
}

export function mailRecentSummary(row: MailRecent): string {
  const detail = row.detail || {}
  if (row.action === 'EMAIL_SKIPPED_MISSING_EMAIL') {
    return `${detail.realName || '账号'}（${detail.employeeNo || `ID ${row.targetId || '-'}`}）未填写邮箱`
  }
  if (row.action === 'EMAIL_RETRY') {
    return `${detail.error || '发送失败'}${detail.retryCount ? `，已安排第 ${detail.retryCount} 次重试` : ''}`
  }
  if (row.action === 'EMAIL_FAILED') {
    return `${detail.error || '发送失败'}${detail.retryCount ? `，第 ${detail.retryCount} 次` : ''}`
  }
  if (row.action === 'EMAIL_CANCELLED_STALE') {
    return detail.reason || '通知事件已失效，邮件发送已取消'
  }
  if (row.action === 'EMAIL_SENT') {
    return detail.recipient ? `收件人 ${detail.recipient}` : '邮件已发送'
  }
  return detail.reason || detail.error || (detail.recipient ? `处理对象 ${detail.recipient}` : '邮件处理结果未识别')
}
