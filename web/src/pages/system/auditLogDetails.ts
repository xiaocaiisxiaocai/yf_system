export type NameSource = 'snapshot' | 'current' | 'unknown'

export interface AuditContext {
  actorName?: string | null
  targetName?: string | null
  requestId?: string | null
  source?: 'HTTP' | 'SYSTEM' | string | null
}

export interface AuditChange {
  field: string
  label: string
  before: unknown
  after: unknown
}

export interface AuditPermission {
  id?: number | string | null
  code?: string | null
  name?: string | null
}

export interface AuditMember {
  id?: number | string | null
  employeeNo?: string | null
  realName?: string | null
}

export type AuditDetail = Record<string, unknown> & {
  auditContext?: AuditContext | null
  changes?: AuditChange[] | null
  addedPermissions?: AuditPermission[] | null
  removedPermissions?: AuditPermission[] | null
  addedMembers?: AuditMember[] | null
  removedMembers?: AuditMember[] | null
}

export interface AuditLogRow {
  id: number
  userId?: number | null
  employeeNo?: string | null
  actorName?: string | null
  actorNameSource?: NameSource | null
  action: string
  targetType?: string | null
  targetId?: string | null
  targetName?: string | null
  targetNameSource?: NameSource | null
  detail?: AuditDetail | null
  ip?: string | null
  createdAt: string
}

export interface ActionMeta {
  label: string
  categoryCode: string
  categoryLabel: string
  color: string
}

const action = (label: string, categoryCode: string, categoryLabel: string, color: string): ActionMeta => ({
  label, categoryCode, categoryLabel, color,
})

