const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const vm = require('node:vm')
const ts = require('typescript')
const React = require('react')
const { create, act } = require('react-test-renderer')

function findElement(node, predicate) {
  if (!React.isValidElement(node)) return undefined
  if (predicate(node)) return node
  for (const child of React.Children.toArray(node.props.children)) {
    const found = findElement(child, predicate)
    if (found) return found
  }
  return undefined
}

function loadFileTable() {
  const component = name => props => React.createElement(name, props, props.children)
  const arco = new Proxy({
    Message: { success() {} },
    Input: Object.assign(component('Input'), { Search: component('Input.Search') }),
    Select: Object.assign(component('Select'), { Option: component('Select.Option') }),
    Typography: { Text: component('Text') },
  }, { get: (target, name) => target[name] ?? component(name) })
  const mocks = {
    '@arco-design/web-react': arco,
    '@arco-design/web-react/icon': new Proxy({}, { get: (_, name) => component(name) }),
    '../api/client': { get: async () => ({ data: { list: [], total: 0, page: 1, pageSize: 10 } }) },
    '../store/auth': { useAuth: () => ({ hasPerm: code => code === 'file:preview' }) },
    '../api/types': { fmtSize: String, fmtTime: String },
    './ActionSlots': { actionSlots: () => null },
    './ChunkUploader': component('ChunkUploader'),
    './PdfPreview': component('PdfPreview'),
  }
  const filename = path.resolve(__dirname, '../src/components/FileTable.tsx')
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
    require: name => mocks[name] ?? (name.endsWith('/store/collaboration') ? { useCollaboration: (selector) => selector({ revision: '' }) } : require(name)),
  }
  vm.runInNewContext(source, context, { filename })
  return exports.default
}

function loadExcelPreview(http, browserWindow = { addEventListener() {}, removeEventListener() {} }) {
  const component = name => props => React.createElement(name, props, props.children)
  const arco = new Proxy({
    Select: Object.assign(component('Select'), { Option: component('Select.Option') }),
    Typography: { Text: component('Text') },
  }, { get: (target, name) => target[name] ?? component(name) })
  const mocks = {
    '@arco-design/web-react': arco,
    '../../generated/excel-viewer.html?raw': '<html></html>',
    '../api/client': http,
  }
  const filename = path.resolve(__dirname, '../src/components/ExcelPreview.tsx')
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
    AbortController, setTimeout, clearTimeout, crypto: require('node:crypto').webcrypto,
    window: browserWindow,
    require: name => mocks[name] ?? require(name),
  }
  vm.runInNewContext(source, context, { filename })
  return exports.default
}

test('preview dispatch offers PPTX within the parser limit and streamed videos independently of document size', async () => {
  const FileTable = loadFileTable()
  let renderer
  await act(async () => { renderer = create(React.createElement(FileTable, { projectId: 1, projectStatus: 'IN_PROGRESS' })) })
  const column = renderer.root.findByType('Table').props.columns[0]
  const available = (ext, sizeBytes) => !!findElement(column.render(`test.${ext}`, {
    id: 7, originalName: `test.${ext}`, ext, sizeBytes,
  }), element => element.props['aria-label'] === '预览文件')
  assert.equal(available('PPTX', 50 * 1024 * 1024), true)
  assert.equal(available('pptx', 50 * 1024 * 1024 + 1), false)
  for (const ext of ['mp4', 'webm', 'ogv']) assert.equal(available(ext, 1024 * 1024 * 1024), true)
  for (const ext of ['ppt', 'html', 'avi', 'mkv']) assert.equal(available(ext, 1024), false)
  await act(async () => renderer.unmount())
})

test('PDF preview accepts the 50 MiB boundary and leaves larger files download-only', async () => {
  const FileTable = loadFileTable()
  let renderer
  await act(async () => {
    renderer = create(React.createElement(FileTable, { projectId: 1, projectStatus: 'IN_PROGRESS' }))
  })
  const fileNameColumn = renderer.root.findByType('Table').props.columns[0]
  const row = sizeBytes => ({ id: sizeBytes, originalName: 'drawing.pdf', ext: 'pdf', sizeBytes })
  const previewButton = sizeBytes => findElement(
    fileNameColumn.render('drawing.pdf', row(sizeBytes)),
    node => node.props['aria-label'] === '预览文件',
  )
  assert.ok(previewButton(50 * 1024 * 1024))
  assert.equal(previewButton(50 * 1024 * 1024 + 1), undefined)
  await act(async () => renderer.unmount())
})

