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

class FakeElement {
  constructor(tagName) {
    this.tagName = tagName.toUpperCase()
    this.children = []
    this.parentNode = null
    this.style = {}
    this.dataset = {}
    this.attributes = {}
    this.listeners = new Map()
    this.clientWidth = 0
    this.clientHeight = 0
    this.scrollTop = 0
    this.width = 0
    this.height = 0
  }
  appendChild(child) {
    child.parentNode = this
    this.children.push(child)
    return child
  }
  insertBefore(child, next) {
    child.parentNode = this
    if (next === null) this.children.push(child)
    else this.children.splice(this.children.indexOf(next), 0, child)
    return child
  }
  removeChild(child) {
    const index = this.children.indexOf(child)
    if (index >= 0) this.children.splice(index, 1)
    child.parentNode = null
  }
  remove() { this.parentNode?.removeChild(this) }
  setAttribute(name, value) {
    this.attributes[name] = String(value)
    if (name.startsWith('data-')) {
      const key = name.slice(5).replace(/-([a-z])/g, (_, letter) => letter.toUpperCase())
      this.dataset[key] = String(value)
    }
  }
  getAttribute(name) { return this.attributes[name] ?? null }
  addEventListener(type, listener) {
    const entries = this.listeners.get(type) ?? new Set()
    entries.add(listener)
    this.listeners.set(type, entries)
  }
  removeEventListener(type, listener) { this.listeners.get(type)?.delete(listener) }
  dispatch(type) { for (const listener of this.listeners.get(type) ?? []) listener({ target: this }) }
  querySelectorAll(selector) {
    const matches = []
    for (const child of this.children) {
      if (selector === 'canvas' && child.tagName === 'CANVAS') matches.push(child)
      matches.push(...child.querySelectorAll(selector))
    }
    return matches
  }
  getContext() { return {} }
}

function loadTsCommonJs(filename, mocks = {}, globals = {}) {
  const output = ts.transpileModule(fs.readFileSync(filename, 'utf8'), {
    compilerOptions: {
      module: ts.ModuleKind.CommonJS,
      jsx: ts.JsxEmit.ReactJSX,
      target: ts.ScriptTarget.ES2022,
      esModuleInterop: true,
    },
  }).outputText
  const module = { exports: {} }
  const context = {
    module,
    exports: module.exports,
    console,
    setTimeout,
    clearTimeout,
    AbortController,
    Uint8Array,
    Error,
    require: name => mocks[name] ?? require(name),
    ...globals,
  }
  vm.runInNewContext(output, context, { filename })
  return module.exports
}

function rendererHarness({
  pageSizes = [{ width: 600, height: 800 }, { width: 1200, height: 600 }, { width: 300, height: 1200 }],
  pendingRender = false,
  pendingParse = false,
  failPage = 0,
} = {}) {
  const renders = []
  const tasks = []
  const observers = []
  const document = { createElement: tag => new FakeElement(tag) }
  class FakeResizeObserver {
    constructor(callback) { this.callback = callback; this.disconnected = false; observers.push(this) }
    observe(target) { this.target = target }
    disconnect() { this.disconnected = true }
  }
  const pdf = {
    numPages: pageSizes.length,
    getPage: async pageNumber => ({
      getViewport: ({ scale }) => ({
        width: pageSizes[pageNumber - 1].width * scale,
        height: pageSizes[pageNumber - 1].height * scale,
      }),
      render: options => {
        const pending = deferred()
        const item = { pageNumber, options, cancelled: false, ...pending }
        renders.push(item)
        if (pageNumber === failPage) pending.reject(new Error('page decode failed'))
        else if (!pendingRender) pending.resolve()
        return {
          promise: pending.promise,
          cancel: () => {
            item.cancelled = true
            pending.reject(Object.assign(new Error('cancelled'), { name: 'RenderingCancelledException' }))
          },
        }
      },
    }),
  }
  const openPdf = data => {
    const pending = deferred()
    const item = { data, destroyed: false, ...pending }
    tasks.push(item)
    if (!pendingParse) pending.resolve(pdf)
    return { promise: pending.promise, destroy: async () => { item.destroyed = true } }
  }
  const source = path.resolve(__dirname, '../vendor/vue-office-pdf/src/main.ts')
  const renderer = loadTsCommonJs(source, {}, {
    document,
    window: { devicePixelRatio: 2 },
    ResizeObserver: FakeResizeObserver,
  })
  const container = new FakeElement('div')
  container.clientWidth = 832
  container.clientHeight = 632
  const events = { loaded: [], pages: [], scales: [], errors: [] }
  const viewer = renderer.init(container, {
    openPdf,
    onDocumentLoaded: value => events.loaded.push(value),
    onPageChange: value => events.pages.push(value),
    onScaleChange: value => events.scales.push(value),
    onPageError: (error, page) => events.errors.push({ error, page }),
  })
  const wrapper = container.children[0]
  wrapper.clientWidth = 832
  wrapper.clientHeight = 632
  return { viewer, container, wrapper, pdf, renders, tasks, observers, events }
}

