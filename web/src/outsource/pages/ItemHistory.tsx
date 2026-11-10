import { useEffect, useState } from 'react'
import { Drawer, Empty, Popover, Spin, Table, Tag } from '@arco-design/web-react'
import type { TableColumnProps } from '@arco-design/web-react'
import http from '../../api/client'
import { fmtTime, type ApiResponses } from '../../api/types'

type Change = ApiResponses['GET /outsource/items/{id}/changes'][number]

const SOURCE: Record<string, { label: string; color: string }> = {
  EDIT: { label: '编辑', color: 'arcoblue' },
  IPMS_SCHEDULED: { label: '定时同步', color: 'green' },
  IPMS_MANUAL: { label: '手动同步', color: 'cyan' },
}

function useChanges(itemId: number | null) {
  const [changes, setChanges] = useState<Change[] | null>(null)
  const [error, setError] = useState(false)
  useEffect(() => {
    if (itemId === null) return
    let active = true
    void Promise.resolve().then(async () => {
      if (!active) return
      setChanges(null); setError(false)
      try {
        const response = await http.get<ApiResponses['GET /outsource/items/{id}/changes']>(`/outsource/items/${itemId}/changes`)
        if (active) setChanges(response.data)
      } catch {
        if (active) setError(true)
      }
    })
    return () => { active = false }
  }, [itemId])
  return { changes, error }
}

/** Every recorded change of one item: IPMS syncs and purchasing edits, newest first. */
export function ItemHistoryDrawer({ item, onClose }: { item: { id: number; workOrderNo: string } | null; onClose: () => void }) {
  const { changes, error } = useChanges(item?.id ?? null)
  const columns: TableColumnProps<Change>[] = [
    { title: '时间', width: 160, render: (_, change) => fmtTime(change.changedAt) },
    { title: '字段', dataIndex: 'label', width: 100 },
    { title: '原值', dataIndex: 'oldValue', width: 130, ellipsis: true, render: (value: string | null) => value ?? '—' },
    { title: '新值', dataIndex: 'newValue', width: 130, ellipsis: true, render: (value: string | null) => value ?? '—' },
    {
      title: '来源', width: 120,
      render: (_, change) => <Tag color={SOURCE[change.source]?.color}>{SOURCE[change.source]?.label ?? change.source}</Tag>,
    },
    { title: '操作人', dataIndex: 'actorName', width: 120, ellipsis: true, render: (value: string | null) => value ?? '—' },
  ]
  return <Drawer width={820} title={item ? `工令 ${item.workOrderNo} · 变更履历` : '变更履历'} visible={item !== null}
    footer={null} onCancel={onClose} unmountOnExit>
    {error ? <Empty description="履历加载失败" /> : <Table<Change> rowKey="id" loading={changes === null} data={changes ?? []}
      columns={columns} pagination={false} scroll={{ x: 760 }} noDataElement={<Empty description="暂无变更" />} />}
  </Drawer>
}

function DueHistory({ itemId, originalDueDate }: { itemId: number; originalDueDate?: string | null }) {
  const { changes, error } = useChanges(itemId)
  if (error) return <span>履历加载失败</span>
  if (changes === null) return <Spin size={16} />
  const due = changes.filter((change) => change.field === 'latestDueDate').reverse()
  return <ol className="outsource-due-history">
    <li><span>{originalDueDate ?? '—'}</span><span className="outsource-due-history-note">原始交期</span></li>
    {due.map((change) => <li key={change.id}>
      <span>{change.newValue ?? '—'}</span>
      <span className="outsource-due-history-note">{fmtTime(change.changedAt).slice(5, 16)} {SOURCE[change.source]?.label ?? change.source}</span>
    </li>)}
  </ol>
}

/** The latest due date; when it differs from the original, it is highlighted and shows its history on hover. */
export function LatestDueCell({ item }: { item: { id: number; originalDueDate?: string | null; latestDueDate?: string | null } }) {
  const changed = (item.originalDueDate ?? null) !== (item.latestDueDate ?? null)
  if (!changed) return <span>{item.latestDueDate ?? ''}</span>
  return <Popover title="交期变更" trigger="hover" content={<DueHistory itemId={item.id} originalDueDate={item.originalDueDate} />}>
    <span className="outsource-due-changed" tabIndex={0}>{item.latestDueDate ?? '—'}</span>
  </Popover>
}
