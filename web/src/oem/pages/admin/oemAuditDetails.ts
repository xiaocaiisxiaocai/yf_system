import type { AuditRow } from '../../api/types'
import { DIRECTION_LABEL, VALIDATION } from '../../components/statusLabels'
import { fmtTime } from '../../../api/types'
import {
  actorIdentity, displayChanges, formatAuditValue, formatByteSize, formatChangeValue, targetIdentity,
  type ActionMeta, type AuditLogRow, type DetailNote, type DisplayIdentity,
} from '../../../pages/system/auditLogDetails'
import type { AuditPresenter } from '../../../components/AuditLogParts'

const action = (label: string, categoryCode: string, categoryLabel: string, color: string): ActionMeta => ({
  label, categoryCode, categoryLabel, color,
})

/** OEM 审计动作编码 → 中文名称与分类；编码取自服务端 OemAuditWriter 的全部写入点。 */
export const OEM_ACTIONS: Record<string, ActionMeta> = {
  OEM_LOGIN: action('登录成功', 'AUTH', '认证安全', 'green'),
  OEM_LOGIN_FAILED: action('登录失败', 'AUTH', '认证安全', 'red'),
  OEM_LOGOUT: action('退出登录', 'AUTH', '认证安全', 'gray'),
  OEM_PASSWORD_CHANGE: action('修改密码', 'AUTH', '认证安全', 'orange'),
  OEM_TRANSFER_CREATE: action('创建传递单', 'TRANSFER', '传递单', 'arcoblue'),
  OEM_TRANSFER_UPDATE: action('更新传递单', 'TRANSFER', '传递单', 'purple'),
  OEM_TRANSFER_SEND: action('提交传递单', 'TRANSFER', '传递单', 'arcoblue'),
  OEM_TRANSFER_RELEASE: action('发送完成', 'TRANSFER', '传递单', 'green'),
  OEM_TRANSFER_REJECT: action('传递单驳回', 'TRANSFER', '传递单', 'red'),
  OEM_TRANSFER_BLOCK: action('文件阻断', 'TRANSFER', '传递单', 'red'),
  OEM_TRANSFER_CANCEL: action('终止传递单', 'TRANSFER', '传递单', 'red'),
  OEM_TRANSFER_ABANDON: action('废弃草稿', 'TRANSFER', '传递单', 'gray'),
  OEM_APPROVAL_APPROVE: action('审批通过', 'APPROVAL', '审批', 'green'),
  OEM_APPROVAL_REASSIGN: action('转交审批', 'APPROVAL', '审批', 'purple'),
  OEM_APPROVAL_BLOCKED: action('审批异常', 'APPROVAL', '审批', 'orange'),
  OEM_FILE_UPLOAD: action('上传文件', 'FILE', '文件', 'arcoblue'),
  OEM_FILE_REMOVE: action('移除文件', 'FILE', '文件', 'orange'),
  OEM_FILE_PREVIEW: action('预览文件', 'FILE', '文件', 'cyan'),
  OEM_DOWNLOAD_START: action('开始下载', 'FILE', '文件', 'cyan'),
  OEM_DOWNLOAD_COMPLETE: action('下载完成', 'FILE', '文件', 'cyan'),
  OEM_FILE_FIRST_RECEIPT: action('首次接收', 'FILE', '文件', 'green'),
  OEM_VALIDATION_RESULT: action('文件校验结果', 'FILE', '文件', 'arcoblue'),
  OEM_PROMOTION_FAILED: action('文件入库失败', 'FILE', '文件', 'red'),
  OEM_COMPANY_CREATE: action('创建厂商', 'DIRECTORY', '厂商与账号', 'arcoblue'),
  OEM_COMPANY_UPDATE: action('更新厂商', 'DIRECTORY', '厂商与账号', 'purple'),
  OEM_COMPANY_STATUS: action('变更厂商状态', 'DIRECTORY', '厂商与账号', 'orange'),
  OEM_COMPANY_DELETE: action('删除厂商', 'DIRECTORY', '厂商与账号', 'red'),
  OEM_ACCOUNT_CREATE: action('创建厂商账号', 'DIRECTORY', '厂商与账号', 'arcoblue'),
  OEM_ACCOUNT_UPDATE: action('更新厂商账号', 'DIRECTORY', '厂商与账号', 'purple'),
  OEM_ACCOUNT_STATUS: action('变更厂商账号状态', 'DIRECTORY', '厂商与账号', 'orange'),
  OEM_ACCOUNT_RESET_PASSWORD: action('重置厂商账号密码', 'DIRECTORY', '厂商与账号', 'orange'),
  OEM_ACCOUNT_DELETE: action('删除厂商账号', 'DIRECTORY', '厂商与账号', 'red'),
  OEM_FLOW_TEMPLATE_CREATE: action('创建审批流程', 'POLICY', '策略与设置', 'arcoblue'),
  OEM_FLOW_TEMPLATE_UPDATE: action('更新审批流程', 'POLICY', '策略与设置', 'purple'),
  OEM_FLOW_TEMPLATE_NODES: action('调整审批节点', 'POLICY', '策略与设置', 'purple'),
  OEM_FLOW_TEMPLATE_SCOPES: action('调整流程适用组织', 'POLICY', '策略与设置', 'purple'),
  OEM_RETENTION_TEMPLATE_CREATE: action('创建删除策略', 'POLICY', '策略与设置', 'arcoblue'),
  OEM_RETENTION_TEMPLATE_UPDATE: action('更新删除策略', 'POLICY', '策略与设置', 'purple'),
  OEM_SETTINGS_UPDATE: action('更新设置', 'POLICY', '策略与设置', 'orange'),
  DEPT_LEADER_CHANGE: action('变更部门负责人', 'POLICY', '策略与设置', 'purple'),
  OEM_PURGE: action('删除文件内容', 'SYSTEM', '系统维护', 'gray'),
  OEM_PURGE_FAILING: action('文件删除失败', 'SYSTEM', '系统维护', 'red'),
  OEM_RECONCILE_COMPLETED: action('存储核对完成', 'SYSTEM', '系统维护', 'green'),
  OEM_RECONCILE_FILE_RELOCATED: action('文件位置已修复', 'SYSTEM', '系统维护', 'arcoblue'),
  OEM_RECONCILE_ORPHAN_PURGED: action('清理孤立文件', 'SYSTEM', '系统维护', 'gray'),
  OEM_FILE_STORAGE_LOST: action('文件存储丢失', 'SYSTEM', '系统维护', 'red'),
  OEM_FILE_MISSING_UNVERIFIED: action('文件缺失待核实', 'SYSTEM', '系统维护', 'red'),
}

