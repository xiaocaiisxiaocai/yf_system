// Expand DrawingML references unsupported by the pinned renderer. Only the
// in-memory preview ZIP is changed; the uploaded file is never rewritten.
import { normalizeGeometry } from './geometry.js'

const A = 'http://schemas.openxmlformats.org/drawingml/2006/main'
const P = 'http://schemas.openxmlformats.org/presentationml/2006/main'
const R = 'http://schemas.openxmlformats.org/package/2006/relationships'
const child = (node, name, ns = A) => Array.from(node?.children || []).find(item => item.namespaceURI === ns && item.localName === name)
const descendants = (node, name, ns = A) => Array.from(node?.getElementsByTagNameNS(ns, name) || [])
const clamp = value => Math.min(1, Math.max(0, value))
const colorNames = new Set(['srgbClr', 'schemeClr', 'sysClr', 'prstClr', 'scrgbClr'])
const colorChild = node => Array.from(node?.children || []).find(item => item.namespaceURI === A && colorNames.has(item.localName))
const fillNames = new Set(['solidFill', 'gradFill', 'blipFill', 'pattFill', 'grpFill', 'noFill'])

function hsl([r, g, b]) {
  const max = Math.max(r, g, b), min = Math.min(r, g, b), delta = max - min
  const lightness = (max + min) / 2
  if (!delta) return [0, 0, lightness]
  const saturation = delta / (1 - Math.abs(2 * lightness - 1))
  const hue = max === r ? ((g - b) / delta + (g < b ? 6 : 0)) / 6
    : max === g ? ((b - r) / delta + 2) / 6 : ((r - g) / delta + 4) / 6
  return [hue, saturation, lightness]
}

function rgb([h, s, l]) {
  const a = clamp(s) * Math.min(clamp(l), 1 - clamp(l))
  return [0, 8, 4].map(n => {
    const k = (n + ((h % 1 + 1) % 1) * 12) % 12
    return clamp(l) - a * Math.max(-1, Math.min(k - 3, 9 - k, 1))
  })
}

// DrawingML tint/shade operate on linear-light channels rather than CSS HSL.
const linear = c => c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4
const srgb = c => c <= 0.0031308 ? c * 12.92 : 1.055 * c ** (1 / 2.4) - 0.055

function resolveColor(element, theme, mapping, placeholder, depth = 0) {
  if (!element || depth > 8) return null
  let channels, alpha = 1
  const value = element.getAttribute('val') || ''
  if (element.localName === 'schemeClr') {
    if (value === 'phClr') {
      const resolved = resolveColor(placeholder, theme, mapping, null, depth + 1)
      if (!resolved) return null
      channels = resolved.channels; alpha = resolved.alpha
    } else {
      const aliases = { bg1: 'lt1', bg2: 'lt2', tx1: 'dk1', tx2: 'dk2' }
      const key = mapping?.getAttribute(value) || aliases[value] || value
      const slot = child(descendants(theme, 'clrScheme')[0], key)
      const resolved = resolveColor(colorChild(slot), theme, mapping, null, depth + 1)
      if (!resolved) return null
      channels = resolved.channels; alpha = resolved.alpha
    }
  } else if (element.localName === 'scrgbClr') {
    channels = ['r', 'g', 'b'].map(name => srgb(clamp(Number(element.getAttribute(name)) / 100000)))
  } else {
    let hex = element.localName === 'sysClr' ? element.getAttribute('lastClr') : value
    if (element.localName === 'prstClr') {
      const cssColor = value.replace(/^dk/, 'dark').replace(/^lt/, 'light').replace(/^med/, 'medium')
      if (!CSS.supports('color', cssColor)) return null
      const canvas = document.createElement('canvas').getContext('2d')
      if (!canvas) return null
      canvas.fillStyle = cssColor
      hex = canvas.fillStyle.replace('#', '')
    }
    if (!/^[0-9a-f]{6}$/i.test(hex || '')) return null
    channels = [0, 2, 4].map(offset => parseInt(hex.slice(offset, offset + 2), 16) / 255)
  }
  for (const transform of element.children) {
    const amount = Number(transform.getAttribute('val')) / 100000
    if (!Number.isFinite(amount)) continue
    switch (transform.localName) {
      case 'tint': channels = channels.map(c => srgb(clamp(linear(c) * amount + 1 - amount))); break
      case 'shade': channels = channels.map(c => srgb(clamp(linear(c) * amount))); break
      case 'lumMod': { const next = hsl(channels); next[2] *= amount; channels = rgb(next); break }
      case 'lumOff': { const next = hsl(channels); next[2] += amount; channels = rgb(next); break }
      case 'satMod': { const next = hsl(channels); next[1] *= amount; channels = rgb(next); break }
      case 'satOff': { const next = hsl(channels); next[1] += amount; channels = rgb(next); break }
      case 'alpha': alpha = amount; break
      case 'alphaMod': alpha *= amount; break
      case 'alphaOff': alpha += amount; break
      default: return null // Preserve transforms that this adapter does not understand.
    }
  }
  return { channels, alpha: clamp(alpha) }
}

