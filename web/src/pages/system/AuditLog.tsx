import { useCallback, useEffect, useMemo, useState } from 'react'
import { Button, Card, DatePicker, Input, Select, Space } from '@arco-design/web-react'
import { IconSearch } from '@arco-design/web-react/icon'
import http from '../../api/client'
import { type PageResp } from '../../api/types'
import {
  AuditDetailDrawer, AuditLoadError, AuditResultBar, AuditTable, type AuditPresenter,
} from '../../components/AuditLogParts'
import {
  ACTIONS, CATEGORY_OPTIONS, TARGET_LABELS, actionForCategory, actionOptionsForCategory,
  actorIdentity, detailNotes, detailSummary, historicalNote, targetIdentity,
  type AuditLogRow,
} from './auditLogDetails'
import type { ApiResponses } from '../../api/types'

interface Filters {
  keyword: string
  category?: string
  action?: string
  targetType?: string
  range: string[]
}

const EMPTY_FILTERS: Filters = { keyword: '', range: [] }

const PRESENTER: AuditPresenter = {
  meta: (row) => ACTIONS[row.action] || { label: row.action, categoryCode: '', categoryLabel: '其他', color: 'gray' },
  actor: actorIdentity,
  target: (row) => targetIdentity(row),
  summary: detailSummary,
  notes: detailNotes,
  historicalNote,
}

export default function AuditLog() {
  const [draft, setDraft] = useState<Filters>(EMPTY_FILTERS)
  const [filters, setFilters] = useState<Filters>(EMPTY_FILTERS)
  const [data, setData] = useState<PageResp<AuditLogRow>>({ list: [], total: 0, page: 1, pageSize: 20 })
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState(false)
  const [selected, setSelected] = useState<AuditLogRow | null>(null)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [reloadKey, setReloadKey] = useState(0)
  const fetchLogs = useCallback(async () => {
    void reloadKey
    const response = await http.get<ApiResponses['GET /admin/audit-logs']>('/admin/audit-logs', {
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
  const beginReload = () => {
    setLoading(true)
    setLoadError(false)
  }
  const reload = () => { beginReload(); setReloadKey((value) => value + 1) }
  const applyFilters = () => { beginReload(); setPage(1); setFilters({ ...draft }) }
  const resetFilters = () => { beginReload(); setDraft(EMPTY_FILTERS); setFilters(EMPTY_FILTERS); setPage(1) }

  return (
    <Card className="page-card page-card--table audit-page">
      <h1 className="visually-hidden">操作日志</h1>

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

      <AuditResultBar
        total={data.total}
        hint={filters.keyword && !filters.range.length ? '关键词默认搜索最近 30 天' : undefined}
        onRefresh={reload}
      />

      {loadError ? <AuditLoadError onRetry={reload} /> : (
        <AuditTable
          presenter={PRESENTER}
          rows={data.list}
          loading={loading}
          total={data.total}
          page={page}
          pageSize={pageSize}
          onPageChange={(nextPage, nextSize) => { beginReload(); setPage(nextPage); setPageSize(nextSize) }}
          onView={setSelected}
        />
      )}

      <AuditDetailDrawer title="操作日志详情" row={selected} presenter={PRESENTER} onClose={() => setSelected(null)} />
    </Card>
  )
}
