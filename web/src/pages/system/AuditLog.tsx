import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  Button, Card, DatePicker, Drawer, Input, Select, Space, Table, Tag, Typography,
} from '@arco-design/web-react'
import { IconEye, IconRefresh, IconSearch } from '@arco-design/web-react/icon'
import http from '../../api/client'
import { type PageResp, fmtTime } from '../../api/types'

type AuditDetail = Record<string, unknown>

interface LogRow {
  id: number
  userId?: number | null
  username?: string | null
  action: string
  targetType?: string | null
  targetId?: string | null
  detail?: AuditDetail | null
  ip?: string | null
  createdAt: string
}

interface Filters {
  keyword: string
  category?: string
  action?: string
  targetType?: string
  range: string[]
}

const ACTIONS: Record<string, { label: string; category: string; color: string }> = {
  LOGIN: { label: '登录成功', category: '认证安全', color: 'green' },
  LOGIN_FAILED: { label: '登录失败', category: '认证安全', color: 'red' },
  LOGIN_LOCKED: { label: '账号锁定', category: '认证安全', color: 'red' },
  LOGOUT: { label: '退出登录', category: '认证安全', color: 'gray' },
  PASSWORD_CHANGE: { label: '修改密码', category: '认证安全', color: 'orange' },
  PROJECT_CREATE: { label: '创建项目', category: '项目协作', color: 'arcoblue' },
  PROJECT_UPDATE: { label: '更新项目', category: '项目协作', color: 'arcoblue' },
  PROJECT_STATUS: { label: '变更项目状态', category: '项目协作', color: 'orange' },
  PROJECT_MEMBERS: { label: '调整项目成员', category: '项目协作', color: 'purple' },
  ROUND_CREATE: { label: '创建确认轮次', category: '项目协作', color: 'arcoblue' },
  ROUND_CONFIRM: { label: '确认轮次', category: '项目协作', color: 'green' },
  ROUND_REJECT: { label: '驳回轮次', category: '项目协作', color: 'red' },
  ROUND_CANCEL: { label: '取消轮次', category: '项目协作', color: 'orange' },
  FILE_UPLOAD: { label: '上传文件', category: '文件', color: 'arcoblue' },
  FILE_DOWNLOAD: { label: '下载文件', category: '文件', color: 'cyan' },
  FILE_BATCH_DOWNLOAD: { label: '批量下载', category: '文件', color: 'cyan' },
  FILE_DELETE: { label: '删除文件', category: '文件', color: 'red' },
  UPLOAD_ABORT: { label: '取消上传', category: '文件', color: 'orange' },
  MESSAGE_CREATE: { label: '发送留言', category: '留言', color: 'arcoblue' },
  MESSAGE_DELETE: { label: '删除留言', category: '留言', color: 'red' },
  USER_CREATE: { label: '创建用户', category: '组织权限', color: 'arcoblue' },
  USER_UPDATE: { label: '更新用户', category: '组织权限', color: 'purple' },
  USER_STATUS: { label: '变更用户状态', category: '组织权限', color: 'orange' },
  USER_RESET_PASSWORD: { label: '重置用户密码', category: '组织权限', color: 'orange' },
  USER_ASSIGN_ROLE: { label: '调整用户角色', category: '组织权限', color: 'purple' },
  USER_ASSIGN_ROLES: { label: '调整用户角色（旧）', category: '组织权限', color: 'purple' },
  DEPT_CREATE: { label: '创建部门', category: '组织权限', color: 'arcoblue' },
  DEPT_UPDATE: { label: '更新部门', category: '组织权限', color: 'purple' },
  DEPT_STATUS: { label: '变更部门状态', category: '组织权限', color: 'orange' },
  ROLE_CREATE: { label: '创建角色', category: '组织权限', color: 'arcoblue' },
  ROLE_UPDATE: { label: '更新角色', category: '组织权限', color: 'purple' },
  ROLE_STATUS: { label: '变更角色状态', category: '组织权限', color: 'orange' },
  ROLE_ASSIGN_PERMS: { label: '调整角色权限', category: '组织权限', color: 'purple' },
  SUPPLIER_CREATE: { label: '创建供应商', category: '供应商', color: 'arcoblue' },
  SUPPLIER_UPDATE: { label: '更新供应商', category: '供应商', color: 'purple' },
  SUPPLIER_STATUS: { label: '变更供应商状态', category: '供应商', color: 'orange' },
  SUPPLIER_ACCOUNT_CREATE: { label: '创建供应商账号', category: '供应商', color: 'arcoblue' },
  SUPPLIER_ACCOUNT_UPDATE: { label: '更新供应商账号', category: '供应商', color: 'purple' },
  SUPPLIER_ACCOUNT_STATUS: { label: '变更供应商账号状态', category: '供应商', color: 'orange' },
  SUPPLIER_ACCOUNT_RESET_PASSWORD: { label: '重置供应商密码', category: '供应商', color: 'orange' },
  CONFIG_UPDATE: { label: '更新系统参数', category: '系统', color: 'orange' },
}

