import { describe, expect, it } from 'vitest'

import { normalizeGeometry } from '../../../../vendor/vue-office-pptx/geometry.js'

const DRAWING_NS = 'http://schemas.openxmlformats.org/drawingml/2006/main'
const PRESENTATION_NS = 'http://schemas.openxmlformats.org/presentationml/2006/main'

function presentationDocument() {
  return new DOMParser().parseFromString(
    `<p:sld xmlns:p="${PRESENTATION_NS}" xmlns:a="${DRAWING_NS}"/>`,
    'application/xml',
  )
}

function add(parent: Element, namespace: string, qualifiedName: string, attributes: Record<string, string | number> = {}) {
  const node = parent.ownerDocument.createElementNS(namespace, qualifiedName)
  for (const [name, value] of Object.entries(attributes)) node.setAttribute(name, String(value))
  parent.append(node)
  return node
}

function shape(document: XMLDocument, preset: string, width: number, height: number, adjustments: Record<string, number> = {}) {
  const spPr = add(document.documentElement, PRESENTATION_NS, 'p:spPr')
  const xfrm = add(spPr, DRAWING_NS, 'a:xfrm', { rot: 1200000, flipH: 1 })
  add(xfrm, DRAWING_NS, 'a:ext', { cx: width, cy: height })
  const prstGeom = add(spPr, DRAWING_NS, 'a:prstGeom', { prst: preset })
  const avLst = add(prstGeom, DRAWING_NS, 'a:avLst')
  for (const [name, value] of Object.entries(adjustments)) {
    add(avLst, DRAWING_NS, 'a:gd', { name, fmla: `val ${value}` })
  }
  const noFill = add(spPr, DRAWING_NS, 'a:noFill')
  const line = add(spPr, DRAWING_NS, 'a:ln', { w: 12700 })
  return { spPr, xfrm, prstGeom, noFill, line }
}

function direct(node: Element, name: string) {
  return Array.from(node.children).find(child => child.namespaceURI === DRAWING_NS && child.localName === name)
}

function customPath(spPr: Element) {
  const custom = direct(spPr, 'custGeom')
  const pathList = direct(custom!, 'pathLst')
  return direct(pathList!, 'path')!
}

function points(path: Element) {
  return Array.from(path.children).flatMap(command =>
    Array.from(command.children).map(point => ({
      command: command.localName,
      x: Number(point.getAttribute('x')),
      y: Number(point.getAttribute('y')),
    })),
  )
}

describe('PPTX geometry normalization migrated from node:test', () => {
  it('normalizes the flowchart connector to the engine-equivalent ellipse only', () => {
    const document = presentationDocument()
    const fixture = shape(document, 'flowChartConnector', 528810, 495760)

    expect(normalizeGeometry(document)).toBe(1)
    expect(fixture.prstGeom.getAttribute('prst')).toBe('ellipse')
    expect(fixture.prstGeom.parentNode).toBe(fixture.spPr)
    expect(direct(fixture.spPr, 'xfrm')).toBe(fixture.xfrm)
    expect(direct(fixture.spPr, 'noFill')).toBe(fixture.noFill)
    expect(direct(fixture.spPr, 'ln')).toBe(fixture.line)
  })

  it('right brace uses the real tall-slide extent and stays within its geometry box', () => {
    const document = presentationDocument()
    const width = 658075
    const height = 3750025
    const fixture = shape(document, 'rightBrace', width, height)

    expect(normalizeGeometry(document)).toBe(1)
    const path = customPath(fixture.spPr)
    expect(path.getAttribute('w')).toBe(String(width))
    expect(path.getAttribute('h')).toBe(String(height))
    expect(path.getAttribute('fill')).toBe('none')
    expect(Array.from(path.children).map(node => node.localName)).toEqual([
      'moveTo', 'cubicBezTo', 'lnTo', 'cubicBezTo', 'cubicBezTo', 'lnTo', 'cubicBezTo',
    ])

    const allPoints = points(path)
    expect(allPoints[0]).toEqual({ command: 'moveTo', x: 0, y: 0 })
    expect(allPoints.at(-1)).toEqual({ command: 'cubicBezTo', x: 0, y: height })
    expect(allPoints.some(point => point.x === width && Math.abs(point.y - height / 2) <= 1)).toBe(true)
    expect(allPoints.every(point => point.x >= 0 && point.x <= width && point.y >= 0 && point.y <= height)).toBe(true)
    expect(direct(fixture.spPr, 'xfrm')).toBe(fixture.xfrm)
    expect(fixture.xfrm.getAttribute('rot')).toBe('1200000')
    expect(fixture.xfrm.getAttribute('flipH')).toBe('1')
    expect(direct(fixture.spPr, 'noFill')).toBe(fixture.noFill)
    expect(direct(fixture.spPr, 'ln')).toBe(fixture.line)
  })

  it('brace adjustments are clamped by aspect ratio and left brace mirrors x coordinates', () => {
    const rightDocument = presentationDocument()
    const leftDocument = presentationDocument()
    const width = 200
    const height = 1000
    const right = shape(rightDocument, 'rightBrace', width, height, { adj1: 50000, adj2: 10000 })
    const left = shape(leftDocument, 'leftBrace', width, height, { adj1: 50000, adj2: 10000 })

    normalizeGeometry(rightDocument)
    normalizeGeometry(leftDocument)
    const rightPoints = points(customPath(right.spPr))
    const leftPoints = points(customPath(left.spPr))
    expect(leftPoints.map(point => point.x)).toEqual(rightPoints.map(point => width - point.x))
    expect(leftPoints.map(point => point.y)).toEqual(rightPoints.map(point => point.y))
    expect(rightPoints.every(point => point.x >= 0 && point.x <= width && point.y >= 0 && point.y <= height)).toBe(true)
  })

  it('left and right brackets mirror and clamp their corner radius to the shape bounds', () => {
    const width = 1000
    const height = 200
    const rightDocument = presentationDocument()
    const leftDocument = presentationDocument()
    const right = shape(rightDocument, 'rightBracket', width, height, { adj: 99999 })
    const left = shape(leftDocument, 'leftBracket', width, height, { adj: 99999 })

    normalizeGeometry(rightDocument)
    normalizeGeometry(leftDocument)
    const rightPath = customPath(right.spPr)
    const rightPoints = points(rightPath)
    const leftPoints = points(customPath(left.spPr))
    expect(Array.from(rightPath.children).map(node => node.localName)).toEqual([
      'moveTo', 'cubicBezTo', 'lnTo', 'cubicBezTo',
    ])
    expect(leftPoints.map(point => point.x)).toEqual(rightPoints.map(point => width - point.x))
    expect(leftPoints.map(point => point.y)).toEqual(rightPoints.map(point => point.y))
    expect(rightPoints.every(point => point.x >= 0 && point.x <= width && point.y >= 0 && point.y <= height)).toBe(true)
  })

  it('leaves supported presets and malformed extents unchanged', () => {
    const document = presentationDocument()
    const supported = shape(document, 'rect', 100, 100)
    const malformed = shape(document, 'rightBrace', 0, 100)

    expect(normalizeGeometry(document)).toBe(0)
    expect(direct(supported.spPr, 'prstGeom')).toBe(supported.prstGeom)
    expect(direct(malformed.spPr, 'prstGeom')).toBe(malformed.prstGeom)
  })
})
