const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const vm = require('node:vm')
const ts = require('typescript')

class Element {
  constructor(tag) {
    this.tagName = tag; this.children = []; this.attributes = {}
    this.style = { setProperty(name, value) { this[name] = value } }
    this.classList = { add() {} }
  }
  setAttribute(name, value) { this.attributes[name] = value }
  appendChild(child) { this.children.push(child); return child }
  append(...children) { this.children.push(...children) }
  get firstElementChild() { return this.children[0] }
}
async function loadEngine() {
  const { patchPptxRenderer } = await import('../vendor/vue-office-pptx/patch-renderer.mjs')
  const original = fs.readFileSync(path.resolve(__dirname, '../node_modules/pptx-preview/dist/pptx-preview.es.js'), 'utf8')
  const patched = patchPptxRenderer(original)
    .replace('export{ut as init};', 'export {W as paragraph, ht as shape, k as BaseNode};')
  const code = ts.transpileModule(patched, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
  const exports = {}
  vm.runInNewContext(code, {
    exports, require, console,
    document: { createElement: tag => new Element(tag), createElementNS: (_, tag) => new Element(tag) },
  })
  return { ...exports, original, patchPptxRenderer }
}
const paragraph = props => ({ inheritProps: {}, inheritRProps: {}, props,
  rows: [{ text: '搜索', props: { size: 36, typeface: '宋体' } }] })

test('paragraph layout has no invented top margin while preserving explicit fractional spacing', async () => {
  const engine = await loadEngine()
  const result = engine.paragraph(paragraph({ spaceBefore: 2.4, spaceAfter: 3.6 }))
  assert.equal(result.style.margin, '0')
  assert.equal(result.style.padding, '2.4px 0px 3.6px 0px')
  assert.equal(result.firstElementChild.style.lineHeight, '1.2')
  assert.equal(engine.paragraph(paragraph({ lineHeight: 1.5 })).firstElementChild.style.lineHeight, '1.5')
})

test('text boxes honor DrawingML default and explicit fractional/zero insets', async () => {
  const engine = await loadEngine()
  const shape = props => engine.shape({ offset: { x: 0, y: 0 }, extend: { w: 210.65, h: 50.89 },
    shape: 'rect', border: {}, prstGeom: {}, isTextBox: true,
    textBody: { inheritProps: {}, props, paragraphs: [paragraph({})] } })
  const defaultText = shape({}).children.find(child => child.className === 'text-wrapper')
  assert.equal(defaultText.style.padding, '3.6px 7.2px 3.6px 7.2px')
  const customText = shape({ lIns: 0, rIns: 1.25, tIns: 2.75, bIns: 0 }).children.find(child => child.className === 'text-wrapper')
  assert.equal(customText.style.padding, '2.75px 1.25px 0px 0px')
})

test('shape positions and sizes retain sub-point precision before browser zoom', async () => {
  const { BaseNode } = await loadEngine()
  const shape = new BaseNode({ 'p:spPr': { 'a:xfrm': { 'a:off': { attrs: { x: '2735857', y: '943804' } }, 'a:ext': { attrs: { cx: '2675262', cy: '646331' } } } } }, {})
  assert.equal(shape.offset.x, 2735857 / 12700)
  assert.equal(shape.offset.y, 943804 / 12700)
  assert.equal(shape.extend.w, 2675262 / 12700)
  assert.equal(shape.extend.h, 646331 / 12700)
})

test('dependency changes cannot silently omit the renderer layout fixes', async () => {
  const { original, patchPptxRenderer } = await loadEngine()
  assert.throws(() => patchPptxRenderer(original.replace('Math.floor(.2*u())', '0')), /anchor changed/)
})