export const OEM_CATEGORY_LABELS: Record<string, string> = {
  AUTH: '认证安全', TRANSFER: '传递单', APPROVAL: '审批', FILE: '文件',
  DIRECTORY: '厂商与账号', POLICY: '策略与设置', SYSTEM: '系统维护',
}

/** 「具体操作」下拉按分类分组，分类顺序与上表一致。 */
export const OEM_ACTION_GROUPS = Object.entries(OEM_CATEGORY_LABELS).map(([code, label]) => ({
  code, label,
  options: Object.entries(OEM_ACTIONS).filter(([, meta]) => meta.categoryCode === code),
}))

const OEM_TARGET_LABELS: Record<string, string> = {
  oem_transfer: '传递单', oem_file: '文件', oem_company: '厂商', oem_account: '厂商账号',
  oem_flow_template: '审批流程', oem_retention_template: '删除策略', oem_settings: '设置', oem_storage: '存储',
  department: '组织', user: '用户',
}

const PURPOSE_LABELS: Record<string, string> = { RECIPIENT: '接收方', SENDER: '发送方', REVIEW: '审批人', NONE: '无', DOWNLOAD: '下载', PREVIEW: '预览' }
const SEND_APPROVAL_LABELS: Record<string, string> = { REQUIRED: '需要审批', NOT_REQUIRED: '无需审批', SKIPPED: '按策略免审' }
const RETENTION_LABELS: Record<string, string> = {
  KEEP: '不自动删除', AFTER_RELEASE: '发布后到期删除', AFTER_FIRST_RECEIPT: '首次接收后宽限删除',
  FIRST_RECEIPT_OR_DEADLINE: '首次接收宽限或最晚期限',
}
const PURGE_REASON_LABELS: Record<string, string> = {
  RETENTION: '保留期到期', BLOCKED: '文件阻断', DRAFT_EXPIRED: '草稿过期', DRAFT_DELETED: '草稿删除',
  FILE_REMOVED: '文件被移除', CANCELLED: '传递单终止', REJECTED: '传递单驳回', ORPHAN: '孤立文件',
}
const SETTINGS_GROUP_LABELS: Record<string, string> = { file: '文件策略', notify: '邮件提醒', File: '文件策略', Notify: '邮件提醒' }
const SESSION_CAP = 'SESSION_CAP'

function text(value: unknown): string | undefined {
  return typeof value === 'string' && value.trim() ? value.trim() : undefined
}

function hasValue(value: unknown) {
  return value != null && value !== ''
}

function label(map: Record<string, string>, value: unknown) {
  const key = text(value)
  return key ? map[key] ?? key : undefined
}

function validationLabel(value: unknown) {
  const key = text(value)
  return key ? VALIDATION[key as keyof typeof VALIDATION]?.[0] ?? key : undefined
}

function approvalLabel(value: unknown) {
  return label(SEND_APPROVAL_LABELS, value)
}

