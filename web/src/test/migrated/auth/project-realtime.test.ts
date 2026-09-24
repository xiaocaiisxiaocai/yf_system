import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const signalR = vi.hoisted(() => ({
  built: null as FakeConnection | null,
  observed: {} as Record<string, unknown>,
}))

vi.mock('@microsoft/signalr', () => ({
  LogLevel: { None: 'none' },
  HubConnectionBuilder: class {
    withUrl(url: string, options: unknown) { signalR.observed.url = url; signalR.observed.urlOptions = options; return this }
    withAutomaticReconnect() { signalR.observed.reconnect = true; return this }
    configureLogging(level: unknown) { signalR.observed.logLevel = level; return this }
    build() { return signalR.built }
  },
}))

import {
  createProjectRealtime,
  startProjectRealtime,
  type ProjectRealtimeConnection,
  type ProjectRealtimeEvent,
} from '../../../services/projectRealtime'

function deferred<T>() {
  let resolve!: (value: T | PromiseLike<T>) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((ok, fail) => { resolve = ok; reject = fail })
  return { promise, resolve, reject }
}

async function settle() {
  await Promise.resolve()
  await Promise.resolve()
  await new Promise((resolve) => setTimeout(resolve, 0))
}

class FakeConnection implements ProjectRealtimeConnection {
  readonly accessTokenFactory: () => Promise<string>
  readonly starts: Array<() => Promise<void>>
  startCalls = 0
  stopCalls = 0
  private projectChanged?: (payload: unknown) => void
  private reconnectingHandler?: () => void
  private reconnectedHandler?: () => void
  private closeHandler?: () => void

  constructor(accessTokenFactory: () => Promise<string>, starts: Array<() => Promise<void>> = []) {
    this.accessTokenFactory = accessTokenFactory
    this.starts = [...starts]
  }

  start() { this.startCalls += 1; return this.starts.shift()?.() ?? Promise.resolve() }
  stop() { this.stopCalls += 1; return Promise.resolve() }
  onProjectChanged(handler: (payload: unknown) => void) { this.projectChanged = handler }
  onReconnecting(handler: () => void) { this.reconnectingHandler = handler }
  onReconnected(handler: () => void) { this.reconnectedHandler = handler }
  onClose(handler: () => void) { this.closeHandler = handler }
  emit(payload: unknown) { this.projectChanged?.(payload) }
  reconnecting() { this.reconnectingHandler?.() }
  reconnected() { this.reconnectedHandler?.() }
  close() { this.closeHandler?.() }
}

function createTimers() {
  const timers: Array<{ callback: () => void; delay: number; cleared: boolean }> = []
  return {
    timers,
    setTimer(callback: () => void, delay: number) {
      const timer = { callback, delay, cleared: false }
      timers.push(timer)
      return timer as unknown as ReturnType<typeof setTimeout>
    },
    clearTimer(timer: ReturnType<typeof setTimeout>) {
      ;(timer as unknown as { cleared: boolean }).cleared = true
    },
  }
}

