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
    require: name => mocks[name] ?? require(name),
  }
  vm.runInNewContext(source, context, { filename })
  return exports.default
}

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
