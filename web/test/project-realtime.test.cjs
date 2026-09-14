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

function settle() {
  return new Promise(resolve => setImmediate(resolve))
}

class FakeConnection {
  constructor(accessTokenFactory, starts = []) {
    this.accessTokenFactory = accessTokenFactory
    this.starts = [...starts]
    this.startCalls = 0
    this.stopCalls = 0
    this.handlers = {}
  }

  start() {
    this.startCalls += 1
    const next = this.starts.shift()
    return next ? next() : Promise.resolve()
  }

  stop() {
    this.stopCalls += 1
    return Promise.resolve()
  }

  onProjectChanged(handler) { this.handlers.projectChanged = handler }
  onReconnecting(handler) { this.handlers.reconnecting = handler }
  onReconnected(handler) { this.handlers.reconnected = handler }
  onClose(handler) { this.handlers.close = handler }
  on(name, handler) { if (name === 'ProjectChanged') this.onProjectChanged(handler) }
  onreconnecting(handler) { this.onReconnecting(handler) }
  onreconnected(handler) { this.onReconnected(handler) }
  onclose(handler) { this.onClose(handler) }
  emit(payload) { this.handlers.projectChanged?.(payload) }
  reconnecting() { this.handlers.reconnecting?.() }
  reconnected() { this.handlers.reconnected?.() }
  close() { this.handlers.close?.() }
}

function loadRealtime(signalR = {}) {
  const sourcePath = path.resolve(__dirname, '../src/services/projectRealtime.ts')
  const source = ts.transpileModule(fs.readFileSync(sourcePath, 'utf8'), {
    compilerOptions: {
      module: ts.ModuleKind.CommonJS,
      target: ts.ScriptTarget.ES2022,
      esModuleInterop: true,
    },
  }).outputText
  const exports = {}
  const windowListeners = new Map()
  const context = {
    console,
    exports,
    module: { exports },
    require: name => name === '@microsoft/signalr' ? signalR : require(name),
    setTimeout,
    clearTimeout,
    window: {
      addEventListener: (name, handler) => windowListeners.set(name, handler),
      removeEventListener: (name, handler) => {
        if (windowListeners.get(name) === handler) windowListeners.delete(name)
      },
    },
  }
  vm.runInNewContext(source, context, { filename: sourcePath })
  return { realtime: context.module.exports, windowListeners }
}

function createTimers() {
  const timers = []
  return {
    timers,
    setTimer(callback, delay) {
      const timer = { callback, delay, cleared: false }
      timers.push(timer)
      return timer
    },
    clearTimer(timer) { timer.cleared = true },
  }
}

test('initial connection accepts only validated ProjectChanged events and reports connected before ready', async () => {
  const { realtime } = loadRealtime()
  const events = []
  const actions = []
  let connection
  const controller = realtime.createProjectRealtime({
    getSession: () => ({ key: '7:1', enabled: true }),
    getAccessToken: async () => 'token',
    subscribeSession: () => () => {},
    createConnection: accessTokenFactory => (connection = new FakeConnection(accessTokenFactory)),
    onEvent: event => events.push({ ...event }),
    onStatus: status => actions.push(`status:${status}`),
    onReady: () => actions.push('ready'),
  })

  controller.start()
  await settle()
  assert.deepEqual(actions.slice(-2), ['status:connected', 'ready'])

  connection.emit({ projectId: 3, kind: 'messages' })
  connection.emit({ projectId: 4, kind: 'receipts' })
  connection.emit({ projectId: 5, kind: 'activity' })
  connection.emit({ projectId: 0, kind: 'messages' })
  connection.emit({ projectId: 1.5, kind: 'messages' })
  connection.emit({ projectId: Number.MAX_SAFE_INTEGER + 1, kind: 'messages' })
  connection.emit({ projectId: 6, kind: 'unknown' })
  connection.emit(null)
  assert.deepEqual(events, [
    { projectId: 3, kind: 'messages' },
    { projectId: 4, kind: 'receipts' },
    { projectId: 5, kind: 'activity' },
  ])
  controller.stop()
})

test('automatic reconnect reports ready again after the connected status', async () => {
  const { realtime } = loadRealtime()
  const actions = []
  let connection
  const controller = realtime.createProjectRealtime({
    getSession: () => ({ key: '7:1', enabled: true }),
    getAccessToken: async () => 'token',
    subscribeSession: () => () => {},
    createConnection: accessTokenFactory => (connection = new FakeConnection(accessTokenFactory)),
    onEvent: () => {},
    onStatus: status => actions.push(`status:${status}`),
    onReady: () => actions.push('ready'),
  })

  controller.start()
  await settle()
  actions.length = 0
  connection.reconnecting()
  connection.reconnected()
  assert.deepEqual(actions, ['status:reconnecting', 'status:connected', 'ready'])
  controller.stop()
})

