import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { init } from '../../../../vendor/vue-office-pdf/src/main'
import type { PdfPreviewOptions } from '../../../../vendor/vue-office-pdf/src/main'
import { deferred, flushAsyncWork } from './testUtils'

type PageSize = { width: number; height: number }
type RenderRecord = {
  pageNumber: number
  options: {
    canvas: HTMLCanvasElement
    viewport: { width: number; height: number }
    transform?: number[]
  }
  cancelled: boolean
  resolve: () => void
  reject: (reason?: unknown) => void
}

type LoadingRecord = ReturnType<typeof deferred<FakePdfDocument>> & {
  data: Uint8Array
  destroyed: boolean
}

type FakePdfDocument = {
  numPages: number
  getPage: (pageNumber: number) => Promise<{
    getViewport: (options: { scale: number }) => { width: number; height: number }
    render: (options: RenderRecord['options']) => { promise: Promise<void>; cancel: () => void }
  }>
}

const observers: TrackingResizeObserver[] = []

class TrackingResizeObserver implements ResizeObserver {
  readonly callback: ResizeObserverCallback
  disconnected = false
  target?: Element

  constructor(callback: ResizeObserverCallback) {
    this.callback = callback
    observers.push(this)
  }

  observe(target: Element) { this.target = target }
  unobserve() {}
  disconnect() { this.disconnected = true }
}

function setSize(element: Element, width: number, height: number) {
  Object.defineProperty(element, 'clientWidth', { configurable: true, value: width })
  Object.defineProperty(element, 'clientHeight', { configurable: true, value: height })
}

function rendererHarness({
  pageSizes = [{ width: 600, height: 800 }, { width: 1200, height: 600 }, { width: 300, height: 1200 }],
  pendingRender = false,
  pendingParse = false,
  failPage = 0,
}: {
  pageSizes?: PageSize[]
  pendingRender?: boolean
  pendingParse?: boolean
  failPage?: number
} = {}) {
  const renders: RenderRecord[] = []
  const tasks: LoadingRecord[] = []
  const pdf: FakePdfDocument = {
    numPages: pageSizes.length,
    getPage: async pageNumber => ({
      getViewport: ({ scale }) => ({
        width: pageSizes[pageNumber - 1].width * scale,
        height: pageSizes[pageNumber - 1].height * scale,
      }),
      render: options => {
        const pending = deferred<void>()
        const record: RenderRecord = { pageNumber, options, cancelled: false, ...pending }
        renders.push(record)
        if (pageNumber === failPage) pending.reject(new Error('page decode failed'))
        else if (!pendingRender) pending.resolve()
        return {
          promise: pending.promise,
          cancel: () => {
            record.cancelled = true
            pending.reject(Object.assign(new Error('cancelled'), { name: 'RenderingCancelledException' }))
          },
        }
      },
    }),
  }
  const openPdf = (data: Uint8Array) => {
    const pending = deferred<FakePdfDocument>()
    const record: LoadingRecord = { data, destroyed: false, ...pending }
    tasks.push(record)
    if (!pendingParse) pending.resolve(pdf)
    return {
      promise: pending.promise,
      destroy: async () => { record.destroyed = true },
    }
  }
  const container = document.createElement('div')
  setSize(container, 832, 632)
  document.body.append(container)
  const events = {
    loaded: [] as number[],
    pages: [] as number[],
    scales: [] as number[],
    errors: [] as Array<{ error: unknown; page: number }>,
  }
  const viewer = init(container, {
    openPdf: openPdf as unknown as PdfPreviewOptions['openPdf'],
    onDocumentLoaded: value => events.loaded.push(value),
    onPageChange: value => events.pages.push(value),
    onScaleChange: value => events.scales.push(value),
    onPageError: (error, page) => events.errors.push({ error, page }),
  })
  const wrapper = container.querySelector<HTMLElement>('.vue-office-pdf')!
  setSize(wrapper, 832, 632)
  return { viewer, container, wrapper, pdf, renders, tasks, events }
}

