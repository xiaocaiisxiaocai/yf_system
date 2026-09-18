import { useEffect, useState } from 'react'
import { Card, Table, Tag } from '@arco-design/web-react'
import { useNavigate } from 'react-router-dom'
import { fmtTime } from '../../api/types'
import type { PendingTask } from '../api/types'
import { useOem } from '../OemContext'

export default function ApprovalInboxPage() {
  const { api, base } = useOem()
  const navigate = useNavigate()
  const [rows, setRows] = useState<PendingTask[]>([])
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    void api.pendingApprovals().then(setRows).finally(() => setLoading(false))
  }, [api])

  return (
    <Card title="待我审批">
      <Table rowKey="taskId" loading={loading} data={rows} pagination={false}
        onRow={(row) => ({ onClick: () => navigate(`${base}/transfers/${row.transferId}`), style: { cursor: 'pointer' } })}
        columns={[
          { title: '传递单', dataIndex: 'title' },
          { title: '目标厂商', dataIndex: 'companyName', width: 180 },
          { title: '发送人', width: 160, render: (_: unknown, row: PendingTask) => `${row.senderName}（${row.senderEmployeeNo}）` },
          {
            title: '审批节点', width: 200,
            render: (_: unknown, row: PendingTask) => <>{row.nodeName} {row.approvalMode === 'ALL' && <Tag size="small">会签</Tag>}</>,
          },
          { title: '发送时间', width: 170, render: (_: unknown, row: PendingTask) => fmtTime(row.sentAt) },
        ]}
      />
    </Card>
  )
}