export const ACTIONS: Record<string, ActionMeta> = {
  LOGIN: action('登录成功', 'AUTH', '认证安全', 'green'),
  LOGIN_FAILED: action('登录失败', 'AUTH', '认证安全', 'red'),
  LOGIN_LOCKED: action('账号锁定', 'AUTH', '认证安全', 'red'),
  LOGOUT: action('退出登录', 'AUTH', '认证安全', 'gray'),
  PASSWORD_CHANGE: action('修改密码', 'AUTH', '认证安全', 'orange'),
  PROFILE_UPDATE: action('更新个人资料', 'AUTH', '认证安全', 'purple'),
  PROJECT_CREATE: action('创建项目', 'PROJECT', '项目协作', 'arcoblue'),
  PROJECT_GROUP_CREATE: action('创建主项目', 'PROJECT', '项目协作', 'arcoblue'),
  PROJECT_GROUP_UPDATE: action('更新主项目', 'PROJECT', '项目协作', 'purple'),
  PROJECT_GROUP_TRANSFER: action('变更项目负责人', 'PROJECT', '项目协作', 'purple'),
  PROJECT_GROUP_STATUS_AUTO: action('同步主项目状态', 'PROJECT', '项目协作', 'green'),
  PROJECT_GROUP_DELETE: action('删除主项目', 'PROJECT', '项目协作', 'red'),
  PROJECT_COPY: action('复制项目', 'PROJECT', '项目协作', 'purple'),
  PROJECT_UPDATE: action('更新项目', 'PROJECT', '项目协作', 'arcoblue'),
  PROJECT_DICTIONARY_CREATE: action('新增数据字典', 'SYSTEM', '系统', 'arcoblue'),
  PROJECT_DICTIONARY_UPDATE: action('更新数据字典', 'SYSTEM', '系统', 'purple'),
  PROJECT_DICTIONARY_DELETE: action('删除数据字典', 'SYSTEM', '系统', 'red'),
  PROJECT_START: action('开始项目', 'PROJECT', '项目协作', 'arcoblue'),
  PROJECT_SUBMIT: action('提交验收', 'PROJECT', '项目协作', 'arcoblue'),
  PROJECT_CONFIRM: action('验收通过', 'PROJECT', '项目协作', 'green'),
  PROJECT_REJECT: action('验收驳回', 'PROJECT', '项目协作', 'red'),
  PROJECT_WITHDRAW: action('撤回验收申请', 'PROJECT', '项目协作', 'orange'),
  PROJECT_ACCEPTANCE_MIGRATE: action('转交公司内部验收', 'PROJECT', '项目协作', 'arcoblue'),
  PROJECT_ACCEPTANCE_NOTIFICATIONS_MIGRATE: action('更新内部验收通知', 'PROJECT', '项目协作', 'arcoblue'),
  PROJECT_TERMINATE: action('终止项目', 'PROJECT', '项目协作', 'red'),
  PROJECT_RESTART: action('重新开始项目', 'PROJECT', '项目协作', 'arcoblue'),
  PROJECT_DELETE: action('删除项目', 'PROJECT', '项目协作', 'red'),
  PROJECT_MEMBERS: action('调整项目成员', 'PROJECT', '项目协作', 'purple'),
  FILE_UPLOAD: action('上传文件', 'FILE', '文件', 'arcoblue'),
  FILE_DOWNLOAD: action('下载文件', 'FILE', '文件', 'cyan'),
  FILE_PREVIEW: action('预览文件', 'FILE', '文件', 'cyan'),
  FILE_BATCH_DOWNLOAD: action('批量下载', 'FILE', '文件', 'cyan'),
  FILE_DELETE: action('删除文件', 'FILE', '文件', 'red'),
  UPLOAD_ABORT: action('取消上传', 'FILE', '文件', 'orange'),
  MESSAGE_CREATE: action('发送留言', 'MESSAGE', '留言', 'arcoblue'),
  MESSAGE_DELETE: action('删除留言', 'MESSAGE', '留言', 'red'),
  MESSAGE_READ: action('查看留言回执', 'MESSAGE', '留言', 'cyan'),
  USER_CREATE: action('创建用户', 'ORG', '组织与权限', 'arcoblue'),
  USER_UPDATE: action('更新用户', 'ORG', '组织与权限', 'purple'),
  USER_STATUS: action('变更用户状态', 'ORG', '组织与权限', 'orange'),
  USER_DELETE: action('删除用户', 'ORG', '组织与权限', 'red'),
  USER_RESET_PASSWORD: action('重置用户密码', 'ORG', '组织与权限', 'orange'),
  USER_ASSIGN_ROLE: action('调整用户角色', 'ORG', '组织与权限', 'purple'),
  USER_ASSIGN_ROLES: action('调整用户角色（旧）', 'ORG', '组织与权限', 'purple'),
  DEPT_CREATE: action('创建组织', 'ORG', '组织与权限', 'arcoblue'),
  DEPT_UPDATE: action('更新组织', 'ORG', '组织与权限', 'purple'),
  DEPT_STATUS: action('变更组织状态', 'ORG', '组织与权限', 'orange'),
  DEPT_DELETE: action('删除组织', 'ORG', '组织与权限', 'red'),
  ROLE_CREATE: action('创建角色', 'ORG', '组织与权限', 'arcoblue'),
  ROLE_UPDATE: action('更新角色', 'ORG', '组织与权限', 'purple'),
  ROLE_STATUS: action('变更角色状态', 'ORG', '组织与权限', 'orange'),
  ROLE_DELETE: action('删除角色', 'ORG', '组织与权限', 'red'),
  ROLE_ASSIGN_PERMS: action('调整角色权限', 'ORG', '组织与权限', 'purple'),
  SUPPLIER_CREATE: action('创建供应商', 'SUPPLIER', '供应商', 'arcoblue'),
  SUPPLIER_UPDATE: action('更新供应商', 'SUPPLIER', '供应商', 'purple'),
  SUPPLIER_STATUS: action('变更供应商状态', 'SUPPLIER', '供应商', 'orange'),
  SUPPLIER_DELETE: action('删除供应商', 'SUPPLIER', '供应商', 'red'),
  SUPPLIER_ACCOUNT_CREATE: action('创建供应商账号', 'SUPPLIER', '供应商', 'arcoblue'),
  SUPPLIER_ACCOUNT_UPDATE: action('更新供应商账号', 'SUPPLIER', '供应商', 'purple'),
  SUPPLIER_ACCOUNT_STATUS: action('变更供应商账号状态', 'SUPPLIER', '供应商', 'orange'),
  SUPPLIER_ACCOUNT_RESET_PASSWORD: action('重置供应商密码', 'SUPPLIER', '供应商', 'orange'),
  SUPPLIER_ACCOUNT_DELETE: action('删除供应商账号', 'SUPPLIER', '供应商', 'red'),
  ROBOT_PART_CREATE: action('创建 Robot 料号', 'SYSTEM', '系统', 'arcoblue'),
  ROBOT_PART_UPDATE: action('更新 Robot 料号', 'SYSTEM', '系统', 'purple'),
  ROBOT_PART_DELETE: action('删除 Robot 料号', 'SYSTEM', '系统', 'red'),
  AUDIT_LOG_DELETE: action('删除操作日志', 'SYSTEM', '系统', 'red'),
  AUDIT_LOG_RETENTION: action('自动清理过期日志', 'SYSTEM', '系统', 'gray'),
  CONFIG_UPDATE: action('更新系统参数', 'SYSTEM', '系统', 'orange'),
  EMAIL_SENT: action('邮件发送成功', 'SYSTEM', '系统', 'green'),
  EMAIL_FAILED: action('邮件发送失败', 'SYSTEM', '系统', 'red'),
  EMAIL_RETRY: action('邮件发送重试', 'SYSTEM', '系统', 'orange'),
  EMAIL_SKIPPED_MISSING_EMAIL: action('邮件未入队（缺少邮箱）', 'SYSTEM', '系统', 'orange'),
  EMAIL_CANCELLED_STALE: action('已取消过期验收邮件', 'SYSTEM', '系统', 'orange'),
}

