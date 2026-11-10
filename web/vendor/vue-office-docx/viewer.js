import './core/packages/vue-docx/src/hack.js'
import docx from './core/packages/vue-docx/src/docx.js'
import JSZip from 'jszip'
import { assertArchiveWithinLimits, documentArchiveLimits } from '../archive-limits.js'
import './core/packages/js-docx/index.css'
import './viewer.css'

const host = document.getElementById('viewer')
// Same wrapper structure as the supplied @js-preview/docx entry (createWrapper).
const wrapper = document.createElement('div')
wrapper.className = 'vue-office-docx'
const main = document.createElement('div')
main.className = 'vue-office-docx-main'
wrapper.append(main)
host.append(wrapper)

let channel = ''
let loaded = false
let active = true
let pages = []
let zoomMode = 'fit'
let scale = 1

const clamp = (value, min, max) => Math.min(max, Math.max(min, value))
const notify = (type, detail = {}) => {
  if (active && channel) parent.postMessage({ type, channel, ...detail }, '*')
}

async function preflightDocx(buffer) {
  await assertArchiveWithinLimits(buffer, documentArchiveLimits)
  const zip = await JSZip.loadAsync(buffer, { createFolders: false })
  const entries = Object.values(zip.files)
  if (entries.length === 0 || entries.length > documentArchiveLimits.maxEntries) throw new Error('Invalid DOCX entry count')
  if (['[Content_Types].xml', 'word/document.xml'].some(name => !zip.file(name))) throw new Error('Missing required DOCX metadata')

  let xmlBytes = 0
  const xmlEntries = []
  for (const entry of entries) {
    if (entry.dir) continue
    const originalName = entry.unsafeOriginalName || entry.name
    if (originalName.replaceAll('\\', '/').split('/').includes('..')) throw new Error('Unsafe DOCX entry path')
    if (!/\.(?:xml|rels)$/i.test(entry.name)) continue
    const size = Number(entry._data?.uncompressedSize)
    if (!Number.isFinite(size) || size < 0 || size > 32 * 1024 * 1024) throw new Error('DOCX XML entry is too large')
    xmlBytes += size
    if (xmlBytes > 96 * 1024 * 1024) throw new Error('DOCX XML content is too large')
    xmlEntries.push(entry)
  }
  const parser = new DOMParser()
  for (const entry of xmlEntries) {
    const source = await entry.async('string')
    if (/<!DOCTYPE|<!ENTITY/i.test(source)) throw new Error('Unsupported DOCX XML declaration')
    const parsed = parser.parseFromString(source, 'application/xml')
    if (!parsed.documentElement || parsed.querySelector('parsererror')) throw new Error('Malformed DOCX XML')
  }
}

function sanitizeRenderedContent() {
  main.querySelectorAll('iframe,object,embed,script,form').forEach(element => element.remove())
  main.querySelectorAll('a').forEach(anchor => {
    anchor.removeAttribute('href')
    anchor.removeAttribute('target')
    anchor.removeAttribute('download')
  })
  main.querySelectorAll('[src],[href]').forEach(element => {
    for (const attribute of ['src', 'href']) {
      const value = element.getAttribute(attribute)
      if (value && !value.startsWith('data:') && !value.startsWith('blob:') && !value.startsWith('#')) {
        element.removeAttribute(attribute)
      }
    }
  })
}

function fitScale() {
  const widest = Math.max(1, ...pages.map(page => page.offsetWidth))
  return clamp((wrapper.clientWidth - 24) / widest, 0.1, 4)
}

function applyZoom(value) {
  zoomMode = value === 'fit' ? 'fit' : 'custom'
  // "适合宽度" never enlarges a page beyond its real size.
  const next = zoomMode === 'fit' ? Math.min(1, fitScale()) : clamp(Number(value) || 1, 0.1, 4)
  scale = Math.round(next * 1000) / 1000
  for (const page of pages) page.style.zoom = String(scale)
  notify('docx:scale-changed', { scale, zoom: zoomMode === 'fit' ? 'fit' : String(scale) })
}

addEventListener('message', async event => {
  if (event.source !== parent || !event.data || typeof event.data.channel !== 'string') return
  if (event.data.type === 'docx:command') {
    if (!loaded || event.data.channel !== channel || !pages.length) return
    if (event.data.command === 'zoom') applyZoom(event.data.value)
    return
  }
  if (event.data.type !== 'docx:load' || loaded) return
  if (!(event.data.buffer instanceof ArrayBuffer)) return
  const signature = new Uint8Array(event.data.buffer, 0, Math.min(4, event.data.buffer.byteLength))
  if (signature[0] !== 0x50 || signature[1] !== 0x4b) return
  loaded = true
  channel = event.data.channel
  try {
    await preflightDocx(event.data.buffer)
    if (!active) return
    await docx.render(event.data.buffer, main, {
      className: 'docx',
      inWrapper: true,
      breakPages: true,
      // Embedded images and fonts become data: URLs, the only sources the frame's CSP allows.
      useBase64URL: true,
      // An altChunk is arbitrary HTML that the engine would place in a nested iframe.
      renderAltChunks: false,
      renderComments: false,
      renderChanges: false,
      experimental: false,
      debug: false,
    })
    if (!active) return
    sanitizeRenderedContent()
    pages = Array.from(main.querySelectorAll('section.docx'))
    if (!pages.length) throw new Error('No pages rendered')
    applyZoom('fit')
    new ResizeObserver(() => { if (zoomMode === 'fit') applyZoom('fit') }).observe(wrapper)
    notify('docx:rendered', { scale, zoom: 'fit' })
  } catch {
    main.replaceChildren()
    notify('docx:error')
  }
})

addEventListener('keydown', event => {
  if (!pages.length || !(event.ctrlKey || event.metaKey)) return
  if (['+', '=', '-'].includes(event.key)) { event.preventDefault(); applyZoom(scale * (event.key === '-' ? 0.8 : 1.25)) }
  if (event.key === '0') { event.preventDefault(); applyZoom('fit') }
  // No save, print or source view from inside the preview.
  if (['s', 'p', 'u'].includes(event.key.toLowerCase())) event.preventDefault()
}, true)
addEventListener('click', event => {
  if (event.target.closest?.('a')) event.preventDefault()
}, true)
addEventListener('contextmenu', event => event.preventDefault())
addEventListener('pagehide', () => { active = false })
