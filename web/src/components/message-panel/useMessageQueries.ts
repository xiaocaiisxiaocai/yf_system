import { useCallback, useEffect, useMemo, useRef, useState, type RefObject } from 'react'
import {
  useInfiniteQuery,
  useMutation,
  useQuery,
  type InfiniteData,
  type QueryKey,
} from '@tanstack/react-query'
import http, { type QuietRequestConfig } from '../../api/client'
import { queryClient } from '../../api/queryClient'
import type { ApiResponses, Message } from '../../api/types'
import { loadReadCounts, type ReadCounts } from '../MessageReceipts'

const PAGE_SIZE = 20
const MAX_TARGET_LOCATE_PAGES = 10

export function isMessageAccessError(error: unknown) {
  const candidate = error as { response?: { status?: unknown }; status?: unknown } | null
  const status = candidate?.response?.status ?? candidate?.status
  return status === 401 || status === 403
}

export type SessionQueryScope = readonly [number | null, number, string]

interface MessagePageParam {
  page: number
  beforeId?: number
  locateTarget: boolean
  requestRevision: string
}

interface MessageWindow {
  messages: Message[]
  total: number
  cursor?: number
  lastBatchSize: number
  baselineTotal: number
  page: number
  seenRevision: string
  targetLocateExhausted: boolean
}

interface MessageSyncWindow {
  messages: Message[]
  total: number
  lastBatchSize: number
  revision: string
}

type MessageFeedData = InfiniteData<MessageWindow, MessagePageParam>
export type MessageFeedKey = readonly ['messages', 'feed', SessionQueryScope, number, number | null]

export const messageKeys = {
  feed: (scope: SessionQueryScope, projectId: number, targetId?: number) =>
    ['messages', 'feed', scope, projectId, targetId ?? null] as MessageFeedKey,
  syncRoot: (scope: SessionQueryScope, projectId: number, targetId?: number) =>
    ['messages', 'sync', scope, projectId, targetId ?? null] as const,
  sync: (scope: SessionQueryScope, projectId: number, targetId: number | undefined, revision: string, oldestId?: number) =>
    [...messageKeys.syncRoot(scope, projectId, targetId), revision, oldestId ?? null] as const,
  receiptPoll: (scope: SessionQueryScope, projectId: number, ids: readonly number[], receiptRevision: string, realtimeConnected: boolean) =>
    ['messages', 'receipt-poll', scope, projectId, ids, receiptRevision, realtimeConnected] as const,
  receiptDetail: (scope: SessionQueryScope, projectId: number, id: number | null, refreshVersion: number) =>
    ['messages', 'receipt-detail', scope, projectId, id, refreshVersion] as const,
  receiptBody: (scope: SessionQueryScope, id: number, refreshKey: number) =>
    ['messages', 'receipt-body', scope, id, refreshKey] as const,
  readCountsRoot: (feedKey: MessageFeedKey) => ['messages', 'read-counts', feedKey] as const,
  readCounts: (feedKey: MessageFeedKey, ids: readonly number[]) =>
    [...messageKeys.readCountsRoot(feedKey), ids] as const,
}

function validateMessages(batch: Message[], beforeId?: number) {
  if (!Array.isArray(batch) || batch.some((item, index) =>
    !Number.isSafeInteger(item?.id) || item.id <= 0
      || index > 0 && item.id >= batch[index - 1].id
      || beforeId !== undefined && item.id >= beforeId)) {
    throw new Error('Invalid message window')
  }
}

function flattenMessages(data?: MessageFeedData) {
  const seen = new Set<number>()
  const messages: Message[] = []
  data?.pages.forEach((page) => page.messages.forEach((message) => {
    if (seen.has(message.id)) return
    seen.add(message.id)
    messages.push(message)
  }))
  return messages
}

function updateFeedData(
  key: MessageFeedKey,
  update: (data: MessageFeedData) => MessageFeedData,
) {
  queryClient.setQueryData<MessageFeedData>(key, current => current ? update(current) : current)
}

function mapFeedMessages(
  key: MessageFeedKey,
  update: (message: Message) => Message,
) {
  updateFeedData(key, data => ({
    ...data,
    pages: data.pages.map(page => ({
      ...page,
      messages: page.messages.map(update),
    })),
  }))
}