export const CATEGORY_OPTIONS = [
  ['AUTH', '认证安全'], ['PROJECT', '项目协作'], ['FILE', '文件'], ['MESSAGE', '留言'],
  ['ORG', '组织与权限'], ['SUPPLIER', '供应商'], ['SYSTEM', '系统'],
] as const

export const TARGET_LABELS: Record<string, string> = {
  user: '用户', role: '角色', department: '组织', supplier: '供应商', project: '子项目', project_group: '主项目',
  file: '文件', message: '留言', upload_session: '上传任务', audit_log: '操作日志',
  system_config: '系统参数', email_outbox: '邮件队列', project_dictionary: '数据字典', robot_part: 'Robot 料号',
}

const FIELD_LABELS: Record<string, string> = {
  realName: '姓名', email: '邮箱', phone: '电话', departmentId: '组织', roleId: '角色',
  name: '名称', description: '说明', status: '状态', permissions: '权限', members: '成员',
  partNumber: 'Robot 料号', model: 'Robot 型号', robotPartId: 'Robot 料号', expectedCompletionDate: '需求完成时间',
}

const STATUS_LABELS: Record<string, string> = {
  ACTIVE: '启用', DISABLED: '禁用', DRAFT: '草稿', IN_PROGRESS: '进行中',
  PENDING_CONFIRMATION: '待验收', COMPLETED: '已完成', TERMINATED: '已终止',
  PENDING: '待处理', SUCCESS: '成功', FAILED: '失败', CANCELLED: '已取消',
}

const SECRET_KEY = /(password|passwd|token|secret|authorization|cookie|credential|private.?key|captcha)/i

const PROJECT_WORKFLOW_ACTIONS = new Set([
  'PROJECT_START', 'PROJECT_SUBMIT', 'PROJECT_CONFIRM', 'PROJECT_REJECT',
  'PROJECT_WITHDRAW', 'PROJECT_TERMINATE', 'PROJECT_RESTART',
])

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function cleanText(value: unknown): string | undefined {
  return typeof value === 'string' && value.trim() ? value.trim() : undefined
}

function asIdArray(value: unknown): string[] {
  if (!Array.isArray(value)) return []
  return value
    .filter((item) => typeof item === 'string' || typeof item === 'number')
    .map(String)
}

export function formatAuditValue(value: unknown): string {
  if (value == null || value === '') return '无'
  if (typeof value === 'boolean') return value ? '是' : '否'
  if (typeof value === 'string') return STATUS_LABELS[value] || value
  if (Array.isArray(value)) return value.length ? value.map(formatAuditValue).join('、') : '无'
  if (isRecord(value)) {
    const name = cleanText(value.name) ?? cleanText(value.realName)
    const code = cleanText(value.code) ?? cleanText(value.employeeNo)
    const id = value.id == null ? undefined : String(value.id)
    if (name || code || id) return [name, code && code !== name ? code : undefined, id ? `ID ${id}` : undefined].filter(Boolean).join(' · ')
    return JSON.stringify(value)
  }
  return String(value)
}

