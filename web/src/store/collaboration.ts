import { create } from 'zustand'
import http, { getRealtimeAccessToken, type QuietRequestConfig } from '../api/client'
import { useAuth } from './auth'
import { startProjectRealtime } from '../services/projectRealtime'
import type { ApiResponses } from '../api/types'

type CollaborationSummaryResponse = ApiResponses['GET /collaboration/summary']
type CollaborationNotificationPageResponse = ApiResponses['GET /collaboration/notifications']
type CollaborationNotificationResponse = CollaborationNotificationPageResponse['list'][number]

export type CollaborationType = CollaborationNotificationResponse['type'] & ('FILE' | 'MESSAGE' | 'PROJECT')
export type CollaborationSummary = CollaborationSummaryResponse
export type CollaborationNotification = Omit<CollaborationNotificationResponse, 'type'> & {
  type: CollaborationType
}
export type CollaborationNotificationPage = Omit<CollaborationNotificationPageResponse, 'list'> & {
  list: CollaborationNotification[]
}

export type CollaborationRefreshStatus = 'idle' | 'loading' | 'ready' | 'error'

interface CollaborationState extends CollaborationSummary {
  status: CollaborationRefreshStatus
  realtimeStatus: 'connecting' | 'connected' | 'reconnecting' | 'disconnected'
  messageRevisions: Record<number, number>
  receiptRevisions: Record<number, number>
  activityRevisions: Record<number, number>
  reconnectRevision: number
  refresh: () => Promise<void>
  reset: () => void
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null
}

function isNonNegativeInteger(value: unknown): value is number {
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= 0
}

function isPositiveInteger(value: unknown): value is number {
  return typeof value === 'number' && Number.isSafeInteger(value) && value > 0
}

function isNullablePositiveInteger(value: unknown): value is number | null {
  return value === null || isPositiveInteger(value)
}

export function parseCollaborationSummary(value: unknown): CollaborationSummary {
  if (!isRecord(value)
    || !isNonNegativeInteger(value.unreadCount)
    || !isNonNegativeInteger(value.latestId)
    || typeof value.revision !== 'string') {
    throw new Error('协作通知概览响应格式错误')
  }
  return {
    unreadCount: value.unreadCount,
    latestId: value.latestId,
    revision: value.revision,
  }
}

function parseCollaborationNotification(value: unknown): CollaborationNotification | null {
  if (!isRecord(value)) return null
  const type = value.type
  if (!isPositiveInteger(value.id)
    || (type !== 'FILE' && type !== 'MESSAGE' && type !== 'PROJECT')
    || typeof value.action !== 'string'
    || !isPositiveInteger(value.projectId)
    || typeof value.projectName !== 'string'
    || typeof value.projectGroupName !== 'string'
    || typeof value.actorName !== 'string'
    || typeof value.title !== 'string'
    || (value.summary !== null && typeof value.summary !== 'string')
    || typeof value.occurredAt !== 'string'
    || !isNullablePositiveInteger(value.targetId)
    || typeof value.targetAvailable !== 'boolean'
    || typeof value.read !== 'boolean') return null
  return {
    id: value.id,
    type,
    action: value.action,
    projectId: value.projectId,
    projectName: value.projectName,
    projectGroupName: value.projectGroupName,
    actorName: value.actorName,
    title: value.title,
    summary: value.summary,
    occurredAt: value.occurredAt,
    targetId: value.targetId,
    targetAvailable: value.targetAvailable,
    read: value.read,
  }
}

export function parseCollaborationNotificationPage(value: unknown): CollaborationNotificationPage {
  if (!isRecord(value)
    || !Array.isArray(value.list)
    || !isNonNegativeInteger(value.total)
    || !isPositiveInteger(value.page)
    || !isPositiveInteger(value.pageSize)
    || !isNonNegativeInteger(value.unreadCount)) {
    throw new Error('协作通知列表响应格式错误')
  }
  const parsed = value.list.map(parseCollaborationNotification)
  if (parsed.some((item) => item === null)) throw new Error('协作通知条目格式错误')
  return {
    list: parsed.filter((item): item is CollaborationNotification => item !== null),
    total: value.total,
    page: value.page,
    pageSize: value.pageSize,
    unreadCount: value.unreadCount,
  }
}

const EMPTY_SUMMARY: CollaborationSummary = { unreadCount: 0, latestId: 0, revision: '' }

export const useCollaboration = create<CollaborationState>((set) => ({
  ...EMPTY_SUMMARY,
  status: 'idle',
  realtimeStatus: 'disconnected',
  messageRevisions: {},
  receiptRevisions: {},
  activityRevisions: {},
  reconnectRevision: 0,
  refresh: async () => {
    if (activePoller) await activePoller.refresh()
    else await refreshCollaborationSummary()
  },
  reset: () => set({ ...EMPTY_SUMMARY, status: 'idle', realtimeStatus: 'disconnected', messageRevisions: {}, receiptRevisions: {}, activityRevisions: {}, reconnectRevision: 0 }),
}))

