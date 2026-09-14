const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const vm = require('node:vm')
const ts = require('typescript')

function deferred() {
  let resolve
  let reject
  const promise = new Promise((ok, fail) => { resolve = ok; reject = fail })
  return { promise, resolve, reject }
}

function createStore(initializer) {
  let state
  const listeners = new Set()
  const setState = (update) => {
    const next = typeof update === 'function' ? update(state) : update
    state = { ...state, ...next }
    for (const listener of listeners) listener(state)
  }
  const getState = () => state
  state = initializer(setState, getState)
  const useStore = (selector = value => value) => selector(state)
  useStore.getState = getState
  useStore.setState = setState
  useStore.subscribe = listener => {
    listeners.add(listener)
    return () => listeners.delete(listener)
  }
  return useStore
}

function loadCollaboration({ http = { get: async () => ({ data: {} }) }, authState, realtimeStart = () => () => {}, globals = {} } = {}) {
  let currentAuth = authState || {
    generation: 1,
    token: 'fixture-token',
    user: { id: 7 },
    menus: ['project:list'],
    permissions: ['project:list'],
  }
  const authListeners = new Set()
  const useAuth = {
    getState: () => currentAuth,
    subscribe: listener => {
      authListeners.add(listener)
      return () => authListeners.delete(listener)
    },
  }
  const sourcePath = path.resolve(__dirname, '../src/store/collaboration.ts')
  const source = ts.transpileModule(fs.readFileSync(sourcePath, 'utf8'), {
    compilerOptions: {
      module: ts.ModuleKind.CommonJS,
      target: ts.ScriptTarget.ES2022,
      esModuleInterop: true,
    },
  }).outputText
  const exports = {}
  const context = {
    AbortController,
    console,
    exports,
    module: { exports },
    require: name => {
      if (name === 'zustand') return { create: createStore }
      if (name === '../api/client') return { __esModule: true, default: http }
      if (name === './auth') return { useAuth }
      if (name === '../services/projectRealtime') return { startProjectRealtime: realtimeStart }
      return require(name)
    },
    setTimeout,
    clearTimeout,
    ...globals,
  }
  vm.runInNewContext(source, context, { filename: sourcePath })
  return {
    collaboration: context.module.exports,
    setAuth(next) {
      currentAuth = next
      for (const listener of authListeners) listener(currentAuth)
    },
  }
}

function validNotification(overrides = {}) {
  return {
    id: 3,
    type: 'MESSAGE',
    action: 'SEND',
    projectId: 11,
    projectName: '协作项目',
    actorName: '张三',
    title: '新增留言',
    summary: '请核对附件',
    occurredAt: '2026-09-13T08:00:00Z',
    targetId: 29,
    targetAvailable: true,
    read: false,
    ...overrides,
  }
}

test('collaboration parsers accept the empty-summary sentinel and reject malformed contracts', () => {
  const { collaboration } = loadCollaboration()
  assert.deepEqual(
    { ...collaboration.parseCollaborationSummary({ unreadCount: 0, latestId: 0, revision: '0' }) },
    { unreadCount: 0, latestId: 0, revision: '0' },
  )
  assert.throws(
    () => collaboration.parseCollaborationSummary({ unreadCount: -1, latestId: 0, revision: '0' }),
    /概览响应格式错误/,
  )

  const page = collaboration.parseCollaborationNotificationPage({
    list: [validNotification()],
    total: 1,
    page: 1,
    pageSize: 20,
    unreadCount: 1,
  })
  assert.equal(page.list[0].targetId, 29)
  assert.throws(
    () => collaboration.parseCollaborationNotificationPage({
      list: [validNotification({ read: 'false' })], total: 1, page: 1, pageSize: 20, unreadCount: 1,
    }),
    /条目格式错误/,
  )
})

