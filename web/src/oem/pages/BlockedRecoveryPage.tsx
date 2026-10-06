import { useState } from 'react'
import { Alert, Button, Card, Space, Table, Typography } from '@arco-design/web-react'
import { useQuery } from '@tanstack/react-query'
import { useNavigate } from 'react-router-dom'
import { fmtTime } from '../../api/types'
import type { TransferSummary } from '../api/types'
import { ApprovalTag, LifecycleTag } from '../components/StatusTags'
import { useOem } from '../OemContext'

/**
 * Minimal recovery inbox. The server independently restricts this query to
 * unpublished APPROVAL_BLOCKED transfers for approval-recovery actors.
 */
export default function BlockedRecoveryPage() {
  const { api, base, queryScope } = useOem()
  const navigate = useNavigate()
  const [page, setPage] = useState(1)
  const blocked = useQuery({
    queryKey: ['oem', ...queryScope, 'approval-recovery', page],
    queryFn: () => api.listTransfers({ page, pageSize: 20, approvalStatus: 'APPROVAL_BLOCKED' }),
  })
  const rows: TransferSummary[] = blocked.data?.list ?? []

  return (
    <Card title="审批阻断处置">
      <Alert
        type="info"
        content="这里只显示尚未发布且审批已阻断的传递单。处置管理员可以改派未完成任务或终止传递，不能代替审批人通过，也不能读取附件内容。"
        style={{ marginBottom: 16 }}
      />
      <Table
        rowKey="id"
        loading={blocked.isPending || blocked.isFetching}
        data={rows}
        pagination={{ current: page, total: blocked.data?.total ?? 0, pageSize: 20, onChange: setPage }}
        columns={[
          { title: '传递单', dataIndex: 'title', render: (value: string) => <Typography.Text bold>{value}</Typography.Text> },
          { title: '目标厂商', dataIndex: 'companyName', width: 180 },
          { title: '发送人', width: 160, render: (_: unknown, row: TransferSummary) => `${row.sender.realName}（${row.sender.employeeNo}）` },
          {
            title: '状态', width: 180,
            render: (_: unknown, row: TransferSummary) => <Space size={4}><LifecycleTag value={row.lifecycleStatus} /><ApprovalTag value={row.approvalStatus} /></Space>,
          },
          { title: '阻断原因', dataIndex: 'approvalBlockedReason', ellipsis: true },
          { title: '发送时间', width: 170, render: (_: unknown, row: TransferSummary) => row.sentAt ? fmtTime(row.sentAt) : '-' },
          {
            title: '操作', width: 90,
            render: (_: unknown, row: TransferSummary) => <Button size="mini" onClick={() => navigate(`${base}/transfers/${row.id}`)}>处置</Button>,
          },
        ]}
      />
    </Card>
  )
}
