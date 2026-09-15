const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const vm = require('node:vm')
const ts = require('typescript')
const React = require('react')
const { create, act } = require('react-test-renderer')

function component(name) {
  return props => React.createElement(name, props, props.children)
}

function loadPreview(http, browserWindow) {
  const arco = new Proxy({
    Select: component('Select'),
  }, { get: (target, name) => target[name] ?? component(name) })
  const mocks = {
    '@arco-design/web-react': arco,
    'react-dom': { createPortal: (node, container) => React.createElement('portal', { container }, node) },
    '../api/client': http,
    '../../generated/pptx-viewer.html?raw': '<html nonce="YF_VIEWER_NONCE"></html>',
    './PptxPreview.css': {},
  }
  const filename = path.resolve(__dirname, '../src/components/PptxPreview.tsx')
  const source = ts.transpileModule(fs.readFileSync(filename, 'utf8'), {
    compilerOptions: {
      module: ts.ModuleKind.CommonJS,
      jsx: ts.JsxEmit.ReactJSX,
      target: ts.ScriptTarget.ES2022,
      esModuleInterop: true,
    },
  }).outputText
  const exports = {}
  const context = {
    exports,
    module: { exports },
    console,
    AbortController,
    setTimeout,
    clearTimeout,
    crypto: require('node:crypto').webcrypto,
    window: browserWindow,
    require: name => mocks[name] ?? require(name),
  }
  vm.runInNewContext(source, context, { filename })
  return exports.default
}

function messageWindow() {
  const listeners = new Set()
  return {
    listeners,
    addEventListener: (_type, listener) => listeners.add(listener),
    removeEventListener: (_type, listener) => listeners.delete(listener),
    emit: event => act(async () => { for (const listener of listeners) listener(event) }),
  }
}

test('PPTX preview keeps authenticated bytes in a sandbox and drives the portal toolbar by channel messages', async () => {
  const browser = messageWindow()
  const sent = []
  let requestOptions
  const contentWindow = { postMessage: message => sent.push(message) }
  const Preview = loadPreview({ get: async (_url, options) => {
    requestOptions = options
    return { data: new Uint8Array([0x50, 0x4b, 3, 4]).buffer }
  } }, browser)
  const toolbarContainer = { nodeType: 1 }
  let renderer
  try {
    await act(async () => {
      renderer = create(React.createElement(Preview, { fileId: 41, toolbarContainer }), {
        createNodeMock: element => element.type === 'iframe' ? { contentWindow } : {},
      })
    })
    const frame = () => renderer.root.findByType('iframe')
    assert.equal(requestOptions.responseType, 'arraybuffer')
    assert.equal(frame().props.sandbox, 'allow-scripts')
    assert.equal(frame().props.referrerPolicy, 'no-referrer')
    assert.doesNotMatch(frame().props.srcDoc, /YF_VIEWER_NONCE/)
    assert.equal(frame().props.style.visibility, 'hidden')
    await act(async () => frame().props.onLoad())
    assert.equal(sent.length, 1)
    assert.equal(sent[0].type, 'pptx:load')
    const channel = sent[0].channel

    await browser.emit({ source: {}, data: { type: 'pptx:rendered', channel, pageCount: 3, pageNumber: 1, scale: 0.8 } })
    assert.equal(frame().props.style.visibility, 'hidden')
    await browser.emit({ source: contentWindow, data: { type: 'pptx:rendered', channel: 'wrong', pageCount: 3 } })
    assert.equal(frame().props.style.visibility, 'hidden')
    await browser.emit({ source: contentWindow, data: { type: 'pptx:rendered', channel, pageCount: 3, pageNumber: 1, scale: 0.8, zoom: 'page' } })
    assert.equal(frame().props.style.visibility, 'visible')
    assert.equal(renderer.root.findByType('portal').props.container, toolbarContainer)

    await browser.emit({ source: contentWindow, data: { type: 'pptx:page-changed', channel, pageNumber: Infinity } })
    await browser.emit({ source: contentWindow, data: { type: 'pptx:scale-changed', channel, scale: 'not-a-number' } })
    assert.equal(renderer.root.findByProps({ 'aria-label': 'PPTX 幻灯片编号' }).props.value, 1)

    await act(async () => renderer.root.findByProps({ 'aria-label': '下一张幻灯片' }).props.onClick())
    assert.deepEqual({ type: sent.at(-1).type, command: sent.at(-1).command, value: sent.at(-1).value },
      { type: 'pptx:command', command: 'page', value: 2 })
    await act(async () => renderer.root.findByProps({ 'aria-label': 'PPTX 缩放' }).props.onChange('fit'))
    assert.equal(sent.at(-1).value, 'fit')
    await act(async () => renderer.root.findByProps({ 'aria-label': '重置 PPTX 缩放' }).props.onClick())
    assert.equal(sent.at(-1).value, 'page')
    assert.doesNotMatch(JSON.stringify(renderer.toJSON()), /下载/)
  } finally {
    if (renderer) await act(async () => renderer.unmount())
  }
  assert.equal(browser.listeners.size, 0)
  assert.equal(requestOptions.signal.aborted, true)
})

