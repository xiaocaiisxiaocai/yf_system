const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const vm = require('node:vm')
const ts = require('typescript')

function loadDetails() {
  const filename = path.resolve(__dirname, '../src/pages/system/auditLogDetails.ts')
  const source = ts.transpileModule(fs.readFileSync(filename, 'utf8'), {
    compilerOptions: {
      module: ts.ModuleKind.CommonJS,
      target: ts.ScriptTarget.ES2022,
    },
  }).outputText
  const module = { exports: {} }
  vm.runInNewContext(source, { exports: module.exports, module, require }, { filename })
  return module.exports
}

const details = loadDetails()
const baseRow = {
  id: 1,
  action: 'ROLE_UPDATE',
  targetType: 'role',
  targetId: '7',
  createdAt: '2026-09-14T08:00:00Z',
}

test('operation-time identity snapshots take priority over current lookup names', () => {
  const row = {
    ...baseRow,
    userId: 3,
    employeeNo: 'A003',
    actorName: '管理员（现名）',
    actorNameSource: 'current',
    targetName: '质量经理（现名）',
    targetNameSource: 'current',
    detail: {
      auditContext: {
        actorName: '管理员（当时）',
        targetName: '审核员（当时）',
        requestId: 'req-1',
        source: 'HTTP',
      },
    },
  }

  assert.equal(details.actorIdentity(row).name, '管理员（当时）')
  assert.equal(details.actorIdentity(row).sourceLabel, '操作时姓名')
  assert.equal(details.targetIdentity(row).name, '审核员（当时）')
  assert.equal(details.targetIdentity(row).sourceLabel, '操作时名称')
})

test('current names are explicitly marked as historical assistance for old logs', () => {
  const row = {
    ...baseRow,
    userId: 3,
    employeeNo: 'A003',
    actorName: '当前管理员',
    actorNameSource: 'current',
    targetName: '当前角色名',
    targetNameSource: 'current',
    detail: { oldName: '旧角色名', newName: '当时的新角色名' },
  }

  assert.equal(details.actorIdentity(row).sourceLabel, '当前姓名（历史辅助）')
  assert.equal(details.targetIdentity(row).name, '当时的新角色名')
  assert.equal(details.targetIdentity(row).sourceLabel, '历史详情名称')
  assert.equal(details.displayChanges(row)[0].before, '旧角色名')
  assert.equal(details.displayChanges(row)[0].after, '当时的新角色名')
})

test('a top-level snapshot still takes priority over legacy detail names', () => {
  const row = {
    ...baseRow,
    targetName: '接口返回的快照名称',
    targetNameSource: 'snapshot',
    detail: { oldName: '旧名', newName: '详情新名' },
  }
  assert.equal(details.targetIdentity(row).name, '接口返回的快照名称')
  assert.equal(details.targetIdentity(row).sourceLabel, '操作时名称')
})

test('structured changes translate status codes and drive a useful summary', () => {
  const row = {
    ...baseRow,
    action: 'ROLE_STATUS',
    detail: {
      changes: [{ field: 'status', label: '状态', before: 'ACTIVE', after: 'DISABLED' }],
    },
  }

  assert.equal(details.formatAuditValue('ACTIVE'), '启用')
  assert.equal(details.formatAuditValue('DISABLED'), '禁用')
  assert.equal(details.detailSummary(row), '状态：启用 → 禁用')
})