function accessSummary(row: AuditLogRow) {
  const detail = row.detail ?? {}
  if (row.action === 'OEM_FILE_PREVIEW') {
    const who = label(PURPOSE_LABELS, detail.purpose)
    return who ? `${who}预览` : '-'
  }
  if (detail.purpose === 'REVIEW') return '审批人查看'
  if (typeof detail.recipientSide === 'boolean') return detail.recipientSide ? '接收方下载' : '发送方下载'
  return '-'
}

function directionLabel(value: unknown) {
  const key = text(value)
  return key ? DIRECTION_LABEL[key as keyof typeof DIRECTION_LABEL] ?? key : undefined
}

function timeOrNever(value: unknown, never: string) {
  return text(value) ? fmtTime(String(value)) : never
}

/** OEM 行按协作日志的行结构解读：只有内部账号的 actorId 才是协作用户 ID。 */
export function toAuditLogRow(row: AuditRow): AuditLogRow {
  return {
    id: row.id,
    userId: row.actorRealm === 'internal' ? row.actorId : null,
    employeeNo: row.employeeNo,
    action: row.action,
    targetType: row.targetType,
    targetId: row.targetId,
    detail: row.detail as AuditLogRow['detail'],
    ip: row.ip,
    createdAt: row.createdAt,
    actorRealm: row.actorRealm,
  }
}

function oemActor(row: AuditLogRow): DisplayIdentity {
  const identity = actorIdentity(row)
  if (row.actorRealm !== 'oem') return identity
  const employeeNo = text(row.employeeNo)
  return { ...identity, identifier: [employeeNo ? `账号 ${employeeNo}` : undefined, '厂商账号'].filter(Boolean).join(' · ') }
}

function changeSummary(row: AuditLogRow) {
  const changes = displayChanges(row)
  if (!changes.length) return undefined
  const first = changes[0]
  return `${first.label}：${formatChangeValue(first, 'before')} → ${formatChangeValue(first, 'after')}${changes.length > 1 ? `；另有 ${changes.length - 1} 项变化` : ''}`
}

function join(...parts: (string | undefined | false)[]) {
  return parts.filter(Boolean).join(' · ')
}

export function oemDetailSummary(row: AuditLogRow): string {
  const detail = row.detail ?? {}
  const reason = text(detail.reason)
  switch (row.action) {
    case 'OEM_LOGIN':
      return detail.reason === SESSION_CAP ? `登录会话超过上限，已退出最早的 ${formatAuditValue(detail.evictedSessions)} 个会话` : '-'
    case 'OEM_TRANSFER_CREATE':
      return directionLabel(detail.direction) ?? '-'
    case 'OEM_TRANSFER_SEND':
      return join(hasValue(detail.files) && `${formatAuditValue(detail.files)} 个文件`, approvalLabel(detail.approval),
        label(RETENTION_LABELS, detail.retentionMode)) || '-'
    case 'OEM_TRANSFER_RELEASE':
      return join(text(detail.expiresAt) ? `有效期至 ${fmtTime(String(detail.expiresAt))}` : '长期有效', label(RETENTION_LABELS, detail.retentionMode))
    case 'OEM_FILE_UPLOAD':
    case 'OEM_FILE_REMOVE':
    case 'OEM_RECONCILE_FILE_RELOCATED':
    case 'OEM_FILE_STORAGE_LOST':
    case 'OEM_FILE_MISSING_UNVERIFIED':
      return hasValue(detail.sizeBytes) ? formatByteSize(detail.sizeBytes) : '-'
    case 'OEM_PURGE':
      return join(label(PURGE_REASON_LABELS, detail.reason), hasValue(detail.sizeBytes) && formatByteSize(detail.sizeBytes)) || '-'
    case 'OEM_PURGE_FAILING':
      return join(hasValue(detail.attempts) && `第 ${formatAuditValue(detail.attempts)} 次失败`, reason) || '-'
    case 'OEM_VALIDATION_RESULT':
      return join(validationLabel(detail.verdict), reason) || '-'
    case 'OEM_FILE_PREVIEW':
    case 'OEM_DOWNLOAD_START':
    case 'OEM_DOWNLOAD_COMPLETE':
      return accessSummary(row)
    case 'OEM_FILE_FIRST_RECEIPT':
      return `计划删除：${timeOrNever(detail.purgeDueAt, '不自动删除')}`
    case 'OEM_RECONCILE_COMPLETED':
      return `缺失 ${formatAuditValue(detail.missing)}，完成删除 ${formatAuditValue(detail.completedPurges)}，清理孤立 ${formatAuditValue(detail.orphansDeleted)}，恢复入库 ${formatAuditValue(detail.resumedPromotions)}`
    case 'OEM_RECONCILE_ORPHAN_PURGED':
      return join(text(detail.storedName), hasValue(detail.size) && formatByteSize(detail.size)) || '-'
    case 'OEM_ACCOUNT_CREATE':
      return join(text(detail.companyName), text(detail.email)) || '-'
    case 'OEM_ACCOUNT_DELETE':
      return join(text(detail.realName), text(detail.employeeNo)) || '-'
    case 'OEM_FLOW_TEMPLATE_CREATE':
      return join(`${formatAuditValue(detail.nodes)} 个审批节点`, `${formatAuditValue(detail.scopes)} 个适用组织`, detail.isDefault === true && '默认流程')
    case 'OEM_RETENTION_TEMPLATE_CREATE':
      return label(RETENTION_LABELS, detail.mode) ?? '-'
    case 'OEM_SETTINGS_UPDATE':
      return join(label(SETTINGS_GROUP_LABELS, detail.group), changeSummary(row)) || '-'
    default:
      break
  }
  const changes = changeSummary(row)
  if (changes) return reason ? `${changes}；原因：${reason}` : changes
  if (reason) return reason
  if (typeof detail.revokedSessions === 'number' && detail.revokedSessions > 0) return `已注销 ${detail.revokedSessions} 个登录会话`
  if (Array.isArray(detail.changes) && detail.changes.length === 0 && /_(UPDATE|STATUS)$/.test(row.action)) return '已保存，未发生字段变化'
  return '-'
}