test('initial failure retries with backoff and network recovery starts one immediate attempt', async () => {
  const { realtime } = loadRealtime()
  const clock = createTimers()
  const secondStart = deferred()
  let ready = 0
  let connection
  const controller = realtime.createProjectRealtime({
    getSession: () => ({ key: '7:1', enabled: true }),
    getAccessToken: async () => 'token',
    subscribeSession: () => () => {},
    createConnection: accessTokenFactory => (connection = new FakeConnection(accessTokenFactory, [
      () => Promise.reject(new Error('offline')),
      () => secondStart.promise,
    ])),
    onEvent: () => {},
    onStatus: () => {},
    onReady: () => { ready += 1 },
    setTimer: clock.setTimer,
    clearTimer: clock.clearTimer,
    initialRetryDelayMs: 1_000,
    maximumRetryDelayMs: 8_000,
  })

  controller.start()
  await settle()
  assert.equal(connection.startCalls, 1)
  assert.equal(clock.timers[0].delay, 1_000)

  controller.retry()
  controller.retry()
  assert.equal(clock.timers[0].cleared, true)
  assert.equal(connection.startCalls, 2)
  secondStart.resolve()
  await settle()
  assert.equal(ready, 1)
  controller.stop()
})

test('account switch and logout reject stale token promises and late hub events', async () => {
  const { realtime } = loadRealtime()
  const firstToken = deferred()
  let session = { key: '7:1', enabled: true }
  let wakeSession = () => {}
  let tokenCalls = 0
  let ready = 0
  const events = []
  const connections = []
  const controller = realtime.createProjectRealtime({
    getSession: () => session,
    getAccessToken: () => ++tokenCalls === 1 ? firstToken.promise : Promise.resolve('token-b'),
    subscribeSession: wake => { wakeSession = wake; return () => {} },
    createConnection: accessTokenFactory => {
      const connection = new FakeConnection(accessTokenFactory, [() => accessTokenFactory()])
      connections.push(connection)
      return connection
    },
    onEvent: event => events.push({ ...event }),
    onStatus: () => {},
    onReady: () => { ready += 1 },
  })

  controller.start()
  await Promise.resolve()
  session = { key: '9:2', enabled: true }
  wakeSession()
  await settle()
  assert.equal(connections.length, 2)
  assert.equal(connections[0].stopCalls, 1)
  assert.equal(ready, 1)

  firstToken.resolve('token-a')
  await settle()
  connections[0].emit({ projectId: 3, kind: 'messages' })
  assert.equal(ready, 1)
  assert.deepEqual(events, [])

  session = { key: '', enabled: false }
  wakeSession()
  connections[1].emit({ projectId: 4, kind: 'activity' })
  assert.equal(connections[1].stopCalls, 1)
  assert.deepEqual(events, [])
  controller.stop()
})

test('stop clears retries, unsubscribes observers, and makes late callbacks inert', async () => {
  const { realtime } = loadRealtime()
  const clock = createTimers()
  let sessionUnsubscribed = 0
  let resumeUnsubscribed = 0
  const statuses = []
  let connection
  const controller = realtime.createProjectRealtime({
    getSession: () => ({ key: '7:1', enabled: true }),
    getAccessToken: async () => 'token',
    subscribeSession: () => () => { sessionUnsubscribed += 1 },
    subscribeResume: () => () => { resumeUnsubscribed += 1 },
    createConnection: accessTokenFactory => (connection = new FakeConnection(accessTokenFactory, [
      () => Promise.reject(new Error('offline')),
    ])),
    onEvent: () => {},
    onStatus: status => statuses.push(status),
    onReady: () => {},
    setTimer: clock.setTimer,
    clearTimer: clock.clearTimer,
  })

  controller.start()
  await settle()
  controller.stop()
  const statusCount = statuses.length
  assert.equal(clock.timers[0].cleared, true)
  assert.equal(connection.stopCalls, 1)
  assert.equal(sessionUnsubscribed, 1)
  assert.equal(resumeUnsubscribed, 1)

  clock.timers[0].callback()
  connection.reconnected()
  connection.emit({ projectId: 2, kind: 'messages' })
  await settle()
  assert.equal(connection.startCalls, 1)
  assert.equal(statuses.length, statusCount)
})

test('production connection uses the hub URL, token factory, automatic reconnect, and disabled logging', async () => {
  const built = new FakeConnection(async () => 'unused')
  const observed = {}
  class FakeBuilder {
    withUrl(url, options) { observed.url = url; observed.urlOptions = options; return this }
    withAutomaticReconnect() { observed.reconnect = true; return this }
    configureLogging(level) { observed.logLevel = level; return this }
    build() { return built }
  }
  const { realtime, windowListeners } = loadRealtime({ HubConnectionBuilder: FakeBuilder, LogLevel: { None: 'none' } })
  const stop = realtime.startProjectRealtime({
    getSession: () => ({ key: '7:1', enabled: true }),
    getAccessToken: async () => 'fresh-token',
    subscribeSession: () => () => {},
    onEvent: () => {},
    onStatus: () => {},
    onReady: () => {},
  })
  await settle()

  assert.equal(observed.url, '/api/v1/collaboration/live')
  assert.equal(observed.reconnect, true)
  assert.equal(observed.logLevel, 'none')
  assert.equal(await observed.urlOptions.accessTokenFactory(), 'fresh-token')
  assert.equal(windowListeners.has('online'), true)
  stop()
  assert.equal(windowListeners.has('online'), false)
})
