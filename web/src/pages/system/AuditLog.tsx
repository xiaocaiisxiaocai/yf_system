import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  Button, Card, Collapse, DatePicker, Drawer, Input, Message, Popconfirm, Select, Space, Table, Tag, Typography,
} from '@arco-design/web-react'
import { IconCheck, IconCopy, IconEye, IconRefresh, IconSearch } from '@arco-design/web-react/icon'
import http from '../../api/client'
import { actionSlots } from '../../components/ActionSlots'
import { useAuth } from '../../store/auth'
import { type PageResp, fmtTime } from '../../api/types'
import {
  ACTIONS, CATEGORY_OPTIONS, TARGET_LABELS, actionForCategory, actionOptionsForCategory,
  actorIdentity, collectionChanges, copySafeDetail, detailNotes, detailSummary, displayChanges, formatChangeValue,
  historicalNote, safeDetailJson, sourceDetailValue, sourceLabel, targetIdentity,
  type AuditLogRow,
} from './auditLogDetails'
import './AuditLog.css'

interface Filters {
  keyword: string
  category?: string
  action?: string
  targetType?: string
  range: string[]
}

const EMPTY_FILTERS: Filters = { keyword: '', range: [] }

function actionMeta(action: string) {
  return ACTIONS[action] || { label: action, categoryCode: '', categoryLabel: '其他', color: 'gray' }
}

function IdentityCard({ title, identity }: { title: string; identity: ReturnType<typeof actorIdentity> }) {
  return (
    <div className="audit-identity-card">
      <span className="audit-identity-title">{title}</span>
      <strong title={identity.name}>{identity.name}</strong>
      <span title={identity.identifier}>{identity.identifier}</span>
      <small className={`audit-source audit-source--${identity.source}`}>{identity.sourceLabel}</small>
    </div>
  )
}

function ChangeCollection({
  title, tone, items,
}: {
  title: string
  tone: 'added' | 'removed'
  items: ReturnType<typeof collectionChanges>['addedPermissions']
}) {
  if (!items.length) return null
  return (
    <div className={`audit-collection audit-collection--${tone}`}>
      <div className="audit-collection-title"><span>{title}</span><Tag>{items.length}</Tag></div>
      <div className="audit-collection-list">
        {items.map((item) => (
          <div className="audit-collection-item" key={item.key}>
            <strong title={item.title}>{item.title}</strong>
            {item.meta && <small title={item.meta}>{item.meta}</small>}
          </div>
        ))}
      </div>
    </div>
  )
}

