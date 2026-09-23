import { describe, expect, it, vi } from 'vitest'
import {
  ACTIONS,
  actionForCategory,
  actionOptionsForCategory,
  actorIdentity,
  collectionChanges,
  copySafeDetail,
  detailNotes,
  detailSummary,
  displayChanges,
  formatAuditValue,
  formatChangeValue,
  historicalNote,
  safeDetailJson,
  sourceDetailValue,
  targetIdentity,
  type AuditLogRow,
} from '../../../pages/system/auditLogDetails'

const baseRow: AuditLogRow = {
  id: 1,
  action: 'ROLE_UPDATE',
  targetType: 'role',
  targetId: '7',
  createdAt: '2026-09-14T08:00:00Z',
}

describe('审计详情生产契约', () => {
  it('operation-time identity snapshots take priority over current lookup names', () => {
    const row: AuditLogRow = {
      ...baseRow,
      userId: 3,
      employeeNo: 'A003',
      actorName: '管理员（现名）',
      actorNameSource: 'current',
      targetName: '质量经理（现名）',
      targetNameSource: 'current',
      detail: { auditContext: { actorName: '管理员（当时）', targetName: '审核员（当时）', requestId: 'req-1', source: 'HTTP' } },
    }

    expect(actorIdentity(row)).toMatchObject({ name: '管理员（当时）', sourceLabel: '操作时姓名' })
    expect(targetIdentity(row)).toMatchObject({ name: '审核员（当时）', sourceLabel: '操作时名称' })
  })

  it('current names are explicitly marked as historical assistance for old logs', () => {
    const row: AuditLogRow = {
      ...baseRow,
      userId: 3,
      employeeNo: 'A003',
      actorName: '当前管理员',
      actorNameSource: 'current',
      targetName: '当前角色名',
      targetNameSource: 'current',
      detail: { oldName: '旧角色名', newName: '当时的新角色名' },
    }

    expect(actorIdentity(row).sourceLabel).toBe('当前姓名（历史辅助）')
    expect(targetIdentity(row)).toMatchObject({ name: '当时的新角色名', sourceLabel: '历史详情名称' })
    expect(displayChanges(row)[0]).toMatchObject({ before: '旧角色名', after: '当时的新角色名' })
  })

  it('a top-level snapshot still takes priority over legacy detail names', () => {
    const row: AuditLogRow = {
      ...baseRow,
      targetName: '接口返回的快照名称',
      targetNameSource: 'snapshot',
      detail: { oldName: '旧名', newName: '详情新名' },
    }
    expect(targetIdentity(row)).toMatchObject({ name: '接口返回的快照名称', sourceLabel: '操作时名称' })
  })

  it('structured changes translate status codes and drive a useful summary', () => {
    const row: AuditLogRow = {
      ...baseRow,
      action: 'ROLE_STATUS',
      detail: { changes: [{ field: 'status', label: '状态', before: 'ACTIVE', after: 'DISABLED' }] },
    }
    expect(formatAuditValue('ACTIVE')).toBe('启用')
    expect(formatAuditValue('DISABLED')).toBe('禁用')
    expect(detailSummary(row)).toBe('状态：启用 → 禁用')
  })

  it('robot part audit metadata and completion dates use business labels', () => {
    expect([ACTIONS.ROBOT_PART_CREATE.label, ACTIONS.ROBOT_PART_UPDATE.label, ACTIONS.ROBOT_PART_DELETE.label])
      .toEqual(['创建 Robot 料号', '更新 Robot 料号', '删除 Robot 料号'])
    const row: AuditLogRow = {
      ...baseRow,
      action: 'ROBOT_PART_UPDATE',
      targetType: 'robot_part',
      detail: { changes: [
        { field: 'partNumber', label: '料号', before: 'A-1', after: 'A-2' },
        { field: 'model', label: '型号', before: '旧型号', after: '新型号' },
        { field: 'robotPartId', label: '', before: null, after: 7 },
        { field: 'expectedCompletionDate', label: '', before: '2026-09-01', after: '2026-09-30' },
      ] },
    }
    expect(targetIdentity(row).name).toBe('Robot 料号 #7')
    expect(displayChanges(row).map((item) => item.label)).toEqual(['Robot 料号', 'Robot 型号', 'Robot 料号', '需求完成时间'])
  })

  it('permission and member additions preserve names, codes, employee numbers and IDs', () => {
    const permissionRow: AuditLogRow = {
      ...baseRow,
      action: 'ROLE_ASSIGN_PERMS',
      detail: {
        addedPermissions: [{ id: 8, code: 'project:edit', name: '编辑项目' }],
        removedPermissions: [{ id: 3, code: 'file:delete', name: '删除文件' }],
      },
    }
    const memberRow: AuditLogRow = {
      ...baseRow,
      action: 'PROJECT_MEMBERS',
      targetType: 'project',
      detail: {
        addedMembers: [{ id: 11, employeeNo: 'E011', realName: '张工' }],
        removedMembers: [{ id: 12, employeeNo: 'E012', realName: '李工' }],
      },
    }
    expect(collectionChanges(permissionRow).addedPermissions[0]).toMatchObject({ title: '编辑项目', meta: 'project:edit · ID 8' })
    expect(detailSummary(permissionRow)).toBe('新增 1 项权限，移除 1 项权限')
    expect(collectionChanges(memberRow).addedMembers[0]).toMatchObject({ title: '张工', meta: '工号 E011 · ID 11' })
    expect(detailSummary(memberRow)).toBe('新增 1 名成员，移除 1 名成员')
  })

  it('legacy role permission logs show set differences as IDs without inventing names', () => {
    const row: AuditLogRow = {
      ...baseRow,
      action: 'ROLE_ASSIGN_PERMS',
      detail: { oldPermissionCount: 2, newPermissionCount: 3, oldPermissionIds: [1, 2], newPermissionIds: [2, 3, 4] },
    }
    const changes = collectionChanges(row)
    expect(changes.addedPermissions.map((item) => item.title)).toEqual(['权限 ID 3', '权限 ID 4'])
    expect(changes.removedPermissions[0].title).toBe('权限 ID 1')
    expect(changes.permissionsAreIds).toBe(true)
    expect(historicalNote(row)).toBe('该历史日志只保存了权限 ID，名称和编码无法还原。')
    expect(detailSummary(row)).not.toMatch(/^无：/)
  })

  it('an explicit empty named permission diff is not mislabeled as ID-only history', () => {
    const row: AuditLogRow = {
      ...baseRow,
      action: 'ROLE_ASSIGN_PERMS',
      detail: { oldPermissionIds: [1, 2], newPermissionIds: [1, 2], addedPermissions: [], removedPermissions: [] },
    }
    expect(collectionChanges(row).permissionsAreIds).toBe(false)
    expect(historicalNote(row)).toBeUndefined()
  })

  it('legacy supplier rename and project logs without detail remain explicit', () => {
    const supplier: AuditLogRow = { ...baseRow, action: 'SUPPLIER_UPDATE', targetType: 'supplier', detail: { oldName: '甲供应商', newName: '乙供应商' } }
    const project: AuditLogRow = { ...baseRow, action: 'PROJECT_UPDATE', targetType: 'project', detail: null }
    expect(detailSummary(supplier)).toBe('供应商名称：甲供应商 → 乙供应商')
    expect(detailSummary(project)).toBe('历史日志未记录详情')
    expect(historicalNote(project)).toMatch(/无法还原当时的具体变化/)
  })

  it('workflow reasons and mail errors remain visible beside structured changes', () => {
    const rejected: AuditLogRow = {
      ...baseRow,
      action: 'PROJECT_REJECT',
      targetType: 'project',
      detail: { changes: [{ field: 'status', label: '项目状态', before: 'PENDING_CONFIRMATION', after: 'IN_PROGRESS' }], reason: '尺寸数据缺失' },
    }
    const mail: AuditLogRow = { ...baseRow, action: 'EMAIL_FAILED', targetType: 'email_outbox', detail: { error: 'SMTP 超时', retryCount: 2 } }
    expect(detailNotes(rejected)[0].label).toBe('驳回原因')
    expect(detailSummary(rejected)).toMatch(/尺寸数据缺失/)
    expect(detailNotes(mail).map((item) => item.label)).toEqual(['失败原因', '重试次数'])
  })

  it('category selection filters actions and clears an incompatible action', () => {
    const orgActions = actionOptionsForCategory('ORG')
    expect(orgActions.some(([code]) => code === 'ROLE_ASSIGN_PERMS')).toBe(true)
    expect(orgActions.every(([, meta]) => meta.categoryLabel === '组织与权限')).toBe(true)
    expect(actionForCategory('ORG', 'LOGIN')).toBeUndefined()
    expect(actionForCategory('AUTH', 'LOGIN')).toBe('LOGIN')
    expect(actionForCategory(undefined, 'LOGIN')).toBe('LOGIN')
  })

  it('frontend action categories match the backend category filter', () => {
    const expectedByCategory: Record<string, string[]> = {
      AUTH: ['LOGIN', 'LOGIN_FAILED', 'LOGIN_LOCKED', 'LOGOUT', 'PASSWORD_CHANGE', 'PROFILE_UPDATE'],
      PROJECT: ['PROJECT_CREATE', 'PROJECT_GROUP_CREATE', 'PROJECT_GROUP_UPDATE', 'PROJECT_GROUP_STATUS_AUTO', 'PROJECT_GROUP_DELETE', 'PROJECT_COPY', 'PROJECT_UPDATE', 'PROJECT_START', 'PROJECT_SUBMIT', 'PROJECT_CONFIRM', 'PROJECT_REJECT', 'PROJECT_WITHDRAW', 'PROJECT_ACCEPTANCE_MIGRATE', 'PROJECT_ACCEPTANCE_NOTIFICATIONS_MIGRATE', 'PROJECT_TERMINATE', 'PROJECT_RESTART', 'PROJECT_DELETE', 'PROJECT_MEMBERS'],
      FILE: ['FILE_UPLOAD', 'FILE_DOWNLOAD', 'FILE_PREVIEW', 'FILE_BATCH_DOWNLOAD', 'FILE_DELETE', 'UPLOAD_ABORT'],
      MESSAGE: ['MESSAGE_CREATE', 'MESSAGE_DELETE', 'MESSAGE_READ'],
      ORG: ['USER_CREATE', 'USER_UPDATE', 'USER_STATUS', 'USER_DELETE', 'USER_RESET_PASSWORD', 'USER_ASSIGN_ROLE', 'USER_ASSIGN_ROLES', 'DEPT_CREATE', 'DEPT_UPDATE', 'DEPT_STATUS', 'DEPT_DELETE', 'ROLE_CREATE', 'ROLE_UPDATE', 'ROLE_STATUS', 'ROLE_DELETE', 'ROLE_ASSIGN_PERMS'],
      SUPPLIER: ['SUPPLIER_CREATE', 'SUPPLIER_UPDATE', 'SUPPLIER_STATUS', 'SUPPLIER_DELETE', 'SUPPLIER_ACCOUNT_CREATE', 'SUPPLIER_ACCOUNT_UPDATE', 'SUPPLIER_ACCOUNT_STATUS', 'SUPPLIER_ACCOUNT_RESET_PASSWORD', 'SUPPLIER_ACCOUNT_DELETE'],
      SYSTEM: ['PROJECT_DICTIONARY_CREATE', 'PROJECT_DICTIONARY_UPDATE', 'PROJECT_DICTIONARY_DELETE', 'ROBOT_PART_CREATE', 'ROBOT_PART_UPDATE', 'ROBOT_PART_DELETE', 'AUDIT_LOG_DELETE', 'AUDIT_LOG_RETENTION', 'CONFIG_UPDATE', 'EMAIL_SENT', 'EMAIL_FAILED', 'EMAIL_RETRY', 'EMAIL_SKIPPED_MISSING_EMAIL', 'EMAIL_CANCELLED_STALE'],
    }
    for (const [category, codes] of Object.entries(expectedByCategory)) {
      expect(actionOptionsForCategory(category).map(([code]) => code).sort()).toEqual([...codes].sort())
    }
    expect(Object.keys(ACTIONS).sort()).toEqual(Object.values(expectedByCategory).flat().sort())
  })

  it('raw detail copy hides nested secrets and reports writer failures', async () => {
    const detail = {
      auditContext: { requestId: 'req-visible' },
      password: 'do-not-show',
      nested: { accessToken: 'also-secret', reason: 'visible' },
      changes: [{ field: 'password', label: '密码', before: 'hidden-before', after: 'hidden-after' }],
    }
    const writer = vi.fn(async (_value: string) => undefined)
    expect(await copySafeDetail(detail, writer)).toBe(true)
    expect(await copySafeDetail(detail, async () => { throw new Error('denied') })).toBe(false)
    const copied = writer.mock.calls[0][0]
    expect(copied).toMatch(/\[已隐藏\]/)
    expect(copied).not.toMatch(/do-not-show|also-secret|hidden-before|hidden-after/)
    expect(copied).toMatch(/req-visible|visible/)
    expect(formatChangeValue({ field: 'password', before: 'old', after: 'new' }, 'after')).toBe('[已隐藏]')
    expect(safeDetailJson(detail)).toBe(copied)
  })

  it('system tasks distinguish non-applicable request metadata from old missing metadata', () => {
    expect(sourceDetailValue({ source: 'SYSTEM' }, null)).toBe('不适用（系统任务）')
    expect(sourceDetailValue({ source: 'HTTP' }, null)).toBe('未记录')
    expect(sourceDetailValue(undefined, null)).toBe('历史日志未记录')
    expect(sourceDetailValue(undefined, '127.0.0.1')).toBe('127.0.0.1')
    expect(sourceDetailValue({ source: 'HTTP' }, ' req-2 ')).toBe('req-2')
  })

  it('new no-op logs are not described as incomplete history and summaries hide secrets', () => {
    const noop: AuditLogRow = { ...baseRow, action: 'SUPPLIER_UPDATE', detail: { changes: [], oldName: '同名', newName: '同名' } }
    expect(displayChanges(noop)).toHaveLength(0)
    expect(detailSummary(noop)).toBe('已保存，未发生字段变化')
    const secret: AuditLogRow = { ...baseRow, action: 'CONFIG_UPDATE', detail: { changes: [{ field: 'password', label: '密码', before: 'hidden-before', after: 'hidden-after' }] } }
    expect(detailSummary(secret)).not.toMatch(/hidden-before|hidden-after/)
    const smtp: AuditLogRow = { ...baseRow, action: 'CONFIG_UPDATE', detail: { changes: [], passwordChanged: true } }
    expect(detailSummary(smtp)).toMatch(/已更新邮箱密码/)
    expect(detailNotes(smtp)[0].value).toMatch(/不记录密码/)
  })

  it('automatic retention records summarize how many expired logs were removed', () => {
    const row: AuditLogRow = {
      ...baseRow,
      action: 'AUDIT_LOG_RETENTION',
      targetType: 'audit_log',
      targetId: null,
      detail: { deleted: 1250, retentionDays: 30, cutoff: '2026-08-23T00:00:00Z' },
    }
    expect(ACTIONS.AUDIT_LOG_RETENTION.categoryCode).toBe('SYSTEM')
    expect(detailSummary(row)).toBe('已自动清理 1250 条超过 30 天的操作日志')
  })
})