interface PollSession {
  key: string | null
  enabled: boolean
}

function currentPollSession(): PollSession {
  const { generation, menus, permissions, token, user } = useAuth.getState()
  const enabled = Boolean(
    token
    && user
    && menus.includes('project:list')
    && permissions.includes('project:list'),
  )
  return { key: enabled && user ? `${generation}:${user.id}` : null, enabled }
}

function authWakeSignature(): string {
  const { generation, menus, permissions, token, user } = useAuth.getState()
  return JSON.stringify([generation, user?.id ?? null, Boolean(token), [...menus].sort(), [...permissions].sort()])
}

/** Token rotations and unrelated auth fields must not collapse the poll backoff or create extra requests. */
function subscribeAuthSession(wake: () => void): () => void {
  let previous = authWakeSignature()
  return useAuth.subscribe(() => {
    const next = authWakeSignature()
    if (next === previous) return
    previous = next
    wake()
  })
}

export async function refreshCollaborationSummary(signal?: AbortSignal, expectedKey = currentPollSession().key): Promise<boolean> {
  if (!expectedKey) {
    useCollaboration.getState().reset()
    return false
  }
  const before = currentPollSession()
  if (!before.enabled || before.key !== expectedKey) return false
  useCollaboration.setState((state) => ({ status: state.status === 'idle' ? 'loading' : state.status }))
  const config: QuietRequestConfig = { signal, quietNetworkError: true, timeout: 10_000 }
  try {
    const response = await http.get<ApiResponses['GET /collaboration/summary']>('/collaboration/summary', config)
    const summary = parseCollaborationSummary(response.data)
    if (signal?.aborted) return false
    const after = currentPollSession()
    if (!after.enabled || after.key !== expectedKey) return false
    useCollaboration.setState({ ...summary, status: 'ready' })
    return true
  } catch {
    if (signal?.aborted) return false
    const after = currentPollSession()
    if (after.enabled && after.key === expectedKey) useCollaboration.setState({ status: 'error' })
    return false
  }
}

export interface CollaborationPollerOptions {
  getSession: () => PollSession
  poll: (sessionKey: string, signal: AbortSignal) => Promise<boolean>
  reset: () => void
  subscribeSession: (wake: () => void) => () => void
  subscribeResume: (wake: () => void) => () => void
  isPaused: () => boolean
  setTimer?: (callback: () => void, delay: number) => ReturnType<typeof setTimeout>
  clearTimer?: (timer: ReturnType<typeof setTimeout>) => void
  intervalMs?: number | (() => number)
  maximumBackoffMs?: number
}

export interface CollaborationPoller {
  start: () => void
  stop: () => void
  refresh: () => Promise<void>
}

/** 可注入时钟和会话的轮询器，保证单飞、会话隔离、取消与失败退避。 */
export function createCollaborationPoller(options: CollaborationPollerOptions): CollaborationPoller {
  const intervalMs = options.intervalMs ?? 5_000
  const maximumBackoffMs = options.maximumBackoffMs ?? 30_000
  const setTimer = options.setTimer ?? setTimeout
  const clearTimer = options.clearTimer ?? clearTimeout
  let active = false
  let sessionKey: string | null = null
  let timer: ReturnType<typeof setTimeout> | null = null
  let request: { controller: AbortController; promise: Promise<void>; key: string } | null = null
  let consecutiveFailures = 0
  let pendingImmediate = false
  let unsubscribeSession: (() => void) | null = null
  let unsubscribeResume: (() => void) | null = null

  const clearScheduled = () => {
    if (timer !== null) clearTimer(timer)
    timer = null
  }

  const syncSession = () => {
    const snapshot = options.getSession()
    if (snapshot.key !== sessionKey) {
      sessionKey = snapshot.key
      consecutiveFailures = 0
      pendingImmediate = false
      clearScheduled()
      request?.controller.abort()
      options.reset()
    } else if (!snapshot.enabled) {
      request?.controller.abort()
    }
    return snapshot
  }

  const schedule = () => {
    clearScheduled()
    if (!active) return
    const snapshot = syncSession()
    if (!snapshot.enabled || !snapshot.key || options.isPaused()) return
    const exponent = Math.max(0, consecutiveFailures - 1)
    const baseInterval = typeof intervalMs === 'function' ? intervalMs() : intervalMs
    const delay = consecutiveFailures === 0
      ? baseInterval
      : Math.min(baseInterval * (2 ** exponent), maximumBackoffMs)
    timer = setTimer(() => {
      timer = null
      void run(false)
    }, delay)
  }

  const run = (immediate: boolean): Promise<void> => {
    if (!active) return Promise.resolve()
    const snapshot = syncSession()
    if (!snapshot.enabled || !snapshot.key || options.isPaused()) return Promise.resolve()
    clearScheduled()
    if (request) {
      pendingImmediate = pendingImmediate || immediate
      return request.promise
    }

    const controller = new AbortController()
    const key = snapshot.key
    const pending = options.poll(key, controller.signal)
      .then((succeeded) => {
        const current = options.getSession()
        if (!active || current.key !== key || !current.enabled || controller.signal.aborted) return
        consecutiveFailures = succeeded ? 0 : consecutiveFailures + 1
      })
      .catch(() => {
        const current = options.getSession()
        if (active && current.key === key && current.enabled && !controller.signal.aborted) consecutiveFailures += 1
      })
      .finally(() => {
        if (request?.controller !== controller) return
        request = null
        const shouldRunNow = pendingImmediate
        pendingImmediate = false
        if (shouldRunNow) void run(true)
        else schedule()
      })
    request = { controller, promise: pending, key }
    return pending
  }

  const wake = () => {
    if (!active) return
    clearScheduled()
    void run(true)
  }

  return {
    start: () => {
      if (active) return
      active = true
      unsubscribeSession = options.subscribeSession(wake)
      unsubscribeResume = options.subscribeResume(wake)
      syncSession()
      wake()
    },
    stop: () => {
      active = false
      clearScheduled()
      request?.controller.abort()
      request = null
      pendingImmediate = false
      unsubscribeSession?.()
      unsubscribeResume?.()
      unsubscribeSession = null
      unsubscribeResume = null
    },
    refresh: () => run(true),
  }
}

