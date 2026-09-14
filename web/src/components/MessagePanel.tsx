import { useCallback, useEffect, useRef, useState } from 'react'
import {
  Avatar, Button, Drawer, Empty, Input, List, Popconfirm, Popover, Space, Spin, Tag, Typography,
} from '@arco-design/web-react'
import { IconCheck, IconDelete, IconSend } from '@arco-design/web-react/icon'
import http, { type QuietRequestConfig } from '../api/client'
import { useAuth } from '../store/auth'
import { type Message as Msg, fmtTime } from '../api/types'

interface Props {
  projectId: number
  projectStatus: string
  onRead?: () => void
  onSent?: () => void
  targetId?: number
  revision?: string
  active?: boolean
}

interface Reader {
  userId: number
  realName: string
  userType: string
  readAt?: string | null
}

interface ReadCounts {
  id: number
  readCount: number
  totalCount: number
}

const RECEIPT_SYNC_CONCURRENCY = 4

async function loadReadCounts(ids: number[]): Promise<ReadCounts[]> {
  const counts: ReadCounts[] = []
  for (let start = 0; start < ids.length; start += RECEIPT_SYNC_CONCURRENCY) {
    const batch = ids.slice(start, start + RECEIPT_SYNC_CONCURRENCY)
    const results = await Promise.all(batch.map(async (id) => {
      try {
        const response = await http.get(`/messages/${id}/reads`)
        if (!Array.isArray(response.data?.readers) || !Array.isArray(response.data?.unread)) return null
        const readers = response.data.readers
        const unread = response.data.unread
        return { id, readCount: readers.length, totalCount: readers.length + unread.length }
      } catch {
        return null
      }
    }))
    counts.push(...results.filter((result): result is ReadCounts => result !== null))
  }
  return counts
}