test('permission and member additions preserve names, codes, employee numbers and IDs', () => {
  const permissionRow = {
    ...baseRow,
    action: 'ROLE_ASSIGN_PERMS',
    detail: {
      addedPermissions: [{ id: 8, code: 'project:edit', name: '编辑项目' }],
      removedPermissions: [{ id: 3, code: 'file:delete', name: '删除文件' }],
    },
  }
  const memberRow = {
    ...baseRow,
    action: 'PROJECT_MEMBERS',
    targetType: 'project',
    detail: {
      addedMembers: [{ id: 11, employeeNo: 'E011', realName: '张工' }],
      removedMembers: [{ id: 12, employeeNo: 'E012', realName: '李工' }],
    },
  }

  const permissions = details.collectionChanges(permissionRow)
  assert.equal(permissions.addedPermissions[0].title, '编辑项目')
  assert.equal(permissions.addedPermissions[0].meta, 'project:edit · ID 8')
  assert.equal(details.detailSummary(permissionRow), '新增 1 项权限，移除 1 项权限')

  const members = details.collectionChanges(memberRow)
  assert.equal(members.addedMembers[0].title, '张工')
  assert.equal(members.addedMembers[0].meta, '工号 E011 · ID 11')
  assert.equal(details.detailSummary(memberRow), '新增 1 名成员，移除 1 名成员')
})

test('legacy role permission logs show set differences as IDs without inventing names', () => {
  const row = {
    ...baseRow,
    action: 'ROLE_ASSIGN_PERMS',
    detail: {
      oldPermissionCount: 2,
      newPermissionCount: 3,
      oldPermissionIds: [1, 2],
      newPermissionIds: [2, 3, 4],
    },
  }
  const changes = details.collectionChanges(row)

  assert.equal(changes.addedPermissions.map(item => item.title).join(','), '权限 ID 3,权限 ID 4')
  assert.equal(changes.removedPermissions[0].title, '权限 ID 1')
  assert.equal(changes.permissionsAreIds, true)
  assert.equal(details.historicalNote(row), '该历史日志只保存了权限 ID，名称和编码无法还原。')
  assert.doesNotMatch(details.detailSummary(row), /^无：/)
})

test('an explicit empty named permission diff is not mislabeled as ID-only history', () => {
  const row = {
    ...baseRow,
    action: 'ROLE_ASSIGN_PERMS',
    detail: {
      oldPermissionIds: [1, 2],
      newPermissionIds: [1, 2],
      addedPermissions: [],
      removedPermissions: [],
    },
  }

  assert.equal(details.collectionChanges(row).permissionsAreIds, false)
  assert.equal(details.historicalNote(row), undefined)
})

test('legacy supplier rename and project logs without detail remain explicit', () => {
  const supplier = {
    ...baseRow,
    action: 'SUPPLIER_UPDATE',
    targetType: 'supplier',
    detail: { oldName: '甲供应商', newName: '乙供应商' },
  }
  const project = { ...baseRow, action: 'PROJECT_UPDATE', targetType: 'project', detail: null }

  assert.equal(details.detailSummary(supplier), '供应商名称：甲供应商 → 乙供应商')
  assert.equal(details.detailSummary(project), '历史日志未记录详情')
  assert.match(details.historicalNote(project), /无法还原当时的具体变化/)
})

test('workflow reasons and mail errors remain visible beside structured changes', () => {
  const rejected = {
    ...baseRow,
    action: 'PROJECT_REJECT',
    targetType: 'project',
    detail: {
      changes: [{ field: 'status', label: '项目状态', before: 'PENDING_CONFIRMATION', after: 'IN_PROGRESS' }],
      reason: '尺寸数据缺失',
    },
  }
  const mail = {
    ...baseRow,
    action: 'EMAIL_FAILED',
    targetType: 'email_outbox',
    detail: { error: 'SMTP 超时', retryCount: 2 },
  }

  assert.equal(details.detailNotes(rejected)[0].label, '驳回原因')
  assert.match(details.detailSummary(rejected), /尺寸数据缺失/)
  assert.equal(details.detailNotes(mail).map(item => item.label).join(','), '失败原因,重试次数')
})

test('category selection filters actions and clears an incompatible action', () => {
  const orgActions = details.actionOptionsForCategory('ORG')
  assert.equal(orgActions.some(([code]) => code === 'ROLE_ASSIGN_PERMS'), true)
  assert.equal(orgActions.every(([, meta]) => meta.categoryLabel === '组织与权限'), true)
  assert.equal(details.actionForCategory('ORG', 'LOGIN'), undefined)
  assert.equal(details.actionForCategory('AUTH', 'LOGIN'), 'LOGIN')
  assert.equal(details.actionForCategory(undefined, 'LOGIN'), 'LOGIN')
})

