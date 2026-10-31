export const MB = 1024 * 1024

export type NotificationEventKey =
  | 'messageCreated' | 'fileUploaded' | 'projectStarted' | 'projectTerminated' | 'projectRestarted'
  | 'projectReopened' | 'projectMaterialReviewed' | 'projectDueSoon' | 'projectOverdue'

export interface NotificationDuePolicy {
  internalEnabled: boolean
  supplierEnabled: boolean
  remindDaysBefore: number
}

export const DUE_REMIND_DAYS = { min: 1, max: 30, fallback: 3 }

export interface NotificationPolicy {
  globalEnabled: boolean
  internalEnabled: boolean
  supplierEnabled: boolean
  events: Record<NotificationEventKey, boolean>
  due: NotificationDuePolicy
}

export const EMPTY_NOTIFICATION_POLICY: NotificationPolicy = {
  globalEnabled: true,
  internalEnabled: true,
  supplierEnabled: true,
  events: {
    messageCreated: true,
    fileUploaded: true,
    projectStarted: true,
    projectTerminated: true,
    projectRestarted: true,
    projectReopened: true,
    projectMaterialReviewed: true,
    projectDueSoon: true,
    projectOverdue: true,
  },
  due: { internalEnabled: true, supplierEnabled: true, remindDaysBefore: DUE_REMIND_DAYS.fallback },
}

export const NOTIFICATION_CONFIG_KEYS = new Set([
  'notify.enabled',
  'notify.internal.enabled',
  'notify.supplier.enabled',
  'notify.event.message_created',
  'notify.event.file_uploaded',
  'notify.event.project_started',
  'notify.event.project_terminated',
  'notify.event.project_restarted',
  'notify.event.project_reopened',
  'notify.event.project_material_reviewed',
  'notify.event.project_due_soon',
  'notify.event.project_overdue',
  'notify.due.internal.enabled',
  'notify.due.supplier.enabled',
  'notify.due.remind_days_before',
])

export const NOTIFICATION_EVENT_ITEMS: Array<{ key: NotificationEventKey; title: string; description: string }> = [
  { key: 'messageCreated', title: '新留言', description: '对方在项目中发表留言时提醒' },
  { key: 'fileUploaded', title: '新文件', description: '对方在项目中上传文件时提醒' },
  { key: 'projectStarted', title: '开始子项目', description: '子项目开始时提醒供应商' },
  { key: 'projectTerminated', title: '终止子项目', description: '子项目终止时提醒供应商' },
  { key: 'projectRestarted', title: '重新开始子项目', description: '已终止子项目重新开始时提醒供应商' },
  { key: 'projectReopened', title: '重开子项目', description: '已完成子项目重开返工时提醒供应商' },
  { key: 'projectMaterialReviewed', title: '资料确认', description: '负责人确认或退回回传资料时提醒供应商' },
  { key: 'projectDueSoon', title: '即将到期', description: '需求完成时间前按提前天数提醒一次' },
  { key: 'projectOverdue', title: '已逾期', description: '超过需求完成时间后提醒一次' },
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

export const GB = 1024 * MB

/** Numeric parameters edited with a unit: the stored value is the displayed value times scale. */
export interface NumericConfig {
  min: number
  max: number
  unit: string
  scale?: number
  precision?: number
  step?: number
}

export const CONFIG_META: Record<string, { name: string; description: string; hint?: string; numeric?: NumericConfig }> = {
  'upload.allowed_exts': { name: '允许上传类型', description: '允许上传的文件扩展名', hint: '使用英文逗号分隔' },
  'upload.chunk_size': { name: '上传分片大小', description: '每个上传分片的大小', numeric: { min: 0.25, max: 64, unit: 'MB', scale: MB, precision: 2, step: 0.25 } },
  'upload.max_file_size': { name: '单文件大小上限', description: '单个文件允许的最大大小', numeric: { min: 1, max: 20 * 1024, unit: 'MB', scale: MB } },
  'upload.max_active_sessions_per_user': { name: '每人同时上传数', description: '每个账号未完成的上传任务上限', numeric: { min: 1, max: 1000, unit: '个' } },
  'upload.max_pending_bytes_per_user': { name: '每人未完成上传总量', description: '每个账号未完成上传的文件总大小上限', numeric: { min: 1, max: 16 * 1024, unit: 'GB', scale: GB } },
  'retention.audit_days': { name: '操作日志保留天数', description: '超过天数的操作日志自动清理', numeric: { min: 1, max: 3650, unit: '天' } },
  'retention.mail_days': { name: '邮件记录保留天数', description: '已发送、失败或取消的邮件记录保留天数', numeric: { min: 1, max: 3650, unit: '天' } },
  'retention.mail_pending_days': { name: '待发送邮件有效天数', description: '超过天数仍未发出的邮件自动取消', numeric: { min: 1, max: 365, unit: '天' } },
  'retention.upload_session_days': { name: '上传记录保留天数', description: '已结束的上传会话记录及其临时目录保留天数', numeric: { min: 30, max: 3650, unit: '天' } },
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
  const due = raw.due && typeof raw.due === 'object' && !Array.isArray(raw.due)
    ? raw.due as Partial<NotificationDuePolicy>
    : {}
  return {
    globalEnabled: raw.globalEnabled !== false,
    internalEnabled: raw.internalEnabled !== false,
    supplierEnabled: raw.supplierEnabled !== false,
    events: {
      messageCreated: events.messageCreated !== false,
      fileUploaded: events.fileUploaded !== false,
      projectStarted: events.projectStarted !== false,
      projectTerminated: events.projectTerminated !== false,
      projectRestarted: events.projectRestarted !== false,
      projectReopened: events.projectReopened !== false,
      projectMaterialReviewed: events.projectMaterialReviewed !== false,
      projectDueSoon: events.projectDueSoon !== false,
      projectOverdue: events.projectOverdue !== false,
    },
    due: {
      internalEnabled: due.internalEnabled !== false,
      supplierEnabled: due.supplierEnabled !== false,
      remindDaysBefore: Number.isInteger(due.remindDaysBefore)
        && due.remindDaysBefore! >= DUE_REMIND_DAYS.min && due.remindDaysBefore! <= DUE_REMIND_DAYS.max
        ? due.remindDaysBefore!
        : DUE_REMIND_DAYS.fallback,
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