export function useMessageFeed({
  scope,
  projectId,
  targetId,
  revision,
}: {
  scope: SessionQueryScope
  projectId: number
  targetId?: number
  revision: string
}) {
  const [scopeUserId, scopeGeneration, scopeGrantSignature] = scope
  const stableScope = useMemo<SessionQueryScope>(
    () => [scopeUserId, scopeGeneration, scopeGrantSignature],
    [scopeUserId, scopeGeneration, scopeGrantSignature],
  )
  const key = useMemo(
    () => messageKeys.feed(stableScope, projectId, targetId),
    [stableScope, projectId, targetId],
  )

  const query = useInfiniteQuery<MessageWindow, Error, MessageFeedData, MessageFeedKey, MessagePageParam>({
    queryKey: key,
    initialPageParam: { page: 1, locateTarget: true, requestRevision: revision },
    queryFn: async ({ pageParam, signal }) => {
      let beforeId = pageParam.beforeId
      const messages: Message[] = []
      let total = 0
      let lastBatchSize = 0
      let locatePages = 0
      let targetLocated = false
      for (;;) {
        signal.throwIfAborted()
        const response = await http.get<ApiResponses['GET /projects/{id}/messages']>(`/projects/${projectId}/messages`, {
          params: { page: pageParam.page, pageSize: PAGE_SIZE, beforeId },
          signal,
        })
        const batch = response.data.list as Message[]
        if (!Array.isArray(batch)) throw new Error('Invalid message window')
        if (!Number.isSafeInteger(response.data.total) || response.data.total < 0) throw new Error('Invalid message total')
        total = response.data.total
        messages.push(...batch)
        targetLocated ||= targetId !== undefined && batch.some(message => message.id === targetId)
        lastBatchSize = batch.length
        locatePages += 1
        const lastId = batch.at(-1)?.id
        if (pageParam.locateTarget && targetId !== undefined && lastId !== undefined
          && (!Number.isSafeInteger(lastId) || lastId <= 0 || beforeId !== undefined && lastId >= beforeId)) {
          throw new Error('Invalid message cursor')
        }
        if (!pageParam.locateTarget || targetId === undefined || targetLocated || lastId === undefined
          || lastId <= targetId || batch.length < PAGE_SIZE || locatePages >= MAX_TARGET_LOCATE_PAGES) break
        beforeId = lastId
      }
      return {
        messages,
        total,
        cursor: messages.at(-1)?.id,
        lastBatchSize,
        baselineTotal: total,
        page: pageParam.page,
        seenRevision: pageParam.requestRevision,
        targetLocateExhausted: pageParam.locateTarget && targetId !== undefined && !targetLocated,
      }
    },
    getNextPageParam: (lastPage, pages) => {
      const loaded = flattenMessages({ pages, pageParams: [] }).length
      const totalChanged = lastPage.total !== pages[0].baselineTotal
      if (lastPage.lastBatchSize !== PAGE_SIZE || !totalChanged && loaded >= lastPage.total) return undefined
      return {
        page: lastPage.page + 1,
        beforeId: lastPage.cursor,
        locateTarget: false,
        requestRevision: lastPage.seenRevision,
      }
    },
    retry: false,
    staleTime: 0,
    gcTime: 0,
    refetchOnMount: 'always',
    refetchOnWindowFocus: false,
  }, queryClient)

  const list = useMemo(() => flattenMessages(query.data), [query.data])
  const lastPage = query.data?.pages.at(-1)
  const total = lastPage?.total ?? query.data?.pages[0]?.total ?? 0
  const seenRevision = query.data?.pages[0]?.seenRevision ?? revision
  const targetLocateExhausted = query.data?.pages[0]?.targetLocateExhausted ?? false

  const applyReadCounts = useCallback((counts: ReadCounts[]) => {
    const byId = new Map(counts.map(item => [item.id, item]))
    mapFeedMessages(key, message => {
      const count = byId.get(message.id)
      return count && (count.readCount !== message.readCount || count.totalCount !== message.totalCount)
        ? { ...message, readCount: count.readCount, totalCount: count.totalCount }
        : message
    })
  }, [key])

  const markReadLocally = useCallback((ids: readonly number[]) => {
    const selected = new Set(ids)
    mapFeedMessages(key, message => selected.has(message.id) ? { ...message, readByMe: true } : message)
  }, [key])

  const addConfirmedMessage = useCallback((message: Message) => {
    let added = false
    updateFeedData(key, data => {
      const known = flattenMessages(data).some(item => item.id === message.id)
      if (known) return data
      added = true
      return {
        ...data,
        pages: data.pages.map((page, index) => ({
          ...page,
          total: page.total + 1,
          messages: index === 0 ? [message, ...page.messages].sort((a, b) => b.id - a.id) : page.messages,
        })),
      }
    })
    return added
  }, [key])

  const removeConfirmedMessage = useCallback((id: number) => {
    updateFeedData(key, data => {
      const exists = flattenMessages(data).some(message => message.id === id)
      if (!exists) return data
      return {
        ...data,
        pages: data.pages.map(page => ({
          ...page,
          total: Math.max(0, page.total - 1),
          messages: page.messages.filter(message => message.id !== id),
        })),
      }
    })
  }, [key])

  const replaceFromSync = useCallback((window: MessageSyncWindow) => {
    updateFeedData(key, current => {
      const known = new Map(flattenMessages(current).map(message => [message.id, message]))
      const messages = window.messages.map(message => {
        const previous = known.get(message.id)
        return previous ? {
          ...message,
          readByMe: message.readByMe || previous.readByMe,
          readCount: previous.readCount,
          totalCount: previous.totalCount,
        } : message
      })
      const firstParam = current.pageParams[0] ?? { page: 1, locateTarget: true, requestRevision: window.revision }
      return {
        pages: [{
          messages,
          total: window.total,
          cursor: messages.at(-1)?.id,
          lastBatchSize: window.lastBatchSize,
          baselineTotal: window.total,
          page: 1,
          seenRevision: window.revision,
          targetLocateExhausted: false,
        }],
        pageParams: [{ ...firstParam, requestRevision: window.revision }],
      }
    })
  }, [key])

  return {
    key,
    query,
    list,
    total,
    seenRevision,
    targetLocateExhausted,
    applyReadCounts,
    markReadLocally,
    addConfirmedMessage,
    removeConfirmedMessage,
    replaceFromSync,
  }
}

