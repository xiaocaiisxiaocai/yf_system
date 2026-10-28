import { useMemo, useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Alert, Button, Card, Space, Table, Tag } from '@arco-design/web-react'
import { Link, useNavigate } from 'react-router-dom'
import { fmtTime } from '../../api/types'
import { ColumnResizeProvider, ColumnVisibilityPicker } from '../../components/columnResize'
import { type ColumnLayoutSpec } from '../../components/tableColumnLayout'
import { useAdjustableColumns } from '../../components/useAdjustableColumns'
import { oemRequestError } from '../api/requestError'
import type { PendingTask } from '../api/types'
import { useOem } from '../OemContext'
import { useHubSwitcher } from './hubSwitcherContext'

const OEM_APPROVAL_COLUMNS: readonly ColumnLayoutSpec[] = [
  { key: 'title', title: '传递单', defaultWidth: 220, minWidth: 140, locked: true },
  { key: 'companyName', title: '目标厂商', defaultWidth: 180, minWidth: 110 },
  { key: 'sender', title: '发送人', defaultWidth: 160, minWidth: 110 },
  { key: 'node', title: '审批节点', defaultWidth: 200, minWidth: 120 },
  { key: 'sentAt', title: '发送时间', defaultWidth: 170, minWidth: 140 },
  { key: 'activatedAt', title: '待审批起始', defaultWidth: 170, minWidth: 140 },
]

const PAGE_SIZE = 20

export default function ApprovalInboxPage() {
  const { api, base, queryScope, userId } = useOem()
  const switcher = useHubSwitcher()
  const navigate = useNavigate()
  const tableHostRef = useRef<HTMLDivElement>(null)
  const [page, setPage] = useState(1)
  const approvals = useQuery({
    queryKey: ['oem', ...queryScope, 'pending-approvals', page],
    queryFn: () => api.pendingApprovals({ page, pageSize: PAGE_SIZE }),
  })
  const rows: PendingTask[] = approvals.data?.list ?? []
  const approvalColumns = useMemo(() => [
    {
      key: 'title', title: '传递单', dataIndex: 'title',
      render: (value: string, row: PendingTask) => (
        <Link to={`${base}/transfers/${row.transferId}`} onClick={(event) => event.stopPropagation()}>{value}</Link>
      ),
    },
    { key: 'companyName', title: '目标厂商', dataIndex: 'companyName' },
    { key: 'sender', title: '发送人', render: (_: unknown, row: PendingTask) => `${row.senderName}（${row.senderEmployeeNo}）` },
    {
      key: 'node', title: '审批节点',
      render: (_: unknown, row: PendingTask) => <>{row.nodeName} {row.approvalMode === 'ALL' && <Tag size="small">会签</Tag>}</>,
    },
    { key: 'sentAt', title: '发送时间', render: (_: unknown, row: PendingTask) => fmtTime(row.sentAt) },
    { key: 'activatedAt', title: '待审批起始', render: (_: unknown, row: PendingTask) => fmtTime(row.activatedAt) },
  ], [base])
  const table = useAdjustableColumns('oem-approvals', userId, OEM_APPROVAL_COLUMNS, approvalColumns, 'title', tableHostRef)

  return (
    <Card className="page-card page-card--table" title={<h1 className="visually-hidden">待我审批</h1>}>
      <div className="page-toolbar responsive-toolbar">
        <Space wrap>
          {switcher}
          <ColumnVisibilityPicker
            specs={OEM_APPROVAL_COLUMNS}
            hidden={table.hidden}
            onToggle={table.toggle}
            onShowAll={table.showAll}
            onHideOptional={table.hideOptional}
            onReset={table.reset}
          />
        </Space>
      </div>
      {approvals.isError && (
        <Alert type="error" style={{ marginBottom: 12 }}
          content={oemRequestError(approvals.error, '待审批列表加载失败，请重试')}
          action={<Button size="small" loading={approvals.isFetching} onClick={() => void approvals.refetch()}>重试</Button>} />
      )}
      <ColumnResizeProvider actions={table.actions}>
        <div ref={tableHostRef} className={`${table.hostClassName} page-table-host`}>
          <Table
            className={`page-table ${table.tableClassName}`}
            rowKey="taskId"
            loading={approvals.isPending || approvals.isFetching}
            data={rows}
            columns={table.columns}
            components={table.components}
            scroll={{ x: table.scrollX, y: 'var(--page-table-scroll-y)' }}
            pagination={{ current: page, total: approvals.data?.total ?? 0, pageSize: PAGE_SIZE, onChange: setPage }}
            onRow={(row) => ({ onClick: () => navigate(`${base}/transfers/${row.transferId}`), style: { cursor: 'pointer' } })}
          />
        </div>
      </ColumnResizeProvider>
    </Card>
  )
}