function normalizeColors(root, theme, mapping, placeholder) {
  for (const element of descendants(root, '*').filter(item => colorNames.has(item.localName))) {
    const resolved = resolveColor(element, theme, mapping, placeholder)
    if (!resolved) continue
    const replacement = root.ownerDocument.createElementNS(A, 'a:srgbClr')
    replacement.setAttribute('val', resolved.channels.map(c => Math.round(clamp(c) * 255).toString(16).padStart(2, '0')).join('').toUpperCase())
    if (resolved.alpha !== 1) {
      const alpha = root.ownerDocument.createElementNS(A, 'a:alpha')
      alpha.setAttribute('val', String(Math.round(resolved.alpha * 100000)))
      replacement.append(alpha)
    }
    element.replaceWith(replacement)
  }
}

function expandStyles(doc, theme, mapping) {
  for (const shape of descendants(doc, 'sp', P)) {
    const properties = child(shape, 'spPr', P), style = child(shape, 'style', P)
    if (!properties || !style) continue
    const fillRef = child(style, 'fillRef')
    const existingFill = Array.from(properties.children).find(item => fillNames.has(item.localName))
    const incompleteGradient = existingFill?.localName === 'gradFill' && !child(existingFill, 'gsLst')
    if ((!existingFill || incompleteGradient) && fillRef) {
      const index = Number(fillRef.getAttribute('idx'))
      if (index === 0 || index === 1000) properties.append(doc.createElementNS(A, 'a:noFill'))
      else {
        const list = descendants(theme, index > 1000 ? 'bgFillStyleLst' : 'fillStyleLst')[0]
        const fill = list?.children[index > 1000 ? index - 1001 : index - 1]
        if (fill) {
          const copy = doc.importNode(fill, true)
          normalizeColors(copy, theme, mapping, colorChild(fillRef))
          if (incompleteGradient) existingFill.replaceWith(copy)
          else properties.append(copy)
        }
      }
    }
    const lineRef = child(style, 'lnRef'), currentLine = child(properties, 'ln')
    const lineIndex = Number(lineRef?.getAttribute('idx'))
    const inheritedLine = descendants(theme, 'lnStyleLst')[0]?.children[lineIndex - 1]
    if (inheritedLine) {
      const copy = doc.importNode(inheritedLine, true)
      normalizeColors(copy, theme, mapping, colorChild(lineRef))
      // Explicit line properties take precedence, including noFill.
      if (currentLine) {
        for (const attr of currentLine.attributes) copy.setAttribute(attr.name, attr.value)
        for (const override of currentLine.children) {
          for (const inherited of Array.from(copy.children)) {
            if (inherited.localName === override.localName || (fillNames.has(inherited.localName) && fillNames.has(override.localName))) inherited.remove()
          }
          copy.append(override.cloneNode(true))
        }
        currentLine.replaceWith(copy)
      } else properties.append(copy)
    }
  }
  normalizeColors(doc.documentElement, theme, mapping)
}

