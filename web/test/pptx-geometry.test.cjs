const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const vm = require('node:vm')

const DRAWING_NS = 'http://schemas.openxmlformats.org/drawingml/2006/main'
const PRESENTATION_NS = 'http://schemas.openxmlformats.org/presentationml/2006/main'

class Element {
  constructor(ownerDocument, namespaceURI, qualifiedName) {
    this.ownerDocument = ownerDocument
    this.namespaceURI = namespaceURI
    this.localName = qualifiedName.split(':').at(-1)
    this.nodeType = 1
    this.parentNode = null
    this.childNodes = []
    this.attributes = new Map()
  }

  appendChild(child) {
    child.parentNode = this
    this.childNodes.push(child)
    return child
  }

  replaceChild(next, previous) {
    const index = this.childNodes.indexOf(previous)
    assert.notEqual(index, -1)
    previous.parentNode = null
    next.parentNode = this
    this.childNodes[index] = next
    return previous
  }

  setAttribute(name, value) { this.attributes.set(name, String(value)) }
  getAttribute(name) { return this.attributes.get(name) ?? null }

  getElementsByTagNameNS(namespaceURI, localName) {
    const found = []
    const visit = node => {
      for (const child of node.childNodes) {
        if (child.namespaceURI === namespaceURI && child.localName === localName) found.push(child)
        visit(child)
      }
    }
    visit(this)
    return found
  }
}

class Document {
  constructor() {
    this.documentElement = this.createElementNS(PRESENTATION_NS, 'p:sld')
  }

  createElementNS(namespaceURI, qualifiedName) {
    return new Element(this, namespaceURI, qualifiedName)
  }

  getElementsByTagNameNS(namespaceURI, localName) {
    const result = []
    if (this.documentElement.namespaceURI === namespaceURI && this.documentElement.localName === localName) {
      result.push(this.documentElement)
    }
    return result.concat(this.documentElement.getElementsByTagNameNS(namespaceURI, localName))
  }
}

function loadNormalizer() {
  const filename = path.resolve(__dirname, '../vendor/vue-office-pptx/geometry.js')
  const source = fs.readFileSync(filename, 'utf8')
    .replace('export function normalizeGeometry', 'function normalizeGeometry')
    .concat('\nmodule.exports = { normalizeGeometry }\n')
  const module = { exports: {} }
  vm.runInNewContext(source, { module, exports: module.exports }, { filename })
  return module.exports.normalizeGeometry
}

function add(parent, namespaceURI, qualifiedName, attributes = {}) {
  const node = parent.ownerDocument.createElementNS(namespaceURI, qualifiedName)
  for (const [name, value] of Object.entries(attributes)) node.setAttribute(name, value)
  parent.appendChild(node)
  return node
}

function shape(document, preset, width, height, adjustments = {}) {
  const spPr = add(document.documentElement, PRESENTATION_NS, 'p:spPr')
  const xfrm = add(spPr, DRAWING_NS, 'a:xfrm', { rot: 1200000, flipH: 1 })
  const ext = add(xfrm, DRAWING_NS, 'a:ext', { cx: width, cy: height })
  const prstGeom = add(spPr, DRAWING_NS, 'a:prstGeom', { prst: preset })
  const avLst = add(prstGeom, DRAWING_NS, 'a:avLst')
  for (const [name, value] of Object.entries(adjustments)) {
    add(avLst, DRAWING_NS, 'a:gd', { name, fmla: `val ${value}` })
  }
  const noFill = add(spPr, DRAWING_NS, 'a:noFill')
  const line = add(spPr, DRAWING_NS, 'a:ln', { w: 12700 })
  return { spPr, xfrm, ext, prstGeom, noFill, line }
}

function direct(node, name) {
  return node.childNodes.find(child => child.namespaceURI === DRAWING_NS && child.localName === name)
}

function customPath(spPr) {
  const custGeom = direct(spPr, 'custGeom')
  return direct(direct(custGeom, 'pathLst'), 'path')
}

function points(pathNode) {
  return pathNode.childNodes.flatMap(command =>
    command.childNodes.map(point => ({
      command: command.localName,
      x: Number(point.getAttribute('x')),
      y: Number(point.getAttribute('y')),
    })))
}

