import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'
import { Button, Typography } from '@arco-design/web-react'
import http, { type QuietRequestConfig } from '../api/client'
import { useAuth } from '../store/auth'
import { type Message as Msg, fmtTime } from '../api/types'
import { MessageImageComposer, MessageImages, pasteMessageImages } from './MessageImages'
import { ReceiptBody, loadReadCounts, type Reader, type ReadCounts } from './MessageReceipts'
import type { ApiResponses } from '../api/types'
import { MessageComposer } from './message-panel/MessageComposer'
import { MessageList } from './message-panel/MessageList'
import { MessageReceiptDrawer } from './message-panel/MessageReceiptDrawer'

interface Props {
  projectId: number
  projectStatus: string
  onRead?: () => void
  targetId?: number
  revision?: string
  active?: boolean
  receiptRevision?: string
  realtimeConnected?: boolean
}

export default function MessagePanel({ projectId, projectStatus, onRead, targetId, revision = '', active = true, receiptRevision = '', realtimeConnected = false }: Props) {
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
  const [images, setImages] = useState<File[]>([])
  const [receipt, setReceipt] = useState<{ id: number; readers: Reader[]; unread: Reader[] } | null>(null)
  const [receiptRequest, setReceiptRequest] = useState<{ id: number; loading: boolean; error: boolean } | null>(null)
  const { hasPerm, user } = useAuth()
  const listRef = useRef<HTMLDivElement>(null)
  const loadSeq = useRef(0)
  const loadAbort = useRef<AbortController>()
  const locatedScope = useRef<number>()
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

  const scopeKey = `${user?.id ?? ''}:${projectId}:${targetId ?? ''}`
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
  const draftScopeKey = `${user?.id ?? ''}:${projectId}`
  const draftScope = useRef(draftScopeKey)

  useEffect(() => {
    if (draftScope.current === draftScopeKey) return
    draftScope.current = draftScopeKey
    setContent('')
    setImages([])
  }, [draftScopeKey])

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
        const region = listRef.current?.closest?.('.message-scroll-area')?.getBoundingClientRect()
        const visibleTop = Math.max(0, region?.top ?? 0)
        const visibleBottom = Math.min(window.innerHeight, region?.bottom ?? window.innerHeight)
        const ids = [...(listRef.current?.querySelectorAll<HTMLElement>('[data-message-id]') ?? [])]
          .filter((element) => {
            const rect = element.getBoundingClientRect()
            return rect.width > 0 && rect.height > 0 && rect.bottom > visibleTop && rect.top < visibleBottom
          })
          .map((element) => Number(element.dataset.messageId)).slice(0, 500)
        if (listLoading.current || !ids.length) return
        const response = await http.get<ApiResponses['GET /projects/{id}/message-receipts']>(`/projects/${projectId}/message-receipts`, {
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
          const detail = await http.get<ApiResponses['GET /messages/{id}/reads']>(`/messages/${detailId}/reads`, { signal: controller.signal, timeout: 10000, quietNetworkError: true } as QuietRequestConfig)
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
      loadAbort.current?.abort()
      const controller = new AbortController()
      loadAbort.current = controller
      listLoading.current = true
      const requestRevision = currentRevision.current
      setLoading(true)
      if (append) setAppendError(false)
      else setLoadError(false)
      try {
        let beforeId = append ? cursor.current : undefined
        const rows: Msg[] = []
        let responseTotal = 0
        let lastBatchSize = 0
        for (;;) {
          const response = await http.get<ApiResponses['GET /projects/{id}/messages']>(`/projects/${projectId}/messages`, {
            params: { page: p, pageSize: 20, beforeId }, signal: controller.signal,
          })
          if (seq !== loadSeq.current || controller.signal.aborted) return
          // senderType is a plain string in the API schema; the frontend narrows it to the two known values.
          const batch = response.data.list as Msg[]
          if (!Array.isArray(batch)) throw new Error('Invalid message window')
          responseTotal = response.data.total
          rows.push(...batch)
          lastBatchSize = batch.length
          const lastId = batch.at(-1)?.id
          if (!append && targetId !== undefined && lastId !== undefined && (!Number.isSafeInteger(lastId)
              || lastId <= 0 || beforeId !== undefined && lastId >= beforeId)) throw new Error('Invalid message cursor')
          // A notification target is a scroll destination, never a list filter.
          // Load the contiguous history from newest through the target page.
          if (append || targetId === undefined || lastId === undefined || lastId <= targetId || batch.length < 20) break
          beforeId = lastId
        }
        const r = { data: { list: rows, total: responseTotal } }
        if (seq !== loadSeq.current) return // 已有更新的请求在途，丢弃旧响应
        setTotal(r.data.total)
        cursor.current = r.data.list.at(-1)?.id
        const nextList = append
          ? [...loadedMessages.current, ...r.data.list.filter((m: Msg) => !loadedMessages.current.some((old) => old.id === m.id))]
          : r.data.list
        if (!append) cursorStartTotal.current = r.data.total
        const totalChangedDuringCursor = append && cursorStartTotal.current !== null && r.data.total !== cursorStartTotal.current
        loadedMessages.current = nextList
        setHasMore(lastBatchSize === 20 && (totalChangedDuringCursor || nextList.length < r.data.total))
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
    return () => { loadSeq.current += 1; loadAbort.current?.abort() }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId, targetId])

  useLayoutEffect(() => {
    if (!active || loading || targetId === undefined || locatedScope.current === messageScope.current.generation) return
    const element = listRef.current?.querySelector<HTMLElement>(`[data-message-id="${targetId}"]`)
    if (element) {
      const owner = listRef.current?.closest?.('.message-scroll-area')
      if (owner) {
        const rect = element.getBoundingClientRect()
        owner.scrollTop += rect.top - owner.getBoundingClientRect().top
          - Math.max(0, (owner.clientHeight - rect.height) / 2)
      } else element.scrollIntoView({ block: 'center' })
      locatedScope.current = messageScope.current.generation
    }
  }, [active, loading, list, targetId])

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
          const response = await http.get<ApiResponses['GET /projects/{id}/messages']>(`/projects/${projectId}/messages`, {
            params: { page: 1, pageSize: 20, beforeId },
            signal: controller.signal, timeout: 10000, quietNetworkError: true,
          } as QuietRequestConfig)
          if (stopped || controller.signal.aborted || generation !== messageScope.current.generation
              || requestLoad !== loadSeq.current || requestMutations !== messageMutations.current) return
          // senderType is a plain string in the API schema; the frontend narrows it to the two known values.
          const batch = response.data.list as Msg[]
          if (!Array.isArray(batch) || !Number.isSafeInteger(response.data.total) || response.data.total < 0
              || batch.some((item, index) => !Number.isSafeInteger(item?.id) || item.id <= 0
                || (index > 0 && item.id >= batch[index - 1].id) || (beforeId !== undefined && item.id >= beforeId))) {
            throw new Error('Invalid message window')
          }
          if (beforeId === undefined) nextTotal = response.data.total
          messages.push(...batch)
          const lastId = batch.at(-1)?.id
          if (lastId === undefined || batch.length < 20 || oldestId === undefined
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
          const owner = root.closest('.message-scroll-area') ?? root.closest('.layout-content') ?? document.scrollingElement
          const viewportTop = owner?.getBoundingClientRect?.().top ?? 80
          if (elements[0]?.getBoundingClientRect().top < viewportTop) {
            const ids = new Set(nextList.map((message) => message.id))
            const element = elements.find((item) => ids.has(Number(item.dataset.messageId)) && item.getBoundingClientRect().bottom > viewportTop)
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
        void http.post<ApiResponses['POST /messages/read']>('/messages/read', { ids }).then(async () => {
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
    const draftImages = images
    if (!canWrite || (!text && !draftImages.length) || sendInFlight.current || listLoading.current && loadedMessages.current.length === 0) return
    const generation = messageScope.current.generation
    sendInFlight.current = true
    setSending(true)
    try {
      let body: FormData | { content: string } = { content: text }
      if (draftImages.length) {
        const form = new FormData()
        form.append('content', text)
        draftImages.forEach(file => form.append('images', file, file.name))
        body = form
      }
      const response = await http.post<Msg>(`/projects/${projectId}/messages`, body)
      if (!mounted.current || generation !== messageScope.current.generation) return
      setContent((current) => current === draft ? '' : current)
      setImages(current => current.filter(file => !draftImages.includes(file)))
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
      const r = await http.get<ApiResponses['GET /messages/{id}/reads']>(`/messages/${id}/reads`)
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
    await http.delete<ApiResponses['DELETE /messages/{id}']>(`/messages/${id}`)
    messageMutations.current += 1
    loadedMessages.current = loadedMessages.current.filter((m) => m.id !== id)
    setList(loadedMessages.current)
    setTotal((current) => Math.max(0, current - 1))
    setSyncRetry((value) => value + 1)
  }

  return (
    <div className="message-panel">
      <div className="section-heading">
        <div>
          <h2>协作留言（{total}）</h2>
          <Typography.Text type="secondary">已读回执仅统计项目负责人和关联供应商的全部启用账号。</Typography.Text>
        </div>
      </div>

      {syncError && (
        <div role="status" style={{ marginBottom: 12 }}>
          <Typography.Text type="error">留言同步失败，正在重试</Typography.Text>
          <Button type="text" size="small" onClick={() => setSyncRetry((value) => value + 1)}>重试同步</Button>
        </div>
      )}

      {canWrite && (
        <MessageComposer
          content={content}
          images={images}
          sending={sending}
          loading={loading}
          listLength={list.length}
          onContentChange={(value) => setContent(Array.from(value).slice(0, 4000).join(''))}
          onImagesChange={setImages}
          onSend={send}
          pasteMessageImages={pasteMessageImages}
          ImageComposer={MessageImageComposer}
        />
      )}

      <MessageList
        list={list}
        total={total}
        hasMore={hasMore}
        loading={loading}
        loadError={loadError}
        appendError={appendError}
        targetId={targetId}
        userId={user?.id}
        watermarkEmployeeNo={user?.employeeNo}
        watermarkRealName={user?.realName}
        canDelete={canDelete}
        receiptRefresh={receiptRefresh}
        listRef={listRef}
        fmtTime={fmtTime}
        onRetryInitial={() => load(1, false)}
        onLoadMore={() => load(page + 1, true)}
        onReceiveReceipt={receiveReceipt}
        onOpenReceipt={openReceipt}
        onRemove={remove}
        MessageImages={MessageImages}
        ReceiptBody={ReceiptBody}
      />

      <MessageReceiptDrawer
        receipt={receipt}
        receiptRequest={receiptRequest}
        fmtTime={fmtTime}
        onCancel={() => {
          receiptSeq.current += 1
          setReceipt(null)
          setReceiptRequest(null)
        }}
        onRetry={openReceipt}
      />
    </div>
  )
}
