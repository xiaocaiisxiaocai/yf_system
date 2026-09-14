import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'
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
  receiptRevision?: string
  realtimeConnected?: boolean
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

export default function MessagePanel({ projectId, projectStatus, onRead, onSent, targetId, revision = '', active = true, receiptRevision = '', realtimeConnected = false }: Props) {
  const [seenRevision, setSeenRevision] = useState(revision)
  const currentRevision = useRef(revision)
  useEffect(() => {
    currentRevision.current = revision
  }, [revision])
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
  const sendInFlight = useRef(false)
  const [syncError, setSyncError] = useState(false)
  const [syncRetry, setSyncRetry] = useState(0)
  const scrollAnchor = useRef<{ id: number; top: number; owner: Element } | null>(null)
  const [receiptRefresh, setReceiptRefresh] = useState(0)
  const countsVersion = useRef(0)
  const messageMutations = useRef(0)
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
    if (changed) {
      countsVersion.current += 1
      setList(loadedMessages.current)
    }
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
    let scrollTimer: ReturnType<typeof setTimeout> | undefined
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
        if (!stopped && !paused() && (!realtimeConnected || failures > 0)) timer = setTimeout(sync, Math.min(5000 * 2 ** failures, 60000))
      }
    }
    const wake = () => {
      if (paused()) { clearTimeout(timer); controller?.abort(); return }
      void sync()
    }
    const onScroll = () => { clearTimeout(scrollTimer); scrollTimer = setTimeout(wake, 150) }
    document.addEventListener('visibilitychange', wake)
    window.addEventListener('focus', wake)
    window.addEventListener('online', wake)
    window.addEventListener('offline', wake)
    window.addEventListener('scroll', onScroll, true)
    void sync()
    return () => {
      stopped = true
      clearTimeout(timer)
      clearTimeout(scrollTimer)
      controller?.abort()
      document.removeEventListener('visibilitychange', wake)
      window.removeEventListener('focus', wake)
      window.removeEventListener('online', wake)
      window.removeEventListener('offline', wake)
      window.removeEventListener('scroll', onScroll, true)
    }
  }, [active, projectId, targetId, user?.id, applyReadCounts, receiptRevision, realtimeConnected, list.length])

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
        if (!append) setSeenRevision(requestRevision)
        setPage(p)
        setLoadError(false)
        setAppendError(false)
        setSyncError(false)
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

  useLayoutEffect(() => {
    const anchor = scrollAnchor.current
    scrollAnchor.current = null
    if (!anchor) return
    const element = listRef.current?.querySelector<HTMLElement>(`[data-message-id="${anchor.id}"]`)
    if (element) anchor.owner.scrollTop += element.getBoundingClientRect().top - anchor.top
  }, [list])

  useEffect(() => {
    if (!active || loading || loadError || !revision || revision === seenRevision) return
    let stopped = false
    let inFlight = false
    let resumePending = false
    let timer: ReturnType<typeof setTimeout> | undefined
    let controller: AbortController | undefined
    const generation = messageScope.current.generation
    const paused = () => typeof document !== 'undefined' && document.visibilityState !== 'visible'
      || typeof navigator !== 'undefined' && navigator.onLine === false
    const refresh = async () => {
      if (stopped || inFlight || paused()) return
      clearTimeout(timer)
      inFlight = true
      controller = new AbortController()
      const requestLoad = loadSeq.current
      const requestCounts = countsVersion.current
      const requestMutations = messageMutations.current
      const oldestId = loadedMessages.current.at(-1)?.id
      const messages: Msg[] = []
      let beforeId: number | undefined
      let nextTotal = 0
      try {
        // Cover the entire loaded window, including gaps larger than one page.
        // Cursor pagination remains stable when messages arrive during this fetch.
        for (;;) {
          const response = await http.get(`/projects/${projectId}/messages`, {
            params: { page: 1, pageSize: 20, beforeId, targetId },
            signal: controller.signal, timeout: 10000, quietNetworkError: true,
          } as QuietRequestConfig)
          if (stopped || controller.signal.aborted || generation !== messageScope.current.generation
              || requestLoad !== loadSeq.current || requestMutations !== messageMutations.current) return
          const batch: Msg[] = response.data.list
          if (!Array.isArray(batch) || !Number.isSafeInteger(response.data.total) || response.data.total < 0
              || batch.some((item, index) => !Number.isSafeInteger(item?.id) || item.id <= 0
                || (index > 0 && item.id >= batch[index - 1].id) || (beforeId !== undefined && item.id >= beforeId))) {
            throw new Error('Invalid message window')
          }
          if (beforeId === undefined) nextTotal = response.data.total
          messages.push(...batch)
          const lastId = batch.at(-1)?.id
          if (lastId === undefined || batch.length < 20 || targetId !== undefined || oldestId === undefined
              || lastId <= oldestId || messages.length >= nextTotal) break
          beforeId = lastId
        }
        const known = new Map(loadedMessages.current.map((message) => [message.id, message]))
        const nextList = messages.map((message) => {
          const previous = known.get(message.id)
          return previous ? { ...message, readByMe: message.readByMe || previous.readByMe,
            ...(requestCounts !== countsVersion.current ? { readCount: previous.readCount, totalCount: previous.totalCount } : {}) } : message
        })
        const root = listRef.current
        if (root && typeof document !== 'undefined') {
          const elements = [...root.querySelectorAll<HTMLElement>('[data-message-id]')]
          // When reading history, anchor the same visible message before paint.
          // At the top of the list, let newly arrived messages appear immediately.
          if (elements[0]?.getBoundingClientRect().top < 80) {
            const ids = new Set(nextList.map((message) => message.id))
            const element = elements.find((item) => ids.has(Number(item.dataset.messageId)) && item.getBoundingClientRect().bottom > 80)
            const owner = root.closest('.layout-content') ?? document.scrollingElement
            if (element && owner) scrollAnchor.current = { id: Number(element.dataset.messageId), top: element.getBoundingClientRect().top, owner }
          }
        }
        loadedMessages.current = nextList
        cursor.current = nextList.at(-1)?.id
        cursorStartTotal.current = nextTotal
        setList(nextList)
        setTotal(nextTotal)
        setHasMore(nextList.length < nextTotal)
        setAppendError(false)
        setSyncError(false)
        setSeenRevision(revision)
        if (openReceiptId.current != null && !nextList.some((message) => message.id === openReceiptId.current)) {
          receiptSeq.current += 1
          setReceipt(null)
          setReceiptRequest(null)
        }
      } catch {
        if (!stopped && !controller.signal.aborted) {
          setSyncError(true)
          timer = setTimeout(refresh, 5000)
        }
      } finally {
        inFlight = false
        if (resumePending && !stopped && !paused()) {
          resumePending = false
          void refresh()
        }
      }
    }
    const wake = () => {
      if (paused()) { clearTimeout(timer); controller?.abort(); return }
      if (inFlight) { resumePending = true; return }
      void refresh()
    }
    if (typeof document !== 'undefined') document.addEventListener('visibilitychange', wake)
    if (typeof window !== 'undefined') {
      window.addEventListener('focus', wake)
      window.addEventListener('online', wake)
      window.addEventListener('offline', wake)
    }
    void refresh()
    return () => {
      stopped = true
      clearTimeout(timer)
      controller?.abort()
      if (typeof document !== 'undefined') document.removeEventListener('visibilitychange', wake)
      if (typeof window !== 'undefined') {
        window.removeEventListener('focus', wake)
        window.removeEventListener('online', wake)
        window.removeEventListener('offline', wake)
      }
    }
  }, [active, loading, loadError, revision, seenRevision, projectId, targetId, user?.id, syncRetry])

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
    const draft = content
    const text = draft.trim()
    if (!text || sendInFlight.current || listLoading.current && loadedMessages.current.length === 0) return
    const generation = messageScope.current.generation
    sendInFlight.current = true
    setSending(true)
    try {
      const response = await http.post<Msg>(`/projects/${projectId}/messages`, { content: text })
      if (!mounted.current || generation !== messageScope.current.generation) return
      setContent((current) => current === draft ? '' : current)
      if (targetId === undefined) {
        // The POST returns the committed message. Show it without another list
        // request, and prevent older in-flight reads from replacing this state.
        messageMutations.current += 1
        loadSeq.current += 1
        listLoading.current = false
        setLoading(false)
        const message = response.data
        if (!loadedMessages.current.some((item) => item.id === message.id)) {
          loadedMessages.current = [message, ...loadedMessages.current].sort((a, b) => b.id - a.id)
          setList(loadedMessages.current)
          setTotal((value) => value + 1)
          cursor.current = loadedMessages.current.at(-1)?.id
        }
        // Reconcile any simultaneous remote change; do not acknowledge its
        // revision just because our own POST completed.
        setSyncRetry((value) => value + 1)
      }
      // A targeted view clears its target and loads once through the scope effect.
      onSent?.()
    } catch {
      // 请求层已显示错误；保留草稿，允许用户重试。
    } finally {
      sendInFlight.current = false
      if (mounted.current) setSending(false)
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
    messageMutations.current += 1
    loadedMessages.current = loadedMessages.current.filter((m) => m.id !== id)
    setList(loadedMessages.current)
    setTotal((current) => Math.max(0, current - 1))
    setSyncRetry((value) => value + 1)
  }

  return (
    <div>
      <div className="section-heading">
        <div>
          <h2>协作留言（{total}）</h2>
        </div>
      </div>

      {syncError && (
        <div role="status" style={{ marginBottom: 12 }}>
          <Typography.Text type="error">留言同步失败，正在重试</Typography.Text>
          <Button type="text" size="small" onClick={() => setSyncRetry((value) => value + 1)}>重试同步</Button>
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
          <Button type="primary" icon={<IconSend />} onClick={send} disabled={!content.trim() || loading && list.length === 0} loading={sending}>
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
        <Spin loading={loading && list.length === 0} style={{ width: '100%' }}>
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
