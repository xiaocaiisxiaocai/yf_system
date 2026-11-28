import { useState } from 'react'
import { Button, Card, Checkbox, Empty, Input, Modal, Space, Tag } from '@arco-design/web-react'
import { IconSearch } from '@arco-design/web-react/icon'
import { useQuery } from '@tanstack/react-query'
import { Link, useLocation, useSearchParams } from 'react-router-dom'
import { fmtTime } from '../api/types'
import LoadError from '../components/LoadError'
import ResizableTable from '../components/ResizableTable'
import { DEFAULT_PAGE_SIZE, PAGE_SIZE_OPTIONS } from '../components/tablePagination'
import { useOem } from '../oem/OemContext'
import { listAnnouncements, type AnnouncementInbox } from './announcementReaderApi'
import { OpenAnnouncement } from './AnnouncementDetail'
import AnnouncementAssetImage from './AnnouncementAssetImage'
import './announcementCenter.css'

export default function AnnouncementCenter() {
  const { api, realm, base, queryScope } = useOem()
  const location = useLocation()
  const [search, setSearch] = useSearchParams()
  const keyword = search.get('keyword') ?? ''
  const unreadOnly = search.get('unreadOnly') === 'true'
  const pageParam = Number(search.get('page'))
  const page = Number.isSafeInteger(pageParam) && pageParam > 0 ? pageParam : 1
  const sizeParam = Number(search.get('pageSize'))
  const pageSize = PAGE_SIZE_OPTIONS.includes(sizeParam) ? sizeParam : DEFAULT_PAGE_SIZE
  const [input, setInput] = useState(keyword)
  const [openId, setOpenId] = useState<number | null>(null)
  const inbox = useQuery({
    queryKey: ['announcement-inbox', ...queryScope, keyword, unreadOnly, page, pageSize],
    queryFn: ({ signal }) => listAnnouncements(api.http, realm, { keyword, unreadOnly, page, pageSize }, signal),
    gcTime: 0, refetchOnWindowFocus: true,
  })
  function update(values: Record<string, string>) {
    const next = new URLSearchParams(search)
    for (const [key, value] of Object.entries(values)) {
      if (value) next.set(key, value)
      else next.delete(key)
    }
    setSearch(next, { replace: true })
  }
  const searchTitle = () => update({ keyword: input.trim(), page: '' })
  const data = inbox.data
  return <Card className="page-card page-card--table">
    <div className="page-heading"><h1>公告中心</h1></div>
    <div className="page-toolbar responsive-toolbar">
      <Space wrap>
        <form onSubmit={(event) => { event.preventDefault(); searchTitle() }}>
          <Space>
            <Input allowClear prefix={<IconSearch />} aria-label="搜索公告标题" placeholder="搜索公告标题"
              value={input} onChange={(value) => {
                setInput(value)
                if (!value && keyword) update({ keyword: '', page: '' })
              }} />
            <Button htmlType="submit">查询</Button>
          </Space>
        </form>
        <Checkbox checked={unreadOnly} onChange={(checked) => update({ unreadOnly: checked ? 'true' : '', page: '' })}>
          只看未读
        </Checkbox>
      </Space>
      {data && !inbox.isError && <span aria-live="polite">未读 {data.unreadCount} 条</span>}
    </div>
    {inbox.isError ? <LoadError message="公告列表加载失败" onRetry={() => void inbox.refetch()} />
      : <ResizableTable<AnnouncementInbox['list'][number]> tableId={`announcements-${realm}`} growKey="preview"
          rowKey="id" className="page-table" data={data?.list ?? []} loading={inbox.isLoading}
          noDataElement={inbox.isLoading ? <span /> : <Empty description={unreadOnly ? '暂无未读公告' : '暂无公告'} />}
          scroll={{ x: 900, y: 'var(--page-table-scroll-y)' }}
          columns={[
            { title: '标题', key: 'title', width: 200, render: (_, row) => <Space>
              {row.pinnedAt && <Tag color="orange">置顶</Tag>}
              <Link className="table-link" to={`${base}/announcements/${row.id}${location.search}`}>{row.title}</Link>
            </Space> },
            { title: '内容预览', dataIndex: 'preview', width: 340, render: (_, row) =>
              <div className="announcement-excerpt">
                {row.firstImageId != null && <AnnouncementAssetImage key={JSON.stringify([queryScope, row.id, row.firstImageId])}
                  thumbnail announcementId={row.id} assetId={row.firstImageId} access={realm} client={api.http} alt="公告首图" />}
                <div className="announcement-excerpt-text">
                  <span>{row.preview || row.firstAttachmentName}</span>
                  <div className="announcement-excerpt-actions">
                    {row.imageCount > 0 && <small>共 {row.imageCount} 张图片</small>}
                    {row.attachmentCount > 0 && <small>共 {row.attachmentCount} 个附件</small>}
                    <Button type="text" size="mini" onClick={() => setOpenId(row.id)}>查看全文</Button>
                  </div>
                </div>
              </div> },
            { title: '发布部门', dataIndex: 'departmentName', width: 150 },
            { title: '发布时间', dataIndex: 'publishedAt', width: 170, render: fmtTime },
            { title: '阅读状态', key: 'isRead', width: 96, render: (_, row) =>
              <Tag color={row.isRead ? undefined : 'arcoblue'}>{row.isRead ? '已读' : '未读'}</Tag> },
          ]}
          pagination={{ current: page, pageSize, total: data?.total ?? 0,
            onChange: (next, size) => update({ page: String(size === pageSize ? next : 1), pageSize: String(size) }),
          }} />}
    {openId !== null && <Modal visible title="公告全文" footer={null} onCancel={() => setOpenId(null)}
      className="announcement-full-text" style={{ width: 'min(720px, 94vw)' }}>
      <OpenAnnouncement key={JSON.stringify([queryScope, openId])} id={String(openId)} />
    </Modal>}
  </Card>
}