const flush = () => new Promise(resolve => setImmediate(resolve))

test('vendored vue-office renderer preserves source/license and removes old loaders and download', () => {
  const root = path.resolve(__dirname, '../vendor/vue-office-pdf')
  const source = fs.readFileSync(path.join(root, 'src/main.ts'), 'utf8')
  const provenance = fs.readFileSync(path.join(root, 'SOURCE.md'), 'utf8')
  const license = fs.readFileSync(path.join(root, 'core/LICENSE'), 'utf8')
  assert.match(provenance, /core\/packages\/js-pdf\/src\/main\.js/)
  assert.match(license, /Copyright \(c\) 2023 hit757/)
  assert.doesNotMatch(source, /unpkg|window\.pdfjsLib|pdfLibJsStr|workerStr|downloadFile/)
  assert.match(source, /openPdf/)
})

test('real adapter lays out mixed page sizes and renders only the visible virtual range', async () => {
  const pageSizes = Array.from({ length: 40 }, (_, index) => index % 2 === 0
    ? { width: 600, height: 800 }
    : { width: 1200, height: 600 })
  const h = rendererHarness({ pageSizes })
  await h.viewer.preview(new Uint8Array([1, 2, 3]))
  await flush()
  assert.deepEqual(h.events.loaded, [40])
  const canvases = h.container.querySelectorAll('canvas')
  assert.ok(canvases.length > 1 && canvases.length <= 6, `unexpected canvas count ${canvases.length}`)
  assert.notEqual(canvases[0].style.width, canvases[1].style.width)
  assert.notEqual(canvases[0].style.height, canvases[1].style.height)
  assert.ok(h.renders.every(render => render.options.canvas.width * render.options.canvas.height <= 16_000_000))
  assert.ok(h.renders.every(render => render.options.canvas.width <= 8192 && render.options.canvas.height <= 8192))
  await h.viewer.destroy()
})

test('page navigation, zoom, reset and virtual scrolling cancel obsolete renders', async () => {
  const pageSizes = Array.from({ length: 20 }, () => ({ width: 600, height: 800 }))
  const h = rendererHarness({ pageSizes, pendingRender: true })
  await h.viewer.preview(new Uint8Array([1]))
  await flush()
  const firstBatch = [...h.renders]
  assert.equal(firstBatch[0].options.viewport.height, 600)
  h.viewer.goToPage(10)
  await flush()
  assert.ok(firstBatch.every(render => render.cancelled))
  assert.equal(h.events.pages.at(-1), 10)
  const visiblePages = h.container.querySelectorAll('canvas').map(canvas => Number(canvas.dataset.pageNumber))
  assert.ok(visiblePages.length <= 7)
  assert.ok(visiblePages.every(page => {
    return page >= 7 && page <= 13
  }), `unexpected virtual pages ${visiblePages.join(',')}`)

  const beforeZoom = [...h.renders]
  h.viewer.setZoom(1.5)
  await flush()
  assert.ok(beforeZoom.every(render => render.cancelled))
  assert.equal(h.renders.at(-1).options.viewport.width, 900)
  h.viewer.setZoom('fit')
  await flush()
  assert.equal(h.events.scales.at(-1), 800 / 600)
  h.viewer.setZoom('page')
  await flush()
  assert.equal(h.events.scales.at(-1), 0.75)
  assert.equal(h.events.pages.at(-1), 10)
  await h.viewer.destroy()
  assert.ok(h.renders.every(render => render.cancelled))
})

test('adapter reports page failures and bounds a huge high-zoom backing store', async () => {
  const h = rendererHarness({ pageSizes: [{ width: 1_000_000_000_000, height: 1_000_000_000_000 }], failPage: 1 })
  await h.viewer.preview(new Uint8Array([1]))
  h.viewer.setZoom(4)
  await flush()
  assert.equal(h.events.errors.at(-1).page, 1)
  const render = h.renders.at(-1)
  assert.ok(render.options.canvas.width * render.options.canvas.height <= 16_000_000)
  assert.ok(render.options.canvas.width <= 8192 && render.options.canvas.height <= 8192)
  assert.equal(render.options.canvas.style.visibility, 'hidden')
  await h.viewer.destroy()
})

