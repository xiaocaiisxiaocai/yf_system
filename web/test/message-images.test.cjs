const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const vm = require('node:vm')
const ts = require('typescript')

function load() {
  const filename = path.resolve(__dirname, '../src/components/MessageImages.tsx')
  const code = ts.transpileModule(fs.readFileSync(filename, 'utf8'), {
    compilerOptions: {
      module: ts.ModuleKind.CommonJS,
      jsx: ts.JsxEmit.ReactJSX,
      target: ts.ScriptTarget.ES2022,
      esModuleInterop: true,
    },
  }).outputText
  const errors = []
  const exports = {}
  vm.runInNewContext(code, {
    exports,
    module: { exports },
    console,
    AbortController,
    IntersectionObserver: undefined,
    URL,
    require(name) {
      if (name === './MessageImages.css') return {}
      if (name === './ImagePreview') return () => null
      if (name === '../api/client') return { get: async () => ({ data: new Blob() }) }
      if (name === '@arco-design/web-react') return {
        Button: () => null,
        Message: { error: (message) => errors.push(message) },
        Modal: () => null,
        Result: () => null,
        Spin: () => null,
        Tooltip: () => null,
      }
      if (name === '@arco-design/web-react/icon') return new Proxy({}, { get: () => () => null })
      return require(name)
    },
  }, { filename })
  return { pasteMessageImages: exports.pasteMessageImages, errors }
}

function clipboardEvent(files) {
  let prevented = false
  return {
    clipboardData: {
      items: files.map((file) => ({ kind: 'file', type: file.type, getAsFile: () => file })),
      files,
    },
    preventDefault() { prevented = true },
    get prevented() { return prevented },
  }
}

test('message image paste leaves ordinary text paste untouched', () => {
  const env = load()
  const event = clipboardEvent([])
  let changed = false
  env.pasteMessageImages(event, [], () => { changed = true })
  assert.equal(event.prevented, false)
  assert.equal(changed, false)
  assert.deepEqual(env.errors, [])
})

test('message image paste enforces the 50 MiB combined boundary', () => {
  const env = load()
  const current = [{ name: 'first.png', type: 'image/png', size: 40 * 1024 * 1024 }]
  const incoming = { name: 'second.jpg', type: 'image/jpeg', size: 11 * 1024 * 1024 }
  const event = clipboardEvent([incoming])
  let next
  env.pasteMessageImages(event, current, (files) => { next = files })
  assert.equal(event.prevented, true)
  assert.equal(next, undefined)
  assert.deepEqual(env.errors, ['本次图片总量最多 50 MiB'])
})

test('message image paste accepts an exact 50 MiB combined payload', () => {
  const env = load()
  const current = [{ name: 'first.png', type: 'image/png', size: 40 * 1024 * 1024 }]
  const incoming = { name: 'second.webp', type: 'image/webp', size: 10 * 1024 * 1024 }
  const event = clipboardEvent([incoming])
  let next
  env.pasteMessageImages(event, current, (files) => { next = files })
  assert.equal(event.prevented, true)
  assert.equal(next.length, 2)
  assert.equal(next[1], incoming)
  assert.deepEqual(env.errors, [])
})

test('message image paste rejects files beyond nine images', () => {
  const env = load()
  const current = Array.from({ length: 9 }, (_, index) => ({
    name: `${index}.png`, type: 'image/png', size: 1,
  }))
  const incoming = { name: 'tenth.png', type: 'image/png', size: 1 }
  const event = clipboardEvent([incoming])
  let changed = false
  env.pasteMessageImages(event, current, () => { changed = true })
  assert.equal(event.prevented, true)
  assert.equal(changed, false)
  assert.deepEqual(env.errors, ['每条留言最多添加 9 张图片'])
})
