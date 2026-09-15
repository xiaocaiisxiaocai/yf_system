const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const vm = require('node:vm')
const ts = require('typescript')
const React = require('react')
const { create, act } = require('react-test-renderer')

function load(http) {
  const timers = new Map()
  let nextTimer = 0
  const component = name => props => React.createElement(name, props, props.children, props.extra)
  const filename = path.resolve(__dirname, '../src/components/VideoPreview.tsx')
  const compiled = ts.transpileModule(fs.readFileSync(filename, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX, target: ts.ScriptTarget.ES2022, esModuleInterop: true },
  }).outputText
  const exports = {}
  const calls = { pause: 0, load: 0, remove: [] }
  const video = { pause() { calls.pause++ }, load() { calls.load++ }, removeAttribute(name) { calls.remove.push(name) } }
  vm.runInNewContext(compiled, {
    exports, module: { exports }, console, URL, AbortController,
    window: { location: { origin: 'http://localhost:5173' } },
    setTimeout(fn, ms) { const id = ++nextTimer; timers.set(id, { fn, ms }); return id },
    clearTimeout(id) { timers.delete(id) },
    require(name) {
      if (name === '../api/client') return http
      if (name === '@arco-design/web-react') return new Proxy({}, { get: (_, name) => component(name) })
      return require(name)
    },
  }, { filename })
  return { Preview: exports.default, timers, calls, video }
}

test('video uses a same-origin media grant, renews it without replacing the player, and releases resources', async () => {
  const requests = []
  const env = load({ async post(url, body, config) {
    requests.push({ url, config })
    return { data: { url: '/api/v1/files/7/media', expiresInSeconds: 300 } }
  } })
  let renderer
  await act(async () => { renderer = create(React.createElement(env.Preview, { fileId: 7 }), { createNodeMock: () => env.video }) })
  const before = renderer.root.findByType('video')
  assert.equal(before.props.src, '/api/v1/files/7/media')
  assert.equal(before.props.controlsList, 'nodownload noremoteplayback')
  assert.equal(requests[0].url, '/files/7/media-session')
  await act(async () => { before.props.onLoadedMetadata() })
  assert.equal(renderer.root.findAllByProps({ role: 'status' }).length, 0)
  const renewal = [...env.timers.values()].find(timer => timer.ms === 150000)
  assert.ok(renewal)
  await act(async () => { renewal.fn() })
  assert.equal(requests.length, 2)
  assert.equal(renderer.root.findByType('video'), before)
  await act(async () => renderer.unmount())
  assert.equal(requests[0].config.signal.aborted, true)
  assert.ok(env.calls.pause > 0)
  assert.ok(env.calls.remove.includes('src'))
  assert.ok(env.calls.load > 0)
})

test('video rejects foreign origins, query credentials, and mismatched file endpoints', async () => {
  for (const url of ['https://example.invalid/api/v1/files/7/media', '/api/v1/files/8/media', '/api/v1/files/7/media?token=secret']) {
    const env = load({ post: async () => ({ data: { url, expiresInSeconds: 300 } }) })
    let renderer
    await act(async () => { renderer = create(React.createElement(env.Preview, { fileId: 7 })) })
    assert.equal(renderer.root.findByType('video').props.src, undefined)
    assert.equal(renderer.root.findByType('Result').props.title, '视频无法播放')
    await act(async () => renderer.unmount())
  }
})

test('video retries failed grants and ignores late grants after close', async () => {
  let count = 0
  let resolveGrant
  const env = load({ post: async () => {
    if (++count === 1) throw new Error('offline')
    return new Promise(resolve => { resolveGrant = resolve })
  } })
  let renderer
  await act(async () => { renderer = create(React.createElement(env.Preview, { fileId: 7 })) })
  assert.equal(renderer.root.findByType('Result').props.title, '视频无法播放')
  await act(async () => renderer.root.findByType('Button').props.onClick())
  assert.equal(count, 2)
  assert.equal(renderer.root.findAllByProps({ role: 'status' }).length, 1)
  await act(async () => renderer.unmount())
  await act(async () => resolveGrant({ data: { url: '/api/v1/files/7/media', expiresInSeconds: 300 } }))
  assert.equal(env.timers.size, 0)
})