export function useMessageSync({
  scope,
  projectId,
  targetId,
  revision,
  seenRevision,
  active,
  feedReady,
  feedFetching,
  oldestId,
}: {
  scope: SessionQueryScope
  projectId: number
  targetId?: number
  revision: string
  seenRevision: string
  active: boolean
  feedReady: boolean
  feedFetching: boolean
  oldestId?: number
}) {
  const [scopeUserId, scopeGeneration, scopeGrantSignature] = scope
  const stableScope = useMemo<SessionQueryScope>(
    () => [scopeUserId, scopeGeneration, scopeGrantSignature],
    [scopeUserId, scopeGeneration, scopeGrantSignature],
  )
  const rootKey = useMemo(
    () => messageKeys.syncRoot(stableScope, projectId, targetId),
    [stableScope, projectId, targetId],
  )
  const key = useMemo(
    () => messageKeys.sync(stableScope, projectId, targetId, revision, oldestId),
    [stableScope, projectId, targetId, revision, oldestId],
  )
  const enabled = active && feedReady && !feedFetching && Boolean(revision) && revision !== seenRevision
  const query = useQuery<MessageSyncWindow>({
    queryKey: key,
    enabled,
    queryFn: async ({ signal }) => {
      const messages: Message[] = []
      let beforeId: number | undefined
      let total = 0
      let lastBatchSize = 0
      for (;;) {
        const response = await http.get<ApiResponses['GET /projects/{id}/messages']>(`/projects/${projectId}/messages`, {
          params: { page: 1, pageSize: PAGE_SIZE, beforeId },
          signal,
          timeout: 10000,
          quietNetworkError: true,
        } as QuietRequestConfig)
        const batch = response.data.list as Message[]
        validateMessages(batch, beforeId)
        if (!Number.isSafeInteger(response.data.total) || response.data.total < 0) throw new Error('Invalid message total')
        if (beforeId === undefined) total = response.data.total
        messages.push(...batch)
        lastBatchSize = batch.length
        const lastId = batch.at(-1)?.id
        if (lastId === undefined || batch.length < PAGE_SIZE || oldestId === undefined
          || lastId <= oldestId || messages.length >= total) break
        beforeId = lastId
      }
      return { messages, total, lastBatchSize, revision }
    },
    retry: false,
    refetchInterval: current => enabled && current.state.status === 'error'
      && !isMessageAccessError(current.state.error) ? 5000 : false,
    staleTime: Infinity,
    gcTime: 0,
    refetchOnWindowFocus: false,
  }, queryClient)
  return { ...query, rootKey, syncError: enabled && query.isError && !isMessageAccessError(query.error) }
}

function sameIds(left: readonly number[], right: readonly number[]) {
  return left.length === right.length && left.every((id, index) => id === right[index])
}