test('wide zoom expands the scroll surface and numeric resize mounts newly visible pages', async () => {
  const pageSizes = Array.from({ length: 20 }, () => ({ width: 600, height: 800 }))
  const h = rendererHarness({ pageSizes })
  await h.viewer.preview(new Uint8Array([1]))
  h.viewer.setZoom(4)
  await flush()

  const wrapperMain = h.wrapper.children[0]
  const canvas = h.container.querySelectorAll('canvas')[0]
  assert.equal(wrapperMain.style.minWidth, '100%')
  assert.equal(wrapperMain.style.width, '2432px')
  assert.equal(canvas.style.width, '2400px')
  // left:50% now centers against the expanded 2432px surface, leaving 16px
  // before the page instead of clipping 784px to the left of the scroll origin.
  assert.equal(Number(wrapperMain.style.width.slice(0, -2)) / 2 - Number(canvas.style.width.slice(0, -2)) / 2, 16)

  h.viewer.setZoom(1)
  await flush()
  const beforeResize = h.container.querySelectorAll('canvas').map(item => Number(item.dataset.pageNumber))
  assert.ok(Math.max(...beforeResize) <= 3)
  h.container.clientHeight = 4000
  h.wrapper.clientHeight = 4000
  h.viewer.resize()
  await flush()
  const afterResize = h.container.querySelectorAll('canvas').map(item => Number(item.dataset.pageNumber))
  assert.ok(Math.max(...afterResize) >= 7, `expected newly visible pages, got ${afterResize.join(',')}`)
  await h.viewer.destroy()
})

test('destroy cancels parsing/rendering and removes worker, observer, listener and DOM ownership', async () => {
  const parsing = rendererHarness({ pendingParse: true })
  const preview = parsing.viewer.preview(new Uint8Array([1]))
  await flush()
  await parsing.viewer.destroy()
  assert.equal(parsing.tasks[0].destroyed, true)
  assert.equal(parsing.observers[0].disconnected, true)
  assert.equal(parsing.wrapper.listeners.get('scroll').size, 0)
  assert.equal(parsing.container.children.length, 0)
  parsing.tasks[0].resolve(parsing.pdf)
  await preview
  assert.equal(parsing.renders.length, 0)

  const rendering = rendererHarness({ pendingRender: true })
  await rendering.viewer.preview(new Uint8Array([2]))
  await flush()
  await rendering.viewer.destroy()
  assert.ok(rendering.renders.every(render => render.cancelled))
  assert.ok(rendering.renders.every(render => render.options.canvas.width === 0 && render.options.canvas.height === 0))
})

function componentHarness({ pendingRequest = false, pendingPreview = false, failPreview = false } = {}) {
  const requests = []
  const viewers = []
  const component = name => props => React.createElement(name, props, props.children)
  const arco = new Proxy({}, { get: (_, name) => component(name) })
  const rendererMock = {
    init: (container, options) => {
      const item = {
        container,
        options,
        destroyed: false,
        previews: [],
        pages: [],
        zooms: [],
        preview(data) {
          const pending = deferred()
          this.previews.push({ data, ...pending })
          if (failPreview) pending.reject(new Error('invalid PDF'))
          else if (!pendingPreview) {
            options.onDocumentLoaded(3)
            options.onPageChange(1)
            options.onScaleChange(0.75)
            pending.resolve()
          }
          return pending.promise
        },
        goToPage(value) {
          const page = Math.min(3, Math.max(1, Math.trunc(value)))
          this.pages.push(page)
          options.onPageChange(page)
        },
        setZoom(value) {
          this.zooms.push(value)
          options.onScaleChange(value === 'page' ? 0.75 : value === 'fit' ? 4 / 3 : value)
        },
        destroy: async function () { this.destroyed = true },
      }
      viewers.push(item)
      return item
    },
  }
  const mocks = {
    '@arco-design/web-react': arco,
    'react-dom': { createPortal: (node, container) => React.createElement('portal', { container }, node) },
    '../api/client': { get: (url, options) => {
      const item = { url, options, ...deferred() }
      requests.push(item)
      if (!pendingRequest) item.resolve({ data: new ArrayBuffer(8) })
      return item.promise
    } },
    './pdfEngine': { openPdf: () => { throw new Error('adapter must own openPdf calls') } },
    '../../vendor/vue-office-pdf/src/main': rendererMock,
  }
  const filename = path.resolve(__dirname, '../src/components/PdfPreview.tsx')
  const exports = loadTsCommonJs(filename, mocks, { window: { devicePixelRatio: 2 } })
  let renderer
  const mount = async (fileId, toolbarContainer) => act(async () => {
    renderer = create(React.createElement(exports.default, { fileId, toolbarContainer }), {
      createNodeMock: () => ({ clientWidth: 832, clientHeight: 632 }),
    })
  })
  return {
    mount,
    requests,
    viewers,
    get renderer() { return renderer },
    update: (fileId, toolbarContainer) => act(async () => renderer.update(React.createElement(exports.default, { fileId, toolbarContainer }))),
  }
}