const NOTE_FIELDS: [key: string, label: string, format?: (value: unknown) => string | undefined][] = [
  ['transferId', '所属传递单', (value) => `#${formatAuditValue(value)}`],
  ['direction', '传递方向', directionLabel],
  ['files', '文件数', (value) => `${formatAuditValue(value)} 个`],
  ['approval', '审批', approvalLabel],
  ['retentionMode', '删除策略', (value) => label(RETENTION_LABELS, value)],
  ['mode', '删除方式', (value) => label(RETENTION_LABELS, value)],
  ['expiresAt', '有效期至', (value) => timeOrNever(value, '长期有效')],
  ['purgeDueAt', '计划删除', (value) => timeOrNever(value, '不自动删除')],
  ['verdict', '校验结果', validationLabel],
  ['purpose', '用途', (value) => label(PURPOSE_LABELS, value)],
  ['recipientSide', '接收方身份', formatAuditValue],
  ['sizeBytes', '文件大小', formatByteSize],
  ['size', '文件大小', formatByteSize],
  ['sha256', 'SHA-256'],
  ['manifest', '清单摘要'],
  ['attempts', '尝试次数'],
  ['companyName', '所属厂商'],
  ['email', '邮箱'],
  ['revokedSessions', '注销会话', (value) => `${formatAuditValue(value)} 个`],
  ['evictedSessions', '退出会话', (value) => `${formatAuditValue(value)} 个`],
  ['group', '设置分组', (value) => label(SETTINGS_GROUP_LABELS, value)],
  ['storedName', '存储文件名'],
]

const DANGER_ACTIONS = new Set([
  'OEM_TRANSFER_REJECT', 'OEM_TRANSFER_BLOCK', 'OEM_TRANSFER_CANCEL', 'OEM_APPROVAL_BLOCKED', 'OEM_PURGE_FAILING',
  'OEM_PROMOTION_FAILED', 'OEM_FILE_STORAGE_LOST', 'OEM_FILE_MISSING_UNVERIFIED',
])

export function oemDetailNotes(row: AuditLogRow): DetailNote[] {
  const detail = row.detail
  if (!detail) return []
  const notes: DetailNote[] = []
  for (const [key, title, format] of NOTE_FIELDS) {
    const value = detail[key]
    if (!hasValue(value) && key !== 'expiresAt' && key !== 'purgeDueAt') continue
    if (!Object.hasOwn(detail, key)) continue
    const formatted = format ? format(value) : formatAuditValue(value)
    if (formatted) notes.push({ label: title, value: formatted, tone: 'normal' })
  }
  const reason = text(detail.reason)
  if (reason && reason !== SESSION_CAP) {
    const title = row.action === 'OEM_PURGE' ? '删除原因' : DANGER_ACTIONS.has(row.action) ? '原因' : '说明'
    notes.push({ label: title, value: label(PURGE_REASON_LABELS, reason) ?? reason, tone: DANGER_ACTIONS.has(row.action) ? 'danger' : 'normal' })
  }
  return notes
}

export const OEM_AUDIT_PRESENTER: AuditPresenter = {
  meta: (row) => OEM_ACTIONS[row.action] || { label: row.action, categoryCode: '', categoryLabel: '其他', color: 'gray' },
  actor: oemActor,
  target: (row) => targetIdentity(row, OEM_TARGET_LABELS),
  summary: oemDetailSummary,
  notes: oemDetailNotes,
}