function applyThemeFonts(doc, theme) {
  const scheme = descendants(theme, 'fontScheme')[0]
  if (!scheme) return
  for (const run of [...descendants(doc, 'r'), ...descendants(doc, 'fld')]) {
    const text = child(run, 't')?.textContent || ''
    if (!text) continue
    let props = child(run, 'rPr')
    if (!props) { props = doc.createElementNS(A, 'a:rPr'); run.prepend(props) }
    const paragraph = run.parentElement
    const defaults = child(child(paragraph, 'pPr'), 'defRPr')
    const ea = child(props, 'ea') || child(defaults, 'ea')
    const latin = child(props, 'latin') || child(defaults, 'latin')
    const explicit = /[\u2e80-\u9fff\uf900-\ufaff]/.test(text) ? ea : latin
    let typeface = explicit?.getAttribute('typeface') || ''
    if (typeface && !typeface.startsWith('+')) {
      if (explicit === child(props, 'ea')) continue
    } else {
      let shape = run.parentElement
      while (shape && !(shape.namespaceURI === P && shape.localName === 'sp')) shape = shape.parentElement
      const family = typeface.startsWith('+mj') || child(child(shape, 'style', P), 'fontRef')?.getAttribute('idx') === 'major' ? 'majorFont' : 'minorFont'
      const fonts = child(scheme, family)
      const language = props.getAttribute('lang') || defaults?.getAttribute('lang') || 'zh-CN'
      const script = /^zh-(TW|HK|MO|Hant)/i.test(language) ? 'Hant' : /^ja/i.test(language) ? 'Jpan' : /^ko/i.test(language) ? 'Hang' : 'Hans'
      typeface = /[\u2e80-\u9fff\uf900-\ufaff]/.test(text)
        ? child(fonts, 'ea')?.getAttribute('typeface') || Array.from(fonts?.children || []).find(item => item.getAttribute('script') === script)?.getAttribute('typeface') || ''
        : child(fonts, 'latin')?.getAttribute('typeface') || ''
    }
    if (typeface) {
      const font = doc.createElementNS(A, 'a:ea')
      font.setAttribute('typeface', typeface)
      const current = child(props, 'ea')
      if (current) current.replaceWith(font); else props.append(font)
    }
  }
}

function relativePart(from, target) {
  const parts = target.startsWith('/') ? [] : from.split('/').slice(0, -1)
  for (const segment of target.split('/')) {
    if (segment === '..') { if (!parts.length) return null; parts.pop() }
    else if (segment && segment !== '.') parts.push(segment)
  }
  return parts.join('/')
}

export async function normalizePptx(zip) {
  const cache = new Map()
  async function xml(name) {
    if (!cache.has(name)) {
      const entry = zip.file(name)
      cache.set(name, entry ? new DOMParser().parseFromString(await entry.async('string'), 'application/xml') : null)
    }
    return cache.get(name)
  }
  async function context(name, depth = 0) {
    if (depth > 4) return {}
    const doc = await xml(name)
    const parts = name.split('/'), filename = parts.pop()
    const rels = await xml([...parts, '_rels', `${filename}.rels`].join('/'))
    const relations = descendants(rels, 'Relationship', R)
    // Masters also link back to every layout: prefer the theme/master parent,
    // otherwise XML relationship order can send us into a master-layout cycle.
    const relation = ['theme', 'slideMaster', 'slideLayout'].map(kind => relations.find(item =>
      (item.getAttribute('Type') || '').endsWith(`/${kind}`) && item.getAttribute('TargetMode') !== 'External')).find(Boolean)
    const target = relation && relativePart(name, relation.getAttribute('Target') || '')
    const inherited = target ? (relation.getAttribute('Type').endsWith('/theme') ? { theme: await xml(target) } : await context(target, depth + 1)) : {}
    const mapping = descendants(doc, 'overrideClrMapping')[0] || descendants(doc, 'clrMap', P)[0] || inherited.mapping
    return { ...inherited, mapping }
  }
  for (const name of Object.keys(zip.files).filter(name => /^ppt\/(slides|slideLayouts|slideMasters)\/[^/]+\.xml$/.test(name))) {
    const doc = await xml(name), { theme, mapping } = await context(name)
    if (!doc) continue
    if (theme) { expandStyles(doc, theme, mapping); applyThemeFonts(doc, theme) }
    normalizeGeometry(doc)
    zip.file(name, new XMLSerializer().serializeToString(doc))
  }
  return zip.generateAsync({ type: 'arraybuffer', compression: 'STORE' })
}