const CATEGORY_OPTIONS = [
  ['AUTH', '认证安全'], ['PROJECT', '项目协作'], ['FILE', '文件'], ['MESSAGE', '留言'], ['ORG', '组织与权限'], ['SUPPLIER', '供应商'], ['SYSTEM', '系统'],
]

const TARGET_LABELS: Record<string, string> = {
  user: '用户', role: '角色', department: '部门', supplier: '供应商', project: '项目',
  round: '轮次', file: '文件', message: '留言', upload_session: '上传任务', system_config: '系统参数',
}

const FIELD_LABELS: Record<string, string> = {
  realName: '姓名', email: '邮箱', phone: '电话', departmentId: '部门', roleId: '角色',
}

function displayValue(value: unknown): string {
  if (value == null || value === '') return '无'
  if (typeof value === 'boolean') return value ? '是' : '否'
  if (Array.isArray(value)) return value.length ? value.join('、') : '无'
  if (typeof value === 'object') return JSON.stringify(value)
  return String(value)
}

function detailSummary(row: LogRow): string {
  const detail = row.detail || {}
  if (row.action === 'USER_ASSIGN_ROLE' || row.action === 'USER_ASSIGN_ROLES') {
    return `${displayValue(detail.username)}：角色 ${displayValue(detail.oldRoleId)} → ${displayValue(detail.newRoleName ?? detail.newRoleId)}`
  }
  if (row.action === 'USER_UPDATE') {
    const fields = Array.isArray(detail.changedFields)
      ? detail.changedFields.map((field) => FIELD_LABELS[String(field)] || String(field)).join('、')
      : ''
    if (!detail.username && !fields) return '历史记录未包含变更字段'
    return `${displayValue(detail.username)}${fields ? `；修改 ${fields}` : ''}`
  }
  if (row.action.endsWith('_STATUS')) {
    const subject = detail.name ?? detail.username ?? detail.code ?? `${TARGET_LABELS[row.targetType || ''] || row.targetType || '对象'} ${row.targetId || ''}`
    const nextStatus = detail.newStatus ?? detail.status ?? detail.to
    if (detail.oldStatus == null) return `${displayValue(subject)}：状态变更为 ${displayValue(nextStatus)}`
    return `${displayValue(subject)}：${displayValue(detail.oldStatus)} → ${displayValue(nextStatus)}`
  }
  if (row.action === 'ROLE_ASSIGN_PERMS') {
    return `${displayValue(detail.code)}：权限点 ${displayValue(detail.oldPermissionCount)} → ${displayValue(detail.newPermissionCount)}`
  }
  const parts = [detail.username, detail.name, detail.code, detail.fileName, detail.projectId, detail.roundNo]
    .filter((value) => value != null && value !== '')
    .map(displayValue)
  return parts.join(' · ') || (row.detail ? '查看结构化详情' : '未记录补充信息')
}