let activePoller: CollaborationPoller | null = null
let stopRealtime: (() => void) | null = null

function subscribeBrowserResume(wake: () => void): () => void {
  const onVisibility = () => { if (document.visibilityState === 'visible') wake() }
  document.addEventListener('visibilitychange', onVisibility)
  window.addEventListener('focus', wake)
  window.addEventListener('online', wake)
  return () => {
    document.removeEventListener('visibilitychange', onVisibility)
    window.removeEventListener('focus', wake)
    window.removeEventListener('online', wake)
  }
}

/**
 * 推送触发的概览刷新先合并、再随机延后：一次变更会推给所有相关在线用户，
 * 合并连发的推送并错开各自的查询时间，避免所有人在同一瞬间一起查询。
 */
export const EVENT_REFRESH_DELAY_MS = 500
export const EVENT_REFRESH_JITTER_MS = 1500

/** AdminLayout 挂载期间唯一的全局协作概览轮询。 */
export function startCollaborationPolling(): () => void {
  activePoller?.stop()
  stopRealtime?.()
  const poller = createCollaborationPoller({
    getSession: currentPollSession,
    poll: (key, signal) => refreshCollaborationSummary(signal, key),
    reset: () => useCollaboration.getState().reset(),
    subscribeSession: subscribeAuthSession,
    subscribeResume: subscribeBrowserResume,
    isPaused: () => document.visibilityState !== 'visible' || navigator.onLine === false,
    intervalMs: () => useCollaboration.getState().realtimeStatus === 'connected' ? 60_000 : 5_000,
  })
  activePoller = poller
  poller.start()
  let eventRefresh: ReturnType<typeof setTimeout> | null = null
  const refreshAfterEvent = () => {
    if (eventRefresh !== null) return
    eventRefresh = setTimeout(() => {
      eventRefresh = null
      void poller.refresh()
    }, EVENT_REFRESH_DELAY_MS + Math.floor(Math.random() * EVENT_REFRESH_JITTER_MS))
  }
  const stop = startProjectRealtime({
    getSession: () => { const session = currentPollSession(); return { ...session, key: session.key ?? '' } },
    getAccessToken: getRealtimeAccessToken,
    subscribeSession: subscribeAuthSession,
    onStatus: (realtimeStatus) => {
      useCollaboration.setState({ realtimeStatus })
      if (realtimeStatus !== 'connected') void poller.refresh()
    },
    onReady: () => {
      useCollaboration.setState((state) => ({ reconnectRevision: state.reconnectRevision + 1 }))
      void poller.refresh()
    },
    onEvent: ({ projectId, kind }) => {
      if (kind === 'messages') useCollaboration.setState((state) => ({ messageRevisions: {
        ...state.messageRevisions, [projectId]: (state.messageRevisions[projectId] ?? 0) + 1,
      } }))
      if (kind === 'receipts') useCollaboration.setState((state) => ({ receiptRevisions: {
        ...state.receiptRevisions, [projectId]: (state.receiptRevisions[projectId] ?? 0) + 1,
      } }))
      if (kind === 'activity' || kind === 'project') useCollaboration.setState((state) => ({ activityRevisions: {
        ...state.activityRevisions, [projectId]: (state.activityRevisions[projectId] ?? 0) + 1,
      } }))
      if (kind !== 'receipts') refreshAfterEvent()
    },
  })
  stopRealtime = stop
  return () => {
    if (eventRefresh !== null) {
      clearTimeout(eventRefresh)
      eventRefresh = null
    }
    if (activePoller === poller) activePoller = null
    poller.stop()
    if (stopRealtime === stop) stopRealtime = null
    stop()
    useCollaboration.getState().reset()
  }
}
