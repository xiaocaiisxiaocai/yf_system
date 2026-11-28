import { useRef, useState } from 'react'
import { Button, Card, Empty, Input, Message, Modal, Select, Space, Spin, Tag, Typography } from '@arco-design/web-react'
import { IconPlus, IconSearch } from '@arco-design/web-react/icon'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useSessionQueryScope } from '../api/queryClient'
import { fmtTime } from '../api/types'
import LoadError from '../components/LoadError'
import ResizableTable from '../components/ResizableTable'
import { actionSlots } from '../components/ActionSlots'
import { DEFAULT_PAGE_SIZE } from '../components/tablePagination'
import { useAuth } from '../store/auth'
import { ANNOUNCEMENT_PERMISSIONS } from './announcementPermissions'
import AnnouncementEditor from './AnnouncementEditor'
import { internalAudience } from './managementLabels'
import AnnouncementReadDetails from './AnnouncementReadDetails'
import { managementApi, type ManageDetail, type ManageRow } from './managementApi'
import './announcementManagement.css'

const states: Record<string, string> = { DRAFT: '草稿', PUBLISHED: '已发布', WITHDRAWN: '已下架' }
type DetailPanel = { mode: 'new' } | { mode: 'preview' | 'edit' | 'copy'; row: ManageRow }
type Panel = DetailPanel | { mode: 'reads' | 'withdraw'; row: ManageRow }

function ManagementPreview({ detail, onClose }: { detail: ManageDetail; onClose: () => void }) {
  const external = detail.externalScope === 'ALL' ? '全部 ODM/OEMA 公司'
    : detail.externalScope === 'SELECTED' ? detail.companies.map(item => item.name).join('、') || '未选公司' : '未选择'
  return <Modal className="announcement-dialog" visible title="管理预览" footer={null} onCancel={onClose} style={{ width: 'min(720px, 96vw)' }}>
    <h3 className="announcement-title">{detail.title || '未命名草稿'}</h3>
    <dl className="announcement-metadata">
      <dt>状态</dt><dd>{states[detail.status]}{detail.pinnedAt ? ' · 已置顶' : ''}</dd>
      <dt>发布部门</dt><dd>{detail.departmentName || '未确定'}</dd>
      {detail.publishedAt && <><dt>发布人</dt><dd>{detail.publisherName}</dd><dt>发布时间</dt><dd>{fmtTime(detail.publishedAt)}</dd></>}
      <dt>内部可见</dt><dd>{internalAudience(detail.internalScope, detail.departmentName, detail.departments.map(item => item.name))}</dd>
      <dt>外部可见</dt><dd>{external}</dd>
      {detail.withdrawnAt && <><dt>下架人</dt><dd>{detail.withdrawnByName}</dd><dt>下架时间</dt><dd>{fmtTime(detail.withdrawnAt)}</dd></>}
    </dl>
    <article className="announcement-body">{detail.body}</article>
  </Modal>
}

function DetailDialog({ panel, onClose, onSaved }: { panel: DetailPanel; onClose: () => void; onSaved: () => void }) {
  const scope = useSessionQueryScope()
  const id = 'row' in panel ? panel.row.id : null
  const detail = useQuery({
    queryKey: ['announcement-management-detail', ...scope, id],
    queryFn: ({ signal }) => managementApi.detail(id!, signal), enabled: id !== null, gcTime: 0,
  })
  const editor = panel.mode !== 'preview'
  const options = useQuery({
    queryKey: ['announcement-options', ...scope], queryFn: ({ signal }) => managementApi.options(signal),
    enabled: editor, gcTime: 0,
  })
  if ((id !== null && detail.isError) || (editor && options.isError)) {
    return <Modal title="公告" visible footer={null} onCancel={onClose}>
      <LoadError message="公告资料加载失败" onRetry={() => {
        if (id !== null) void detail.refetch()
        if (editor) void options.refetch()
      }} /></Modal>
  }
  if (panel.mode === 'preview' && detail.data) return <ManagementPreview detail={detail.data} onClose={onClose} />
  if (editor && options.data && (id === null || detail.data)) {
    return <AnnouncementEditor source={detail.data} copy={panel.mode === 'copy'} options={options.data} onClose={onClose} onSaved={onSaved} />
  }
  return <Modal visible title="公告" footer={null} onCancel={onClose}><Spin /></Modal>
}

