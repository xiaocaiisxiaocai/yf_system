import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'

export type ProjectRealtimeKind = 'messages' | 'receipts' | 'activity'
export type ProjectRealtimeStatus = 'connecting' | 'connected' | 'reconnecting' | 'disconnected'

export interface ProjectRealtimeEvent {
  projectId: number
  kind: ProjectRealtimeKind
}

export interface ProjectRealtimeSession {
  /** Opaque user/session generation key. It must not contain an access token. */
  key: string
  enabled: boolean
}

export interface ProjectRealtimeOptions {
  getSession: () => ProjectRealtimeSession
  getAccessToken: () => Promise<string>
  subscribeSession: (wake: () => void) => () => void
  onEvent: (event: ProjectRealtimeEvent) => void
  onReady: () => void | Promise<void>
  onStatus: (status: ProjectRealtimeStatus) => void
  subscribeResume?: (wake: () => void) => () => void
}

export interface ProjectRealtimeConnection {
  start: () => Promise<void>
  stop: () => Promise<void>
  onProjectChanged: (handler: (payload: unknown) => void) => void
  onReconnecting: (handler: () => void) => void
  onReconnected: (handler: () => void) => void
  onClose: (handler: () => void) => void
}

export type ProjectRealtimeConnectionFactory = (
  accessTokenFactory: () => Promise<string>,
) => ProjectRealtimeConnection

export interface CreateProjectRealtimeOptions extends ProjectRealtimeOptions {
  createConnection: ProjectRealtimeConnectionFactory
  setTimer?: (callback: () => void, delay: number) => ReturnType<typeof setTimeout>
  clearTimer?: (timer: ReturnType<typeof setTimeout>) => void
  initialRetryDelayMs?: number
  maximumRetryDelayMs?: number
}

export interface ProjectRealtimeController {
  start: () => void
  stop: () => void
  retry: () => void
}

export function parseProjectRealtimeEvent(payload: unknown): ProjectRealtimeEvent | null {
  if (typeof payload !== 'object' || payload === null) return null
  const value = payload as Record<string, unknown>
  if (typeof value.projectId !== 'number'
    || !Number.isSafeInteger(value.projectId)
    || value.projectId <= 0
    || (value.kind !== 'messages' && value.kind !== 'receipts' && value.kind !== 'activity')) return null
  return { projectId: value.projectId, kind: value.kind }
}

function ignoreRejected(result: void | Promise<void>): void {
  if (result && typeof result.then === 'function') void result.catch(() => undefined)
}

/**
 * Owns one SignalR connection for the current opaque session key. All callbacks
 * are generation-checked so a stopped or replaced connection cannot affect the
 * next account.
 */
