import { useRef, useState, type DragEvent } from 'react'
import { Button, Empty, InputNumber, Message, Modal, Radio, Space, Spin } from '@arco-design/web-react'
import { IconArrowDown, IconArrowUp, IconDragDotVertical } from '@arco-design/web-react/icon'
import { useQuery } from '@tanstack/react-query'
import { useSessionQueryScope } from '../api/queryClient'
import { requestErrorMessage } from '../api/requestError'
import LoadError from '../components/LoadError'
import ResizableTable from '../components/ResizableTable'
import { MAX_ORDER, moveOrderRow, previewOrder } from './announcementOrder'
import { announcementOrderApi, type OrderRow, type OrderSnapshot } from './announcementOrderApi'

type Draft = { baseline: OrderSnapshot; rows: OrderRow[] }

export default function AnnouncementOrderDialog({ onClose, onSaved }: { onClose: () => void; onSaved: () => void }) {
  const scope = useSessionQueryScope()
  const [pinned, setPinned] = useState(false)
  const [draft, setDraft] = useState<Draft | null>(null)
  const [preview, setPreview] = useState(false)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(10)
  const [busy, setBusy] = useState(false)
  const [conflict, setConflict] = useState(false)
  const [error, setError] = useState('')
  const dragging = useRef<number | null>(null)
  const saving = useRef(false)
  const query = useQuery({
    queryKey: ['announcement-ordering', ...scope, pinned],
    queryFn: ({ signal }) => announcementOrderApi.get(pinned, signal),
    gcTime: 0, refetchOnWindowFocus: false,
  })
  const baseline = draft?.baseline ?? query.data
  const rows = draft?.rows ?? query.data?.list ?? []
  const readonly = preview || busy || conflict
  const orderById = new Map(baseline?.list.map(row => [row.id, row.sortOrder]))
  const changes = rows.filter(row => row.sortOrder !== orderById.get(row.id))
    .map(({ id, sortOrder }) => ({ id, sortOrder }))
  function edit(next: OrderRow[]) {
    if (!baseline || readonly) return
    setError('')
    setDraft({ baseline, rows: next })
  }
  function move(source: number, target: number) {
    const next = moveOrderRow(rows, source, target)
    edit(next)
    setPage(Math.floor(next.findIndex(r => r.id === source) / pageSize) + 1)
  }
  async function save() {
    if (!baseline || !preview || conflict || saving.current || !changes.length) return
    setBusy(true)
    saving.current = true
    try {
      await announcementOrderApi.save(pinned, baseline.revision, changes)
      Message.success('公告顺序已保存')
      onSaved()
    } catch (e) {
      const status = (e as { response?: { status?: number } })?.response?.status
      if (status === 401 || status === 403) {
        Message.error('当前账号无权调整这些公告')
        onClose()
      } else {
        setConflict(status === 409)
        setError(requestErrorMessage(e, '保存公告顺序失败'))
      }
    } finally {
      saving.current = false
      setBusy(false)
    }
  }
  async function reload() {
    setBusy(true)
    const result = await query.refetch()
    if (result.isSuccess) {
      setDraft(null)
      setPreview(false)
      setConflict(false)
      setError('')
      setPage(1)
    }
    setBusy(false)
  }
  const offset = (page - 1) * pageSize
  return <Modal visible title={preview ? '公告排序预览' : '调整公告顺序'} className="announcement-dialog"
    style={{ width: 'min(880px, 96vw)' }} onCancel={() => { if (!busy) onClose() }}
    maskClosable={false} closable={!busy} footer={<Space>
      <Button disabled={busy} onClick={onClose}>取消</Button>
      {conflict ? <Button type="primary" loading={busy} onClick={() => void reload()}>刷新并重新排列</Button>
        : preview ? <>
          <Button disabled={busy} onClick={() => setPreview(false)}>返回调整</Button>
          <Button type="primary" loading={busy} onClick={() => void save()}>保存排序</Button>
        </> : <Button type="primary" disabled={!changes.length || query.isError} onClick={() => {
          if (baseline) setDraft({ baseline, rows: previewOrder(rows) })
          setPreview(true)
          setPage(1)
        }}>预览排序</Button>}
    </Space>}>
    <Radio.Group type="button" value={pinned ? 'pinned' : 'normal'} disabled={readonly || !!changes.length}
      onChange={value => { setPinned(value === 'pinned'); setDraft(null); setPage(1); setError('') }}>
      <Radio value="normal">普通公告</Radio><Radio value="pinned">置顶公告</Radio>
    </Radio.Group>
    <p>{preview ? '当前显示本管理范围内保存后的顺序。' : '顺序号越小越靠前；留空时按时间排列。可拖动或使用箭头调整。'}</p>
    {error && <p role="alert">{error}</p>}
    {query.isError ? <LoadError message="公告排序加载失败" onRetry={() => void reload()} />
      : !baseline ? <Spin /> : <ResizableTable<OrderRow> rowKey="id" tableId="announcement-ordering" growKey="title"
        scroll={{ x: 600 }} data={rows.slice(offset, offset + pageSize)}
        noDataElement={<Empty description="本组暂无可调整的公告" />}
        onRow={row => ({
          onDragOver: (event: DragEvent<HTMLTableRowElement>) => { if (!readonly && dragging.current !== null) event.preventDefault() },
          onDrop: (event: DragEvent<HTMLTableRowElement>) => {
            event.preventDefault()
            if (!readonly && dragging.current !== null) move(dragging.current, row.id)
            dragging.current = null
          },
        })}
        columns={[
          { title: '公告标题', key: 'title', width: 250, render: (_, row) => <Space>
            {!preview && <Button type="text" size="mini" icon={<IconDragDotVertical />} draggable={!readonly}
              aria-label={`拖动${row.title}`} style={{ cursor: 'grab' }}
              onDragStart={e => { dragging.current = row.id; e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', String(row.id)) }}
              onDragEnd={() => { dragging.current = null }} />}
            {row.title}
          </Space> },
          { title: '发布部门', dataIndex: 'departmentName', width: 120 },
          { title: '顺序号', key: 'rank', width: 135, render: (_, row) => preview ? row.sortOrder ?? '自动'
            : <InputNumber aria-label={`${row.title}顺序号`} min={0} max={MAX_ORDER} precision={0}
              style={{ width: '100%' }} size="small" placeholder="自动" value={row.sortOrder ?? ''}
              onChange={v => edit(rows.map(r => r.id === row.id ? { ...r, sortOrder: Number.isInteger(v) ? v : null } : r))} /> },
          ...(!preview ? [{ title: '移动', key: 'move', width: 90, render: (_: unknown, row: OrderRow) => {
            const index = rows.findIndex(item => item.id === row.id)
            return <Space size={0}>
              <Button type="text" size="mini" aria-label={`${row.title}上移`} icon={<IconArrowUp />}
                disabled={index === 0 || readonly} onClick={() => move(row.id, rows[index - 1].id)} />
              <Button type="text" size="mini" aria-label={`${row.title}下移`} icon={<IconArrowDown />}
                disabled={index === rows.length - 1 || readonly} onClick={() => move(row.id, rows[index + 1].id)} />
            </Space>
          } }] : []),
        ]}
        pagination={{ current: page, pageSize, total: rows.length, sizeOptions: [10, 20, 50, 100],
          onChange: (current, size) => { setPage(size === pageSize ? current : 1); setPageSize(size) } }} />}
  </Modal>
}
