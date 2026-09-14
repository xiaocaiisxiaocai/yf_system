const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const vm = require('node:vm')
const ts = require('typescript')
const React = require('react')
const { create, act } = require('react-test-renderer')
const os = require('node:os')
const { pathToFileURL } = require('node:url')
const { spawnSync } = require('node:child_process')

test('production PDF engine reads a real document without native Uint8Array.toHex', () => {
  const engineFile = path.resolve(__dirname, '../src/components/pdfEngine.ts')
  let source = fs.readFileSync(engineFile, 'utf8')
  // Resolve the exact API and worker selected by production, without mocking either.
  source = source.replace(/import workerUrl from '([^']+)\?url'/, (_, specifier) =>
    `const workerUrl = ${JSON.stringify(pathToFileURL(require.resolve(specifier)).href)}`)
  source = source.replace(/from '(pdfjs-dist[^']*)'/g, (_, specifier) =>
    `from ${JSON.stringify(pathToFileURL(require.resolve(specifier)).href)}`)
  source = source.replace('import.meta.env.BASE_URL', JSON.stringify('file:///unused-test-assets/'))
  const output = ts.transpileModule(source, {
    compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 },
  }).outputText
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'yf-pdf-compat-'))
  try {
    const modulePath = path.join(temp, 'engine.mjs')
    fs.writeFileSync(modulePath, output)
    const script = `
      import assert from 'node:assert/strict';
      import fs from 'node:fs';
      delete Uint8Array.prototype.toHex;
      assert.equal(Uint8Array.prototype.toHex, undefined);
      const { openPdf } = await import(${JSON.stringify(pathToFileURL(modulePath).href)});
      const task = openPdf(new Uint8Array(fs.readFileSync(${JSON.stringify(path.resolve(__dirname, 'fixtures/pdf-compatibility.pdf'))})));
      try {
        const pdf = await task.promise;
        assert.equal(pdf.numPages, 1);
        const page = await pdf.getPage(1);
        const content = await page.getTextContent();
        assert.match(content.items.map(item => item.str).join(' '), /Local acceptance PDF/);
      } finally { await task.destroy(); }
    `
    const result = spawnSync(process.execPath, ['--input-type=module', '-e', script], { encoding: 'utf8', timeout: 15000 })
    assert.equal(result.status, 0, result.stderr || result.stdout || String(result.error))
  } finally {
    fs.rmSync(temp, { recursive: true, force: true })
  }
})

const deferred = () => {
  let resolve, reject
  const promise = new Promise((yes, no) => { resolve = yes; reject = no })
  return { promise, resolve, reject }
}

function harness({ pendingRequest = false, pendingRender = false, pendingParse = false, failLoad = false } = {}) {
  const requests = [], tasks = [], renders = []
  const component = name => props => React.createElement(name, props, props.children)
  const arco = new Proxy({}, { get: (_, name) => component(name) })
  const doc = {
    numPages: 3,
    getPage: async number => ({
      getViewport: ({ scale }) => ({ width: 600 * scale, height: 800 * scale }),
      render: options => {
        const pending = deferred()
        const item = { number, options, ...pending, cancelled: false }
        renders.push(item)
        if (!pendingRender) pending.resolve()
        return { promise: pending.promise, cancel: () => { item.cancelled = true; pending.reject(Object.assign(new Error('cancelled'), { name: 'RenderingCancelledException' })) } }
      },
    }),
  }
  const mocks = {
    '@arco-design/web-react': arco,
    '../api/client': { get: (url, options) => {
      const item = { url, options, ...deferred() }
      requests.push(item)
      if (!pendingRequest) item.resolve({ data: new ArrayBuffer(8) })
      return item.promise
    } },
    './pdfEngine': { openPdf: data => {
      const item = { data, destroyed: false, ...deferred() }
      tasks.push(item)
      if (!pendingParse) {
        if (failLoad) item.reject(new Error('invalid PDF'))
        else item.resolve(doc)
      }
      return { promise: item.promise, destroy: async () => { item.destroyed = true } }
    } },
  }
  const filename = path.resolve(__dirname, '../src/components/PdfPreview.tsx')
  const source = ts.transpileModule(fs.readFileSync(filename, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX, target: ts.ScriptTarget.ES2022, esModuleInterop: true },
  }).outputText
  const exports = {}
  const context = { exports, module: { exports }, console, AbortController, Uint8Array, Blob, URL,
    window: { devicePixelRatio: 2 },
    ResizeObserver: class { observe() {} disconnect() {} },
    require: name => mocks[name] ?? require(name),
  }
  vm.runInNewContext(source, context, { filename })
  let renderer
  const mount = async fileId => act(async () => {
    renderer = create(React.createElement(exports.default, { fileId }), { createNodeMock: element => element.type === 'canvas'
      ? { width: 0, height: 0, style: {}, getContext: () => ({}) }
      : { clientWidth: 832, clientHeight: 632, getBoundingClientRect: () => ({ width: 832, height: 632 }) } })
  })
  return { mount, doc, requests, tasks, renders, get renderer() { return renderer }, update: fileId => act(async () => renderer.update(React.createElement(exports.default, { fileId }))) }
}

