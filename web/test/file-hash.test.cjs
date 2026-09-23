const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const vm = require('node:vm')
const ts = require('typescript')

function loadHash(mocks, globals = {}) {
  const filename = path.resolve(__dirname, '..', 'src/api/file-hash.ts')
  const source = ts.transpileModule(fs.readFileSync(filename, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, esModuleInterop: true },
  }).outputText
  const exports = {}
  vm.runInNewContext(source, {
    exports, module: { exports }, setTimeout, clearTimeout, setInterval, clearInterval, ...globals,
    require: (name) => mocks[name],
  }, { filename })
  return exports
}

function workerModule(behaviour) {
  const created = []
  class FakeWorker {
    constructor() { this.terminated = false; created.push(this) }
    postMessage(data) { setTimeout(() => behaviour(this, data), 0) }
    terminate() { this.terminated = true }
  }
  return { module: { __esModule: true, default: FakeWorker }, created }
}

const inThread = (digest) => ({ __esModule: true, fileMd5: async () => digest })

test('hashing runs in the worker and forwards its progress', async () => {
  const worker = workerModule((self) => {
    self.onmessage({ data: { type: 'progress', fraction: 0.5 } })
    self.onmessage({ data: { type: 'done', digest: 'from-worker' } })
  })
  const { fileMd5 } = loadHash({ './file-hash.worker?worker': worker.module, './file-hash-core': inThread('in-thread') },
    { Worker: function Worker() {} })
  const progress = []
  assert.equal(await fileMd5({}, () => false, (value) => progress.push(value)), 'from-worker')
  assert.deepEqual(progress, [0.5])
  assert.equal(worker.created[0].terminated, true)
})

test('without Worker support, or when the worker fails to load, hashing falls back to the main thread', async () => {
  const unused = workerModule(() => assert.fail('worker must not be used'))
  const noWorker = loadHash({ './file-hash.worker?worker': unused.module, './file-hash-core': inThread('in-thread') })
  assert.equal(await noWorker.fileMd5({}), 'in-thread')

  const broken = workerModule((self) => self.onerror({ preventDefault() {} }))
  const failing = loadHash({ './file-hash.worker?worker': broken.module, './file-hash-core': inThread('in-thread') },
    { Worker: function Worker() {} })
  assert.equal(await failing.fileMd5({}), 'in-thread')
  assert.equal(broken.created[0].terminated, true)
})

test('cancelling stops the worker', async () => {
  const silent = workerModule(() => {})
  const { fileMd5 } = loadHash({ './file-hash.worker?worker': silent.module, './file-hash-core': inThread('in-thread') },
    { Worker: function Worker() {} })
  let cancel = false
  const pending = fileMd5({}, () => cancel)
  cancel = true
  await assert.rejects(pending, /取消/)
  assert.equal(silent.created[0].terminated, true)
})
