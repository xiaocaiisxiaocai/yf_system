import { init } from 'pptx-preview'
import JSZip from 'jszip'
import './viewer.css'

const host = document.getElementById('viewer')
let channel = ''
let loaded = false
let active = true
let scrollHost
let slides = []
let currentPage = 1
let zoomMode = 'page'
let scale = 1
let scrollFrame = 0

const clamp = (value, min, max) => Math.min(max, Math.max(min, value))
const notify = (type, detail = {}) => {
  if (active && channel) parent.postMessage({ type, channel, ...detail }, '*')
}

async function preflightPptx(buffer) {
  const zip = await JSZip.loadAsync(buffer, { createFolders: false })
  const entries = Object.values(zip.files)
  if (entries.length === 0 || entries.length > 5000) throw new Error('Invalid PPTX entry count')
  const required = ['[Content_Types].xml', 'ppt/presentation.xml']
  if (required.some(name => !zip.file(name))) throw new Error('Missing required PPTX metadata')

  let expandedBytes = 0
  let xmlBytes = 0
  const xmlEntries = []
  for (const entry of entries) {
    if (entry.dir) continue
    const originalName = entry.unsafeOriginalName || entry.name
    if (originalName.replaceAll('\\', '/').split('/').includes('..')) throw new Error('Unsafe PPTX entry path')
    const size = Number(entry._data?.uncompressedSize)
    if (!Number.isFinite(size) || size < 0) throw new Error('Invalid PPTX entry size')
    expandedBytes += size
    if (expandedBytes > 512 * 1024 * 1024) throw new Error('PPTX expanded size is too large')
    if (/\.(?:xml|rels)$/i.test(entry.name)) {
      if (size > 8 * 1024 * 1024) throw new Error('PPTX XML entry is too large')
      xmlBytes += size
      if (xmlBytes > 64 * 1024 * 1024) throw new Error('PPTX XML content is too large')
      xmlEntries.push(entry)
    }
  }

  const parser = new DOMParser()
  for (const entry of xmlEntries) {
    const source = await entry.async('string')
    if (/<!DOCTYPE|<!ENTITY/i.test(source)) throw new Error('Unsupported PPTX XML declaration')
    const document = parser.parseFromString(source, 'application/xml')
    if (!document.documentElement || document.querySelector('parsererror')) throw new Error('Malformed PPTX XML')
  }
}

function sanitizeRenderedContent() {
  host.querySelectorAll('a').forEach(anchor => {
    anchor.removeAttribute('href')
    anchor.removeAttribute('target')
    anchor.removeAttribute('download')
  })
  host.querySelectorAll('[src],[href]').forEach(element => {
    for (const attribute of ['src', 'href']) {
      const value = element.getAttribute(attribute)
      if (value && !value.startsWith('data:') && !value.startsWith('blob:') && !value.startsWith('#')) {
        element.removeAttribute(attribute)
      }
    }
  })
}

function fitScale(mode) {
  const first = slides[0]
  if (!first || !scrollHost) return 1
  const width = first.offsetWidth || 1
  const height = first.offsetHeight || 1
  const fitWidth = (scrollHost.clientWidth - 24) / width
  return clamp(mode === 'page'
    ? Math.min(fitWidth, (scrollHost.clientHeight - 24) / height)
    : fitWidth, 0.1, 4)
}

function goToPage(value, behavior = 'auto') {
  if (!slides.length) return
  currentPage = clamp(Math.round(Number(value) || 1), 1, slides.length)
  slides[currentPage - 1].scrollIntoView({ behavior, block: 'start', inline: 'center' })
  notify('pptx:page-changed', { pageNumber: currentPage })
}

function applyZoom(value, keepPage = true) {
  const targetPage = currentPage
  zoomMode = value === 'page' || value === 'fit' ? value : 'custom'
  const next = zoomMode === 'custom' ? clamp(Number(value) || 1, 0.1, 4) : fitScale(zoomMode)
  scale = Math.round(next * 1000) / 1000
  for (const slide of slides) slide.style.zoom = String(scale)
  // Leave enough trailing space to align the final slide at the viewport top,
  // including when several small slides fit on a narrow portrait screen.
  if (scrollHost && slides.length) {
    const lastHeight = slides[slides.length - 1].offsetHeight * scale
    scrollHost.style.paddingBottom = `${Math.max(12, scrollHost.clientHeight - lastHeight)}px`
  }
  notify('pptx:scale-changed', { scale, zoom: zoomMode === 'custom' ? String(scale) : zoomMode })
  if (keepPage) requestAnimationFrame(() => goToPage(targetPage, 'auto'))
}

