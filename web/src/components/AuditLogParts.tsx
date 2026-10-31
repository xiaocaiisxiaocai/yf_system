import { useState, type ReactNode } from 'react'
import { Button, Collapse, Drawer, Message, Table, Tag, Typography } from '@arco-design/web-react'
import { IconCheck, IconCopy, IconEye, IconRefresh } from '@arco-design/web-react/icon'
import { actionSlots } from './ActionSlots'
import { fmtTime } from '../api/types'
import {
  collectionChanges, copySafeDetail, displayChanges, formatChangeValue, safeDetailJson, sourceDetailValue, sourceLabel,
  type ActionMeta, type AuditLogRow, type DetailNote, type DisplayIdentity,
} from '../pages/system/auditLogDetails'
import './AuditLogParts.css'

/** 协作操作日志与 OEM 审计日志共用的展示层：两边各自把日志行解读成同一组展示字段。 */
export interface AuditPresenter {
  meta: (row: AuditLogRow) => ActionMeta
  actor: (row: AuditLogRow) => DisplayIdentity
  target: (row: AuditLogRow) => DisplayIdentity
  summary: (row: AuditLogRow) => string
  notes: (row: AuditLogRow) => DetailNote[]
  historicalNote?: (row: AuditLogRow) => string | undefined
}

/** Clipboard API 只在 HTTPS/localhost 可用；HTTP 部署时退回隐藏文本框 + execCommand。 */
async function writeClipboardText(text: string) {
  if (window.isSecureContext && navigator.clipboard?.writeText) {
    await navigator.clipboard.writeText(text)
    return
  }
  const textarea = document.createElement('textarea')
  textarea.value = text
  textarea.setAttribute('readonly', '')
  textarea.style.position = 'fixed'
  textarea.style.opacity = '0'
  document.body.appendChild(textarea)
  try {
    textarea.select()
    if (!document.execCommand('copy')) throw new Error('copy command rejected')
  } finally {
    textarea.remove()
  }
}