test('frontend action categories match the backend category filter', () => {
  const backend = fs.readFileSync(path.resolve(__dirname, '../../server_dotnet/Yf.Api/Modules/System/SystemModule.cs'), 'utf8')
  const backendCategory = new Map()
  for (const [, category, actions] of backend.matchAll(/\["([A-Z]+)"\] = "([A-Z_ ]+)"\.Split/g)) {
    for (const code of actions.split(' ')) backendCategory.set(code, category)
  }
  assert.ok(backendCategory.size > 0, 'backend categories must be parseable')
  const frontendCategory = new Map(Object.entries(details.ACTIONS).map(([code, meta]) => [code, meta.categoryCode]))
  for (const code of new Set([...backendCategory.keys(), ...frontendCategory.keys()])) {
    assert.equal(frontendCategory.get(code), backendCategory.get(code), code)
  }
})

test('raw detail copy hides nested secrets and reports writer failures', async () => {
  const detail = {
    auditContext: { requestId: 'req-visible' },
    password: 'do-not-show',
    nested: { accessToken: 'also-secret', reason: 'visible' },
    changes: [{ field: 'password', before: 'hidden-before', after: 'hidden-after' }],
  }
  const writes = []
  const copied = await details.copySafeDetail(detail, async value => { writes.push(value) })
  const failed = await details.copySafeDetail(detail, async () => { throw new Error('denied') })

  assert.equal(copied, true)
  assert.equal(failed, false)
  assert.match(writes[0], /\[已隐藏\]/)
  assert.doesNotMatch(writes[0], /do-not-show|also-secret|hidden-before|hidden-after/)
  assert.match(writes[0], /req-visible|visible/)
  assert.equal(details.formatChangeValue({ field: 'password', before: 'old', after: 'new' }, 'after'), '[已隐藏]')
})

test('system tasks distinguish non-applicable request metadata from old missing metadata', () => {
  assert.equal(details.sourceDetailValue({ source: 'SYSTEM' }, null), '不适用（系统任务）')
  assert.equal(details.sourceDetailValue({ source: 'HTTP' }, null), '未记录')
  assert.equal(details.sourceDetailValue(undefined, null), '历史日志未记录')
  assert.equal(details.sourceDetailValue(undefined, '127.0.0.1'), '127.0.0.1')
  assert.equal(details.sourceDetailValue({ source: 'HTTP' }, ' req-2 '), 'req-2')
})

test('new no-op logs are not described as incomplete history and summaries hide secrets', () => {
  const noop = { ...baseRow, action: 'SUPPLIER_UPDATE', detail: { changes: [], oldName: '同名', newName: '同名' } }
  assert.equal(details.displayChanges(noop).length, 0)
  assert.equal(details.detailSummary(noop), '已保存，未发生字段变化')
  const secret = { ...baseRow, action: 'CONFIG_UPDATE', detail: { changes: [{ field: 'password', label: '密码', before: 'hidden-before', after: 'hidden-after' }] } }
  assert.doesNotMatch(details.detailSummary(secret), /hidden-before|hidden-after/)
  const smtp = { ...baseRow, action: 'CONFIG_UPDATE', detail: { changes: [], passwordChanged: true } }
  assert.match(details.detailSummary(smtp), /已更新邮箱密码/)
  assert.match(details.detailNotes(smtp)[0].value, /不记录密码/)
})

test('automatic retention records summarize how many expired logs were removed', () => {
  const row = { ...baseRow, action: 'AUDIT_LOG_RETENTION', targetType: 'audit_log', targetId: null,
    detail: { deleted: 1250, retentionDays: 30, cutoff: '2026-08-23T00:00:00Z' } }
  assert.equal(details.ACTIONS.AUDIT_LOG_RETENTION.categoryCode, 'SYSTEM')
  assert.equal(details.detailSummary(row), '已自动清理 1250 条超过 30 天的操作日志')
})
