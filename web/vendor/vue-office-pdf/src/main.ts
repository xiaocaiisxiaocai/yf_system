/*
 * Adapted from vue-office @js-preview/pdf 2.0.10
 * Source: core/packages/js-pdf/src/main.js in D:/Temp/vue-office源码2024-12-30.zip
 * Copyright (c) 2023 hit757, MIT. See ../core/LICENSE and ../SOURCE.md.
 */
import type {
  PDFDocumentLoadingTask,
  PDFDocumentProxy,
  RenderTask,
} from 'pdfjs-dist'

export type PdfZoom = 'page' | 'fit' | number

type PageSize = {
  width: number
  height: number
}

type PageLayout = PageSize & {
  pageNumber: number
  scale: number
  top: number
  bottom: number
}

type ActiveRender = {
  canvas: HTMLCanvasElement
  task?: RenderTask
}

export type PdfPreviewOptions = {
  openPdf: (data: Uint8Array) => PDFDocumentLoadingTask
  gap?: number
  overscan?: number
  onDocumentLoaded?: (pageCount: number) => void
  onPageChange?: (pageNumber: number) => void
  onScaleChange?: (scale: number) => void
  onPageError?: (error: unknown, pageNumber: number) => void
}

const PAGE_PADDING = 16
const MAX_CANVAS_PIXELS = 16_000_000
const MAX_CANVAS_EDGE = 8192

function isCancelled(error: unknown) {
  return error instanceof Error && error.name === 'RenderingCancelledException'
}

function normalizeZoom(value: PdfZoom): PdfZoom {
  if (value === 'page' || value === 'fit') return value
  return Math.min(4, Math.max(0.1, Number.isFinite(value) ? value : 1))
}

/**
 * The upstream class is intentionally kept as an instance-owned DOM renderer.
 * React owns the toolbar and data request; this class owns PDF.js and every DOM,
 * observer, timer, render task, and worker created for the preview surface.
 */
class JsPdfPreview {
  private readonly container: HTMLElement
  private readonly options: PdfPreviewOptions
  private readonly wrapper: HTMLDivElement
  private readonly wrapperMain: HTMLDivElement
  private readonly activeRenders = new Map<number, ActiveRender>()
  private readonly resizeObserver: ResizeObserver | null
  private loadingTask: PDFDocumentLoadingTask | null = null
  private pdfDocument: PDFDocumentProxy | null = null
  private pageSizes: PageSize[] = []
  private layouts: PageLayout[] = []
  private zoom: PdfZoom = 'page'
  private currentPage = 1
  private revision = 0
  private scrollTimer: ReturnType<typeof setTimeout> | null = null
  private destroyed = false
  private measuredWidth = 0
  private measuredHeight = 0

  constructor(container: HTMLElement, options: PdfPreviewOptions) {
    this.container = container
    this.options = options
    this.wrapper = document.createElement('div')
    this.wrapper.className = 'vue-office-pdf'
    this.wrapper.setAttribute('role', 'document')
    this.wrapper.setAttribute('aria-label', 'PDF 页面列表')
    Object.assign(this.wrapper.style, {
      boxSizing: 'border-box',
      height: '100%',
      overflow: 'auto',
      scrollbarGutter: 'stable',
      position: 'relative',
      textAlign: 'center',
    })

    this.wrapperMain = document.createElement('div')
    this.wrapperMain.className = 'vue-office-pdf-wrapper'
    Object.assign(this.wrapperMain.style, {
      boxSizing: 'border-box',
      minHeight: '100%',
      minWidth: '100%',
      position: 'relative',
    })
    this.wrapper.appendChild(this.wrapperMain)
    this.container.appendChild(this.wrapper)
    this.wrapper.addEventListener('scroll', this.onScroll)

    this.resizeObserver = typeof ResizeObserver === 'undefined'
      ? null
      : new ResizeObserver(() => this.resize())
    this.resizeObserver?.observe(this.container)
    this.measure()
  }