export default function MessagePanel({ projectId, projectStatus, onRead, onSent, targetId, revision = '', active = true }: Props) {
  const [seenRevision, setSeenRevision] = useState(revision)
  const currentRevision = useRef(revision)
  useEffect(() => {
    currentRevision.current = revision
    // Establish the first server snapshot without presenting historical data as a new arrival.
    // eslint-disable-next-line react/set-state-in-effect
    if (!seenRevision && revision) setSeenRevision(revision)
  }, [revision, seenRevision])
  const [list, setList] = useState<Msg[]>([])
  const [total, setTotal] = useState(0)
  const [hasMore, setHasMore] = useState(false)
  const [page, setPage] = useState(1)
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState(false)
  const [appendError, setAppendError] = useState(false)
  const [content, setContent] = useState('')
  const [receipt, setReceipt] = useState<{ id: number; readers: Reader[]; unread: Reader[] } | null>(null)
  const [receiptRequest, setReceiptRequest] = useState<{ id: number; loading: boolean; error: boolean } | null>(null)
  const { hasPerm, user } = useAuth()
  const listRef = useRef<HTMLDivElement>(null)
  const loadSeq = useRef(0)
  const receiptSeq = useRef(0)
  const cursor = useRef<number | undefined>()
  const cursorStartTotal = useRef<number | null>(null)
  const loadedMessages = useRef<Msg[]>([])
  const markingRead = useRef(new Set<number>())
  const mounted = useRef(true)
  const messageScope = useRef({ key: '', generation: 0 })
  const [sending, setSending] = useState(false)
  const [receiptRefresh, setReceiptRefresh] = useState(0)
  const countsVersion = useRef(0)
  const listLoading = useRef(false)
  const openReceiptId = useRef<number | null>(null)
  openReceiptId.current = receipt?.id ?? null

  const scopeKey = `${projectId}:${targetId ?? ''}`
  if (messageScope.current.key !== scopeKey) {
    messageScope.current = { key: scopeKey, generation: messageScope.current.generation + 1 }
  }

  useEffect(() => {
    mounted.current = true
    return () => {
      mounted.current = false
    }
  }, [])

  const canWrite = hasPerm('message:create') && projectStatus !== 'COMPLETED' && projectStatus !== 'TERMINATED'
  const canDelete = hasPerm('message:delete_any') && projectStatus !== 'COMPLETED' && projectStatus !== 'TERMINATED'

  const applyReadCounts = useCallback((counts: ReadCounts[]) => {
    const byId = new Map(counts.map((item) => [item.id, item]))
    let changed = false
    loadedMessages.current = loadedMessages.current.map((message) => {
      const next = byId.get(message.id)
      if (!next || next.readCount === message.readCount && next.totalCount === message.totalCount) return message
      changed = true
      return { ...message, readCount: next.readCount, totalCount: next.totalCount }
    })
    if (changed) setList(loadedMessages.current)
  }, [])

  const receiveReceipt = useCallback((id: number, readCount: number, totalCount: number) => {
    countsVersion.current += 1
    applyReadCounts([{ id, readCount, totalCount }])
  }, [applyReadCounts])

  // Read receipts change without creating a project activity. Refresh only metadata,
  // so polling never resets a draft, a history cursor, or the user's scroll position.
  useEffect(() => {
    if (!active || typeof document === 'undefined' || typeof window === 'undefined') return
    let stopped = false
    let inFlight = false
    let failures = 0
    let timer: ReturnType<typeof setTimeout> | undefined
    let controller: AbortController | undefined
    const generation = messageScope.current.generation
    const paused = () => document.visibilityState !== 'visible'
      || typeof navigator !== 'undefined' && navigator.onLine === false
    const sync = async () => {
      if (stopped || inFlight) return
      clearTimeout(timer)
      if (paused()) return
      inFlight = true
      controller = new AbortController()
      const requestVersion = countsVersion.current
      const requestLoad = loadSeq.current
      try {
        const ids = [...(listRef.current?.querySelectorAll<HTMLElement>('[data-message-id]') ?? [])]
          .filter((element) => {
            const rect = element.getBoundingClientRect()
            return rect.width > 0 && rect.height > 0 && rect.bottom > 0 && rect.top < window.innerHeight
          })
          .map((element) => Number(element.dataset.messageId)).slice(0, 500)
        if (listLoading.current || !ids.length) return
        const response = await http.get(`/projects/${projectId}/message-receipts`, {
          params: { ids: ids.join(',') }, signal: controller.signal, timeout: 10000, quietNetworkError: true,
        } as QuietRequestConfig)
        if (stopped || controller.signal.aborted || messageScope.current.generation !== generation
            || requestLoad !== loadSeq.current || requestVersion !== countsVersion.current) return
        if (!Array.isArray(response.data) || response.data.some((item: ReadCounts) =>
          !item || !ids.includes(item.id) || !Number.isInteger(item.readCount) || !Number.isInteger(item.totalCount)
          || item.readCount < 0 || item.totalCount < item.readCount)) throw new Error('Invalid receipt counts')
        applyReadCounts(response.data)
        failures = 0
        setReceiptRefresh((value) => value + 1)
        const detailId = openReceiptId.current
        if (detailId != null) {
          const seq = receiptSeq.current
          const detail = await http.get(`/messages/${detailId}/reads`, { signal: controller.signal, timeout: 10000, quietNetworkError: true } as QuietRequestConfig)
          if (!stopped && !controller.signal.aborted && seq === receiptSeq.current
              && openReceiptId.current === detailId && messageScope.current.generation === generation) {
            setReceipt({ id: detailId, readers: detail.data.readers, unread: detail.data.unread })
          }
        }
      } catch {
        failures += 1
      } finally {
        inFlight = false
        if (!stopped && !paused()) timer = setTimeout(sync, Math.min(5000 * 2 ** failures, 60000))
      }
    }
    const wake = () => {
      if (paused()) { clearTimeout(timer); controller?.abort(); return }
      void sync()
    }
    document.addEventListener('visibilitychange', wake)
    window.addEventListener('focus', wake)
    window.addEventListener('online', wake)
    window.addEventListener('offline', wake)
    void sync()
    return () => {
      stopped = true
      clearTimeout(timer)
      controller?.abort()
      document.removeEventListener('visibilitychange', wake)
      window.removeEventListener('focus', wake)
      window.removeEventListener('online', wake)
      window.removeEventListener('offline', wake)
    }
  }, [active, projectId, targetId, user?.id, applyReadCounts])

  const load = useCallback(
    async (p: number, append: boolean) => {
      const seq = ++loadSeq.current
      listLoading.current = true
      const requestRevision = currentRevision.current
      setLoading(true)
      if (append) setAppendError(false)
      else setLoadError(false)
      try {
        const r = await http.get(`/projects/${projectId}/messages`, {
          params: { page: p, pageSize: 20, beforeId: append ? cursor.current : undefined, targetId },
        })
        if (seq !== loadSeq.current) return // 已有更新的请求在途，丢弃旧响应
        setTotal(r.data.total)
        cursor.current = r.data.list.at(-1)?.id
        const nextList = append
          ? [...loadedMessages.current, ...r.data.list.filter((m: Msg) => !loadedMessages.current.some((old) => old.id === m.id))]
          : r.data.list
        if (!append) cursorStartTotal.current = r.data.total
        const totalChangedDuringCursor = append && cursorStartTotal.current !== null && r.data.total !== cursorStartTotal.current
        loadedMessages.current = nextList
        setHasMore(r.data.list.length === 20 && (totalChangedDuringCursor || nextList.length < r.data.total))
        setList(nextList)
        setSeenRevision(requestRevision)
        setPage(p)
        setLoadError(false)
        setAppendError(false)
      } catch {
        if (seq === loadSeq.current) {
          if (append) setAppendError(true)
          else setLoadError(true)
        }
      } finally {
        if (seq === loadSeq.current) { listLoading.current = false; setLoading(false) }
      }
    },
    [projectId, targetId]
  )

  useEffect(() => {
    load(1, false)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId, targetId])

  // 只有留言实际进入可视区域后才上报已读，避免“加载第一页=全部已读”。
  useEffect(() => {
    const root = listRef.current
    if (!root || typeof IntersectionObserver === 'undefined') return
    const observer = new IntersectionObserver(
      (entries) => {
        const ids = entries
          .filter((entry) => entry.isIntersecting && entry.intersectionRatio >= 0.6)
          .map((entry) => Number((entry.target as HTMLElement).closest<HTMLElement>('[data-message-id]')?.dataset.messageId))
          .filter((id) => Number.isFinite(id) && !markingRead.current.has(id))
        if (!ids.length) return
        const scopeGeneration = messageScope.current.generation
        ids.forEach((id) => markingRead.current.add(id))
        void http.post('/messages/read', { ids }).then(async () => {
          if (!mounted.current || messageScope.current.generation !== scopeGeneration) return
          loadedMessages.current = loadedMessages.current.map((m) => (ids.includes(m.id) ? { ...m, readByMe: true } : m))
          setList(loadedMessages.current)
          onRead?.()

          const version = ++countsVersion.current
          const counts = await loadReadCounts(ids)
          if (!mounted.current || messageScope.current.generation !== scopeGeneration || version !== countsVersion.current) return
          applyReadCounts(counts)
        }).catch(() => {
          ids.forEach((id) => markingRead.current.delete(id))
        })
      },
      { threshold: 0.6 }
    )
    // 观察正文末尾的小标记；长留言不可能有 60% 的整块高度同时进入视口。
    root.querySelectorAll<HTMLElement>('[data-unread="true"] .message-read-marker').forEach((node) => observer.observe(node))
    return () => observer.disconnect()
  }, [list, onRead, projectId, targetId, applyReadCounts])

  const send = async () => {
    const text = content.trim()
    if (!text || sending) return
    setSending(true)
    try {
      await http.post(`/projects/${projectId}/messages`, { content: text })
      setContent('')
      load(1, false)
      onSent?.()
    } catch {
      // 请求层已显示错误；保留草稿，允许用户重试。
    } finally {
      setSending(false)
    }
  }

  const openReceipt = async (id: number) => {
    const seq = ++receiptSeq.current
    setReceipt(null)
    setReceiptRequest({ id, loading: true, error: false })
    try {
      const r = await http.get(`/messages/${id}/reads`)
      if (seq === receiptSeq.current) {
        countsVersion.current += 1
        applyReadCounts([{ id, readCount: r.data.readers.length, totalCount: r.data.readers.length + r.data.unread.length }])
        setReceipt({ id, readers: r.data.readers, unread: r.data.unread })
        setReceiptRequest(null)
      }
    } catch {
      if (seq !== receiptSeq.current) return
      setReceiptRequest({ id, loading: false, error: true })
    }
  }

  const remove = async (id: number) => {
    await http.delete(`/messages/${id}`)
    loadedMessages.current = loadedMessages.current.filter((m) => m.id !== id)
    setList(loadedMessages.current)
    setTotal((current) => Math.max(0, current - 1))
  }

  return (
    <div>
      <div className="section-heading">
        <div>
          <h2>协作留言（{total}）</h2>
        </div>
      </div>

      {revision && seenRevision && revision !== seenRevision && (
        <div role="status" style={{ marginBottom: 12 }}>
          <Typography.Text type="secondary">协作信息有更新</Typography.Text>
          <Button type="text" size="small" onClick={() => load(1, false)}>查看最新留言</Button>
        </div>
      )}

      {canWrite && (
        <div className="message-composer" style={{ display: 'flex', gap: 8, marginBottom: 16 }}>
          <Input.TextArea
            placeholder="输入留言，Ctrl+Enter 发送"
            value={content}
            onChange={(value) => setContent(Array.from(value).slice(0, 4000).join(''))}
            autoSize={{ minRows: 2, maxRows: 5 }}
            style={{ flex: 1 }}
            onKeyDown={(e) => {
              if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') send()
            }}
          />
          <Button type="primary" icon={<IconSend />} onClick={send} disabled={!content.trim()} loading={sending}>
            发送
          </Button>
        </div>
      )}

      {loadError ? (
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '24px 0' }}>
          <Typography.Text type="error">加载失败</Typography.Text>
          <Button size="small" onClick={() => load(1, false)}>重试</Button>
        </div>
      ) : (
        <Spin loading={loading && page === 1} style={{ width: '100%' }}>
          {list.length === 0 && !loading && !hasMore ? (
            <Empty description="暂无留言" />
          ) : (
            <div ref={listRef}>
              {list.map((m) => (
              <div
                className="msg-item"
                key={m.id}
                data-message-id={m.id}
                aria-label={m.id === targetId ? '当前定位留言' : undefined}
                style={m.id === targetId ? { borderLeft: '3px solid rgb(var(--primary-6))', paddingLeft: 12, background: 'var(--color-fill-1)' } : undefined}
                data-unread={!m.readByMe && m.senderId !== user?.id ? 'true' : 'false'}
              >
                <Space align="start">
                  <Avatar
                    size={32}
                    style={{ background: m.senderType === 'SUPPLIER' ? '#7b61ff' : '#165dff' }}
                  >
                    {m.senderName.slice(0, 1)}
                  </Avatar>
                  <div style={{ flex: 1 }}>
                    <Space size={8}>
                      <Typography.Text bold>{m.senderName}</Typography.Text>
                      <Tag size="small" color={m.senderType === 'SUPPLIER' ? 'purple' : 'arcoblue'}>
                        {m.senderType === 'SUPPLIER' ? '供应商' : '公司'}
                      </Tag>
                      <Tag size="small" color="gray">项目留言</Tag>
                      <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                        {fmtTime(m.createdAt)}
                      </Typography.Text>
                    </Space>
                    <div style={{ marginTop: 4, whiteSpace: 'pre-wrap' }}>{m.content}</div>
                    <div className="message-read-marker" aria-hidden="true" />
                    <div style={{ marginTop: 4 }}>
                      <Popover
                        content={
                          <div style={{ maxWidth: 320 }}>
                            <Typography.Text bold style={{ fontSize: 12 }}>已读人员</Typography.Text>
                            <ReceiptBody id={m.id} refreshKey={receiptRefresh} onLoaded={receiveReceipt} />
                          </div>
                        }
                        trigger="click"
                        triggerProps={{ escToClose: true, unmountOnExit: true }}
                      >
                        <Button
                          size="mini"
                          type="text"
                          status={m.readCount >= m.totalCount && m.totalCount > 0 ? 'success' : undefined}
                          aria-label={`查看已读人员：${m.readCount}/${m.totalCount}`}
                          icon={<IconCheck />}
                        >
                          已读 {m.readCount}/{m.totalCount}
                        </Button>
                      </Popover>
                      {m.senderId === user?.id && (
                        <Button size="mini" type="text" onClick={() => openReceipt(m.id)} style={{ marginLeft: 8 }}>
                          回执详情
                        </Button>
                      )}
                      {canDelete && (
                        <Popconfirm title="删除这条留言？删除后双方均不可见。" onOk={() => remove(m.id)}>
                          <Button size="mini" type="text" status="danger" icon={<IconDelete />} style={{ marginLeft: 8 }}>
                            删除
                          </Button>
                        </Popconfirm>
                      )}
                    </div>
                  </div>
                </Space>
              </div>
              ))}
              {appendError && (
                <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 8, padding: 12 }}>
                  <Typography.Text type="error">加载失败</Typography.Text>
                  <Button size="small" onClick={() => load(page + 1, true)}>重试</Button>
                </div>
              )}
              {hasMore && (
                <div style={{ textAlign: 'center', padding: 12 }}>
                  <Button onClick={() => load(page + 1, true)} loading={loading}>
                    加载更多（{list.length}/{total}）
                  </Button>
                </div>
              )}
            </div>
          )}
        </Spin>
      )}

      <Drawer
        width={420}
        title="已读回执"
        visible={!!receipt || !!receiptRequest}
        onCancel={() => {
          receiptSeq.current += 1
          setReceipt(null)
          setReceiptRequest(null)
        }}
        footer={null}
      >
        {receiptRequest?.loading && !receipt && <Typography.Text type="secondary">回执加载中…</Typography.Text>}
        {receiptRequest?.error && !receipt && (
          <Space>
            <Typography.Text type="error">回执加载失败</Typography.Text>
            <Button size="small" onClick={() => openReceipt(receiptRequest.id)}>重试</Button>
          </Space>
        )}
        {receipt && (
          <>
            <Typography.Text bold>已读（{receipt.readers.length}）</Typography.Text>
            <List
              size="small"
              dataSource={receipt.readers}
              render={(r) => (
                <List.Item key={r.userId} extra={fmtTime(r.readAt)}>
                  {r.realName}
                  <Tag size="small" color={r.userType === 'SUPPLIER' ? 'purple' : 'arcoblue'} style={{ marginLeft: 6 }}>
                    {r.userType === 'SUPPLIER' ? '供应商' : '公司'}
                  </Tag>
                </List.Item>
              )}
            />
            <Typography.Text bold style={{ display: 'block', marginTop: 12 }}>
              未读（{receipt.unread.length}）
            </Typography.Text>
            <List
              size="small"
              dataSource={receipt.unread}
              render={(r) => (
                <List.Item key={r.userId}>
                  {r.realName}
                  <Tag size="small" color={r.userType === 'SUPPLIER' ? 'purple' : 'arcoblue'} style={{ marginLeft: 6 }}>
                    {r.userType === 'SUPPLIER' ? '供应商' : '公司'}
                  </Tag>
                </List.Item>
              )}
            />
          </>
        )}
      </Drawer>
    </div>
  )
}