test('component keeps portal toolbar pagination, fit modes, zoom and reset without download', async () => {
  const h = componentHarness()
  const toolbarContainer = { nodeType: 1 }
  await h.mount(27, toolbarContainer)
  assert.equal(h.requests[0].url, '/files/27/content')
  assert.equal(h.requests[0].options.responseType, 'arraybuffer')
  assert.equal(h.renderer.root.findAllByType('iframe').length, 0)
  assert.equal(h.renderer.root.findByType('portal').props.container, toolbarContainer)
  assert.equal(h.viewers[0].previews[0].data.byteLength, 8)

  await act(async () => h.renderer.root.findByProps({ 'aria-label': '下一页' }).props.onClick())
  assert.equal(h.viewers[0].pages.at(-1), 2)
  await act(async () => h.renderer.root.findByProps({ 'aria-label': 'PDF 页码' }).props.onChange(99))
  assert.equal(h.viewers[0].pages.at(-1), 3)
  await act(async () => h.renderer.root.findByProps({ 'aria-label': 'PDF 缩放' }).props.onChange('fit'))
  assert.equal(h.viewers[0].zooms.at(-1), 'fit')
  await act(async () => h.renderer.root.findByProps({ 'aria-label': '放大 PDF' }).props.onClick())
  assert.equal(typeof h.viewers[0].zooms.at(-1), 'number')
  await act(async () => h.renderer.root.findByProps({ 'aria-label': '缩小 PDF' }).props.onClick())
  await act(async () => h.renderer.root.findByProps({ 'aria-label': '重置 PDF 缩放' }).props.onClick())
  assert.equal(h.viewers[0].zooms.at(-1), 'page')
  assert.doesNotMatch(JSON.stringify(h.renderer.toJSON()), /下载/)
  await act(async () => h.renderer.unmount())
  assert.equal(h.viewers[0].destroyed, true)
})

test('switching files and closing abort requests, destroy viewers and ignore late responses', async () => {
  const requests = componentHarness({ pendingRequest: true })
  await requests.mount(1)
  await requests.update(2)
  assert.equal(requests.requests[0].options.signal.aborted, true)
  await act(async () => requests.requests[0].resolve({ data: new ArrayBuffer(1) }))
  assert.equal(requests.viewers.length, 0)
  await act(async () => requests.requests[1].resolve({ data: new ArrayBuffer(2) }))
  assert.equal(requests.viewers.length, 1)
  await requests.update(3)
  assert.equal(requests.viewers[0].destroyed, true)
  await act(async () => requests.renderer.unmount())
  assert.equal(requests.requests[2].options.signal.aborted, true)
  await act(async () => requests.requests[2].resolve({ data: new ArrayBuffer(3) }))
  assert.equal(requests.viewers.length, 1)

  const parsing = componentHarness({ pendingPreview: true })
  await parsing.mount(4)
  assert.equal(parsing.viewers.length, 1)
  await act(async () => parsing.renderer.unmount())
  assert.equal(parsing.viewers[0].destroyed, true)
  await act(async () => {
    parsing.viewers[0].options.onDocumentLoaded(3)
    parsing.viewers[0].previews[0].resolve()
  })
})

test('load and page errors stay visible and retry creates a fresh owned viewer', async () => {
  const loading = componentHarness({ failPreview: true })
  await loading.mount(27)
  assert.match(JSON.stringify(loading.renderer.toJSON()), /PDF.*失败/)
  await act(async () => loading.renderer.root.findByProps({ 'aria-label': '重试 PDF 预览' }).props.onClick())
  assert.equal(loading.requests.length, 2)
  assert.equal(loading.viewers[0].destroyed, true)
  await act(async () => loading.renderer.unmount())

  const page = componentHarness()
  await page.mount(28)
  await act(async () => page.viewers[0].options.onPageError(new Error('decode'), 1))
  assert.match(JSON.stringify(page.renderer.toJSON()), /PDF 页面渲染失败/)
  await act(async () => page.renderer.unmount())
})