beforeEach(() => {
  observers.length = 0
  globalThis.ResizeObserver = TrackingResizeObserver
  vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue({} as never)
})

afterEach(() => {
  document.querySelectorAll('.vue-office-pdf').forEach(node => node.parentElement?.remove())
})

describe('vendored PDF renderer', () => {
  it('real adapter lays out mixed page sizes and renders only the visible virtual range', async () => {
    const pageSizes = Array.from({ length: 40 }, (_, index) => index % 2 === 0
      ? { width: 600, height: 800 }
      : { width: 1200, height: 600 })
    const h = rendererHarness({ pageSizes })

    await h.viewer.preview(new Uint8Array([1, 2, 3]))
    await flushAsyncWork()
    expect(h.events.loaded).toEqual([40])
    const canvases = [...h.container.querySelectorAll('canvas')]
    expect(canvases.length).toBeGreaterThan(1)
    expect(canvases.length).toBeLessThanOrEqual(6)
    expect(canvases[0].style.width).not.toBe(canvases[1].style.width)
    expect(canvases[0].style.height).not.toBe(canvases[1].style.height)
    for (const render of h.renders) {
      expect(render.options.canvas.width * render.options.canvas.height).toBeLessThanOrEqual(16_000_000)
      expect(render.options.canvas.width).toBeLessThanOrEqual(8192)
      expect(render.options.canvas.height).toBeLessThanOrEqual(8192)
    }
    await h.viewer.destroy()
  })

  it('page navigation, zoom, reset and virtual scrolling cancel obsolete renders', async () => {
    const h = rendererHarness({
      pageSizes: Array.from({ length: 20 }, () => ({ width: 600, height: 800 })),
      pendingRender: true,
    })
    await h.viewer.preview(new Uint8Array([1]))
    await flushAsyncWork()
    const firstBatch = [...h.renders]
    expect(firstBatch[0].options.viewport.height).toBe(600)

    h.viewer.goToPage(10)
    await flushAsyncWork()
    expect(firstBatch.every(render => render.cancelled)).toBe(true)
    expect(h.events.pages.at(-1)).toBe(10)
    const visiblePages = [...h.container.querySelectorAll<HTMLCanvasElement>('canvas')]
      .map(canvas => Number(canvas.dataset.pageNumber))
    expect(visiblePages.length).toBeLessThanOrEqual(7)
    expect(visiblePages.every(page => page >= 7 && page <= 13)).toBe(true)

    const beforeZoom = [...h.renders]
    h.viewer.setZoom(1.5)
    await flushAsyncWork()
    expect(beforeZoom.every(render => render.cancelled)).toBe(true)
    expect(h.renders.at(-1)?.options.viewport.width).toBe(900)
    h.viewer.setZoom('fit')
    await flushAsyncWork()
    expect(h.events.scales.at(-1)).toBeCloseTo(800 / 600)
    h.viewer.setZoom('page')
    await flushAsyncWork()
    expect(h.events.scales.at(-1)).toBe(0.75)
    expect(h.events.pages.at(-1)).toBe(10)
    await h.viewer.destroy()
    expect(h.renders.every(render => render.cancelled)).toBe(true)
  })

  it('adapter reports page failures and bounds a huge high-zoom backing store', async () => {
    const h = rendererHarness({
      pageSizes: [{ width: 1_000_000_000_000, height: 1_000_000_000_000 }],
      failPage: 1,
    })
    await h.viewer.preview(new Uint8Array([1]))
    h.viewer.setZoom(4)
    await flushAsyncWork()

    expect(h.events.errors.at(-1)?.page).toBe(1)
    const render = h.renders.at(-1)!
    expect(render.options.canvas.width * render.options.canvas.height).toBeLessThanOrEqual(16_000_000)
    expect(render.options.canvas.width).toBeLessThanOrEqual(8192)
    expect(render.options.canvas.height).toBeLessThanOrEqual(8192)
    expect(render.options.canvas).toHaveStyle({ visibility: 'hidden' })
    await h.viewer.destroy()
  })

  it('fit modes exclude the vertical scrollbar gutter from page and scroll-surface width', async () => {
    const h = rendererHarness({ pageSizes: Array.from({ length: 10 }, () => ({ width: 600, height: 800 })) })
    setSize(h.wrapper, 817, 632)
    await h.viewer.preview(new Uint8Array([1]))
    await flushAsyncWork()
    const surface = h.wrapper.querySelector<HTMLElement>('.vue-office-pdf-wrapper')!
    expect(Number.parseFloat(surface.style.width)).toBeLessThanOrEqual(817)

    h.viewer.setZoom('fit')
    await flushAsyncWork()
    expect(Number.parseFloat(surface.style.width)).toBeLessThanOrEqual(817)
    expect(h.renders.at(-1)?.options.viewport.width).toBe(785)
    h.viewer.setZoom(0.96)
    await flushAsyncWork()
    expect(Number.parseFloat(surface.style.width)).toBeLessThanOrEqual(817)
    await h.viewer.destroy()
  })

  it('wide zoom expands the scroll surface and numeric resize mounts newly visible pages', async () => {
    const h = rendererHarness({ pageSizes: Array.from({ length: 20 }, () => ({ width: 600, height: 800 })) })
    await h.viewer.preview(new Uint8Array([1]))
    h.viewer.setZoom(4)
    await flushAsyncWork()
    const surface = h.wrapper.querySelector<HTMLElement>('.vue-office-pdf-wrapper')!
    const canvas = h.container.querySelector('canvas')!
    expect(surface.style.minWidth).toBe('100%')
    expect(surface.style.width).toBe('2432px')
    expect(canvas.style.width).toBe('2400px')
    expect(Number(surface.style.width.slice(0, -2)) / 2 - Number(canvas.style.width.slice(0, -2)) / 2).toBe(16)

    h.viewer.setZoom(1)
    await flushAsyncWork()
    const beforeResize = [...h.container.querySelectorAll<HTMLCanvasElement>('canvas')]
      .map(item => Number(item.dataset.pageNumber))
    expect(Math.max(...beforeResize)).toBeLessThanOrEqual(3)
    setSize(h.container, 832, 4000)
    setSize(h.wrapper, 832, 4000)
    h.viewer.resize()
    await flushAsyncWork()
    const afterResize = [...h.container.querySelectorAll<HTMLCanvasElement>('canvas')]
      .map(item => Number(item.dataset.pageNumber))
    expect(Math.max(...afterResize)).toBeGreaterThanOrEqual(7)
    await h.viewer.destroy()
  })

  it('destroy cancels parsing/rendering and removes worker, observer, listener and DOM ownership', async () => {
    const parsing = rendererHarness({ pendingParse: true })
    const removeListener = vi.spyOn(parsing.wrapper, 'removeEventListener')
    const preview = parsing.viewer.preview(new Uint8Array([1]))
    await flushAsyncWork()
    await parsing.viewer.destroy()
    expect(parsing.tasks[0].destroyed).toBe(true)
    expect(observers[0].disconnected).toBe(true)
    expect(removeListener).toHaveBeenCalledWith('scroll', expect.any(Function))
    expect(parsing.container.children).toHaveLength(0)
    parsing.tasks[0].resolve(parsing.pdf)
    await preview
    expect(parsing.renders).toHaveLength(0)

    const rendering = rendererHarness({ pendingRender: true })
    await rendering.viewer.preview(new Uint8Array([2]))
    await flushAsyncWork()
    await rendering.viewer.destroy()
    expect(rendering.renders.every(render => render.cancelled)).toBe(true)
    expect(rendering.renders.every(render => render.options.canvas.width === 0 && render.options.canvas.height === 0)).toBe(true)
  })
})
