import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { Button, Typography } from '@arco-design/web-react'
import { createSessionQueryScope, queryClient } from '../api/queryClient'
import { useAuth } from '../store/auth'
import { fmtTime } from '../api/types'
import { MessageImageComposer, MessageImages, pasteMessageImages } from './MessageImages'
import { ReceiptBody } from './MessageReceipts'
import { MessageComposer } from './message-panel/MessageComposer'
import { MessageList } from './message-panel/MessageList'
import { MessageReceiptDrawer } from './message-panel/MessageReceiptDrawer'
import {
  cancelMessageQueries,
  isMessageAccessError,
  messageKeys,
  refreshReadCounts,
  startMessageSend,
  useMessageCommands,
  useMessageFeed,
  useMessageReceiptDetail,
  useMessageReceiptPolling,
  useMessageSync,
} from './message-panel/useMessageQueries'

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

export default function MessagePanel({
  projectId,
  projectStatus,
  onRead,
  targetId,
  revision = '',
  active = true,
  receiptRevision = '',
  realtimeConnected = false,
}: Props) {
  const hasPerm = useAuth(state => state.hasPerm)
  const user = useAuth(state => state.user)
  const generation = useAuth(state => state.generation)
  const permissions = useAuth(state => state.permissions)
  const menus = useAuth(state => state.menus)
  const mustChangePassword = useAuth(state => state.mustChangePassword)
  const scope = useMemo(
    () => createSessionQueryScope({ user, generation, permissions, menus, mustChangePassword }),
    [user, generation, permissions, menus, mustChangePassword],
  )
  const [content, setContent] = useState('')
  const [images, setImages] = useState<File[]>([])
  const [sending, setSending] = useState(false)
  const [selectedReceiptId, setSelectedReceiptId] = useState<number | null>(null)
  const listRef = useRef<HTMLDivElement>(null)
  const locatedScope = useRef('')
  const markingRead = useRef(new Set<number>())
  const sendInFlight = useRef(false)
  const scrollAnchor = useRef<{ id: number; top: number; owner: Element } | null>(null)

  const feed = useMessageFeed({ scope, projectId, targetId, revision })
  const {
    key: feedKey,
    query: feedQuery,
    list: feedList,
    total: feedTotal,
    seenRevision,
    applyReadCounts,
    markReadLocally,
    addConfirmedMessage,
    removeConfirmedMessage,
    replaceFromSync,
  } = feed
  const feedIdentity = JSON.stringify(feedKey)
  const currentFeedIdentity = useRef(feedIdentity)
  const draftScopeKey = JSON.stringify([scope, projectId])
  const draftScope = useRef(draftScopeKey)

  const canWrite = hasPerm('message:create') && projectStatus !== 'COMPLETED' && projectStatus !== 'TERMINATED'
  const canDelete = hasPerm('message:delete_any') && projectStatus !== 'COMPLETED' && projectStatus !== 'TERMINATED'
  const loading = feedQuery.isPending || feedQuery.isFetchingNextPage
  const loadError = feedQuery.isError && !feedQuery.data
  const appendError = feedQuery.isFetchNextPageError

  useLayoutEffect(() => {
    currentFeedIdentity.current = feedIdentity
  }, [feedIdentity])

  useEffect(() => {
    if (draftScope.current === draftScopeKey) return
    draftScope.current = draftScopeKey
    setContent('')
    setImages([])
  }, [draftScopeKey])

  useEffect(() => {
    markingRead.current.clear()
    return () => {
      if (currentFeedIdentity.current === feedIdentity) currentFeedIdentity.current = ''
      void queryClient.cancelQueries({ queryKey: messageKeys.readCountsRoot(feedKey) }, { silent: true })
    }
  }, [feedIdentity, feedKey])

  const sync = useMessageSync({
    scope,
    projectId,
    targetId,
    revision,
    seenRevision,
    active,
    feedReady: feedQuery.isSuccess,
    feedFetching: feedQuery.isFetching,
    oldestId: feedList.at(-1)?.id,
  })

  useEffect(() => {
    if (!sync.data) return
    const root = listRef.current
    if (root && typeof document !== 'undefined') {
      const elements = [...root.querySelectorAll<HTMLElement>('[data-message-id]')]
      const owner = root.closest('.message-scroll-area') ?? root.closest('.layout-content') ?? document.scrollingElement
      const viewportTop = owner?.getBoundingClientRect?.().top ?? 80
      if (elements[0]?.getBoundingClientRect().top < viewportTop) {
        const nextIds = new Set(sync.data.messages.map(message => message.id))
        const element = elements.find(item => nextIds.has(Number(item.dataset.messageId)) && item.getBoundingClientRect().bottom > viewportTop)
        if (element && owner) {
          scrollAnchor.current = {
            id: Number(element.dataset.messageId),
            top: element.getBoundingClientRect().top,
            owner,
          }
        }
      }
    }
    replaceFromSync(sync.data)
    // The selected drawer is local UI state invalidated by the authoritative sync snapshot.
    // oxlint-disable-next-line react/set-state-in-effect
    setSelectedReceiptId(current => current !== null && !sync.data.messages.some(message => message.id === current)
      ? null
      : current)
  }, [sync.data, sync.dataUpdatedAt, replaceFromSync])

  useLayoutEffect(() => {
    if (!active || loading || targetId === undefined || locatedScope.current === feedIdentity) return
    const element = listRef.current?.querySelector<HTMLElement>(`[data-message-id="${targetId}"]`)
    if (!element) return
    const owner = listRef.current?.closest?.('.message-scroll-area')
    if (owner) {
      const rect = element.getBoundingClientRect()
      owner.scrollTop += rect.top - owner.getBoundingClientRect().top
        - Math.max(0, (owner.clientHeight - rect.height) / 2)
    } else {
      element.scrollIntoView({ block: 'center' })
    }
    locatedScope.current = feedIdentity
  }, [active, loading, feedList, feedIdentity, targetId])

  useLayoutEffect(() => {
    const anchor = scrollAnchor.current
    scrollAnchor.current = null
    if (!anchor) return
    const element = listRef.current?.querySelector<HTMLElement>(`[data-message-id="${anchor.id}"]`)
    if (element) anchor.owner.scrollTop += element.getBoundingClientRect().top - anchor.top
  }, [feedList])

  const receiveReceipt = useCallback((id: number, readCount: number, totalCount: number) => {
    applyReadCounts([{ id, readCount, totalCount }])
  }, [applyReadCounts])

  const receiptPoll = useMessageReceiptPolling({
    scope,
    projectId,
    active,
    receiptRevision,
    realtimeConnected,
    list: feedList,
    listRef,
    onCounts: applyReadCounts,
  })

  const receiptDetail = useMessageReceiptDetail({
    scope,
    projectId,
    id: selectedReceiptId,
    refreshVersion: receiptPoll.dataUpdatedAt,
  })
  const accessRevoked = isMessageAccessError(feedQuery.error)
    || isMessageAccessError(sync.error)
    || isMessageAccessError(receiptPoll.error)
  const visibleMessages = useMemo(() => {
    if (accessRevoked) return []
    const counts = new Map(receiptPoll.data?.map(item => [item.id, item]))
    return counts.size ? feedList.map(message => {
      const count = counts.get(message.id)
      return count ? { ...message, readCount: count.readCount, totalCount: count.totalCount } : message
    }) : feedList
  }, [accessRevoked, feedList, receiptPoll.data])
  const visibleTotal = accessRevoked ? 0 : feedTotal

  useEffect(() => {
    if (selectedReceiptId === null || !receiptDetail.data) return
    applyReadCounts([{
      id: selectedReceiptId,
      readCount: receiptDetail.data.readers.length,
      totalCount: receiptDetail.data.readers.length + receiptDetail.data.unread.length,
    }])
  }, [selectedReceiptId, receiptDetail.data, receiptDetail.dataUpdatedAt, applyReadCounts])

  const commands = useMessageCommands()

  useEffect(() => {
    const root = listRef.current
    if (!root || typeof IntersectionObserver === 'undefined') return
    const observer = new IntersectionObserver(
      entries => {
        const ids = entries
          .filter(entry => entry.isIntersecting && entry.intersectionRatio >= 0.6)
          .map(entry => Number((entry.target as HTMLElement).closest<HTMLElement>('[data-message-id]')?.dataset.messageId))
          .filter(id => Number.isFinite(id) && !markingRead.current.has(id))
        if (!ids.length) return
        const requestFeedKey = feedKey
        const requestIdentity = feedIdentity
        ids.forEach(id => markingRead.current.add(id))
        void commands.markRead.mutateAsync(ids).then(async () => {
          markReadLocally(ids)
          if (currentFeedIdentity.current === requestIdentity) onRead?.()
          const counts = await refreshReadCounts(requestFeedKey, ids)
          applyReadCounts(counts)
        }).catch(() => {
          if (currentFeedIdentity.current === requestIdentity) ids.forEach(id => markingRead.current.delete(id))
        })
      },
      { threshold: 0.6 },
    )
    root.querySelectorAll<HTMLElement>('[data-unread="true"] .message-read-marker').forEach(node => observer.observe(node))
    return () => observer.disconnect()
  }, [visibleMessages, feedIdentity, feedKey, markReadLocally, applyReadCounts, commands.markRead, onRead])

  const send = async () => {
    const draft = content
    const text = draft.trim()
    const draftImages = images
    if (!canWrite || (!text && !draftImages.length) || sendInFlight.current || feedQuery.isPending) return
    const requestFeedKey = feedKey
    const addMessage = addConfirmedMessage
    const requestSyncRoot = sync.rootKey
    const requestDraftScope = draftScope.current
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
      const message = await commands.send.mutateAsync(startMessageSend(projectId, body))
      const syncWasFetching = queryClient.isFetching({ queryKey: requestSyncRoot }) > 0
      await cancelMessageQueries([requestFeedKey, requestSyncRoot])
      addMessage(message)
      if (draftScope.current === requestDraftScope) {
        setContent(current => current === draft ? '' : current)
        setImages(current => current.filter(file => !draftImages.includes(file)))
      }
      if (syncWasFetching) void queryClient.invalidateQueries({ queryKey: requestSyncRoot })
    } catch {
      // 请求层已显示错误；保留草稿，允许用户重试。
    } finally {
      sendInFlight.current = false
      setSending(false)
    }
  }

  const remove = useCallback(async (id: number) => {
    const requestFeedKey = feedKey
    const removeMessage = removeConfirmedMessage
    const requestSyncRoot = sync.rootKey
    try {
      await commands.remove.mutateAsync(id)
      const syncWasFetching = queryClient.isFetching({ queryKey: requestSyncRoot }) > 0
      await cancelMessageQueries([requestFeedKey, requestSyncRoot])
      removeMessage(id)
      setSelectedReceiptId(current => current === id ? null : current)
      if (syncWasFetching) void queryClient.invalidateQueries({ queryKey: requestSyncRoot })
    } catch {
      // 请求层已显示错误；确认框收到已处理的 Promise，避免全局 unhandledrejection。
    }
  }, [commands.remove, feedKey, removeConfirmedMessage, sync.rootKey])

  const receipt = !isMessageAccessError(receiptDetail.error) && selectedReceiptId !== null && receiptDetail.data
    ? { id: selectedReceiptId, readers: receiptDetail.data.readers, unread: receiptDetail.data.unread }
    : null
  const receiptRequest = selectedReceiptId !== null && !receipt
    ? { id: selectedReceiptId, loading: receiptDetail.isPending || receiptDetail.isFetching, error: receiptDetail.isError }
    : null

  return (
    <div className="message-panel">
      <div className="section-heading">
        <div>
          <h2>协作留言（{visibleTotal}）</h2>
          <Typography.Text type="secondary">已读回执仅统计项目负责人和关联供应商的全部启用账号。</Typography.Text>
        </div>
      </div>

      {sync.syncError && (
        <div role="status" style={{ marginBottom: 12 }}>
          <Typography.Text type="error">留言同步失败，正在重试</Typography.Text>
          <Button type="text" size="small" onClick={() => void sync.refetch({ cancelRefetch: true })}>重试同步</Button>
        </div>
      )}

      {targetId !== undefined && feed.targetLocateExhausted && !feedList.some(message => message.id === targetId) && (
        <Typography.Text type="secondary" role="status" className="message-locate-notice">
          未在最近加载的留言中找到目标，可能已删除；可继续加载更早留言。
        </Typography.Text>
      )}

      {canWrite && !accessRevoked && (
        <MessageComposer
          content={content}
          images={images}
          sending={sending}
          loading={loading}
          listLength={visibleMessages.length}
          onContentChange={value => setContent(Array.from(value).slice(0, 4000).join(''))}
          onImagesChange={setImages}
          onSend={() => void send()}
          pasteMessageImages={pasteMessageImages}
          ImageComposer={MessageImageComposer}
        />
      )}

      <MessageList
        list={visibleMessages}
        total={visibleTotal}
        hasMore={feedQuery.hasNextPage}
        loading={loading}
        loadError={loadError || accessRevoked}
        appendError={appendError}
        targetId={targetId}
        userId={user?.id}
        watermarkEmployeeNo={user?.employeeNo}
        watermarkRealName={user?.realName}
        canDelete={canDelete}
        receiptRefresh={receiptPoll.dataUpdatedAt}
        listRef={listRef}
        fmtTime={fmtTime}
        onRetryInitial={() => void feedQuery.refetch()}
        onLoadMore={() => feedQuery.fetchNextPage()}
        onReceiveReceipt={receiveReceipt}
        onOpenReceipt={setSelectedReceiptId}
        onRemove={remove}
        MessageImages={MessageImages}
        ReceiptBody={ReceiptBody}
      />

      <MessageReceiptDrawer
        receipt={receipt}
        receiptRequest={receiptRequest}
        fmtTime={fmtTime}
        onCancel={() => setSelectedReceiptId(null)}
        onRetry={() => void receiptDetail.refetch()}
      />
    </div>
  )
}