  async preview(data: Uint8Array) {
    if (this.destroyed) return
    const revision = ++this.revision
    this.cancelAllRenders()
    await this.destroyLoadingTask()
    if (this.destroyed || revision !== this.revision) return

    const task = this.options.openPdf(data)
    this.loadingTask = task
    try {
      const pdfDocument = await task.promise
      if (this.destroyed || revision !== this.revision || task !== this.loadingTask) return
      this.pdfDocument = pdfDocument
      this.pageSizes = await this.readPageSizes(pdfDocument, revision)
      if (this.destroyed || revision !== this.revision || task !== this.loadingTask) return

      this.currentPage = 1
      this.measure()
      this.relayout()
      this.wrapper.scrollTop = 0
      this.options.onDocumentLoaded?.(pdfDocument.numPages)
      this.options.onPageChange?.(1)
      this.emitScale()
      this.renderVisible()
    } catch (error) {
      if (this.destroyed || revision !== this.revision || task !== this.loadingTask) return
      this.loadingTask = null
      this.pdfDocument = null
      this.pageSizes = []
      this.layouts = []
      this.clearCanvases()
      await task.destroy().catch(() => undefined)
      throw error
    }
  }

  setZoom(value: PdfZoom) {
    if (this.destroyed || !this.pdfDocument) return
    this.zoom = normalizeZoom(value)
    this.cancelAllRenders()
    this.measure()
    this.relayout()
    this.scrollToPage(this.currentPage)
    this.emitScale()
    this.renderVisible()
  }

  goToPage(value: number) {
    if (this.destroyed || this.layouts.length === 0 || !Number.isFinite(value)) return
    const pageNumber = Math.min(this.layouts.length, Math.max(1, Math.trunc(value)))
    this.currentPage = pageNumber
    this.scrollToPage(pageNumber)
    this.options.onPageChange?.(pageNumber)
    this.emitScale()
    this.renderVisible()
  }

  resize() {
    if (this.destroyed) return
    const changed = this.measure()
    if (!changed || this.layouts.length === 0) return
    if (typeof this.zoom === 'number') {
      // Numeric zoom keeps page geometry, but a taller viewport can expose more
      // virtual pages and a wider viewport changes the centered scroll surface.
      this.relayout()
      this.renderVisible()
      return
    }
    this.cancelAllRenders()
    this.relayout()
    this.scrollToPage(this.currentPage)
    this.emitScale()
    this.renderVisible()
  }

  async destroy() {
    if (this.destroyed) return
    this.destroyed = true
    ++this.revision
    this.wrapper.removeEventListener('scroll', this.onScroll)
    this.resizeObserver?.disconnect()
    if (this.scrollTimer !== null) {
      clearTimeout(this.scrollTimer)
      this.scrollTimer = null
    }
    this.cancelAllRenders()
    await this.destroyLoadingTask()
    this.wrapper.remove()
    this.pageSizes = []
    this.layouts = []
  }

  private readonly onScroll = () => {
    if (this.scrollTimer !== null) clearTimeout(this.scrollTimer)
    this.scrollTimer = setTimeout(() => {
      this.scrollTimer = null
      if (this.destroyed) return
      this.updateCurrentPageFromScroll()
      this.renderVisible()
    }, 40)
  }

  private measure() {
    // The scroll surface is narrower than its host with classic scrollbars.
    const width = Math.max(1, (this.wrapper.clientWidth || this.container.clientWidth) - PAGE_PADDING * 2)
    const height = Math.max(1, this.container.clientHeight - PAGE_PADDING * 2)
    const changed = width !== this.measuredWidth || height !== this.measuredHeight
    this.measuredWidth = width
    this.measuredHeight = height
    return changed
  }