export function createProjectRealtime(options: CreateProjectRealtimeOptions): ProjectRealtimeController {
  const setTimer = options.setTimer ?? setTimeout
  const clearTimer = options.clearTimer ?? clearTimeout
  const initialRetryDelayMs = options.initialRetryDelayMs ?? 1_000
  const maximumRetryDelayMs = options.maximumRetryDelayMs ?? 30_000
  let active = false
  let generation = 0
  let sessionKey: string | null = null
  let connection: ProjectRealtimeConnection | null = null
  let phase: ProjectRealtimeStatus = 'disconnected'
  let retryFailures = 0
  let retryTimer: ReturnType<typeof setTimeout> | null = null
  let startAttempt: { generation: number; promise: Promise<void> } | null = null
  let unsubscribeSession: (() => void) | null = null
  let unsubscribeResume: (() => void) | null = null

  const sessionMatches = (expectedGeneration: number, expectedKey: string) => {
    if (!active || generation !== expectedGeneration || sessionKey !== expectedKey) return false
    const current = options.getSession()
    return current.enabled && current.key === expectedKey
  }

  const reportStatus = (status: ProjectRealtimeStatus) => {
    phase = status
    try {
      options.onStatus(status)
    } catch {
      // Observer failures must not break the connection lifecycle.
    }
  }

  const reportReady = (expectedGeneration: number, expectedKey: string) => {
    if (!sessionMatches(expectedGeneration, expectedKey)) return
    try {
      ignoreRejected(options.onReady())
    } catch {
      // The authoritative refresh can fail independently of the live channel.
    }
  }

  const clearRetry = () => {
    if (retryTimer !== null) clearTimer(retryTimer)
    retryTimer = null
  }

  const stopConnection = (target: ProjectRealtimeConnection | null) => {
    if (!target) return
    void target.stop().catch(() => undefined)
  }

  let connectCurrent = () => {}

  const scheduleRetry = (expectedGeneration: number, expectedKey: string) => {
    if (!sessionMatches(expectedGeneration, expectedKey) || retryTimer !== null) return
    retryFailures += 1
    reportStatus('disconnected')
    const delay = Math.min(
      initialRetryDelayMs * (2 ** Math.max(0, retryFailures - 1)),
      maximumRetryDelayMs,
    )
    retryTimer = setTimer(() => {
      retryTimer = null
      connectCurrent()
    }, delay)
  }

  const buildConnection = (expectedGeneration: number, expectedKey: string) => {
    const accessTokenFactory = async () => {
      const token = await options.getAccessToken()
      if (!sessionMatches(expectedGeneration, expectedKey)) throw new Error('实时协作会话已变化')
      return token
    }
    const created = options.createConnection(accessTokenFactory)
    created.onProjectChanged((payload) => {
      if (!sessionMatches(expectedGeneration, expectedKey)) return
      const event = parseProjectRealtimeEvent(payload)
      if (!event) return
      try {
        options.onEvent(event)
      } catch {
        // A consumer failure must not unregister the SignalR handler.
      }
    })
    created.onReconnecting(() => {
      if (!sessionMatches(expectedGeneration, expectedKey)) return
      clearRetry()
      reportStatus('reconnecting')
    })
    created.onReconnected(() => {
      if (!sessionMatches(expectedGeneration, expectedKey)) return
      clearRetry()
      retryFailures = 0
      reportStatus('connected')
      reportReady(expectedGeneration, expectedKey)
    })
    created.onClose(() => {
      if (!sessionMatches(expectedGeneration, expectedKey)) return
      scheduleRetry(expectedGeneration, expectedKey)
    })
    return created
  }

  connectCurrent = () => {
    if (!active || !sessionKey || phase === 'connected' || phase === 'reconnecting') return
    const expectedGeneration = generation
    const expectedKey = sessionKey
    if (!sessionMatches(expectedGeneration, expectedKey)) return
    if (startAttempt?.generation === expectedGeneration) return

    clearRetry()
    try {
      connection ??= buildConnection(expectedGeneration, expectedKey)
    } catch {
      scheduleRetry(expectedGeneration, expectedKey)
      return
    }
    const target = connection
    reportStatus('connecting')
    let starting: Promise<void>
    try {
      starting = target.start()
    } catch {
      scheduleRetry(expectedGeneration, expectedKey)
      return
    }
    const pending = starting
      .then(() => {
        if (!sessionMatches(expectedGeneration, expectedKey) || connection !== target) {
          stopConnection(target)
          return
        }
        retryFailures = 0
        reportStatus('connected')
        reportReady(expectedGeneration, expectedKey)
      })
      .catch(() => {
        if (connection === target) scheduleRetry(expectedGeneration, expectedKey)
      })
      .finally(() => {
        if (startAttempt?.promise === pending) startAttempt = null
      })
    startAttempt = { generation: expectedGeneration, promise: pending }
  }

  const synchronize = () => {
    if (!active) return
    const snapshot = options.getSession()
    const nextKey = snapshot.enabled && snapshot.key ? snapshot.key : null
    if (nextKey === sessionKey) {
      if (nextKey && phase === 'disconnected') connectCurrent()
      return
    }

    generation += 1
    clearRetry()
    retryFailures = 0
    const previous = connection
    connection = null
    startAttempt = null
    sessionKey = nextKey
    reportStatus('disconnected')
    stopConnection(previous)
    if (nextKey) connectCurrent()
  }

  const retry = () => {
    if (!active) return
    synchronize()
    if (!sessionKey || phase === 'connected' || phase === 'reconnecting') return
    clearRetry()
    connectCurrent()
  }

  return {
    start: () => {
      if (active) return
      active = true
      unsubscribeSession = options.subscribeSession(synchronize)
      unsubscribeResume = options.subscribeResume?.(retry) ?? null
      synchronize()
    },
    stop: () => {
      if (!active) return
      active = false
      generation += 1
      clearRetry()
      retryFailures = 0
      sessionKey = null
      startAttempt = null
      const previous = connection
      connection = null
      reportStatus('disconnected')
      stopConnection(previous)
      unsubscribeSession?.()
      unsubscribeResume?.()
      unsubscribeSession = null
      unsubscribeResume = null
    },
    retry,
  }
}

function createSignalRConnection(accessTokenFactory: () => Promise<string>): ProjectRealtimeConnection {
  const connection = new HubConnectionBuilder()
    .withUrl('/api/v1/collaboration/live', { accessTokenFactory })
    .withAutomaticReconnect()
    .configureLogging(LogLevel.None)
    .build()
  return {
    start: () => connection.start(),
    stop: () => connection.stop(),
    onProjectChanged: (handler) => connection.on('ProjectChanged', handler),
    onReconnecting: (handler) => connection.onreconnecting(handler),
    onReconnected: (handler) => connection.onreconnected(handler),
    onClose: (handler) => connection.onclose(handler),
  }
}

function subscribeBrowserResume(wake: () => void): () => void {
  window.addEventListener('online', wake)
  return () => window.removeEventListener('online', wake)
}

/** Starts the production SignalR channel and returns its complete cleanup function. */
export function startProjectRealtime(options: ProjectRealtimeOptions): () => void {
  const controller = createProjectRealtime({
    ...options,
    createConnection: createSignalRConnection,
    subscribeResume: options.subscribeResume ?? subscribeBrowserResume,
  })
  controller.start()
  return controller.stop
}
