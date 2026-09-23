import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({
  get: vi.fn(),
  realtimeOptions: null as null | {
    onStatus: (status: 'connecting' | 'connected' | 'reconnecting' | 'disconnected') => void
    onReady: () => void
    onEvent: (event: { projectId: number; kind: 'messages' | 'receipts' | 'activity' }) => void
  },
  realtimeStop: vi.fn(),
}))

vi.mock('../../../api/client', () => ({ default: { get: mocks.get } }))
vi.mock('../../../services/projectRealtime', () => ({
  startProjectRealtime: vi.fn((options) => {
    mocks.realtimeOptions = options
    return mocks.realtimeStop
  }),
}))

import { useAuth } from '../../../store/auth'
import {
  createCollaborationPoller,
  refreshCollaborationSummary,
  startCollaborationPolling,
  useCollaboration,
} from '../../../store/collaboration'

function deferred<T>() {
  let resolve!: (value: T | PromiseLike<T>) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((ok, fail) => { resolve = ok; reject = fail })
  return { promise, resolve, reject }
}

async function settle() {
  for (let index = 0; index < 8; index += 1) await Promise.resolve()
}

function authenticated(id = 7, generation = 1) {
  useAuth.setState({
    token: `token-${id}`,
    user: {
      id,
      employeeNo: `E${id}`,
      realName: `User ${id}`,
      email: '',
      userType: 'INTERNAL',
      supplierId: null,
      isSystemAdmin: false,
    },
    permissions: ['project:list'],
    menus: ['project:list'],
    generation,
    booted: true,
  })
}

describe('collaboration migration', () => {
  beforeEach(() => {
    localStorage.clear()
    authenticated()
    useCollaboration.getState().reset()
    mocks.get.mockReset().mockResolvedValue({ data: { unreadCount: 0, latestId: 1, revision: 'server-1' } })
    mocks.realtimeOptions = null
    mocks.realtimeStop.mockReset()
  })

  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  it('realtime events invalidate only the affected project and reconnect forces catch-up', async () => {
    vi.useFakeTimers()
    const stop = startCollaborationPolling()
    await settle()
    const live = mocks.realtimeOptions!
    live.onStatus('connected')
    live.onReady()
    await settle()
    expect(useCollaboration.getState().reconnectRevision).toBe(1)
    const afterReady = mocks.get.mock.calls.length
    expect(afterReady).toBeGreaterThan(0)

    live.onEvent({ projectId: 11, kind: 'messages' })
    const requestsAfterMessage = mocks.get.mock.calls.length
    live.onEvent({ projectId: 12, kind: 'receipts' })
    await settle()
    expect(useCollaboration.getState().messageRevisions).toEqual({ 11: 1 })
    expect(useCollaboration.getState().receiptRevisions).toEqual({ 12: 1 })
    expect(mocks.get).toHaveBeenCalledTimes(requestsAfterMessage)

    live.onStatus('reconnecting')
    await settle()
    expect(mocks.get.mock.calls.length).toBeGreaterThan(afterReady)
    live.onStatus('connected')
    live.onReady()
    await settle()
    expect(useCollaboration.getState().reconnectRevision).toBe(2)
    stop()
    expect(mocks.realtimeStop).toHaveBeenCalledOnce()
    expect(vi.getTimerCount()).toBe(0)
    expect(useCollaboration.getState().realtimeStatus).toBe('disconnected')
    await settle()
  })

  it('summary refresh cannot restore stale account data after cancellation', async () => {
    const response = deferred<{ data: { unreadCount: number; latestId: number; revision: string } }>()
    mocks.get.mockReturnValueOnce(response.promise)
    const controller = new AbortController()
    const pending = refreshCollaborationSummary(controller.signal, '1:7')
    expect(useCollaboration.getState().status).toBe('loading')
    controller.abort()
    useCollaboration.getState().reset()
    response.resolve({ data: { unreadCount: 8, latestId: 12, revision: '12:8' } })
    await expect(pending).resolves.toBe(false)
    expect(useCollaboration.getState()).toMatchObject({ status: 'idle', revision: '', unreadCount: 0 })
  })

  it('poller is single-flight and an account switch aborts before polling the new scope', async () => {
    let session = { key: '1:7', enabled: true }
    let wakeSession: () => void = () => undefined
    const requests: Array<{
      key: string
      signal: AbortSignal
      result: ReturnType<typeof deferred<boolean>>
    }> = []
    const poller = createCollaborationPoller({
      getSession: () => session,
      poll: (key, signal) => {
        const result = deferred<boolean>()
        requests.push({ key, signal, result })
        return result.promise
      },
      reset: () => undefined,
      subscribeSession: (wake) => { wakeSession = wake; return () => undefined },
      subscribeResume: () => () => undefined,
      isPaused: () => false,
      setTimer: () => 1 as unknown as ReturnType<typeof setTimeout>,
      clearTimer: () => undefined,
    })
    poller.start()
    await settle()
    expect(requests).toHaveLength(1)
    const sameRequest = poller.refresh()
    void poller.refresh()
    expect(requests).toHaveLength(1)
    session = { key: '2:9', enabled: true }
    wakeSession()
    expect(requests[0].signal.aborted).toBe(true)
    requests[0].result.resolve(false)
    await sameRequest
    await settle()
    expect(requests).toHaveLength(2)
    expect(requests[1].key).toBe('2:9')
    poller.stop()
    expect(requests[1].signal.aborted).toBe(true)
    requests[1].result.resolve(false)
  })

  it('poll failures back off and stop cancels the scheduled continuation', async () => {
    const timers: Array<{ callback: () => void; delay: number; cleared: boolean }> = []
    const poller = createCollaborationPoller({
      getSession: () => ({ key: '1:7', enabled: true }),
      poll: async () => false,
      reset: () => undefined,
      subscribeSession: () => () => undefined,
      subscribeResume: () => () => undefined,
      isPaused: () => false,
      intervalMs: 5_000,
      maximumBackoffMs: 60_000,
      setTimer: (callback, delay) => {
        const timer = { callback, delay, cleared: false }
        timers.push(timer)
        return timer as unknown as ReturnType<typeof setTimeout>
      },
      clearTimer: (timer) => { (timer as unknown as { cleared: boolean }).cleared = true },
    })
    poller.start()
    await settle()
    expect(timers[0].delay).toBe(5_000)
    timers[0].callback()
    await settle()
    expect(timers[1].delay).toBe(10_000)
    poller.stop()
    expect(timers[1].cleared).toBe(true)
  })

  it('bursts of realtime pushes collapse into one jittered summary refresh', async () => {
    vi.useFakeTimers()
    vi.spyOn(Math, 'random').mockReturnValue(0.5)
    const stop = startCollaborationPolling()
    await settle()
    const baseline = mocks.get.mock.calls.length
    const live = mocks.realtimeOptions!
    live.onEvent({ projectId: 2, kind: 'messages' })
    live.onEvent({ projectId: 2, kind: 'activity' })
    live.onEvent({ projectId: 3, kind: 'messages' })
    expect(mocks.get).toHaveBeenCalledTimes(baseline)
    await vi.advanceTimersByTimeAsync(1_249)
    expect(mocks.get).toHaveBeenCalledTimes(baseline)
    await vi.advanceTimersByTimeAsync(1)
    expect(mocks.get).toHaveBeenCalledTimes(baseline + 1)
    stop()
  })
})