function updateCurrentPage() {
  if (!scrollHost || !slides.length) return
  // On narrow screens several slides fit vertically. Track the first visible
  // slide, so jumping to slide 2 does not immediately report slide 4 instead.
  const top = scrollHost.getBoundingClientRect().top + 4
  const firstVisible = slides.findIndex(slide => slide.getBoundingClientRect().bottom > top)
  const nearest = firstVisible < 0 ? slides.length - 1 : firstVisible
  if (nearest + 1 !== currentPage) {
    currentPage = nearest + 1
    notify('pptx:page-changed', { pageNumber: currentPage })
  }
}

function observeViewer() {
  scrollHost = host.querySelector('.pptx-preview-wrapper') || host
  scrollHost.addEventListener('scroll', () => {
    cancelAnimationFrame(scrollFrame)
    scrollFrame = requestAnimationFrame(updateCurrentPage)
  }, { passive: true })
  new ResizeObserver(() => {
    if (zoomMode === 'page' || zoomMode === 'fit') applyZoom(zoomMode)
  }).observe(scrollHost)
}

addEventListener('message', async event => {
  if (event.source !== parent || !event.data || typeof event.data.channel !== 'string') return
  if (event.data.type === 'pptx:command') {
    if (!loaded || event.data.channel !== channel) return
    if (event.data.command === 'page') goToPage(event.data.value)
    if (event.data.command === 'zoom') applyZoom(event.data.value)
    return
  }
  if (event.data.type !== 'pptx:load' || loaded) return
  if (!(event.data.buffer instanceof ArrayBuffer)) return
  const signature = new Uint8Array(event.data.buffer, 0, Math.min(4, event.data.buffer.byteLength))
  if (signature[0] !== 0x50 || signature[1] !== 0x4b) return
  loaded = true
  channel = event.data.channel
  try {
    await preflightPptx(event.data.buffer)
    if (!active) return
    const width = Math.max(320, host.clientWidth || 960)
    const height = Math.max(240, host.clientHeight || 540)
    const viewer = init(host, { width, height })
    const pptx = await viewer.preview(event.data.buffer)
    if (!active) return
    sanitizeRenderedContent()
    slides = Array.from(host.querySelectorAll('.pptx-preview-slide-wrapper'))
    if (!slides.length) throw new Error('No slides rendered')
    slides.forEach((slide, index) => slide.dataset.slideIndex = String(index + 1))
    observeViewer()
    applyZoom('page', false)
    goToPage(1, 'auto')
    notify('pptx:rendered', {
      pageCount: Math.max(slides.length, Array.isArray(pptx?.slides) ? pptx.slides.length : 0),
      pageNumber: 1,
      scale,
      zoom: zoomMode,
    })
  } catch {
    notify('pptx:error')
  }
})

addEventListener('keydown', event => {
  if (!loaded) return
  if (event.key === 'PageDown' || event.key === 'ArrowDown') { event.preventDefault(); goToPage(currentPage + 1) }
  if (event.key === 'PageUp' || event.key === 'ArrowUp') { event.preventDefault(); goToPage(currentPage - 1) }
  if ((event.ctrlKey || event.metaKey) && ['+', '=', '-'].includes(event.key)) {
    event.preventDefault()
    applyZoom(scale * (event.key === '-' ? 0.8 : 1.25))
  }
  if ((event.ctrlKey || event.metaKey) && event.key === '0') { event.preventDefault(); applyZoom('page') }
}, true)
addEventListener('click', event => {
  if (event.target.closest?.('a')) event.preventDefault()
}, true)
addEventListener('contextmenu', event => event.preventDefault())
addEventListener('pagehide', () => {
  active = false
  cancelAnimationFrame(scrollFrame)
})