function AuditDetail({ row }: { row: AuditLogRow }) {
  const [copied, setCopied] = useState(false)
  const meta = actionMeta(row.action)
  const actor = actorIdentity(row)
  const target = targetIdentity(row)
  const changes = displayChanges(row)
  const collections = collectionChanges(row)
  const notes = detailNotes(row)
  const note = historicalNote(row)
  const context = row.detail?.auditContext
  const rawJson = safeDetailJson(row.detail)
  const hasCollections = collections.addedPermissions.length > 0
    || collections.removedPermissions.length > 0
    || collections.addedMembers.length > 0
    || collections.removedMembers.length > 0

  const copyRaw = async () => {
    const success = await copySafeDetail(row.detail, navigator.clipboard?.writeText?.bind(navigator.clipboard))
    if (success) {
      setCopied(true)
      Message.success('原始数据已复制')
    } else {
      setCopied(false)
      Message.error('复制失败，请手动选择原始数据')
    }
  }

  return (
    <div className="audit-detail">
      <div className="audit-detail-title">
        <div>
          <Tag color={meta.color}>{meta.label}</Tag>
          <span>{meta.categoryLabel}</span>
        </div>
        <Typography.Text type="secondary">日志 #{row.id}</Typography.Text>
      </div>

      <section className="audit-section" aria-labelledby="audit-overview-heading">
        <h2 id="audit-overview-heading">操作概览</h2>
        <p className="audit-overview">
          <strong>{actor.name}</strong>
          <span>于 {fmtTime(row.createdAt)} 对</span>
          <strong>{target.name}</strong>
          <span>执行了“{meta.label}”</span>
        </p>
        <div className="audit-identities">
          <IdentityCard title="操作人" identity={actor} />
          <IdentityCard title="操作对象" identity={target} />
        </div>
      </section>

      <section className="audit-section" aria-labelledby="audit-changes-heading">
        <h2 id="audit-changes-heading">变化明细</h2>
        {changes.length > 0 && (
          <div className="audit-change-table">
            <div className="audit-change-head"><span>字段</span><span>修改前</span><span>修改后</span></div>
            {changes.map((change, index) => (
              <div className="audit-change-row" key={`${change.field}-${index}`}>
                <strong title={change.label}>{change.label}</strong>
                <span className="audit-before" title={formatChangeValue(change, 'before')}>{formatChangeValue(change, 'before')}</span>
                <span className="audit-after" title={formatChangeValue(change, 'after')}>{formatChangeValue(change, 'after')}</span>
              </div>
            ))}
          </div>
        )}
        {hasCollections && (
          <div className="audit-collections">
            <ChangeCollection title="新增权限" tone="added" items={collections.addedPermissions} />
            <ChangeCollection title="移除权限" tone="removed" items={collections.removedPermissions} />
            <ChangeCollection title="新增成员" tone="added" items={collections.addedMembers} />
            <ChangeCollection title="移除成员" tone="removed" items={collections.removedMembers} />
          </div>
        )}
        {notes.length > 0 && (
          <div className="audit-detail-notes">
            {notes.map((item, index) => (
              <div className={`audit-detail-note audit-detail-note--${item.tone}`} key={`${item.label}-${index}`}>
                <span>{item.label}</span><strong>{item.value}</strong>
              </div>
            ))}
          </div>
        )}
        {changes.length === 0 && !hasCollections && notes.length === 0 && <div className="audit-empty-change">{detailSummary(row)}</div>}
        {note && <div className="audit-history-note">{note}</div>}
      </section>

      <section className="audit-section" aria-labelledby="audit-source-heading">
        <h2 id="audit-source-heading">来源信息</h2>
        <div className="audit-source-grid">
          <span>来源</span><strong>{sourceLabel(context?.source)}</strong>
          <span>请求 ID</span><span className="audit-source-value">{sourceDetailValue(context, context?.requestId)}</span>
          <span>IP 地址</span><span className="audit-source-value">{sourceDetailValue(context, row.ip)}</span>
          <span>动作编码</span><code>{row.action}</code>
        </div>
      </section>

      <Collapse className="audit-raw-collapse" defaultActiveKey={[]}>
        <Collapse.Item name="raw" header="原始数据">
          <div className="audit-raw-toolbar">
            <span>敏感字段会隐藏后再显示和复制</span>
            <Button size="mini" icon={copied ? <IconCheck /> : <IconCopy />} onClick={copyRaw}>{copied ? '已复制' : '复制'}</Button>
          </div>
          <pre className="audit-json">{rawJson}</pre>
        </Collapse.Item>
      </Collapse>
    </div>
  )
}

