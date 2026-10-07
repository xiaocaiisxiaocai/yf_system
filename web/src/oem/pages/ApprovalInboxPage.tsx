import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Card, Table, Tag } from '@arco-design/web-react'
import { Link, useNavigate } from 'react-router-dom'
import { fmtTime } from '../../api/types'
import type { PendingTask } from '../api/types'
import { useOem } from '../OemContext'

const PAGE_SIZE = 20

export default function ApprovalInboxPage() {
  const { api, base, queryScope } = useOem()
  const navigate = useNavigate()
  const [page, setPage] = useState(1)
  const approvals = useQuery({
    queryKey: ['oem', ...queryScope, 'pending-approvals', page],
    queryFn: () => api.pendingApprovals({ page, pageSize: PAGE_SIZE }),
  })
  const rows: PendingTask[] = approvals.data?.list ?? []

  return (
    <Card title="待我审批">
      <Table rowKey="taskId" loading={approvals.isPending || approvals.isFetching} data={rows}
        pagination={{ current: page, total: approvals.data?.total ?? 0, pageSize: PAGE_SIZE, onChange: setPage }}
        onRow={(row) => ({ onClick: () => navigate(`${base}/transfers/${row.transferId}`), style: { cursor: 'pointer' } })}
        columns={[
          {
            title: '传递单', dataIndex: 'title',
            // A real link keeps rows reachable by keyboard; the row click remains a mouse shortcut.
            render: (value: string, row: PendingTask) => (
              <Link to={`${base}/transfers/${row.transferId}`} onClick={(event) => event.stopPropagation()}>{value}</Link>
            ),
          },
          { title: '目标厂商', dataIndex: 'companyName', width: 180 },
          { title: '发送人', width: 160, render: (_: unknown, row: PendingTask) => `${row.senderName}（${row.senderEmployeeNo}）` },
          {
            title: '审批节点', width: 200,
            render: (_: unknown, row: PendingTask) => <>{row.nodeName} {row.approvalMode === 'ALL' && <Tag size="small">会签</Tag>}</>,
          },
          { title: '发送时间', width: 170, render: (_: unknown, row: PendingTask) => fmtTime(row.sentAt) },
          { title: '待审批起始', width: 170, render: (_: unknown, row: PendingTask) => fmtTime(row.activatedAt) },
        ]}
      />
    </Card>
  )
}
