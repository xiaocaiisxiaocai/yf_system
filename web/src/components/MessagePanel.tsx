import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { Button, Tooltip, Typography } from '@arco-design/web-react'
import { IconQuestionCircle } from '@arco-design/web-react/icon'
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
  type MessageSendBody,
  useMessageCommands,
  useMessageFeed,
  useMessageReceiptDetail,
  useMessageReceiptPolling,
  useMessageSync,
} from './message-panel/useMessageQueries'

/** randomUUID 只在 HTTPS/localhost 可用；HTTP 部署时退回 getRandomValues（不要求安全上下文）生成 v4 UUID。 */
function newClientMessageId() {
  if (typeof globalThis.crypto?.randomUUID === 'function') return globalThis.crypto.randomUUID()
  const bytes = globalThis.crypto.getRandomValues(new Uint8Array(16))
  bytes[6] = (bytes[6] & 0x0f) | 0x40
  bytes[8] = (bytes[8] & 0x3f) | 0x80
  const hex = Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join('')
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`
}

function draftImageIdentity(files: readonly File[]) {
  return files.map(file => `${file.name}\0${file.size}\0${file.lastModified}`).join('\n')
}

/** 同一份草稿（修剪后的文字 + 图片身份）重试时复用编号；内容一变就换新编号。 */
function clientMessageIdFor(
  slot: { current: { clientMessageId: string; text: string; imageIdentity: string } | null },
  text: string,
  files: readonly File[],
) {
  const imageIdentity = draftImageIdentity(files)
  const current = slot.current
  if (current && current.text === text && current.imageIdentity === imageIdentity) return current.clientMessageId
  const clientMessageId = newClientMessageId()
  slot.current = { clientMessageId, text, imageIdentity }
  return clientMessageId
}

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
  const sentDraft = useRef<{ clientMessageId: string; text: string; imageIdentity: string } | null>(null)
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

  const canWrite = hasPerm('message:create') && projectStatus !== 'DRAFT' && projectStatus !== 'COMPLETED' && projectStatus !== 'TERMINATED'
  const signsMessages = user?.userType === 'SUPPLIER'
  // 签名每条留言都要重新输入，发送成功后清空，不记住上一次的姓名。
  const [signerName, setSignerName] = useState('')
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
    sentDraft.current = null
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
    // 隐藏的标签页或面板不标记已读；不依赖隐藏面板的高度恰好为 0。
    if (!active || !root || typeof IntersectionObserver === 'undefined') return
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
  }, [active, visibleMessages, feedIdentity, feedKey, markReadLocally, applyReadCounts, commands.markRead, onRead])

  const send = async () => {
    const draft = content
    const text = draft.trim()
    const draftImages = images
    if (!canWrite || (!text && !draftImages.length) || sendInFlight.current || feedQuery.isPending) return
    const requestFeedKey = feedKey
    const addMessage = addConfirmedMessage
    const requestSyncRoot = sync.rootKey
    const requestDraftScope = draftScope.current
    const signature = signsMessages ? signerName.trim() : ''
    // 签名也是留言内容的一部分：换了签名就换新的 clientMessageId，避免与上次失败的请求冲突。
    const clientMessageId = clientMessageIdFor(sentDraft, `${signature}\u0000${text}`, draftImages)
    sendInFlight.current = true
    setSending(true)
    try {
      let body: FormData | MessageSendBody = { content: text, clientMessageId, signerName: signature || undefined }
      if (draftImages.length) {
        const form = new FormData()
        form.append('content', text)
        form.append('clientMessageId', clientMessageId)
        if (signature) form.append('signerName', signature)
        draftImages.forEach(file => form.append('images', file, file.name))
        body = form
      }
      const message = await commands.send.mutateAsync(startMessageSend(projectId, body))
      const syncWasFetching = queryClient.isFetching({ queryKey: requestSyncRoot }) > 0
      await cancelMessageQueries([requestFeedKey, requestSyncRoot])
      addMessage(message)
      sentDraft.current = null
      if (draftScope.current === requestDraftScope) {
        setContent(current => current === draft ? '' : current)
        setSignerName(current => current.trim() === signature ? '' : current)
        setImages(current => current.filter(file => !draftImages.includes(file)))
      }
      if (syncWasFetching) void queryClient.invalidateQueries({ queryKey: requestSyncRoot })
    } catch {
      // 请求层已显示错误。保留草稿和同一 clientMessageId，失败（含 40901 内容冲突）不自动换号重发。
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
      <div className="message-panel-heading">
        <h2>协作留言（{visibleTotal}）</h2>
        <Tooltip content="已读回执仅统计项目负责人和关联供应商的全部启用账号。">
          <IconQuestionCircle className="message-panel-help" aria-label="已读回执统计范围" tabIndex={0} />
        </Tooltip>
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

      {projectStatus === 'DRAFT' && hasPerm('message:create') && (
        <Typography.Text type="secondary" role="status" className="message-locate-notice">项目尚未开始，暂不能留言</Typography.Text>
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
          signerName={signerName}
          onSignerNameChange={signsMessages ? (value => setSignerName(Array.from(value).slice(0, 32).join(''))) : undefined}
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