test('normalizes the flowchart connector to the engine-equivalent ellipse only', () => {
  const document = new Document()
  const fixture = shape(document, 'flowChartConnector', 528810, 495760)

  assert.equal(loadNormalizer()(document), 1)
  assert.equal(fixture.prstGeom.getAttribute('prst'), 'ellipse')
  assert.equal(fixture.prstGeom.parentNode, fixture.spPr)
  assert.equal(direct(fixture.spPr, 'xfrm'), fixture.xfrm)
  assert.equal(direct(fixture.spPr, 'noFill'), fixture.noFill)
  assert.equal(direct(fixture.spPr, 'ln'), fixture.line)
})

test('right brace uses the real tall-slide extent and stays within its geometry box', () => {
  const document = new Document()
  const width = 658075
  const height = 3750025
  const fixture = shape(document, 'rightBrace', width, height)

  assert.equal(loadNormalizer()(document), 1)
  const pathNode = customPath(fixture.spPr)
  assert.equal(pathNode.getAttribute('w'), String(width))
  assert.equal(pathNode.getAttribute('h'), String(height))
  assert.equal(pathNode.getAttribute('fill'), 'none')
  assert.deepEqual(pathNode.childNodes.map(node => node.localName),
    ['moveTo', 'cubicBezTo', 'lnTo', 'cubicBezTo', 'cubicBezTo', 'lnTo', 'cubicBezTo'])

  const allPoints = points(pathNode)
  assert.deepEqual(allPoints[0], { command: 'moveTo', x: 0, y: 0 })
  assert.deepEqual(allPoints.at(-1), { command: 'cubicBezTo', x: 0, y: height })
  assert.ok(allPoints.some(point => point.x === width && Math.abs(point.y - height / 2) <= 1))
  assert.ok(allPoints.every(point => point.x >= 0 && point.x <= width && point.y >= 0 && point.y <= height))
  assert.equal(direct(fixture.spPr, 'xfrm'), fixture.xfrm)
  assert.equal(fixture.xfrm.getAttribute('rot'), '1200000')
  assert.equal(fixture.xfrm.getAttribute('flipH'), '1')
  assert.equal(direct(fixture.spPr, 'noFill'), fixture.noFill)
  assert.equal(direct(fixture.spPr, 'ln'), fixture.line)
})

test('brace adjustments are clamped by aspect ratio and left brace mirrors x coordinates', () => {
  const rightDocument = new Document()
  const leftDocument = new Document()
  const width = 200
  const height = 1000
  const right = shape(rightDocument, 'rightBrace', width, height, { adj1: 50000, adj2: 10000 })
  const left = shape(leftDocument, 'leftBrace', width, height, { adj1: 50000, adj2: 10000 })
  const normalize = loadNormalizer()

  normalize(rightDocument)
  normalize(leftDocument)
  const rightPoints = points(customPath(right.spPr))
  const leftPoints = points(customPath(left.spPr))
  assert.deepEqual(leftPoints.map(point => point.x), rightPoints.map(point => width - point.x))
  assert.deepEqual(leftPoints.map(point => point.y), rightPoints.map(point => point.y))
  assert.ok(rightPoints.every(point => point.x >= 0 && point.x <= width && point.y >= 0 && point.y <= height))
})

test('left and right brackets mirror and clamp their corner radius to the shape bounds', () => {
  const normalize = loadNormalizer()
  const width = 1000
  const height = 200
  const rightDocument = new Document()
  const leftDocument = new Document()
  const right = shape(rightDocument, 'rightBracket', width, height, { adj: 99999 })
  const left = shape(leftDocument, 'leftBracket', width, height, { adj: 99999 })

  normalize(rightDocument)
  normalize(leftDocument)
  const rightPath = customPath(right.spPr)
  const rightPoints = points(rightPath)
  const leftPoints = points(customPath(left.spPr))
  assert.deepEqual(rightPath.childNodes.map(node => node.localName),
    ['moveTo', 'cubicBezTo', 'lnTo', 'cubicBezTo'])
  assert.deepEqual(leftPoints.map(point => point.x), rightPoints.map(point => width - point.x))
  assert.deepEqual(leftPoints.map(point => point.y), rightPoints.map(point => point.y))
  assert.ok(rightPoints.every(point => point.x >= 0 && point.x <= width && point.y >= 0 && point.y <= height))
})

test('leaves supported presets and malformed extents unchanged', () => {
  const document = new Document()
  const supported = shape(document, 'rect', 100, 100)
  const malformed = shape(document, 'rightBrace', 0, 100)

  assert.equal(loadNormalizer()(document), 0)
  assert.equal(direct(supported.spPr, 'prstGeom'), supported.prstGeom)
  assert.equal(direct(malformed.spPr, 'prstGeom'), malformed.prstGeom)
})