test('PPTX preview rejects non-finite render status values from the isolated frame', async () => {
  const browser = messageWindow()
  const sent = []
  const contentWindow = { postMessage: message => sent.push(message) }
  const Preview = loadPreview({ get: async () => ({ data: new Uint8Array([0x50, 0x4b, 3, 4]).buffer }) }, browser)
  let renderer
  try {
    await act(async () => {
      renderer = create(React.createElement(Preview, { fileId: 42 }), {
        createNodeMock: element => element.type === 'iframe' ? { contentWindow } : {},
      })
    })
    await act(async () => renderer.root.findByType('iframe').props.onLoad())
    await browser.emit({ source: contentWindow, data: {
      type: 'pptx:rendered', channel: sent[0].channel, pageCount: 'NaN', pageNumber: 1, scale: 1,
    } })
    assert.equal(renderer.root.findByType('Result').props.title, 'PPTX 预览失败')
    assert.match(renderer.root.findByType('Result').props.subTitle, /无效结果/)
  } finally {
    if (renderer) await act(async () => renderer.unmount())
  }
})

test('PPTX preview aborts stale requests and ignores bytes that resolve after a file switch', async () => {
  const browser = messageWindow()
  const requests = []
  const Preview = loadPreview({ get: (_url, options) => {
    let resolve
    const promise = new Promise(next => { resolve = next })
    requests.push({ options, resolve })
    return promise
  } }, browser)
  let renderer
  const posted = []
  const contentWindow = { postMessage: message => posted.push(message) }
  await act(async () => {
    renderer = create(React.createElement(Preview, { fileId: 1 }), {
      createNodeMock: element => element.type === 'iframe' ? { contentWindow } : {},
    })
  })
  await act(async () => renderer.root.findByType('iframe').props.onLoad())
  await act(async () => renderer.update(React.createElement(Preview, { fileId: 2 })))
  assert.equal(requests[0].options.signal.aborted, true)
  await act(async () => {
    requests[0].resolve({ data: new Uint8Array([0x50, 0x4b, 3, 4]).buffer })
    await Promise.resolve()
  })
  assert.equal(posted.length, 0)
  await act(async () => renderer.unmount())
  assert.equal(requests[1].options.signal.aborted, true)
})

test('PPTX preview retry owns a fresh request and iframe channel after a render error', async () => {
  const browser = messageWindow()
  const sent = []
  let requests = 0
  const contentWindow = { postMessage: message => sent.push(message) }
  const Preview = loadPreview({ get: async () => {
    requests++
    return { data: new Uint8Array([0x50, 0x4b, 3, 4]).buffer }
  } }, browser)
  let renderer
  try {
    await act(async () => {
      renderer = create(React.createElement(Preview, { fileId: 9 }), {
        createNodeMock: element => element.type === 'iframe' ? { contentWindow } : {},
      })
    })
    await act(async () => renderer.root.findByType('iframe').props.onLoad())
    await browser.emit({ source: contentWindow, data: { type: 'pptx:error', channel: sent[0].channel } })
    const retry = renderer.root.findByType('Result').props.extra
    assert.equal(retry.props['aria-label'], '重试 PPTX 预览')
    await act(async () => retry.props.onClick())
    assert.equal(requests, 2)
    await act(async () => renderer.root.findByType('iframe').props.onLoad())
    assert.equal(sent.length, 2)
    assert.notEqual(sent[0].channel, sent[1].channel)
  } finally {
    if (renderer) await act(async () => renderer.unmount())
  }
})

test('PPTX viewer build is network isolated and embeds the local renderer protocol', () => {
  const build = fs.readFileSync(path.resolve(__dirname, '../scripts/build-pptx-preview.mjs'), 'utf8')
  const viewer = fs.readFileSync(path.resolve(__dirname, '../vendor/vue-office-pptx/viewer.js'), 'utf8')
  assert.match(build, /connect-src 'none'/)
  assert.match(build, /object-src 'none'/)
  assert.match(build, /frame-src 'none'/)
  assert.doesNotMatch(build, /<script[^>]+src=/i)
  assert.doesNotMatch(build, /<link[^>]+href=/i)
  assert.match(viewer, /pptx:rendered/)
  assert.match(viewer, /pptx-preview-slide-wrapper/)
})