function visibleMessageIds(listRef: RefObject<HTMLDivElement | null>) {
  if (typeof document === 'undefined' || typeof window === 'undefined') return []
  const region = listRef.current?.closest?.('.message-scroll-area')?.getBoundingClientRect()
  const visibleTop = Math.max(0, region?.top ?? 0)
  const visibleBottom = Math.min(window.innerHeight, region?.bottom ?? window.innerHeight)
  return [...(listRef.current?.querySelectorAll<HTMLElement>('[data-message-id]') ?? [])]
    .filter(element => {
      const rect = element.getBoundingClientRect()
      return rect.width > 0 && rect.height > 0 && rect.bottom > visibleTop && rect.top < visibleBottom
    })
    .map(element => Number(element.dataset.messageId))
    .filter(Number.isSafeInteger)
    .slice(0, 500)
}

export function useMessageReceiptPolling({
  scope,
  projectId,
  active,
  receiptRevision,
  realtimeConnected,
  list,
  listRef,
  onCounts,
}: {
  scope: SessionQueryScope
  projectId: number
  active: boolean
  receiptRevision: string
  realtimeConnected: boolean
  list: readonly Message[]
  listRef: RefObject<HTMLDivElement | null>
  onCounts: (counts: ReadCounts[]) => void
}) {
  const [ids, setIds] = useState<number[]>([])
  const idsRef = useRef<number[]>([])
  const [scopeUserId, scopeGeneration, scopeGrantSignature] = scope
  const stableScope = useMemo<SessionQueryScope>(
    () => [scopeUserId, scopeGeneration, scopeGrantSignature],
    [scopeUserId, scopeGeneration, scopeGrantSignature],
  )
  const availableIds = useMemo(() => new Set(list.map(message => message.id)), [list])
  const effectiveIds = useMemo(
    () => active ? ids.filter(id => availableIds.has(id)) : [],
    [active, ids, availableIds],
  )
  const receiptQueryKey = useMemo(
    () => messageKeys.receiptPoll(stableScope, projectId, effectiveIds, receiptRevision, realtimeConnected),
    [stableScope, projectId, effectiveIds, receiptRevision, realtimeConnected],
  )
  const query = useQuery<ReadCounts[]>({
    queryKey: receiptQueryKey,
    enabled: active && effectiveIds.length > 0,
    queryFn: async ({ signal }) => {
      const response = await http.get<ApiResponses['GET /projects/{id}/message-receipts']>(`/projects/${projectId}/message-receipts`, {
        params: { ids: effectiveIds.join(',') },
        signal,
        timeout: 10000,
        quietNetworkError: true,
      } as QuietRequestConfig)
      if (!Array.isArray(response.data) || response.data.some((item: ReadCounts) =>
        !item || !effectiveIds.includes(item.id) || !Number.isInteger(item.readCount)
          || !Number.isInteger(item.totalCount) || item.readCount < 0 || item.totalCount < item.readCount)) {
        throw new Error('Invalid receipt counts')
      }
      return response.data
    },
    retry: false,
    staleTime: Infinity,
    gcTime: 0,
    refetchOnWindowFocus: false,
    refetchInterval: current => {
      if (!active || !effectiveIds.length || realtimeConnected) return false
      if (isMessageAccessError(current.state.error)) return false
      return Math.min(5000 * 2 ** current.state.fetchFailureCount, 60000)
    },
  }, queryClient)
  const refetchReceipts = query.refetch
  const receiptQueryKeyRef = useRef<QueryKey>(receiptQueryKey)
  const refetchReceiptsRef = useRef(refetchReceipts)
  useEffect(() => {
    receiptQueryKeyRef.current = receiptQueryKey
    refetchReceiptsRef.current = refetchReceipts
  }, [receiptQueryKey, refetchReceipts])

  const updateIds = useCallback((next: number[], wake: boolean) => {
    if (!sameIds(idsRef.current, next)) {
      idsRef.current = next
      setIds(next)
    } else if (wake && next.length) {
      void refetchReceiptsRef.current({ cancelRefetch: true })
    }
  }, [])

  const scanVisible = useCallback((wake: boolean) => {
    if (document.visibilityState !== 'visible' || typeof navigator !== 'undefined' && navigator.onLine === false) return
    updateIds(visibleMessageIds(listRef), wake)
  }, [listRef, updateIds])

  useEffect(() => {
    if (query.data) {
      onCounts(query.data)
    }
  }, [query.data, query.dataUpdatedAt, onCounts])

  useEffect(() => {
    if (!active || typeof IntersectionObserver === 'undefined') return
    const root = listRef.current
    if (!root) return
    const visible = new Set<number>()
    const orderedIds = () => [...root.querySelectorAll<HTMLElement>('[data-message-id]')]
      .map(element => Number(element.dataset.messageId))
      .filter(id => visible.has(id))
      .slice(0, 500)
    const observer = new IntersectionObserver((entries) => {
      for (const entry of entries) {
        const id = Number((entry.target as HTMLElement).dataset.messageId)
        if (!Number.isSafeInteger(id)) continue
        if (entry.isIntersecting) visible.add(id)
        else visible.delete(id)
      }
      updateIds(orderedIds(), true)
    }, { root, threshold: 0.01 })
    root.querySelectorAll<HTMLElement>('[data-message-id]').forEach(element => observer.observe(element))
    return () => observer.disconnect()
  }, [active, list, listRef, updateIds])

  useEffect(() => {
    if (active && typeof IntersectionObserver === 'undefined') scanVisible(false)
  }, [active, list, scanVisible])

  useEffect(() => {
    if (!active || typeof document === 'undefined' || typeof window === 'undefined') return
    let timer: ReturnType<typeof setTimeout> | undefined
    let scrollTimer: ReturnType<typeof setTimeout> | undefined
    const scheduleScan = () => {
      if (typeof IntersectionObserver !== 'undefined') return
      clearTimeout(timer)
      timer = setTimeout(() => {
        scanVisible(false)
        scheduleScan()
      }, 5000)
    }
    const wake = () => {
      if (document.visibilityState !== 'visible' || typeof navigator !== 'undefined' && navigator.onLine === false) {
        void queryClient.cancelQueries({ queryKey: receiptQueryKeyRef.current }, { silent: true })
        return
      }
      scanVisible(true)
    }
    const onScroll = () => {
      clearTimeout(scrollTimer)
      scrollTimer = setTimeout(() => scanVisible(true), 150)
    }
    const scrollArea = listRef.current
    scanVisible(false)
    scheduleScan()
    document.addEventListener('visibilitychange', wake)
    window.addEventListener('focus', wake)
    window.addEventListener('online', wake)
    window.addEventListener('offline', wake)
    if (typeof IntersectionObserver === 'undefined') scrollArea?.addEventListener('scroll', onScroll)
    return () => {
      clearTimeout(timer)
      clearTimeout(scrollTimer)
      document.removeEventListener('visibilitychange', wake)
      window.removeEventListener('focus', wake)
      window.removeEventListener('online', wake)
      window.removeEventListener('offline', wake)
      scrollArea?.removeEventListener('scroll', onScroll)
    }
  }, [active, listRef, scanVisible])

  return query
}