test('PDF bytes render to canvas without relying on a browser PDF iframe', async () => {
  const h = harness()
  await h.mount(27)
  assert.equal(h.requests[0].url, '/files/27/content')
  assert.equal(h.requests[0].options.responseType, 'arraybuffer')
  assert.equal(h.renderer.root.findAllByType('iframe').length, 0)
  assert.equal(h.renders.length, 1)
  assert.equal(h.renders[0].number, 1)
  assert.ok(h.renders[0].options.canvas.width > 0)
  await act(async () => h.renderer.unmount())
  assert.ok(h.tasks.every(task => task.destroyed))
})

test('PDF pagination and zoom render the selected page and cancel obsolete work', async () => {
  const h = harness({ pendingRender: true })
  await h.mount(27)
  await act(async () => h.renderer.root.findByProps({ 'aria-label': '下一页' }).props.onClick())
  assert.equal(h.renders[0].cancelled, true)
  assert.equal(h.renders.at(-1).number, 2)
  await act(async () => h.renderer.root.findByProps({ 'aria-label': 'PDF 缩放' }).props.onChange('1.5'))
  assert.equal(h.renders.at(-1).options.viewport.width, 900)
  await act(async () => h.renderer.root.findByProps({ 'aria-label': 'PDF 页码' }).props.onChange(99))
  assert.equal(h.renders.at(-1).number, 3)
  await act(async () => h.renderer.unmount())
  assert.ok(h.renders.every(render => render.cancelled))
})

test('PDF fits the whole page and supports zoom in, zoom out and reset without changing pages', async () => {
  const h = harness()
  await h.mount(27)
  assert.equal(h.renders.at(-1).options.viewport.height, 600)
  assert.equal(h.renders.at(-1).options.viewport.width, 450)
  await act(async () => h.renderer.root.findByProps({ 'aria-label': '放大 PDF' }).props.onClick())
  assert.ok(h.renders.at(-1).options.viewport.height > 600)
  await act(async () => h.renderer.root.findByProps({ 'aria-label': '缩小 PDF' }).props.onClick())
  assert.ok(h.renders.at(-1).options.viewport.height < 752)
  await act(async () => h.renderer.root.findByProps({ 'aria-label': '下一页' }).props.onClick())
  await act(async () => h.renderer.root.findByProps({ 'aria-label': '重置 PDF 缩放' }).props.onClick())
  assert.equal(h.renders.at(-1).options.viewport.height, 600)
  assert.equal(h.renders.at(-1).number, 2)
  await act(async () => h.renderer.unmount())
})

test('switching files or closing during loading aborts requests and ignores late PDFs', async () => {
  const h = harness({ pendingRequest: true })
  await h.mount(1)
  await h.update(2)
  assert.equal(h.requests[0].options.signal.aborted, true)
  await act(async () => h.requests[0].resolve({ data: new ArrayBuffer(1) }))
  assert.equal(h.tasks.length, 0)
  await act(async () => h.requests[1].resolve({ data: new ArrayBuffer(2) }))
  assert.equal(h.tasks.length, 1)
  await h.update(3)
  assert.equal(h.tasks[0].destroyed, true)
  await act(async () => h.renderer.unmount())
  assert.equal(h.requests[2].options.signal.aborted, true)
  await act(async () => h.requests[2].resolve({ data: new ArrayBuffer(3) }))
  assert.equal(h.tasks.length, 1)
})

test('invalid PDF parsing reports an error and retry starts a fresh request', async () => {
  const h = harness({ failLoad: true })
  await h.mount(27)
  assert.match(JSON.stringify(h.renderer.toJSON()), /PDF.*失败/)
  await act(async () => h.renderer.root.findByProps({ 'aria-label': '重试 PDF 预览' }).props.onClick())
  assert.equal(h.requests.length, 2)
  assert.equal(h.tasks[0].destroyed, true)
  await act(async () => h.renderer.unmount())
})

test('closing while the worker parses destroys it and ignores its late result', async () => {
  const h = harness({ pendingParse: true })
  await h.mount(27)
  assert.equal(h.tasks.length, 1)
  await act(async () => h.renderer.unmount())
  assert.equal(h.tasks[0].destroyed, true)
  await act(async () => h.tasks[0].resolve(h.doc))
  assert.equal(h.renders.length, 0)
})

test('failed page rendering produces a visible error instead of an empty canvas', async () => {
  const h = harness({ pendingRender: true })
  await h.mount(27)
  await act(async () => h.renders[0].reject(new Error('page decode failed')))
  assert.match(JSON.stringify(h.renderer.toJSON()), /PDF 页面渲染失败/)
  assert.equal(h.renderer.root.findByType('canvas').props.style.display, 'none')
  await act(async () => h.renderer.unmount())
})
