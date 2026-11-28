import { useState } from 'react'
import { Empty, Modal } from '@arco-design/web-react'
import { useQuery } from '@tanstack/react-query'
import { fmtTime } from '../api/types'
import { useSessionQueryScope } from '../api/queryClient'
import LoadError from '../components/LoadError'
import ResizableTable from '../components/ResizableTable'
import { DEFAULT_PAGE_SIZE } from '../components/tablePagination'
import { managementApi, type ManageRow, type ReadPage } from './managementApi'

export default function AnnouncementReadDetails({ row, onClose }: { row: ManageRow; onClose: () => void }) {
  const scope = useSessionQueryScope()
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(DEFAULT_PAGE_SIZE)
  const reads = useQuery({
    queryKey: ['announcement-reads', ...scope, row.id, page, pageSize],
    queryFn: ({ signal }) => managementApi.reads(row.id, page, pageSize, signal),
    gcTime: 0,
  })
  return <Modal className="announcement-dialog" visible title={'阅读明细 · ' + row.title} onCancel={onClose} footer={null}
    style={{ width: 'min(840px, 96vw)' }}>
    {reads.isError ? <LoadError message="阅读明细加载失败" onRetry={() => void reads.refetch()} />
      : <ResizableTable<ReadPage['list'][number]> tableId="announcement-read-details" loading={reads.isFetching}
        rowKey={item => item.realm + ':' + item.accountId} data={reads.data?.list ?? []}
        noDataElement={<Empty description="暂无账号打开过此公告" />}
        scroll={{ x: 720 }} columns={[
          { title: '账号类型', dataIndex: 'realm', width: 120, render: (realm) => realm === 'internal' ? '内部账号' : 'ODM/OEMA' },
          { title: '登录账号', dataIndex: 'accountName', width: 130 },
          { title: '显示姓名', dataIndex: 'displayName', width: 120 },
          { title: '首次打开时间', dataIndex: 'firstReadAt', width: 175, render: fmtTime },
          { title: '最近打开时间', dataIndex: 'lastReadAt', width: 175, render: fmtTime },
        ]} pagination={{ current: page, pageSize, total: reads.data?.total ?? 0,
          onChange: (next, size) => { setPage(size === pageSize ? next : 1); setPageSize(size) } }} />}
  </Modal>
}