function ManagementContent() {
  const scope = useSessionQueryScope()
  const grants = useAuth(state => state.permissions)
  const publish = grants.includes(ANNOUNCEMENT_PERMISSIONS.publish)
  const pin = grants.includes(ANNOUNCEMENT_PERMISSIONS.pin)
  const withdraw = grants.includes(ANNOUNCEMENT_PERMISSIONS.withdraw)
  const reads = grants.includes(ANNOUNCEMENT_PERMISSIONS.reads)
  const [searchInput, setSearchInput] = useState('')
  const [keyword, setKeyword] = useState('')
  const [status, setStatus] = useState<string>()
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(DEFAULT_PAGE_SIZE)
  const [panel, setPanel] = useState<Panel | null>(null)
  const [busy, setBusy] = useState(false)
  const writing = useRef(false)
  const client = useQueryClient()
  const list = useQuery({
    queryKey: ['announcement-management-list', ...scope, keyword, status, page, pageSize],
    queryFn: async ({ signal }) => {
      try {
        return await managementApi.list({ keyword, status, page, pageSize }, signal)
      } catch (error) {
        const failure = error as { code?: string; response?: { status?: number } } | null
        const status = failure?.response?.status
        if (failure?.code === 'ERR_NETWORK' || failure?.code === 'ECONNABORTED' || failure?.code === 'ETIMEDOUT'
          || status === 408 || status === 429 || (status !== undefined && status >= 500)) throw error
        // Replace the cached rows on a definitive refusal so a later offline retry cannot restore them.
        return null
      }
    },
    refetchOnWindowFocus: true, gcTime: 0,
  })
  const lastPage = list.data ? Math.max(1, Math.ceil(list.data.total / pageSize)) : page
  if (page > lastPage) setPage(lastPage)
  const refresh = () => {
    void client.invalidateQueries({ queryKey: ['announcement-management-list'] })
    void client.invalidateQueries({ queryKey: ['announcement-inbox'] })
  }
  const finish = () => { setPanel(null); refresh() }
  const changeState = async (row: ManageRow, action: 'withdraw' | 'pin' | 'unpin') => {
    if (writing.current) return
    writing.current = true
    setBusy(true)
    try {
      await managementApi[action](row.id)
      Message.success({ withdraw: '公告已下架', pin: '公告已置顶', unpin: '已取消置顶' }[action])
      setPanel(null)
    } catch {
      // The HTTP client shows the service rejection. Refresh actions in case the permissions or state changed.
    } finally {
      writing.current = false
      setBusy(false)
      refresh()
    }
  }
  const search = () => { setKeyword(searchInput.trim()); setPage(1) }
  if (list.data === null || (list.isError && !list.data)) {
    if (panel) setPanel(null)
    return <Card className="page-card page-card--table announcement-management">
      <div className="page-heading"><h1>公告管理</h1></div>
      <LoadError message="公告管理列表加载失败" onRetry={() => void list.refetch()} />
    </Card>
  }
  return <Card className="page-card page-card--table announcement-management">
    <div className="page-heading"><h1>公告管理</h1></div>
    <div className="page-toolbar responsive-toolbar"><Space wrap>
      <Input aria-label="搜索公告标题" allowClear prefix={<IconSearch />} value={searchInput} placeholder="搜索公告标题"
        onChange={value => { setSearchInput(value); if (!value) { setKeyword(''); setPage(1) } }} onPressEnter={search} />
      <Select aria-label="公告状态" placeholder="全部状态" allowClear style={{ width: 130 }}
        value={status} onChange={value => { setStatus(value || undefined); setPage(1) }}
        options={Object.entries(states).map(([value, label]) => ({ value, label }))} />
      <Button onClick={search}>查询</Button>
    </Space>{publish && <Button type="primary" icon={<IconPlus />} onClick={() => setPanel({ mode: 'new' })}>新建公告</Button>}</div>
    {list.isRefetchError && <div role="status"><Space>
      <Typography.Text type="warning">刷新失败，当前显示上次加载的公告。</Typography.Text>
      <Button size="mini" onClick={() => void list.refetch()}>重试</Button>
    </Space></div>}
    <ResizableTable<ManageRow> tableId="announcement-management" className="page-table" growKey="title" rowKey="id"
          loading={list.isLoading} data={list.data?.list ?? []} noDataElement={<Empty description="暂无公告" />}
          scroll={{ x: 1000, y: 'var(--page-table-scroll-y)' }}
          columns={[
            { title: '标题', key: 'title', width: 230, render: (_, row) =>
              <button type="button" className="table-link announcement-title-link" onClick={() => setPanel({ mode: 'preview', row })}>{row.title || '未命名草稿'}</button> },
            { title: '发布部门', dataIndex: 'departmentName', width: 120 },
            { title: '发布人', dataIndex: 'publisherName', width: 100, render: name => name || '—' },
            { title: '状态', dataIndex: 'status', width: 90, render: value => states[value] ?? value },
            { title: '发布时间', dataIndex: 'publishedAt', width: 170, render: fmtTime },
            { title: '置顶状态', dataIndex: 'pinnedAt', width: 95, render: at => at ? <Tag color="orange">已置顶</Tag> : '—' },
            { title: '操作', key: 'actions', width: 75 * [publish, pin, withdraw, reads].filter(Boolean).length + 24, fixed: 'right',
              render: (_, row) => actionSlots([
                publish && <Button key="edit" size="mini" type="text" disabled={busy || (row.status === 'DRAFT' && !row.canEdit)}
                  onClick={() => setPanel({ mode: row.status === 'DRAFT' ? 'edit' : 'copy', row })}>
                  {row.status === 'DRAFT' ? '编辑' : '复制为草稿'}</Button>,
                pin && <Button key="pin" size="mini" type="text" disabled={busy || !row.canPin}
                  onClick={() => void changeState(row, row.pinnedAt ? 'unpin' : 'pin')}>{row.pinnedAt ? '取消置顶' : '置顶'}</Button>,
                withdraw && <Button key="withdraw" size="mini" type="text" status="danger" disabled={busy || !row.canWithdraw}
                  onClick={() => setPanel({ mode: 'withdraw', row })}>下架</Button>,
                reads && <Button key="reads" size="mini" type="text" disabled={busy || !row.canReadDetails}
                  onClick={() => setPanel({ mode: 'reads', row })}>阅读明细</Button>,
              ], 'account') },
          ]}
          pagination={{ current: page, pageSize, total: list.data?.total ?? 0,
            onChange: (next, size) => { setPage(size === pageSize ? next : 1); setPageSize(size) } }} />
    {panel?.mode === 'withdraw' && <Modal visible title="确认下架公告？" okText="确认下架" cancelText="取消"
        okButtonProps={{ status: 'danger' }} confirmLoading={busy} maskClosable={false} closable={!busy}
        onCancel={() => { if (!writing.current) setPanel(null) }} onOk={() => changeState(panel.row, 'withdraw')}>
      下架《{panel.row.title}》后，所有读者将无法再查看此公告，已有阅读记录将保留。
    </Modal>}
    {panel?.mode === 'reads' && <AnnouncementReadDetails row={panel.row} onClose={() => setPanel(null)} />}
    {panel && (panel.mode === 'preview' || panel.mode === 'edit' || panel.mode === 'copy' || panel.mode === 'new') &&
      <DetailDialog panel={panel} onClose={() => setPanel(null)} onSaved={finish} />}
  </Card>
}

export default function AnnouncementManagementPage() {
  const scope = useSessionQueryScope()
  return <ManagementContent key={JSON.stringify(scope)} />
}
