const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const vm = require('node:vm')
const ts = require('typescript')
const React = require('react')
const { create, act } = require('react-test-renderer')

const component = name => props => React.createElement(name, props)
const arco = new Proxy({ Typography: { Text: component('Text') }, Message: { info() {}, warning() {}, success() {} } }, {
  get: (object, key) => object[key] || component(key),
})
const file = name => ({ name, size: 1, slice: () => new Blob(['x']) })
const abortedUpload = (_url, _blob, { signal }) => new Promise((_resolve, reject) => {
  const abort = () => reject(new Error('aborted'))
  if (signal.aborted) abort()
  else signal.addEventListener('abort', abort, { once: true })
})

async function fixture(overrides = {}, offerSubmit = false, context = {}) {
  const calls = { init: [], merge: [], delete: [], closed: 0, done: 0, all: 0, submit: 0 }
  const http = {
    post: async (url, body) => {
      if (url === '/uploads/init') {
        calls.init.push(body.fileName)
        return { data: { sessionId: body.fileName, chunkSize: 1, totalChunks: 1, uploadedChunks: [] } }
      }
      calls.merge.push(url)
      return { data: { id: calls.merge.length } }
    },
    put: async () => {},
    delete: async url => { calls.delete.push(url) },
    ...overrides,
  }
  const source = ts.transpileModule(fs.readFileSync(path.join(__dirname, '../src/components/ChunkUploader.tsx'), 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX, target: ts.ScriptTarget.ES2022, esModuleInterop: true },
  }).outputText
  const exports = {}
  const mocks = {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../api/client': http, '../api/types': { fmtSize: String },
    '../api/file-hash': { fileMd5: async () => 'digest' },
  }
  vm.runInNewContext(source, { exports, module: { exports }, console, AbortController, ...context,
    require: name => mocks[name] || require(name) })
  let renderer
  await act(async () => {
    renderer = create(React.createElement(exports.default, {
      projectId: 1, visible: true,
      onClose: () => { calls.closed++ }, onDone: () => { calls.done++ },
      onAllUploaded: () => { calls.all++ },
      onSubmitForAcceptance: offerSubmit ? async () => { calls.submit++ } : undefined,
    }))
  })
  return {
    calls, http, renderer,
    select: async names => act(async () => renderer.root.findByType('input').props.onChange({ target: { files: names.map(file) } })),
    start: () => renderer.root.findByType('Modal').props.footer.props.children[1].props.onClick(),
    close: () => renderer.root.findByType('Modal').props.onCancel(),
    button: text => renderer.root.findAllByType('Button').find(node => node.props.children === text),
    remove: name => renderer.root.findAllByType('Button').find(node => node.props['aria-label'] === `移除「${name}」`).props.onClick(),
    dispose: async () => act(async () => renderer.unmount()),
  }
}

test('failed cancellation keeps the dialog and server session until a successful retry', async () => {
  const f = await fixture({ put: abortedUpload })
  try {
    let deletes = 0, upload
    f.http.delete = async () => { if (++deletes === 1) throw new Error('network unavailable') }
    await f.select(['a'])
    await act(async () => { upload = f.start() })
    await act(async () => { await f.close(); await upload })
    assert.equal(f.calls.closed, 0)
    assert.ok(f.button('重试取消'))
    assert.equal(f.calls.all, 0)
    await act(async () => f.close())
    assert.equal(deletes, 2)
    assert.equal(f.calls.closed, 1)
    assert.equal(f.calls.all, 0)
  } finally { await f.dispose() }
})

test('removing an interrupted upload cleans its server session and retains retry on cleanup failure', async () => {
  const f = await fixture({ put: async () => { throw new Error('chunk failed') } })
  try {
    let deletes = 0
    f.http.delete = async url => { assert.equal(url, '/uploads/a'); if (++deletes === 1) throw new Error('network') }
    await f.select(['a'])
    await act(async () => f.start())
    await act(async () => f.remove('a'))
    assert.equal(deletes, 1)
    assert.ok(f.button('重试取消'))
    assert.equal(f.calls.closed, 0)
    await act(async () => f.button('重试取消').props.onClick())
    assert.equal(deletes, 2)
    assert.equal(f.calls.all, 0)
    await act(async () => f.close())
    assert.equal(f.calls.closed, 1)
  } finally { await f.dispose() }
})

test('one completed file plus one cancelled file never reports batch success or submits acceptance', async () => {
  const f = await fixture({ put: (url, blob, config) => url.includes('/a/') ? Promise.resolve() : abortedUpload(url, blob, config) }, true)
  try {
    let upload
    await f.select(['a', 'b'])
    await act(async () => f.renderer.root.findByType('Checkbox').props.onChange(true))
    await act(async () => { upload = f.start() })
    assert.equal(f.calls.done, 1)
    await act(async () => { await f.button('取消').props.onClick(); await upload })
    assert.deepEqual(f.calls.delete, ['/uploads/b'])
    assert.equal(f.calls.all, 0)
    assert.equal(f.calls.submit, 0)
    assert.equal(f.calls.closed, 0)
    await act(async () => f.close())
    assert.equal(f.calls.closed, 1)
  } finally { await f.dispose() }
})

test('removing a failed file cannot turn a partial batch into a successful delivery', async () => {
  const f = await fixture({ put: async url => { if (url.includes('/b/')) throw new Error('chunk failed') } }, true)
  try {
    await f.select(['a', 'b'])
    await act(async () => f.renderer.root.findByType('Checkbox').props.onChange(true))
    await act(async () => f.start())
    await act(async () => f.remove('b'))
    assert.deepEqual(f.calls.delete, ['/uploads/b'])
    assert.equal(f.calls.done, 1)
    assert.equal(f.calls.all, 0)
    assert.equal(f.calls.submit, 0)
  } finally { await f.dispose() }
})