export function formatChangeValue(change: Pick<AuditChange, 'field' | 'before' | 'after'>, side: 'before' | 'after') {
  return SECRET_KEY.test(change.field) ? '[已隐藏]' : formatAuditValue(change[side])
}

export function actionOptionsForCategory(category?: string) {
  return Object.entries(ACTIONS)
    .filter(([, meta]) => !category || meta.categoryCode === category)
    .sort((a, b) => a[1].label.localeCompare(b[1].label, 'zh-CN'))
}

export function actionForCategory(category: string | undefined, selectedAction: string | undefined) {
  if (!selectedAction || !category) return selectedAction
  return ACTIONS[selectedAction]?.categoryCode === category ? selectedAction : undefined
}

export interface DisplayIdentity {
  name: string
  identifier: string
  source: NameSource | 'detail' | 'system'
  sourceLabel: string
}

function auditContext(detail?: AuditDetail | null): AuditContext | undefined {
  return detail && isRecord(detail.auditContext) ? detail.auditContext : undefined
}

export function actorIdentity(row: AuditLogRow): DisplayIdentity {
  const context = auditContext(row.detail)
  const snapshotName = cleanText(context?.actorName)
  const employeeNo = cleanText(row.employeeNo)
  const identifier = [employeeNo ? `工号 ${employeeNo}` : undefined, row.userId != null ? `ID ${row.userId}` : undefined].filter(Boolean).join(' · ')
  if (snapshotName) return { name: snapshotName, identifier: identifier || '操作账号未记录', source: 'snapshot', sourceLabel: '操作时姓名' }
  const currentName = cleanText(row.actorName)
  if (currentName) {
    const source = row.actorNameSource || 'unknown'
    const sourceLabel = source === 'snapshot' ? '操作时姓名' : source === 'current' ? '当前姓名（历史辅助）' : '姓名来源未知'
    return { name: currentName, identifier: identifier || '操作账号未记录', source, sourceLabel }
  }
  if (employeeNo) return { name: employeeNo, identifier: row.userId != null ? `ID ${row.userId}` : '历史工号', source: 'unknown', sourceLabel: '未记录姓名' }
  if (row.userId != null) return { name: `用户 #${row.userId}`, identifier: '姓名与工号未记录', source: 'unknown', sourceLabel: '未记录姓名' }
  return { name: '系统', identifier: context?.source === 'SYSTEM' ? '系统任务' : '系统事件', source: 'system', sourceLabel: '非用户操作' }
}

function legacyTargetName(row: AuditLogRow): string | undefined {
  const detail = row.detail
  if (!detail) return undefined
  return cleanText(detail.targetName)
    ?? cleanText(detail.name)
    ?? cleanText(detail.newName)
    ?? cleanText(detail.fileName)
    ?? cleanText(detail.projectName)
}

export function targetIdentity(row: AuditLogRow): DisplayIdentity {
  const context = auditContext(row.detail)
  const snapshotName = cleanText(context?.targetName)
  const hasTarget = Boolean(row.targetType || row.targetId || snapshotName || cleanText(row.targetName) || legacyTargetName(row))
  if (!hasTarget) return { name: '无特定对象', identifier: '本次操作未关联业务对象', source: 'unknown', sourceLabel: '无对象' }
  const typeLabel = row.targetType ? TARGET_LABELS[row.targetType] || row.targetType : '操作对象'
  const identifier = row.targetId ? `${typeLabel} · ID ${row.targetId}` : typeLabel
  if (snapshotName) return { name: snapshotName, identifier, source: 'snapshot', sourceLabel: '操作时名称' }
  const topName = cleanText(row.targetName)
  if (topName && row.targetNameSource === 'snapshot') return { name: topName, identifier, source: 'snapshot', sourceLabel: '操作时名称' }
  const historicalName = legacyTargetName(row)
  if (historicalName) return { name: historicalName, identifier, source: 'detail', sourceLabel: '历史详情名称' }
  if (topName) {
    const source = row.targetNameSource || 'unknown'
    const sourceLabel = source === 'snapshot' ? '操作时名称' : source === 'current' ? '当前名称（历史辅助）' : '名称来源未知'
    return { name: topName, identifier, source, sourceLabel }
  }
  return { name: row.targetId ? `${typeLabel} #${row.targetId}` : typeLabel, identifier: row.targetId ? `ID ${row.targetId}` : '未记录对象 ID', source: 'unknown', sourceLabel: '未记录名称' }
}