export function useMessageReceiptDetail({
  scope,
  projectId,
  id,
  refreshVersion,
}: {
  scope: SessionQueryScope
  projectId: number
  id: number | null
  refreshVersion: number
}) {
  return useQuery<ApiResponses['GET /messages/{id}/reads']>({
    queryKey: messageKeys.receiptDetail(scope, projectId, id, refreshVersion),
    enabled: id !== null,
    queryFn: async ({ signal }) => {
      const response = await http.get<ApiResponses['GET /messages/{id}/reads']>(`/messages/${id}/reads`, { signal })
      return response.data
    },
    retry: false,
    staleTime: 0,
    gcTime: 0,
    refetchOnWindowFocus: false,
  }, queryClient)
}

export function useMessageCommands() {
  const send = useMutation({
    mutationFn: async (request: Promise<{ data: Message }>) => (await request).data,
  }, queryClient)
  const remove = useMutation({
    mutationFn: async (id: number) => {
      await http.delete<ApiResponses['DELETE /messages/{id}']>(`/messages/${id}`)
      return id
    },
  }, queryClient)
  const markRead = useMutation({
    mutationFn: async (ids: number[]) => {
      await http.post<ApiResponses['POST /messages/read']>('/messages/read', { ids })
      return ids
    },
  }, queryClient)
  return { send, remove, markRead }
}

export function startMessageSend(projectId: number, body: FormData | { content: string }) {
  return http.post<Message>(`/projects/${projectId}/messages`, body)
}

export async function refreshReadCounts(feedKey: MessageFeedKey, ids: number[]) {
  return queryClient.fetchQuery({
    queryKey: messageKeys.readCounts(feedKey, ids),
    queryFn: ({ signal }) => loadReadCounts(ids, signal),
    staleTime: 0,
    gcTime: 0,
    retry: false,
  })
}

export function cancelMessageQueries(keys: readonly QueryKey[]) {
  return Promise.all(keys.map(queryKey => queryClient.cancelQueries({ queryKey }, { silent: true })))
}