test('realtime events invalidate only the affected project and reconnect forces catch-up', async () => {
  let live
  let stopped = false
  let requests = 0
  const timers = new Map()
  let timerId = 0
  const { collaboration } = loadCollaboration({
    http: { get: async () => { requests++; return { data: { unreadCount: 0, latestId: 1, revision: 'server-1' } } } },
    realtimeStart: options => { live = options; return () => { stopped = true } },
    globals: {
      document: { visibilityState: 'visible', addEventListener() {}, removeEventListener() {} },
      window: { addEventListener() {}, removeEventListener() {} },
      navigator: { onLine: true },
      setTimeout: (callback, delay) => { const id = ++timerId; timers.set(id, { callback, delay }); return id },
      clearTimeout: id => timers.delete(id),
    },
  })
  const stop = collaboration.startCollaborationPolling()
  await new Promise(resolve => setImmediate(resolve))
  live.onStatus('connected')
  live.onReady()
  await new Promise(resolve => setImmediate(resolve))
  assert.equal(collaboration.useCollaboration.getState().reconnectRevision, 1)
  assert.ok([...timers.values()].some(timer => timer.delay === 60000), 'connected transport uses a slow reconciliation interval')
  live.onEvent({ projectId: 11, kind: 'messages' })
  await new Promise(resolve => setImmediate(resolve))
  const afterMessage = requests
  live.onEvent({ projectId: 12, kind: 'receipts' })
  await new Promise(resolve => setImmediate(resolve))
  assert.equal(collaboration.useCollaboration.getState().messageRevisions[11], 1)
  assert.equal(collaboration.useCollaboration.getState().messageRevisions[12], undefined)
  assert.equal(collaboration.useCollaboration.getState().receiptRevisions[12], 1)
  assert.equal(requests, afterMessage, 'receipt push does not refetch the global activity feed')
  live.onStatus('reconnecting')
  await new Promise(resolve => setImmediate(resolve))
  assert.ok([...timers.values()].some(timer => timer.delay === 5000), 'disconnected transport restores fallback polling')
  live.onStatus('connected')
  live.onReady()
  await new Promise(resolve => setImmediate(resolve))
  assert.equal(collaboration.useCollaboration.getState().reconnectRevision, 2)
  stop()
  assert.equal(stopped, true)
  assert.equal(timers.size, 0)
  assert.equal(collaboration.useCollaboration.getState().realtimeStatus, 'disconnected')
})

test('summary refresh cannot restore stale account data after cancellation', async () => {
  const response = deferred()
  const { collaboration } = loadCollaboration({ http: { get: async () => response.promise } })
  const controller = new AbortController()
  const pending = collaboration.refreshCollaborationSummary(controller.signal, '1:7')
  assert.equal(collaboration.useCollaboration.getState().status, 'loading')

  controller.abort()
  collaboration.useCollaboration.getState().reset()
  response.resolve({ data: { unreadCount: 8, latestId: 12, revision: '12:8' } })
  assert.equal(await pending, false)
  assert.equal(collaboration.useCollaboration.getState().status, 'idle')
  assert.equal(collaboration.useCollaboration.getState().revision, '')
  assert.equal(collaboration.useCollaboration.getState().unreadCount, 0)
})

test('poller is single-flight and an account switch aborts before polling the new scope', async () => {
  const { collaboration } = loadCollaboration()
  let session = { key: '1:7', enabled: true }
  let wakeSession = () => {}
  const requests = []
  const poller = collaboration.createCollaborationPoller({
    getSession: () => session,
    poll: (key, signal) => {
      const result = deferred()
      requests.push({ key, signal, result })
      return result.promise
    },
    reset: () => {},
    subscribeSession: wake => { wakeSession = wake; return () => {} },
    subscribeResume: () => () => {},
    isPaused: () => false,
    setTimer: () => 1,
    clearTimer: () => {},
  })

  poller.start()
  await Promise.resolve()
  assert.equal(requests.length, 1)
  const sameRequest = poller.refresh()
  poller.refresh()
  assert.equal(requests.length, 1)

  session = { key: '2:9', enabled: true }
  wakeSession()
  assert.equal(requests[0].signal.aborted, true)
  requests[0].result.resolve(false)
  await sameRequest
  await new Promise(resolve => setImmediate(resolve))
  assert.equal(requests.length, 2)
  assert.equal(requests[1].key, '2:9')

  poller.stop()
  assert.equal(requests[1].signal.aborted, true)
  requests[1].result.resolve(false)
})

test('poll failures back off and stop cancels the scheduled continuation', async () => {
  const { collaboration } = loadCollaboration()
  const timers = []
  const poller = collaboration.createCollaborationPoller({
    getSession: () => ({ key: '1:7', enabled: true }),
    poll: async () => false,
    reset: () => {},
    subscribeSession: () => () => {},
    subscribeResume: () => () => {},
    isPaused: () => false,
    intervalMs: 5_000,
    maximumBackoffMs: 60_000,
    setTimer: (callback, delay) => {
      const timer = { callback, delay, cleared: false }
      timers.push(timer)
      return timer
    },
    clearTimer: timer => { timer.cleared = true },
  })

  poller.start()
  await new Promise(resolve => setImmediate(resolve))
  assert.equal(timers[0].delay, 5_000)
  timers[0].callback()
  await new Promise(resolve => setImmediate(resolve))
  assert.equal(timers[1].delay, 10_000)
  poller.stop()
  assert.equal(timers[1].cleared, true)
})
