import { useMemo, useRef, useState } from 'react'
import { Alert, Button, Card, Space, Table, Typography } from '@arco-design/web-react'
import { useQuery } from '@tanstack/react-query'
import { useNavigate } from 'react-router-dom'
import { fmtTime } from '../../api/types'
import { ColumnResizeProvider, ColumnVisibilityPicker } from '../../components/columnResize'
import { type ColumnLayoutSpec } from '../../components/tableColumnLayout'
import { useAdjustableColumns } from '../../components/useAdjustableColumns'
import { oemRequestError } from '../api/requestError'
import type { TransferSummary } from '../api/types'
import { ApprovalTag, LifecycleTag } from '../components/StatusTags'
import { useOem } from '../OemContext'
import { useHubSwitcher } from './hubSwitcherContext'

const OEM_RECOVERY_COLUMNS: readonly ColumnLayoutSpec[] = [
  { key: 'title', title: '传递单', defaultWidth: 220, minWidth: 140, locked: true },
  { key: 'companyName', title: '目标厂商', defaultWidth: 180, minWidth: 110 },
  { key: 'sender', title: '发送人', defaultWidth: 160, minWidth: 110 },
  { key: 'status', title: '状态', defaultWidth: 180, minWidth: 120 },
  { key: 'reason', title: '异常原因', defaultWidth: 220, minWidth: 120 },
  { key: 'sentAt', title: '发送时间', defaultWidth: 170, minWidth: 140 },
  { key: 'actions', title: '操作', defaultWidth: 90, minWidth: 80, maxWidth: 160, locked: true },
]

/**
 * Minimal recovery inbox. The server independently restricts this query to
 * unpublished APPROVAL_BLOCKED transfers for approval-recovery actors.
 */
export default function BlockedRecoveryPage() {
  const { api, base, queryScope, userId } = useOem()
  const switcher = useHubSwitcher()
  const navigate = useNavigate()
  const tableHostRef = useRef<HTMLDivElement>(null)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const blocked = useQuery({
    queryKey: ['oem', ...queryScope, 'approval-recovery', page, pageSize],
    queryFn: () => api.listTransfers({ page, pageSize, approvalStatus: 'APPROVAL_BLOCKED' }),
  })
  const rows: TransferSummary[] = blocked.data?.list ?? []
  const recoveryColumns = useMemo(() => [
    { key: 'title', title: '传递单', dataIndex: 'title', render: (value: string) => <Typography.Text bold>{value}</Typography.Text> },
    { key: 'companyName', title: '目标厂商', dataIndex: 'companyName' },
    { key: 'sender', title: '发送人', render: (_: unknown, row: TransferSummary) => `${row.sender.realName}（${row.sender.employeeNo}）` },
    {
      key: 'status', title: '状态',
      render: (_: unknown, row: TransferSummary) => <Space size={4}><LifecycleTag value={row.lifecycleStatus} /><ApprovalTag value={row.approvalStatus} /></Space>,
    },
    { key: 'reason', title: '异常原因', dataIndex: 'approvalBlockedReason', ellipsis: true },
    { key: 'sentAt', title: '发送时间', render: (_: unknown, row: TransferSummary) => row.sentAt ? fmtTime(row.sentAt) : '-' },
    {
      key: 'actions', title: '操作',
      render: (_: unknown, row: TransferSummary) => <Button size="mini" type="text" onClick={() => navigate(`${base}/transfers/${row.id}`)}>处置</Button>,
    },
  ], [base, navigate])
  const table = useAdjustableColumns('oem-approval-recovery', userId, OEM_RECOVERY_COLUMNS, recoveryColumns, 'title', tableHostRef)

  return (
    <Card className="page-card page-card--table" title={<h1 className="visually-hidden">审批异常</h1>}>
      <div className="page-toolbar responsive-toolbar">
        <Space wrap>
          {switcher}
          <ColumnVisibilityPicker
            specs={OEM_RECOVERY_COLUMNS}
            hidden={table.hidden}
            onToggle={table.toggle}
            onShowAll={table.showAll}
            onHideOptional={table.hideOptional}
            onReset={table.reset}
          />
        </Space>
      </div>
      <Alert
        type="info"
        content="这里只显示尚未发布且审批出现异常的传递单。处置管理员可以改派未完成任务或终止传递，不能代替审批人通过，也不能读取附件内容。"
        style={{ marginBottom: 16 }}
      />
      {blocked.isError && (
        <Alert type="error" style={{ marginBottom: 12 }}
          content={oemRequestError(blocked.error, '审批异常列表加载失败，请重试')}
          action={<Button size="small" loading={blocked.isFetching} onClick={() => void blocked.refetch()}>重试</Button>} />
      )}
      <ColumnResizeProvider actions={table.actions}>
        <div ref={tableHostRef} className={`${table.hostClassName} page-table-host`}>
          <Table
            className={`page-table ${table.tableClassName}`}
            rowKey="id"
            loading={blocked.isPending || blocked.isFetching}
            data={rows}
            columns={table.columns}
            components={table.components}
            scroll={{ x: table.scrollX, y: 'var(--page-table-scroll-y)' }}
            pagination={{ current: page, total: blocked.data?.total ?? 0, pageSize, showTotal: true, sizeCanChange: true, onChange: (nextPage, nextSize) => { setPage(nextPage); setPageSize(nextSize) } }}
          />
        </div>
      </ColumnResizeProvider>
    </Card>
  )
}