export default function AuditLog() {
  const canDelete = useAuth((state) => state.hasPerm('log:delete'))
  const [draft, setDraft] = useState<Filters>(EMPTY_FILTERS)
  const [filters, setFilters] = useState<Filters>(EMPTY_FILTERS)
  const [data, setData] = useState<PageResp<AuditLogRow>>({ list: [], total: 0, page: 1, pageSize: 20 })
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState(false)
  const [selected, setSelected] = useState<AuditLogRow | null>(null)
  const [selectedIds, setSelectedIds] = useState<number[]>([])
  const [deleting, setDeleting] = useState(false)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [reloadKey, setReloadKey] = useState(0)
  const deleteState = useRef({ ids: [] as number[], busy: true, allowed: false })
  const visibleRows = useRef<AuditLogRow[]>([])
  const deleteBusy = loading || deleting

  useEffect(() => {
    deleteState.current.busy = deleteBusy
    deleteState.current.allowed = canDelete
    visibleRows.current = data.list
  }, [canDelete, data.list, deleteBusy])

  const fetchLogs = useCallback(async () => {
    void reloadKey
    const response = await http.get('/admin/audit-logs', {
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
    return response.data as PageResp<AuditLogRow>
  }, [filters, page, pageSize, reloadKey])

  useEffect(() => {
    let active = true
    fetchLogs()
      .then((next) => {
        if (active) {
          setData(next)
          setLoadError(false)
          const lastPage = Math.max(1, Math.ceil(next.total / next.pageSize))
          setPage((currentPage) => currentPage > lastPage ? lastPage : currentPage)
        }
      })
      .catch(() => { if (active) setLoadError(true) })
      .finally(() => { if (active) setLoading(false) })
    return () => { active = false }
  }, [fetchLogs])

  const actionOptions = useMemo(() => actionOptionsForCategory(draft.category), [draft.category])
  const clearSelection = () => {
    deleteState.current.ids = []
    setSelectedIds([])
  }
  const beginReload = () => {
    clearSelection()
    deleteState.current.busy = true
    setLoading(true)
    setLoadError(false)
  }
  const applyFilters = () => { beginReload(); setPage(1); setFilters({ ...draft }) }
  const resetFilters = () => { beginReload(); setDraft(EMPTY_FILTERS); setFilters(EMPTY_FILTERS); setPage(1) }

  const removeOne = async (row: AuditLogRow) => {
    const isCurrentDeletableRow = visibleRows.current.some((current) => current.id === row.id && current.action !== 'AUDIT_LOG_DELETE')
    if (!deleteState.current.allowed || deleteState.current.busy || !isCurrentDeletableRow) return
    deleteState.current.busy = true
    setDeleting(true)
    let reload = false
    try {
      const response = await http.delete(`/admin/audit-logs/${row.id}`)
      Message.success(`已删除 ${Number(response.data?.deleted ?? 0)} 条`)
      if (selected?.id === row.id) setSelected(null)
      beginReload()
      setReloadKey((value) => value + 1)
      reload = true
    } finally {
      setDeleting(false)
      if (!reload) deleteState.current.busy = false
    }
  }

  const removeSelected = async () => {
    const selectableIds = new Set(visibleRows.current.filter((row) => row.action !== 'AUDIT_LOG_DELETE').map((row) => row.id))
    const ids = deleteState.current.ids.slice()
    if (!deleteState.current.allowed || deleteState.current.busy || ids.length === 0 || ids.some((id) => !selectableIds.has(id))) {
      clearSelection()
      return
    }
    deleteState.current.busy = true
    setDeleting(true)
    let reload = false
    try {
      const response = await http.post('/admin/audit-logs/batch-delete', { ids })
      Message.success(`已删除 ${Number(response.data?.deleted ?? 0)} 条`)
      setSelected(null)
      beginReload()
      setReloadKey((value) => value + 1)
      reload = true
    } finally {
      setDeleting(false)
      if (!reload) deleteState.current.busy = false
    }
  }

  return (
    <Card className="page-card page-card--table audit-page">
      <div className="audit-heading page-heading">
        <div><h1>操作日志</h1></div>
        <Button icon={<IconRefresh />} onClick={() => { beginReload(); setReloadKey((value) => value + 1) }}>刷新</Button>
      </div>

      <div className="audit-filter-panel">
        <Input allowClear value={draft.keyword} placeholder="搜索姓名、对象或详情" prefix={<IconSearch />} onChange={(value) => setDraft((state) => ({ ...state, keyword: value }))} onPressEnter={applyFilters} />
        <Select
          allowClear value={draft.category} placeholder="业务分类"
          onChange={(value) => setDraft((state) => {
            const category = value as string | undefined
            return { ...state, category, action: actionForCategory(category, state.action) }
          })}
        >
          {CATEGORY_OPTIONS.map(([value, label]) => <Select.Option key={value} value={value}>{label}</Select.Option>)}
        </Select>
        <Select allowClear showSearch value={draft.action} placeholder="具体操作" onChange={(value) => setDraft((state) => ({ ...state, action: value as string | undefined }))}>
          {actionOptions.map(([value, meta]) => <Select.Option key={value} value={value}>{meta.label}</Select.Option>)}
        </Select>
        <Select allowClear value={draft.targetType} placeholder="对象类型" onChange={(value) => setDraft((state) => ({ ...state, targetType: value as string | undefined }))}>
          {Object.entries(TARGET_LABELS).map(([value, label]) => <Select.Option key={value} value={value}>{label}</Select.Option>)}
        </Select>
        <DatePicker.RangePicker showTime value={draft.range} onChange={(value) => setDraft((state) => ({ ...state, range: (value as string[]) || [] }))} />
        <Space><Button type="primary" onClick={applyFilters}>查询</Button><Button onClick={resetFilters}>重置</Button></Space>
      </div>

      <div className="audit-result-bar">
        <Typography.Text type="secondary">共 {data.total} 条记录</Typography.Text>
        {canDelete && (
          <Popconfirm title={`确认删除选中的 ${selectedIds.length} 条日志？`} disabled={selectedIds.length === 0 || deleteBusy} onOk={removeSelected}>
            <Button status="danger" disabled={selectedIds.length === 0 || deleteBusy}>删除所选</Button>
          </Popconfirm>
        )}
      </div>

      {loadError ? (
        <div className="audit-load-error">
          <Typography.Text type="error">加载失败</Typography.Text>
          <Button size="small" onClick={() => { beginReload(); setReloadKey((value) => value + 1) }}>重试</Button>
        </div>
      ) : (
        <Table
          className="page-table"
          rowKey="id"
          loading={loading}
          data={data.list}
          scroll={{ x: 1128, y: 'var(--page-table-scroll-y)' }}
          rowSelection={canDelete ? {
            selectedRowKeys: selectedIds,
            checkboxProps: (row?: AuditLogRow) => ({ disabled: deleteBusy || row?.action === 'AUDIT_LOG_DELETE' }),
            onChange: (keys) => {
              const selectableIds = new Set(data.list.filter((row) => row.action !== 'AUDIT_LOG_DELETE').map((row) => row.id))
              const ids = keys.map(Number).filter((id) => selectableIds.has(id))
              deleteState.current.ids = ids
              setSelectedIds(ids)
            },
          } : undefined}
          columns={[
            { title: '时间', dataIndex: 'createdAt', width: 160, render: fmtTime },
            {
              title: '操作人', dataIndex: 'employeeNo', width: 168,
              render: (_: unknown, row?: AuditLogRow) => {
                if (!row) return '-'
                const identity = actorIdentity(row)
                return <div className="audit-actor"><span className="audit-avatar">{identity.name.slice(0, 1).toUpperCase()}</span><span><b title={identity.name}>{identity.name}</b><small title={identity.identifier}>{identity.identifier}</small></span></div>
              },
            },
            {
              title: '操作类型', dataIndex: 'action', width: 180,
              render: (value: string) => {
                const meta = actionMeta(value)
                return <div className="audit-action"><Tag color={meta.color}>{meta.label}</Tag><small title={value}>{meta.categoryLabel} · {value}</small></div>
              },
            },
            { title: '内容摘要', width: 280, render: (_: unknown, row?: AuditLogRow) => row ? <Typography.Text ellipsis={{ showTooltip: true }}>{detailSummary(row)}</Typography.Text> : '-' },
            {
              title: '对象', width: 180,
              render: (_: unknown, row?: AuditLogRow) => {
                if (!row) return '-'
                const identity = targetIdentity(row)
                return <div className="audit-target"><span title={identity.name}>{identity.name}</span><small title={identity.identifier}>{identity.identifier}</small></div>
              },
            },
            { title: '操作', width: 120, fixed: 'right' as const, align: 'center' as const, render: (_: unknown, row?: AuditLogRow) => row ? actionSlots([
              <Button key="view" size="mini" type="text" icon={<IconEye />} onClick={() => setSelected(row)}>查看</Button>,
              canDelete && row.action !== 'AUDIT_LOG_DELETE' && (
                <Popconfirm key="delete" title="确认删除这条日志？" disabled={deleteBusy} onOk={() => removeOne(row)}>
                  <Button size="mini" type="text" status="danger" disabled={deleteBusy}>删除</Button>
                </Popconfirm>
              ),
            ], 'single') : null },
          ]}
          pagination={{ total: data.total, current: page, pageSize, showTotal: true, sizeCanChange: true, onChange: (nextPage, nextSize) => { beginReload(); setPage(nextPage); setPageSize(nextSize) } }}
        />
      )}

      <Drawer className="audit-detail-drawer" width="min(720px, 100vw)" title="操作日志详情" visible={!!selected} onCancel={() => setSelected(null)} footer={null}>
        {selected && <AuditDetail key={selected.id} row={selected} />}
      </Drawer>
    </Card>
  )
}