test('Excel preview aborts its content request and never posts late bytes after unmount', async () => {
  let resolveDownload
  let requestSignal
  let posted = 0
  const download = new Promise(resolve => { resolveDownload = resolve })
  const ExcelPreview = loadExcelPreview({
    get: async (_url, options) => {
      requestSignal = options.signal
      return download
    },
  })

  let renderer
  await act(async () => {
    renderer = create(React.createElement(ExcelPreview, { fileId: 7 }), { createNodeMock: () => ({ contentWindow: { postMessage: () => posted++ } }) })
  })
  await act(async () => {
    renderer.root.findByType('iframe').props.onLoad()
    renderer.unmount()
  })
  await act(async () => {
    resolveDownload({ data: new Uint8Array([0x50, 0x4b]).buffer })
    await download
    await Promise.resolve()
  })

  assert.deepEqual(
    { aborted: requestSignal?.aborted === true, posted },
    { aborted: true, posted: 0 },
  )
})

test('Excel preview waits for its frame and accepts status only from that frame and channel', async () => {
  const listeners = new Set()
  const messages = []
  const contentWindow = { postMessage: message => messages.push(message) }
  const ExcelPreview = loadExcelPreview({ get: async () => ({ data: new Uint8Array([0x50, 0x4b, 3, 4]).buffer }) }, {
    addEventListener: (_type, listener) => listeners.add(listener),
    removeEventListener: (_type, listener) => listeners.delete(listener),
  })
  let renderer
  try {
    await act(async () => { renderer = create(React.createElement(ExcelPreview, { fileId: 9 }), { createNodeMock: () => ({ contentWindow }) }) })
    assert.equal(messages.length, 0)
    const frame = () => renderer.root.findByType('iframe')
    assert.equal(frame().props.sandbox, 'allow-scripts')
    await act(async () => frame().props.onLoad())
    assert.equal(messages.length, 1)
    const channel = messages[0].channel
    const emit = event => act(async () => { for (const listener of listeners) listener(event) })
    await emit({ source: {}, data: { type: 'excel:rendered', channel } })
    await emit({ source: contentWindow, data: { type: 'excel:rendered', channel: 'wrong' } })
    assert.equal(frame().props.style.visibility, 'hidden')
    await emit({ source: contentWindow, data: { type: 'excel:rendered', channel } })
    assert.equal(frame().props.style.visibility, 'visible')
  } finally { if (renderer) await act(async () => renderer.unmount()) }
  assert.equal(listeners.size, 0)
})

test('Excel preview retry fetches a new buffer and waits for the replacement frame', async () => {
  let requests = 0
  let listener
  const messages = []
  const contentWindow = { postMessage: message => messages.push(message) }
  const ExcelPreview = loadExcelPreview({ get: async () => { requests++; return { data: new Uint8Array([0x50, 0x4b, 3, 4]).buffer } } }, {
    addEventListener: (_type, next) => { listener = next }, removeEventListener() {},
  })
  let renderer
  try {
    await act(async () => { renderer = create(React.createElement(ExcelPreview, { fileId: 10 }), { createNodeMock: () => ({ contentWindow }) }) })
    await act(async () => renderer.root.findByType('iframe').props.onLoad())
    await act(async () => listener({ source: contentWindow, data: { type: 'excel:error', channel: messages[0].channel } }))
    const retry = renderer.root.findByType('Result').props.extra
    await act(async () => retry.props.onClick())
    assert.equal(requests, 2)
    assert.equal(messages.length, 1)
    await act(async () => renderer.root.findByType('iframe').props.onLoad())
    assert.equal(messages.length, 2)
    assert.notEqual(messages[0].buffer, messages[1].buffer)
  } finally { if (renderer) await act(async () => renderer.unmount()) }
})