describe('project realtime migration', () => {
  beforeEach(() => {
    signalR.observed = {}
    signalR.built = null
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('initial connection accepts only validated ProjectChanged events and reports connected before ready', async () => {
    const events: ProjectRealtimeEvent[] = []
    const actions: string[] = []
    let connection!: FakeConnection
    const controller = createProjectRealtime({
      getSession: () => ({ key: '7:1', enabled: true }),
      getAccessToken: async () => 'token',
      subscribeSession: () => () => undefined,
      createConnection: (factory) => (connection = new FakeConnection(factory)),
      onEvent: (event) => events.push(event),
      onStatus: (status) => actions.push(`status:${status}`),
      onReady: () => { actions.push('ready') },
    })

    controller.start()
    await settle()
    expect(actions.slice(-2)).toEqual(['status:connected', 'ready'])
    connection.emit({ projectId: 3, kind: 'messages' })
    connection.emit({ projectId: 4, kind: 'receipts' })
    connection.emit({ projectId: 5, kind: 'activity' })
    connection.emit({ projectId: 6, kind: 'project' })
    for (const invalid of [
      { projectId: 0, kind: 'messages' },
      { projectId: 1.5, kind: 'messages' },
      { projectId: Number.MAX_SAFE_INTEGER + 1, kind: 'messages' },
      { projectId: 6, kind: 'unknown' },
      null,
    ]) connection.emit(invalid)
    expect(events).toEqual([
      { projectId: 3, kind: 'messages' },
      { projectId: 4, kind: 'receipts' },
      { projectId: 5, kind: 'activity' },
      { projectId: 6, kind: 'project' },
    ])
    controller.stop()
  })

  it('automatic reconnect reports ready again after the connected status', async () => {
    const actions: string[] = []
    let connection!: FakeConnection
    const controller = createProjectRealtime({
      getSession: () => ({ key: '7:1', enabled: true }),
      getAccessToken: async () => 'token',
      subscribeSession: () => () => undefined,
      createConnection: (factory) => (connection = new FakeConnection(factory)),
      onEvent: () => undefined,
      onStatus: (status) => actions.push(`status:${status}`),
      onReady: () => { actions.push('ready') },
    })
    controller.start()
    await settle()
    actions.length = 0
    connection.reconnecting()
    connection.reconnected()
    expect(actions).toEqual(['status:reconnecting', 'status:connected', 'ready'])
    controller.stop()
  })

  it('initial failure retries with backoff and network recovery starts one immediate attempt', async () => {
    const clock = createTimers()
    const secondStart = deferred<void>()
    let ready = 0
    let connection!: FakeConnection
    const controller = createProjectRealtime({
      getSession: () => ({ key: '7:1', enabled: true }),
      getAccessToken: async () => 'token',
      subscribeSession: () => () => undefined,
      createConnection: (factory) => (connection = new FakeConnection(factory, [
        () => Promise.reject(new Error('offline')),
        () => secondStart.promise,
      ])),
      onEvent: () => undefined,
      onStatus: () => undefined,
      onReady: () => { ready += 1 },
      setTimer: clock.setTimer,
      clearTimer: clock.clearTimer,
      initialRetryDelayMs: 1_000,
      maximumRetryDelayMs: 8_000,
    })
    controller.start()
    await settle()
    expect(connection.startCalls).toBe(1)
    expect(clock.timers[0].delay).toBe(1_000)
    controller.retry()
    controller.retry()
    expect(clock.timers[0].cleared).toBe(true)
    expect(connection.startCalls).toBe(2)
    secondStart.resolve()
    await settle()
    expect(ready).toBe(1)
    controller.stop()
  })

  it('an explicit connection 401 forces token refresh on the next attempt', async () => {
    const clock = createTimers()
    const forced: boolean[] = []
    let connection!: FakeConnection
    const controller = createProjectRealtime({
      getSession: () => ({ key: '7:1', enabled: true }),
      getAccessToken: async (forceRefresh = false) => { forced.push(forceRefresh); return forceRefresh ? 'refreshed' : 'cached' },
      subscribeSession: () => () => undefined,
      createConnection: (factory) => (connection = new FakeConnection(factory, [
        async () => { await factory(); throw { statusCode: 401 } },
        async () => { await factory() },
      ])),
      onEvent: () => undefined,
      onStatus: () => undefined,
      onReady: () => undefined,
      setTimer: clock.setTimer,
      clearTimer: clock.clearTimer,
    })
    controller.start()
    await settle()
    expect(forced).toEqual([false])
    clock.timers[0].callback()
    await settle()
    expect(forced).toEqual([false, true])
    expect(connection.startCalls).toBe(2)
    controller.stop()
  })

  it('account switch and logout reject stale token promises and late hub events', async () => {
    const firstToken = deferred<string>()
    let session = { key: '7:1', enabled: true }
    let wakeSession: () => void = () => undefined
    let tokenCalls = 0
    let ready = 0
    const events: ProjectRealtimeEvent[] = []
    const connections: FakeConnection[] = []
    const controller = createProjectRealtime({
      getSession: () => session,
      getAccessToken: () => ++tokenCalls === 1 ? firstToken.promise : Promise.resolve('token-b'),
      subscribeSession: (wake) => { wakeSession = wake; return () => undefined },
      createConnection: (factory) => {
        const connection = new FakeConnection(factory, [() => factory().then(() => undefined)])
        connections.push(connection)
        return connection
      },
      onEvent: (event) => events.push(event),
      onStatus: () => undefined,
      onReady: () => { ready += 1 },
    })
    controller.start()
    await Promise.resolve()
    session = { key: '9:2', enabled: true }
    wakeSession()
    await settle()
    expect(connections).toHaveLength(2)
    expect(connections[0].stopCalls).toBe(1)
    expect(ready).toBe(1)
    firstToken.resolve('token-a')
    await settle()
    connections[0].emit({ projectId: 3, kind: 'messages' })
    expect(ready).toBe(1)
    expect(events).toEqual([])
    session = { key: '', enabled: false }
    wakeSession()
    connections[1].emit({ projectId: 4, kind: 'activity' })
    expect(connections[1].stopCalls).toBe(1)
    expect(events).toEqual([])
    controller.stop()
  })

  it('stop clears retries, unsubscribes observers, and makes late callbacks inert', async () => {
    const clock = createTimers()
    let sessionUnsubscribed = 0
    let resumeUnsubscribed = 0
    const statuses: string[] = []
    let connection!: FakeConnection
    const controller = createProjectRealtime({
      getSession: () => ({ key: '7:1', enabled: true }),
      getAccessToken: async () => 'token',
      subscribeSession: () => () => { sessionUnsubscribed += 1 },
      subscribeResume: () => () => { resumeUnsubscribed += 1 },
      createConnection: (factory) => (connection = new FakeConnection(factory, [() => Promise.reject(new Error('offline'))])),
      onEvent: () => undefined,
      onStatus: (status) => statuses.push(status),
      onReady: () => undefined,
      setTimer: clock.setTimer,
      clearTimer: clock.clearTimer,
    })
    controller.start()
    await settle()
    controller.stop()
    const statusCount = statuses.length
    expect(clock.timers[0].cleared).toBe(true)
    expect(connection.stopCalls).toBe(1)
    expect(sessionUnsubscribed).toBe(1)
    expect(resumeUnsubscribed).toBe(1)
    clock.timers[0].callback()
    connection.reconnected()
    connection.emit({ projectId: 2, kind: 'messages' })
    await settle()
    expect(connection.startCalls).toBe(1)
    expect(statuses).toHaveLength(statusCount)
  })

  it('production connection uses the hub URL, token factory, automatic reconnect, and disabled logging', async () => {
    const built = new FakeConnection(async () => 'unused')
    signalR.built = built
    const add = vi.spyOn(window, 'addEventListener')
    const remove = vi.spyOn(window, 'removeEventListener')
    const addDocument = vi.spyOn(document, 'addEventListener')
    const removeDocument = vi.spyOn(document, 'removeEventListener')
    const stop = startProjectRealtime({
      getSession: () => ({ key: '7:1', enabled: true }),
      getAccessToken: async () => 'fresh-token',
      subscribeSession: () => () => undefined,
      onEvent: () => undefined,
      onStatus: () => undefined,
      onReady: () => undefined,
    })
    await settle()
    expect(signalR.observed).toMatchObject({
      url: '/api/v1/collaboration/live',
      reconnect: true,
      logLevel: 'none',
    })
    const options = signalR.observed.urlOptions as { accessTokenFactory: () => Promise<string> }
    expect(await options.accessTokenFactory()).toBe('fresh-token')
    expect(add).toHaveBeenCalledWith('online', expect.any(Function))
    expect(addDocument).toHaveBeenCalledWith('visibilitychange', expect.any(Function))
    stop()
    expect(remove).toHaveBeenCalledWith('online', expect.any(Function))
    expect(removeDocument).toHaveBeenCalledWith('visibilitychange', expect.any(Function))
  })
})