export interface DisplayChange {
  field: string
  label: string
  before: unknown
  after: unknown
  historical?: boolean
}

function explicitChanges(detail?: AuditDetail | null): DisplayChange[] {
  if (!Array.isArray(detail?.changes)) return []
  return detail.changes.flatMap((item) => {
    if (!isRecord(item)) return []
    const field = cleanText(item.field)
    if (!field || !Object.hasOwn(item, 'before') || !Object.hasOwn(item, 'after')) return []
    const contractLabel = ['partNumber', 'model', 'robotPartId', 'expectedCompletionDate'].includes(field) ? FIELD_LABELS[field] : undefined
    return [{ field, label: contractLabel || cleanText(item.label) || FIELD_LABELS[field] || field, before: item.before, after: item.after }]
  })
}

export function displayChanges(row: AuditLogRow): DisplayChange[] {
  const detail = row.detail
  const explicit = explicitChanges(detail)
  if (Array.isArray(detail?.changes)) return explicit.filter((item) => !['permissions', 'members'].includes(item.field))
  if (!detail) return []
  if (row.action === 'PROJECT_COPY' && isRecord(detail.source) && isRecord(detail.target)) {
    return [
      { field: 'projectName', label: '项目', before: detail.source.name, after: detail.target.name, historical: true },
      { field: 'status', label: '新项目状态', before: null, after: detail.target.status, historical: true },
    ]
  }
  if ((row.action === 'SUPPLIER_UPDATE' || row.action === 'ROLE_UPDATE') && (detail.oldName != null || detail.newName != null)) {
    return [{ field: 'name', label: row.action === 'SUPPLIER_UPDATE' ? '供应商名称' : '角色名称', before: detail.oldName, after: detail.newName, historical: true }]
  }
  if (row.action.endsWith('_STATUS') && (detail.oldStatus != null || detail.newStatus != null)) {
    return [{ field: 'status', label: '状态', before: detail.oldStatus, after: detail.newStatus ?? detail.status ?? detail.to, historical: true }]
  }
  if ((row.action === 'USER_ASSIGN_ROLE' || row.action === 'USER_ASSIGN_ROLES') && (detail.oldRoleId != null || detail.newRoleId != null)) {
    return [{ field: 'roleId', label: '角色', before: detail.oldRoleName ?? (detail.oldRoleId == null ? null : `角色 ID ${detail.oldRoleId}`), after: detail.newRoleName ?? (detail.newRoleId == null ? null : `角色 ID ${detail.newRoleId}`), historical: true }]
  }
  if (PROJECT_WORKFLOW_ACTIONS.has(row.action) && (detail.from != null || detail.to != null)) {
    return [{ field: 'status', label: '项目状态', before: detail.from, after: detail.to, historical: true }]
  }
  return []
}

export interface CollectionItem {
  key: string
  title: string
  meta: string
}

export interface CollectionChanges {
  addedPermissions: CollectionItem[]
  removedPermissions: CollectionItem[]
  addedMembers: CollectionItem[]
  removedMembers: CollectionItem[]
  permissionsAreIds: boolean
  membersAreIds: boolean
}

function permissionItems(value: unknown): CollectionItem[] {
  if (!Array.isArray(value)) return []
  return value.flatMap((item, index) => {
    if (!isRecord(item)) return []
    const id = item.id == null ? undefined : String(item.id)
    const name = cleanText(item.name)
    const code = cleanText(item.code)
    if (!id && !name && !code) return []
    return [{ key: id || code || `${name}-${index}`, title: name || code || `权限 ID ${id}`, meta: [code && code !== name ? code : undefined, id ? `ID ${id}` : undefined].filter(Boolean).join(' · ') }]
  })
}