function IdentityCard({ title, identity }: { title: string; identity: DisplayIdentity }) {
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

function AuditDetail({ row, presenter }: { row: AuditLogRow; presenter: AuditPresenter }) {
  const [copied, setCopied] = useState(false)
  const meta = presenter.meta(row)
  const actor = presenter.actor(row)
  const target = presenter.target(row)
  const changes = displayChanges(row)
  const collections = collectionChanges(row)
  const notes = presenter.notes(row)
  const note = presenter.historicalNote?.(row)
  const context = row.detail?.auditContext
  const rawJson = safeDetailJson(row.detail)
  const hasCollections = collections.addedPermissions.length > 0
    || collections.removedPermissions.length > 0
    || collections.addedMembers.length > 0
    || collections.removedMembers.length > 0
  const summary = presenter.summary(row)

  const copyRaw = async () => {
    const success = await copySafeDetail(row.detail, writeClipboardText)
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
            {changes.map((change) => (
              <div className="audit-change-row" key={`${change.field}:${formatChangeValue(change, 'before')}:${formatChangeValue(change, 'after')}`}>
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
            {notes.map((item) => (
              <div className={`audit-detail-note audit-detail-note--${item.tone}`} key={item.label}>
                <span>{item.label}</span><strong>{item.value}</strong>
              </div>
            ))}
          </div>
        )}
        {changes.length === 0 && !hasCollections && notes.length === 0 && (
          <div className="audit-empty-change">{summary === '-' ? '本次操作没有可展示的变化' : summary}</div>
        )}
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

export function AuditDetailDrawer({
  title, row, presenter, onClose,
}: {
  title: string
  row: AuditLogRow | null
  presenter: AuditPresenter
  onClose: () => void
}) {
  return (
    <Drawer className="audit-detail-drawer" width="min(720px, 100vw)" title={title} visible={!!row} onCancel={onClose} footer={null}>
      {row && <AuditDetail key={row.id} row={row} presenter={presenter} />}
    </Drawer>
  )
}

/** 结果栏：总数与关键提示在左，刷新在右；分页器不再重复显示总数。 */
export function AuditResultBar({ total, hint, onRefresh }: { total: number; hint?: string; onRefresh: () => void }) {
  return (
    <div className="audit-result-bar">
      <Typography.Text type="secondary">
        {total >= 10_001 ? '10000+ 条记录，请缩小筛选范围查看完整结果' : `共 ${total} 条记录`}
        {hint ? `；${hint}` : ''}
      </Typography.Text>
      <Button size="small" icon={<IconRefresh />} onClick={onRefresh}>刷新</Button>
    </div>
  )
}

export function AuditLoadError({ onRetry }: { onRetry: () => void }) {
  return (
    <div className="audit-load-error">
      <Typography.Text type="error">加载失败</Typography.Text>
      <Button size="small" onClick={onRetry}>重试</Button>
    </div>
  )
}

function TwoLineCell({ primary, secondary, className }: { primary: ReactNode; secondary: string; className: string }) {
  return (
    <div className={`audit-cell ${className}`}>
      {primary}
      <small title={secondary}>{secondary}</small>
    </div>
  )
}

const AUDIT_TABLE_SCROLL_X = 1180

/** 两个日志页的表格列完全一致；列宽按内容的典型长度分配，内容摘要吸收剩余宽度。 */
function auditColumns(presenter: AuditPresenter, onView: (row: AuditLogRow) => void) {
  return [
    { title: '时间', dataIndex: 'createdAt', width: 170, render: fmtTime },
    {
      title: '操作人', key: 'actor', width: 190,
      render: (_: unknown, row?: AuditLogRow) => {
        if (!row) return '-'
        const identity = presenter.actor(row)
        return <TwoLineCell className="audit-actor" primary={<b title={identity.name}>{identity.name}</b>} secondary={identity.identifier} />
      },
    },
    {
      title: '操作类型', key: 'action', width: 220,
      render: (_: unknown, row?: AuditLogRow) => {
        if (!row) return '-'
        const meta = presenter.meta(row)
        return <TwoLineCell className="audit-action" primary={<Tag color={meta.color}>{meta.label}</Tag>} secondary={`${meta.categoryLabel} · ${row.action}`} />
      },
    },
    {
      title: '内容摘要', key: 'summary', width: 300,
      render: (_: unknown, row?: AuditLogRow) => {
        const summary = row ? presenter.summary(row) : '-'
        return summary === '-'
          ? <Typography.Text type="secondary">-</Typography.Text>
          : <Typography.Text className="audit-summary" ellipsis={{ showTooltip: true }}>{summary}</Typography.Text>
      },
    },
    {
      title: '对象', key: 'target', width: 200,
      render: (_: unknown, row?: AuditLogRow) => {
        if (!row) return '-'
        const identity = presenter.target(row)
        return <TwoLineCell className="audit-target" primary={<span title={identity.name}>{identity.name}</span>} secondary={identity.identifier} />
      },
    },
    {
      title: '操作', key: 'operations', width: 100, fixed: 'right' as const,
      render: (_: unknown, row?: AuditLogRow) => row ? actionSlots([
        <Button key="view" size="mini" type="text" icon={<IconEye />} onClick={() => onView(row)}>查看</Button>,
      ], 'single') : null,
    },
  ]
}

export function AuditTable({
  presenter, rows, loading, total, page, pageSize, onPageChange, onView,
}: {
  presenter: AuditPresenter
  rows: AuditLogRow[]
  loading: boolean
  total: number
  page: number
  pageSize: number
  onPageChange: (page: number, pageSize: number) => void
  onView: (row: AuditLogRow) => void
}) {
  return (
    <Table
      className="page-table audit-table"
      rowKey="id"
      loading={loading}
      data={rows}
      scroll={{ x: AUDIT_TABLE_SCROLL_X, y: 'var(--page-table-scroll-y)' }}
      columns={auditColumns(presenter, onView)}
      pagination={{ total, current: page, pageSize, sizeCanChange: true, onChange: onPageChange }}
    />
  )
}
