const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const vm = require('node:vm')
const ts = require('typescript')
const React = require('react')
const { create, act } = require('react-test-renderer')

function load(http) {
  const filename = path.resolve(__dirname, '../src/components/ImagePreview.tsx')
  const code = ts.transpileModule(fs.readFileSync(filename, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX, target: ts.ScriptTarget.ES2022, esModuleInterop: true },
  }).outputText
  const revoked = [], created = [], exports = {}
  const component = name => props => React.createElement(name, props, props.children, props.extra)
  vm.runInNewContext(code, {
    exports, module: { exports }, console, AbortController, setTimeout, clearTimeout,
    ResizeObserver: class { observe() {} disconnect() {} },
    requestAnimationFrame: callback => callback(),
    URL: { createObjectURL(blob) { created.push(blob); return `blob:fixture-${created.length}` }, revokeObjectURL(url) { revoked.push(url) } },
    require(name) {
      if (name === './ImagePreview.css') return {}
      if (name === '../api/client') return http
      if (name === '../../vendor/preview-wheel.js') return require(path.resolve(__dirname, '../vendor/preview-wheel.js'))
      if (name === '@arco-design/web-react') return new Proxy({}, { get: (_, name) => component(name) })
      return require(name)
    },
  }, { filename })
  return { Preview: exports.default, revoked, created }
}

test('image preview fits large images and supports original size and reset', async () => {
  const env = load({ get: async () => ({ data: new Blob(['image']) }) })
  let renderer
  try {
    await act(async () => { renderer = create(React.createElement(env.Preview, { fileId: 1, name: 'test.png' }), {
      createNodeMock: () => ({ clientWidth: 1032, clientHeight: 632, addEventListener() {}, removeEventListener() {},
        getBoundingClientRect: () => ({ left: 0, top: 0, width: 1000, height: 500 }) }),
    }) })
    await act(async () => renderer.root.findByType('img').props.onLoad({ currentTarget: { naturalWidth: 2000, naturalHeight: 1000 } }))
    assert.equal(renderer.root.findByType('img').props.style.width, 1000)
    await act(async () => renderer.root.findAllByType('Button').find(button => React.Children.toArray(button.props.children).includes('原始大小')).props.onClick())
    assert.equal(renderer.root.findByType('img').props.style.width, 2000)
    await act(async () => renderer.root.findAllByType('Button').find(button => button.props['aria-label'] === '重置图片缩放').props.onClick())
    assert.equal(renderer.root.findByType('img').props.style.width, 1000)
    assert.equal(renderer.root.findAllByType('a').length, 0)
  } finally { if (renderer) await act(async () => renderer.unmount()) }
  assert.deepEqual(env.revoked, ['blob:fixture-1'])
})

test('failed image decoding can retry and releases the failed object URL', async () => {
  let calls = 0
  const env = load({ get: async () => { calls++; return { data: new Blob(['image']) } } })
  let renderer
  try {
    await act(async () => { renderer = create(React.createElement(env.Preview, { fileId: 2, name: 'test.png' })) })
    await act(async () => renderer.root.findByType('img').props.onError())
    assert.equal(renderer.root.findByType('Result').props.title, '图片预览失败')
    await act(async () => renderer.root.findByType('Button').props.onClick())
    assert.equal(calls, 2)
    assert.deepEqual(env.revoked, ['blob:fixture-1'])
    assert.equal(renderer.root.findByType('img').props.src, 'blob:fixture-2')
  } finally { if (renderer) await act(async () => renderer.unmount()) }
})

test('closing image preview cancels authentication request and ignores late bytes', async () => {
  let resolve, config, url
  const env = load({ get: (path, options) => { url = path; config = options; return new Promise(done => { resolve = done }) } })
  let renderer
  await act(async () => { renderer = create(React.createElement(env.Preview, { fileId: 3, name: 'test.jpg' })) })
  assert.equal(url, '/files/3/content')
  assert.equal(config.responseType, 'blob')
  await act(async () => renderer.unmount())
  assert.equal(config.signal.aborted, true)
  await act(async () => resolve({ data: new Blob(['late image']) }))
  assert.equal(env.created.length, 0)
})