  private async readPageSizes(pdfDocument: PDFDocumentProxy, revision: number) {
    const sizes = new Array<PageSize>(pdfDocument.numPages)
    let nextPage = 1
    const workers = Array.from({ length: Math.min(8, pdfDocument.numPages) }, async () => {
      while (!this.destroyed && revision === this.revision) {
        const pageNumber = nextPage++
        if (pageNumber > pdfDocument.numPages) return
        const page = await pdfDocument.getPage(pageNumber)
        if (this.destroyed || revision !== this.revision) return
        const viewport = page.getViewport({ scale: 1 })
        sizes[pageNumber - 1] = { width: viewport.width, height: viewport.height }
      }
    })
    await Promise.all(workers)
    return sizes
  }

  private relayout() {
    const gap = this.options.gap ?? 12
    let top = PAGE_PADDING
    this.layouts = this.pageSizes.map((size, index) => {
      const scale = this.zoom === 'page'
        ? Math.min(this.measuredWidth / size.width, this.measuredHeight / size.height)
        : this.zoom === 'fit'
          ? this.measuredWidth / size.width
          : this.zoom
      const width = Math.max(1, size.width * scale)
      const height = Math.max(1, size.height * scale)
      const layout = { pageNumber: index + 1, scale, width, height, top, bottom: top + height }
      top = layout.bottom + gap
      return layout
    })
    const contentHeight = this.layouts.length === 0 ? 0 : top - gap + PAGE_PADDING
    const widestPage = this.layouts.reduce((width, layout) => Math.max(width, layout.width), 0)
    // min-width:100% fills the available scrollport without reintroducing the
    // vertical scrollbar width. Wider pages still have a reachable left edge.
    this.wrapperMain.style.width = `${widestPage + PAGE_PADDING * 2}px`
    this.wrapperMain.style.height = `${Math.max(this.container.clientHeight, contentHeight)}px`
  }

  private updateCurrentPageFromScroll() {
    if (this.layouts.length === 0) return
    const middle = this.wrapper.scrollTop + Math.max(1, this.wrapper.clientHeight) / 2
    let selected = this.layouts[0]
    let distance = Math.abs((selected.top + selected.bottom) / 2 - middle)
    for (const layout of this.layouts) {
      const candidate = Math.abs((layout.top + layout.bottom) / 2 - middle)
      if (candidate < distance) {
        selected = layout
        distance = candidate
      }
    }
    if (selected.pageNumber !== this.currentPage) {
      this.currentPage = selected.pageNumber
      this.options.onPageChange?.(selected.pageNumber)
      this.emitScale()
    }
  }

  private scrollToPage(pageNumber: number) {
    const layout = this.layouts[pageNumber - 1]
    if (!layout) return
    this.wrapper.scrollTop = Math.max(0, layout.top - PAGE_PADDING)
  }

  private emitScale() {
    const layout = this.layouts[this.currentPage - 1]
    if (layout) this.options.onScaleChange?.(layout.scale)
  }

  private renderVisible() {
    if (this.destroyed || !this.pdfDocument || this.layouts.length === 0) return
    const viewportTop = this.wrapper.scrollTop
    const viewportBottom = viewportTop + Math.max(1, this.wrapper.clientHeight || this.container.clientHeight)
    let start = this.layouts.findIndex(layout => layout.bottom >= viewportTop)
    if (start < 0) start = this.layouts.length - 1
    let end = start
    while (end + 1 < this.layouts.length && this.layouts[end + 1].top <= viewportBottom) end++
    const overscan = this.options.overscan ?? 2
    start = Math.max(0, start - overscan)
    end = Math.min(this.layouts.length - 1, end + overscan)

    for (const [pageNumber, active] of this.activeRenders) {
      if (pageNumber < start + 1 || pageNumber > end + 1) this.removeRender(active, pageNumber)
    }
    for (let index = start; index <= end; index++) {
      const layout = this.layouts[index]
      if (!this.activeRenders.has(layout.pageNumber)) this.renderPage(layout)
    }
  }