function memberItems(value: unknown): CollectionItem[] {
  if (!Array.isArray(value)) return []
  return value.flatMap((item, index) => {
    if (!isRecord(item)) return []
    const id = item.id == null ? undefined : String(item.id)
    const realName = cleanText(item.realName)
    const employeeNo = cleanText(item.employeeNo)
    if (!id && !realName && !employeeNo) return []
    return [{ key: id || employeeNo || `${realName}-${index}`, title: realName || employeeNo || `成员 ID ${id}`, meta: [employeeNo && employeeNo !== realName ? `工号 ${employeeNo}` : undefined, id ? `ID ${id}` : undefined].filter(Boolean).join(' · ') }]
  })
}

function idDiff(before: unknown, after: unknown, noun: '权限' | '成员') {
  const beforeIds = asIdArray(before)
  const afterIds = asIdArray(after)
  const beforeSet = new Set(beforeIds)
  const afterSet = new Set(afterIds)
  return {
    added: afterIds.filter((id) => !beforeSet.has(id)).map((id) => ({ key: id, title: `${noun} ID ${id}`, meta: '历史日志未记录名称' })),
    removed: beforeIds.filter((id) => !afterSet.has(id)).map((id) => ({ key: id, title: `${noun} ID ${id}`, meta: '历史日志未记录名称' })),
  }
}

export function collectionChanges(row: AuditLogRow): CollectionChanges {
  const detail = row.detail || {}
  const hasPermissionCollections = Array.isArray(detail.addedPermissions) || Array.isArray(detail.removedPermissions)
  const hasMemberCollections = Array.isArray(detail.addedMembers) || Array.isArray(detail.removedMembers)
  let addedPermissions = permissionItems(detail.addedPermissions)
  let removedPermissions = permissionItems(detail.removedPermissions)
  let addedMembers = memberItems(detail.addedMembers)
  let removedMembers = memberItems(detail.removedMembers)
  let permissionsAreIds = false
  let membersAreIds = false
  if (!hasPermissionCollections && (Array.isArray(detail.oldPermissionIds) || Array.isArray(detail.newPermissionIds))) {
    const diff = idDiff(detail.oldPermissionIds, detail.newPermissionIds, '权限')
    addedPermissions = diff.added
    removedPermissions = diff.removed
    permissionsAreIds = true
  }
  if (!hasMemberCollections && (Array.isArray(detail.oldMemberIds) || Array.isArray(detail.newMemberIds))) {
    const diff = idDiff(detail.oldMemberIds, detail.newMemberIds, '成员')
    addedMembers = diff.added
    removedMembers = diff.removed
    membersAreIds = true
  }
  return { addedPermissions, removedPermissions, addedMembers, removedMembers, permissionsAreIds, membersAreIds }
}

export interface DetailNote {
  label: string
  value: string
  tone: 'normal' | 'danger'
}

export function detailNotes(row: AuditLogRow): DetailNote[] {
  const detail = row.detail
  if (!detail) return []
  const notes: DetailNote[] = []
  if (row.action === 'AUDIT_LOG_DELETE' && Array.isArray(detail.deletedRecords)) {
    const records = detail.deletedRecords.filter(isRecord)
    for (const record of records.slice(0, 20)) {
      notes.push({ label: `原日志 #${formatAuditValue(record.id)}`,
        value: `${formatAuditValue(record.action)}；操作人 ${formatAuditValue(record.employeeNo ?? record.actorId)}；时间 ${formatAuditValue(record.createdAt)}`,
        tone: 'normal' })
    }
    if (records.length > 20) notes.push({ label: '完整摘要', value: `共 ${records.length} 条，可复制详情查看全部记录`, tone: 'normal' })
  }
  if (row.action === 'PROJECT_COPY') {
    notes.push({ label: '复制文件', value: `${formatAuditValue(detail.fileCount)} 个`, tone: 'normal' })
    notes.push({ label: '文件总大小', value: formatByteSize(detail.totalBytes), tone: 'normal' })
  }
  if (detail.passwordChanged === true) notes.push({ label: '邮箱凭据', value: '已更新（不记录密码或授权码）', tone: 'normal' })
  const reason = cleanText(detail.reason)
  if (reason) {
    const label = row.action === 'PROJECT_REJECT' ? '驳回原因'
      : row.action === 'PROJECT_TERMINATE' ? '终止原因'
        : row.action === 'PROJECT_WITHDRAW' ? '撤回原因'
          : row.action === 'PROJECT_RESTART' ? '重新开始说明'
            : '操作说明'
    notes.push({ label, value: reason, tone: row.action === 'PROJECT_REJECT' || row.action === 'PROJECT_TERMINATE' ? 'danger' : 'normal' })
  }
  const error = cleanText(detail.error)
  if (error && (row.action === 'EMAIL_FAILED' || row.action === 'EMAIL_RETRY')) {
    notes.push({ label: row.action === 'EMAIL_FAILED' ? '失败原因' : '上次失败原因', value: error, tone: 'danger' })
  }
  if (detail.retryCount != null && (row.action === 'EMAIL_FAILED' || row.action === 'EMAIL_RETRY')) {
    notes.push({ label: '重试次数', value: formatAuditValue(detail.retryCount), tone: 'normal' })
  }
  return notes
}