export default function AuditLog() {
  const emptyFilters: Filters = { keyword: '', range: [] }
  const [draft, setDraft] = useState<Filters>(emptyFilters)
  const [filters, setFilters] = useState<Filters>(emptyFilters)
  const [data, setData] = useState<PageResp<LogRow>>({ list: [], total: 0, page: 1, pageSize: 20 })
  const [loading, setLoading] = useState(true)
  const [selected, setSelected] = useState<LogRow | null>(null)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [reloadKey, setReloadKey] = useState(0)

  const fetchLogs = useCallback(async () => {
    void reloadKey
    const r = await http.get('/admin/audit-logs', {
      params: {
        page,
        pageSize,
        keyword: filters.keyword || undefined,
        category: filters.category,
        action: filters.action,
        targetType: filters.targetType,
        start: filters.range[0] ? new Date(filters.range[0].replace(' ', 'T')).toISOString() : undefined,
        end: filters.range[1] ? new Date(filters.range[1].replace(' ', 'T')).toISOString() : undefined,
      },
    })
    return r.data as PageResp<LogRow>
  }, [filters, page, pageSize, reloadKey])

  useEffect(() => {
    let active = true
    fetchLogs()
      .then((next) => { if (active) setData(next) })
      .finally(() => { if (active) setLoading(false) })
    return () => { active = false }
  }, [fetchLogs])

  const actionOptions = useMemo(() => Object.entries(ACTIONS).sort((a, b) => a[1].label.localeCompare(b[1].label, 'zh-CN')), [])
  const applyFilters = () => { setLoading(true); setPage(1); setFilters({ ...draft }) }
  const resetFilters = () => { setLoading(true); setDraft(emptyFilters); setFilters(emptyFilters); setPage(1) }

  return (
    <Card className="page-card audit-page">
      <div className="audit-heading">
        <div>
          <Typography.Title heading={5} style={{ margin: 0 }}>操作日志</Typography.Title>
          <Typography.Text type="secondary">记录关键业务与权限变更；敏感密码不会写入日志</Typography.Text>
        </div>
        <Button icon={<IconRefresh />} onClick={() => { setLoading(true); setReloadKey((value) => value + 1) }}>刷新</Button>
      </div>

      <div className="audit-filter-panel">
        <Input allowClear value={draft.keyword} placeholder="操作人 / 动作编码 / 对象" prefix={<IconSearch />} onChange={(value) => setDraft((state) => ({ ...state, keyword: value }))} onPressEnter={applyFilters} />
        <Select allowClear value={draft.category} placeholder="业务分类" onChange={(value) => setDraft((state) => ({ ...state, category: value as string | undefined }))}>
          {CATEGORY_OPTIONS.map(([value, label]) => <Select.Option key={value} value={value}>{label}</Select.Option>)}
        </Select>
        <Select allowClear showSearch value={draft.action} placeholder="具体操作" onChange={(value) => setDraft((state) => ({ ...state, action: value as string | undefined }))}>
          {actionOptions.map(([value, meta]) => <Select.Option key={value} value={value}>{meta.label}（{value}）</Select.Option>)}
        </Select>
        <Select allowClear value={draft.targetType} placeholder="对象类型" onChange={(value) => setDraft((state) => ({ ...state, targetType: value as string | undefined }))}>
          {Object.entries(TARGET_LABELS).map(([value, label]) => <Select.Option key={value} value={value}>{label}</Select.Option>)}
        </Select>
        <DatePicker.RangePicker showTime value={draft.range} onChange={(value) => setDraft((state) => ({ ...state, range: (value as string[]) || [] }))} />
        <Space><Button type="primary" onClick={applyFilters}>查询</Button><Button onClick={resetFilters}>重置</Button></Space>
      </div>

      <div className="audit-result-bar"><Typography.Text type="secondary">共 {data.total} 条记录</Typography.Text></div>

      <Table
        rowKey="id"
        loading={loading}
        data={data.list}
        scroll={{ x: 910 }}
        columns={[
          { title: '时间', dataIndex: 'createdAt', width: 160, render: fmtTime },
          {
            title: '操作人', dataIndex: 'username', width: 125,
            render: (value?: string, row?: LogRow) => <div className="audit-actor"><span className="audit-avatar">{(value || '系').slice(0, 1).toUpperCase()}</span><span><b>{value || '系统'}</b><small>{row?.userId ? `ID ${row.userId}` : '系统事件'}</small></span></div>,
          },
          {
            title: '操作', dataIndex: 'action', width: 180,
            render: (value: string) => {
              const meta = ACTIONS[value] || { label: value, category: '其他', color: 'gray' }
              return <div className="audit-action"><Tag color={meta.color}>{meta.label}</Tag><small>{value}</small></div>
            },
          },
          { title: '内容摘要', width: 260, render: (_: unknown, row?: LogRow) => row ? <Typography.Text ellipsis={{ showTooltip: true }}>{detailSummary(row)}</Typography.Text> : '-' },
          {
            title: '对象', width: 110,
            render: (_: unknown, row?: LogRow) => row?.targetType ? <div className="audit-target"><span>{TARGET_LABELS[row.targetType] || row.targetType}</span><small>{row.targetId ? `#${row.targetId}` : '未指定 ID'}</small></div> : '-',
          },
          { title: '详情', width: 75, fixed: 'right' as const, align: 'center' as const, render: (_: unknown, row?: LogRow) => row ? <Button size="mini" type="text" icon={<IconEye />} onClick={() => setSelected(row)}>查看</Button> : null },
        ]}
        pagination={{ total: data.total, current: page, pageSize, showTotal: true, sizeCanChange: true, onChange: (nextPage, nextSize) => { setLoading(true); setPage(nextPage); setPageSize(nextSize) } }}
      />

      <Drawer width={520} title="操作日志详情" visible={!!selected} onCancel={() => setSelected(null)} footer={null}>
        {selected && (
          <div className="audit-detail">
            <div className="audit-detail-title"><Tag color={(ACTIONS[selected.action] || { color: 'gray' }).color}>{ACTIONS[selected.action]?.label || selected.action}</Tag><Typography.Text type="secondary">日志 #{selected.id}</Typography.Text></div>
            <div className="audit-detail-grid">
              <span>发生时间</span><b>{fmtTime(selected.createdAt)}</b>
              <span>操作人</span><b>{selected.username || '系统'}{selected.userId ? `（ID ${selected.userId}）` : ''}</b>
              <span>动作编码</span><code>{selected.action}</code>
              <span>操作对象</span><b>{selected.targetType ? `${TARGET_LABELS[selected.targetType] || selected.targetType}${selected.targetId ? ` #${selected.targetId}` : ''}` : '-'}</b>
              <span>IP 地址</span><b>{selected.ip || '-'}</b>
              <span>内容摘要</span><b>{detailSummary(selected)}</b>
            </div>
            <Typography.Text className="audit-json-label">结构化数据</Typography.Text>
            <pre className="audit-json">{selected.detail ? JSON.stringify(selected.detail, null, 2) : '本条日志没有补充数据'}</pre>
          </div>
        )}
      </Drawer>
    </Card>
  )
}