  private renderPage(layout: PageLayout) {
    const pdfDocument = this.pdfDocument
    if (!pdfDocument) return
    const canvas = document.createElement('canvas')
    canvas.setAttribute('data-page-number', String(layout.pageNumber))
    canvas.setAttribute('role', 'img')
    canvas.setAttribute('aria-label', `PDF 第 ${layout.pageNumber} 页`)
    Object.assign(canvas.style, {
      backgroundColor: '#fff',
      boxShadow: '0 1px 8px rgb(0 0 0 / 12%)',
      height: `${layout.height}px`,
      left: '50%',
      position: 'absolute',
      top: `${layout.top}px`,
      transform: 'translateX(-50%)',
      visibility: 'hidden',
      width: `${layout.width}px`,
    })
    this.insertCanvas(canvas, layout.pageNumber)
    const active = { canvas } satisfies ActiveRender
    this.activeRenders.set(layout.pageNumber, active)
    void this.drawPage(pdfDocument, layout, active)
  }

  private async drawPage(pdfDocument: PDFDocumentProxy, layout: PageLayout, active: ActiveRender) {
    try {
      const page = await pdfDocument.getPage(layout.pageNumber)
      if (!this.isCurrentRender(layout.pageNumber, active)) return
      const viewport = page.getViewport({ scale: layout.scale })
      const density = Math.min(
        window.devicePixelRatio || 1,
        2,
        Math.sqrt(MAX_CANVAS_PIXELS / Math.max(1, viewport.width * viewport.height)),
        MAX_CANVAS_EDGE / Math.max(1, viewport.width),
        MAX_CANVAS_EDGE / Math.max(1, viewport.height),
      )
      active.canvas.width = Math.max(1, Math.floor(viewport.width * density))
      active.canvas.height = Math.max(1, Math.floor(viewport.height * density))
      const task = page.render({
        canvas: active.canvas,
        viewport,
        transform: density === 1 ? undefined : [density, 0, 0, density, 0, 0],
        background: '#ffffff',
      })
      active.task = task
      await task.promise
      if (this.isCurrentRender(layout.pageNumber, active)) active.canvas.style.visibility = 'visible'
    } catch (error) {
      if (!this.isCurrentRender(layout.pageNumber, active) || isCancelled(error)) return
      active.canvas.style.visibility = 'hidden'
      this.options.onPageError?.(error, layout.pageNumber)
    }
  }

  private isCurrentRender(pageNumber: number, active: ActiveRender) {
    return !this.destroyed && this.activeRenders.get(pageNumber) === active
  }

  private insertCanvas(canvas: HTMLCanvasElement, pageNumber: number) {
    const next = Array.from(this.wrapperMain.children).find(child =>
      Number((child as HTMLElement).dataset.pageNumber) > pageNumber)
    this.wrapperMain.insertBefore(canvas, next ?? null)
  }

  private removeRender(active: ActiveRender, pageNumber: number) {
    if (this.activeRenders.get(pageNumber) !== active) return
    this.activeRenders.delete(pageNumber)
    active.task?.cancel()
    active.canvas.width = 0
    active.canvas.height = 0
    active.canvas.remove()
  }

  private cancelAllRenders() {
    for (const [pageNumber, active] of this.activeRenders) this.removeRender(active, pageNumber)
    this.clearCanvases()
  }

  private clearCanvases() {
    for (const child of Array.from(this.wrapperMain.querySelectorAll('canvas'))) {
      child.width = 0
      child.height = 0
      child.remove()
    }
    this.activeRenders.clear()
  }

  private async destroyLoadingTask() {
    const task = this.loadingTask
    this.loadingTask = null
    this.pdfDocument = null
    this.pageSizes = []
    this.layouts = []
    if (task) await task.destroy().catch(() => undefined)
  }
}

export function init(container: HTMLElement, options: PdfPreviewOptions) {
  return new JsPdfPreview(container, options)
}

export type PdfPreviewInstance = ReturnType<typeof init>
