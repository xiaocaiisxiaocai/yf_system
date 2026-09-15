const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const vm = require('node:vm')
const A = 'http://schemas.openxmlformats.org/drawingml/2006/main'
const filename = path.resolve(__dirname, '../vendor/vue-office-pptx/normalize-pptx.js')
const source = fs.readFileSync(filename, 'utf8').replace(/^import .*$/m, '')
  .replace('export async function normalizePptx', 'async function normalizePptx')
const moduleValue = { exports: {} }
vm.runInNewContext(source + '\nmodule.exports={resolveColor,relativePart};', { module: moduleValue })
const { resolveColor, relativePart } = moduleValue.exports

function node(localName, attrs = {}, children = []) {
  return {
    localName, namespaceURI: A, children,
    getAttribute: key => attrs[key] ?? null,
    getElementsByTagNameNS(ns, name) {
      return children.flatMap(item => [
        ...(item.namespaceURI === ns && (name === '*' || item.localName === name) ? [item] : []),
        ...item.getElementsByTagNameNS(ns, name),
      ])
    },
  }
}
const hex = color => Array.from(color.channels, value => Math.round(value * 255).toString(16).padStart(2, '0')).join('').toUpperCase()

test('theme placeholder gradient applies tint in linear light, not a black or HSL-flat fill', () => {
  const black = node('srgbClr', { val: '000000' })
  const start = resolveColor(node('schemeClr', { val: 'phClr' }, [node('lumMod', { val: '110000' }), node('satMod', { val: '105000' }), node('tint', { val: '67000' })]), null, null, black)
  const end = resolveColor(node('schemeClr', { val: 'phClr' }, [node('tint', { val: '81000' })]), null, null, black)
  assert.equal(hex(start), '9B9B9B')
  assert.equal(hex(end), '797979')
})

test('color map aliases resolve theme system colors and preserve opacity including zero', () => {
  const theme = node('theme', {}, [node('clrScheme', {}, [node('dk1', {}, [node('sysClr', { val: 'windowText', lastClr: '102030' })])])])
  const value = resolveColor(node('schemeClr', { val: 'tx1' }, [node('alpha', { val: '0' })]), theme)
  assert.equal(hex(value), '102030')
  assert.equal(value.alpha, 0)
})

test('unknown transforms and unresolved placeholders are left to the original engine', () => {
  assert.equal(resolveColor(node('srgbClr', { val: '445566' }, [node('unknownTransform', { val: '90000' })])), null)
  assert.equal(resolveColor(node('schemeClr', { val: 'phClr' })), null)
})

test('package relationship resolution follows sibling parts and refuses escaping the ZIP root', () => {
  assert.equal(relativePart('ppt/slides/slide1.xml', '../slideLayouts/slideLayout2.xml'), 'ppt/slideLayouts/slideLayout2.xml')
  assert.equal(relativePart('ppt/slideMasters/slideMaster1.xml', '../theme/theme1.xml'), 'ppt/theme/theme1.xml')
  assert.equal(relativePart('ppt/slides/slide1.xml', '../../../outside.xml'), null)
})