/** 气泡内联的已读名单（轻量版） */
function ReceiptBody({ id, refreshKey = 0, onLoaded }: {
  id: number
  refreshKey?: number
  onLoaded?: (id: number, readCount: number, totalCount: number) => void
}) {
  const [receipt, setReceipt] = useState<{
    id: number
    names: string[]
    loading: boolean
    error: boolean
  }>(() => ({ id, names: [], loading: true, error: false }))
  const requestSeq = useRef(0)

  const retry = () => {
    setReceipt({ id, names: [], loading: true, error: false })
    const seq = ++requestSeq.current
    http.get(`/messages/${id}/reads`)
      .then((r) => {
        if (seq !== requestSeq.current) return
        onLoaded?.(id, r.data.readers.length, r.data.readers.length + r.data.unread.length)
        setReceipt({
          id,
          names: r.data.readers.map((x: Reader) => x.realName),
          loading: false,
          error: false,
        })
      })
      .catch(() => {
        if (seq === requestSeq.current) {
          setReceipt({ id, names: [], loading: false, error: true })
        }
      })
  }

  useEffect(() => {
    const seq = ++requestSeq.current
    http.get(`/messages/${id}/reads`)
      .then((r) => {
        if (seq !== requestSeq.current) return
        onLoaded?.(id, r.data.readers.length, r.data.readers.length + r.data.unread.length)
        setReceipt({
          id,
          names: r.data.readers.map((x: Reader) => x.realName),
          loading: false,
          error: false,
        })
      })
      .catch(() => {
        if (seq === requestSeq.current) {
          setReceipt({ id, names: [], loading: false, error: true })
        }
      })
    return () => {
      requestSeq.current += 1
    }
  }, [id, refreshKey, onLoaded])

  const current = receipt.id === id
    ? receipt
    : { id, names: [], loading: true, error: false }

  if (current.loading) return <div style={{ fontSize: 12 }}>回执加载中…</div>
  if (current.error) {
    return (
      <div style={{ fontSize: 12 }}>
        <Typography.Text type="error">回执加载失败</Typography.Text>
        <Button size="mini" type="text" onClick={retry}>重试</Button>
      </div>
    )
  }
  return <div style={{ fontSize: 12 }}>{current.names.length ? current.names.join('、') : '暂无'}</div>
}