test('closing an uncertain merge neither aborts the session nor submits acceptance', async () => {
  const f = await fixture({}, true)
  try {
    const post = f.http.post
    f.http.post = async (url, body) => { if (url.endsWith('/merge')) throw new Error('response lost'); return post(url, body) }
    await f.select(['a'])
    await act(async () => f.renderer.root.findByType('Checkbox').props.onChange(true))
    await act(async () => f.start())
    assert.ok(f.button('重试确认'))
    await act(async () => f.close())
    assert.deepEqual(f.calls.delete, [])
    assert.equal(f.calls.all, 0)
    assert.equal(f.calls.submit, 0)
    assert.equal(f.calls.closed, 1)
  } finally { await f.dispose() }
})

for (const optIn of [false, true]) {
  test(`a fully successful batch submits acceptance only with explicit opt-in (${optIn})`, async () => {
    const f = await fixture({}, true)
    try {
      await f.select(['a', 'b'])
      assert.equal(f.renderer.root.findByType('Checkbox').props.checked, false)
      if (optIn) await act(async () => f.renderer.root.findByType('Checkbox').props.onChange(true))
      await act(async () => f.start())
      assert.equal(f.calls.done, 2)
      assert.equal(f.calls.all, 1)
      assert.equal(f.calls.submit, optIn ? 1 : 0)
      assert.equal(f.calls.closed, 1)
    } finally { await f.dispose() }
  })
}

test('duplicate close and start requests cannot overlap pending cancellation', async () => {
  const f = await fixture({ put: abortedUpload })
  try {
    let release, first, second
    const pendingDelete = new Promise(resolve => { release = resolve })
    f.http.delete = async url => { f.calls.delete.push(url); await pendingDelete }
    await f.select(['a'])
    const start = f.start
    await act(async () => { void start() })
    await act(async () => { first = f.close(); second = f.close() })
    assert.equal(f.calls.closed, 0)
    assert.equal(f.calls.delete.length, 1)
    assert.equal(f.renderer.root.findByType('input').props.disabled, true)
    await act(async () => { release(); await first; await second })
    assert.equal(f.calls.init.length, 1)
    assert.equal(f.calls.closed, 1)
  } finally { await f.dispose() }
})

// Backoff delays run immediately so retry tests stay fast.
const instantTimers = { setTimeout: fn => setTimeout(fn, 0), clearTimeout }
const networkError = () => Object.assign(new Error('Network Error'), { code: 'ERR_NETWORK' })

test('a transient chunk failure is retried quietly and the upload completes without user action', async () => {
  const puts = []
  const f = await fixture({
    put: async (_url, _blob, config) => {
      puts.push(config.quietNetworkError)
      if (puts.length === 1) throw networkError()
    },
  }, false, instantTimers)
  try {
    await f.select(['a'])
    await act(async () => { await f.start() })
    assert.deepEqual(puts, [true, true], 'retries stay quiet while another attempt remains')
    assert.deepEqual(f.calls.merge, ['/uploads/a/merge'])
    assert.equal(f.calls.delete.length, 0)
  } finally { await f.dispose() }
})

test('chunk retries stop after the attempt limit and only the last failure is shown', async () => {
  const puts = []
  const f = await fixture({ put: async (_url, _blob, config) => { puts.push(config.quietNetworkError); throw networkError() } },
    false, instantTimers)
  try {
    await f.select(['a'])
    await act(async () => { await f.start() })
    assert.deepEqual(puts, [true, true, false])
    assert.equal(f.calls.merge.length, 0)
    assert.ok(f.renderer.root.findAllByType('Text').some(node => String(node.props.children).includes('上传中断')))
  } finally { await f.dispose() }
})

test('business rejections of a chunk are not retried', async () => {
  let puts = 0
  const f = await fixture({ put: async () => { puts++; throw Object.assign(new Error('conflict'), { response: { status: 409 } }) } },
    false, instantTimers)
  try {
    await f.select(['a'])
    await act(async () => { await f.start() })
    assert.equal(puts, 1)
    assert.equal(f.calls.merge.length, 0)
  } finally { await f.dispose() }
})

test('at most two files upload at once and a waiting file can be removed before it starts', async () => {
  const releases = new Map()
  const f = await fixture({ put: url => new Promise(resolve => releases.set(url.split('/')[2], resolve)) })
  try {
    await f.select(['a', 'b', 'c', 'd'])
    let started
    await act(async () => { started = f.start() })
    assert.deepEqual(f.calls.init.sort(), ['a', 'b'], 'only two files may start')
    const waiting = f.renderer.root.findAllByType('Text').filter(node => String(node.props.children).includes('排队中'))
    assert.equal(waiting.length, 2)

    await act(async () => { await f.remove('d') })
    assert.equal(f.renderer.root.findAllByType('Button').some(node => node.props['aria-label'] === '移除「d」'), false)

    await act(async () => { releases.get('a')() })
    assert.deepEqual(f.calls.init.sort(), ['a', 'b', 'c'], 'a freed slot starts the next waiting file')
    await act(async () => { releases.get('b')(); releases.get('c')(); await started })
    assert.deepEqual(f.calls.merge.sort(), ['/uploads/a/merge', '/uploads/b/merge', '/uploads/c/merge'])
    assert.equal(f.calls.init.includes('d'), false, 'a removed waiting file never reaches the server')
  } finally { await f.dispose() }
})