function formatByteSize(value: unknown): string {
  const bytes = Number(value)
  if (!Number.isFinite(bytes) || bytes < 0) return formatAuditValue(value)
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`
  return `${(bytes / 1024 / 1024 / 1024).toFixed(2)} GB`
}

function countSummary(added: number, removed: number, noun: string) {
  return [`新增 ${added} ${noun}`, `移除 ${removed} ${noun}`].join('，')
}

export function detailSummary(row: AuditLogRow): string {
  const detail = row.detail
  if (row.action === 'AUDIT_LOG_DELETE' && typeof detail?.deleted === 'number')
    return `已手动删除 ${detail.deleted} 条过期日志，原操作摘要保留在详情中`
  if (row.action === 'PROJECT_COPY' && isRecord(detail?.source) && isRecord(detail?.target)) {
    const sourceName = cleanText(detail.source.name) || '原项目'
    const targetName = cleanText(detail.target.name) || '新项目'
    return `${sourceName} → ${targetName}；复制 ${formatAuditValue(detail.fileCount)} 个文件，共 ${formatByteSize(detail.totalBytes)}`
  }
  const changes = displayChanges(row)
  const collections = collectionChanges(row)
  if (collections.addedPermissions.length || collections.removedPermissions.length) {
    return countSummary(collections.addedPermissions.length, collections.removedPermissions.length, '项权限')
  }
  if (collections.addedMembers.length || collections.removedMembers.length) {
    return countSummary(collections.addedMembers.length, collections.removedMembers.length, '名成员')
  }
  if (changes.length) {
    const first = changes[0]
    const changeText = `${first.label}：${formatChangeValue(first, 'before')} → ${formatChangeValue(first, 'after')}${changes.length > 1 ? `；另有 ${changes.length - 1} 项变化` : ''}`
    const reason = detailNotes(row).find((note) => note.label.includes('原因') || note.label.includes('说明'))
    return reason ? `${changeText}；${reason.label}：${reason.value}` : changeText
  }
  if (row.action === 'AUDIT_LOG_RETENTION' && typeof detail?.deleted === 'number') {
    return `已自动清理 ${detail.deleted} 条超过 ${formatAuditValue(detail.retentionDays)} 天的操作日志`
  }
  if (row.action === 'MESSAGE_READ' && Array.isArray(detail?.messageIds)) return `标记 ${detail.messageIds.length} 条留言为已读`
  if (detail?.passwordChanged === true) return '已更新邮箱密码或授权码（不记录具体内容）'
  if (Array.isArray(detail?.changes) && detail.changes.length === 0
      && (row.action.endsWith('_UPDATE') || row.action.endsWith('_STATUS') || row.action === 'ROLE_ASSIGN_PERMS')) return '已保存，未发生字段变化'
  if (row.action === 'PROJECT_MEMBERS' && Array.isArray(detail?.addedMembers) && Array.isArray(detail?.removedMembers)) return '成员配置未发生变化'
  if (row.action === 'ROLE_ASSIGN_PERMS') {
    if (detail?.oldPermissionCount != null || detail?.newPermissionCount != null) {
      return `权限点数量：${formatAuditValue(detail.oldPermissionCount)} → ${formatAuditValue(detail.newPermissionCount)}`
    }
    return detail ? '权限配置已调整' : '历史日志未记录权限明细'
  }
  if (row.action === 'USER_UPDATE' || row.action === 'PROFILE_UPDATE' || row.action === 'SUPPLIER_ACCOUNT_UPDATE') {
    const fields = Array.isArray(detail?.changedFields)
      ? detail.changedFields.map((field) => FIELD_LABELS[String(field)] || String(field)).join('、')
      : ''
    return fields ? `修改了${fields}` : '历史日志未记录变更字段'
  }
  if (row.action === 'EMAIL_SKIPPED_MISSING_EMAIL') {
    const person = [cleanText(detail?.realName), cleanText(detail?.employeeNo)].filter(Boolean).join(' · ')
    return `${person ? `${person}：` : ''}未填写邮箱，通知未入队`
  }
  if (row.action === 'EMAIL_FAILED') return `${formatAuditValue(detail?.error)}${detail?.retryCount ? `，第 ${formatAuditValue(detail.retryCount)} 次` : ''}`
  if (row.action === 'EMAIL_RETRY') return `${formatAuditValue(detail?.error)}，已安排第 ${formatAuditValue(detail?.retryCount)} 次重试`
  if (row.action === 'EMAIL_SENT') return detail?.recipient ? `收件人 ${formatAuditValue(detail.recipient)}` : '邮件已发送'
  if (!detail) return row.targetType === 'project' ? '历史日志未记录详情' : '未记录补充信息'
  const parts = [detail.name, detail.fileName, detail.employeeNo, detail.projectId]
    .filter((value) => value != null && value !== '')
    .map(formatAuditValue)
  return parts.join(' · ') || '查看详情'
}

export function historicalNote(row: AuditLogRow): string | undefined {
  if (!row.detail) return row.targetType === 'project' ? '该历史项目日志未记录详情，无法还原当时的具体变化。' : '该历史日志未记录补充详情。'
  if (row.action === 'ROLE_ASSIGN_PERMS' && collectionChanges(row).permissionsAreIds) return '该历史日志只保存了权限 ID，名称和编码无法还原。'
  if (row.action === 'PROJECT_MEMBERS' && collectionChanges(row).membersAreIds) return '该历史日志只保存了成员 ID，姓名和工号无法还原。'
  const changedFields = Array.isArray(row.detail.changedFields) ? row.detail.changedFields.length : 0
  if (changedFields && !displayChanges(row).length) return '该历史日志只保存了变更字段，未记录修改前后的值。'
  return undefined
}

function redactSecrets(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(redactSecrets)
  if (!isRecord(value)) return value
  if (typeof value.field === 'string' && SECRET_KEY.test(value.field)) {
    return Object.fromEntries(Object.entries(value).map(([key, item]) => [key,
      key === 'before' || key === 'after' || SECRET_KEY.test(key) ? '[已隐藏]' : redactSecrets(item)]))
  }
  return Object.fromEntries(Object.entries(value).map(([key, item]) => [key, SECRET_KEY.test(key) ? '[已隐藏]' : redactSecrets(item)]))
}

export function safeDetailJson(detail?: AuditDetail | null): string {
  return detail ? JSON.stringify(redactSecrets(detail), null, 2) : '本条日志没有补充数据'
}

export async function copySafeDetail(detail: AuditDetail | null | undefined, writeText?: (value: string) => Promise<void>) {
  if (!writeText) return false
  try {
    await writeText(safeDetailJson(detail))
    return true
  } catch {
    return false
  }
}

export function sourceLabel(source?: string | null) {
  if (source === 'HTTP') return 'HTTP 请求'
  if (source === 'SYSTEM') return '系统任务'
  return '历史日志未记录'
}

export function sourceDetailValue(context: AuditContext | null | undefined, value?: string | null) {
  if (cleanText(value)) return cleanText(value)
  if (!context) return '历史日志未记录'
  if (context.source === 'SYSTEM') return '不适用（系统任务）'
  return cleanText(value) || '未记录'
}
