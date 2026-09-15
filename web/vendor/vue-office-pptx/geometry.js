const DRAWING_NS = 'http://schemas.openxmlformats.org/drawingml/2006/main'
const KAPPA = 0.5522847498307936

const clamp = (value, min, max) => Math.min(max, Math.max(min, value))
const rounded = value => String(Math.round(value))

function directChild(element, localName) {
  return Array.from(element?.childNodes || []).find(node =>
    node.nodeType === 1 && node.namespaceURI === DRAWING_NS && node.localName === localName)
}

function adjustment(prstGeom, name, fallback) {
  const avLst = directChild(prstGeom, 'avLst')
  const guide = Array.from(avLst?.childNodes || []).find(node =>
    node.nodeType === 1 && node.namespaceURI === DRAWING_NS &&
    node.localName === 'gd' && node.getAttribute('name') === name)
  const match = guide?.getAttribute('fmla')?.match(/^\s*val\s+(-?(?:\d+(?:\.\d*)?|\.\d+))\s*$/)
  const value = match ? Number(match[1]) : fallback
  return Number.isFinite(value) ? value : fallback
}

function element(document, localName, attributes = {}) {
  const node = document.createElementNS(DRAWING_NS, `a:${localName}`)
  for (const [name, value] of Object.entries(attributes)) node.setAttribute(name, String(value))
  return node
}

function command(document, localName, points = []) {
  const node = element(document, localName)
  for (const [x, y] of points) node.appendChild(element(document, 'pt', {
    x: rounded(x),
    y: rounded(y),
  }))
  return node
}

function extentFor(prstGeom) {
  const spPr = prstGeom.parentNode
  const xfrm = directChild(spPr, 'xfrm')
  const ext = directChild(xfrm, 'ext')
  const width = Number(ext?.getAttribute('cx'))
  const height = Number(ext?.getAttribute('cy'))
  return Number.isFinite(width) && width > 0 && Number.isFinite(height) && height > 0
    ? { width, height }
    : null
}

function bracePoints(prstGeom, width, height, mirror) {
  const shortSide = Math.min(width, height)
  const waist = clamp(adjustment(prstGeom, 'adj2', 50000), 0, 100000)
  const balancedHalf = Math.min(waist, 100000 - waist) / 2
  const maxThickness = balancedHalf * height / shortSide
  const thickness = clamp(adjustment(prstGeom, 'adj1', 8333), 0, maxThickness)
  const radiusY = shortSide * thickness / 100000
  const radiusX = width / 2
  const middleY = height * waist / 100000
  const upperWaistY = middleY - radiusY
  const lowerWaistY = middleY + radiusY
  const lowerEndY = height - radiusY
  const x = value => mirror ? width - value : value

  return [
    ['moveTo', [[x(0), 0]]],
    ['cubicBezTo', [[x(KAPPA * radiusX), 0], [x(radiusX), radiusY * (1 - KAPPA)], [x(radiusX), radiusY]]],
    ['lnTo', [[x(radiusX), upperWaistY]]],
    ['cubicBezTo', [[x(radiusX), upperWaistY + KAPPA * radiusY], [x(width - KAPPA * radiusX), middleY], [x(width), middleY]]],
    ['cubicBezTo', [[x(width - KAPPA * radiusX), middleY], [x(radiusX), lowerWaistY - KAPPA * radiusY], [x(radiusX), lowerWaistY]]],
    ['lnTo', [[x(radiusX), lowerEndY]]],
    ['cubicBezTo', [[x(radiusX), lowerEndY + KAPPA * radiusY], [x(KAPPA * radiusX), height], [x(0), height]]],
  ]
}

function bracketPoints(prstGeom, width, height, mirror) {
  const shortSide = Math.min(width, height)
  const maxAdjustment = 50000 * height / shortSide
  const size = clamp(adjustment(prstGeom, 'adj', 8333), 0, maxAdjustment)
  const radiusY = shortSide * size / 100000
  const lowerY = height - radiusY
  const x = value => mirror ? width - value : value

  return [
    ['moveTo', [[x(0), 0]]],
    ['cubicBezTo', [[x(KAPPA * width), 0], [x(width), radiusY * (1 - KAPPA)], [x(width), radiusY]]],
    ['lnTo', [[x(width), lowerY]]],
    ['cubicBezTo', [[x(width), lowerY + KAPPA * radiusY], [x(KAPPA * width), height], [x(0), height]]],
  ]
}

function customGeometry(document, width, height, commands) {
  const custGeom = element(document, 'custGeom')
  custGeom.appendChild(element(document, 'avLst'))
  custGeom.appendChild(element(document, 'gdLst'))
  custGeom.appendChild(element(document, 'ahLst'))
  custGeom.appendChild(element(document, 'cxnLst'))
  custGeom.appendChild(element(document, 'rect', { l: 0, t: 0, r: rounded(width), b: rounded(height) }))
  const pathLst = element(document, 'pathLst')
  const path = element(document, 'path', {
    w: rounded(width),
    h: rounded(height),
    fill: 'none',
    extrusionOk: 'false',
  })
  for (const [name, points] of commands) path.appendChild(command(document, name, points))
  pathLst.appendChild(path)
  custGeom.appendChild(pathLst)
  return custGeom
}

/**
 * Rewrites the few DrawingML presets unsupported by pptx-preview 0.0.19.
 * The brace and bracket formulae follow ECMA-376's preset geometry as published
 * in Apache POI's presetShapeDefinitions.xml; quarter arcs are represented by
 * the standard cubic Bezier approximation because the renderer only accepts
 * moveTo, lnTo and cubicBezTo custom-path commands.
 */
export function normalizeGeometry(document) {
  if (!document?.getElementsByTagNameNS || !document?.createElementNS) return 0
  const geometries = Array.from(document.getElementsByTagNameNS(DRAWING_NS, 'prstGeom'))
  let normalized = 0

  for (const prstGeom of geometries) {
    const preset = prstGeom.getAttribute('prst')
    if (preset === 'flowChartConnector') {
      prstGeom.setAttribute('prst', 'ellipse')
      normalized++
      continue
    }

    const isBrace = preset === 'rightBrace' || preset === 'leftBrace'
    const isBracket = preset === 'rightBracket' || preset === 'leftBracket'
    if (!isBrace && !isBracket) continue

    const extent = extentFor(prstGeom)
    if (!extent) continue
    const mirror = preset === 'leftBrace' || preset === 'leftBracket'
    const commands = isBrace
      ? bracePoints(prstGeom, extent.width, extent.height, mirror)
      : bracketPoints(prstGeom, extent.width, extent.height, mirror)
    prstGeom.parentNode.replaceChild(
      customGeometry(document, extent.width, extent.height, commands),
      prstGeom,
    )
    normalized++
  }

  return normalized
}
