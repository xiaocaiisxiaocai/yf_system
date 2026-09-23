import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { deferred } from './testUtils'

type ViewerCallbacks = {
  onDocumentLoaded?: (pageCount: number) => void
  onPageChange?: (page: number) => void
  onScaleChange?: (scale: number) => void
  onPageError?: (error: unknown, page: number) => void
}

type PreviewCall = ReturnType<typeof deferred<void>> & { data: Uint8Array }

type ViewerRecord = {
  container: HTMLElement
  callbacks: ViewerCallbacks
  destroyed: boolean
  previews: PreviewCall[]
  pages: number[]
  zooms: Array<number | 'page' | 'fit'>
  preview: (data: Uint8Array) => Promise<void>
  goToPage: (value: number) => void
  setZoom: (value: number | 'page' | 'fit') => void
  destroy: () => Promise<void>
}

const mocks = vi.hoisted(() => ({
  get: vi.fn(),
  init: vi.fn(),
  openPdf: vi.fn(),
}))

vi.mock('../../../api/client', () => ({
  default: { get: mocks.get },
}))

vi.mock('../../../components/pdfEngine', () => ({
  openPdf: mocks.openPdf,
}))

vi.mock('../../../../vendor/vue-office-pdf/src/main', () => ({
  init: mocks.init,
}))

import PdfPreview from '../../../components/PdfPreview'

function configureViewer({ pendingPreview = false, failPreview = false } = {}) {
  const viewers: ViewerRecord[] = []
  mocks.init.mockImplementation((container: HTMLElement, callbacks: ViewerCallbacks) => {
    const viewer: ViewerRecord = {
      container,
      callbacks,
      destroyed: false,
      previews: [],
      pages: [],
      zooms: [],
      preview(data) {
        const pending = { data, ...deferred<void>() }
        this.previews.push(pending)
        if (failPreview) pending.reject(new Error('invalid PDF'))
        else if (!pendingPreview) {
          callbacks.onDocumentLoaded?.(3)
          callbacks.onPageChange?.(1)
          callbacks.onScaleChange?.(0.75)
          pending.resolve()
        }
        return pending.promise
      },
      goToPage(value) {
        const page = Math.min(3, Math.max(1, Math.trunc(value)))
        this.pages.push(page)
        callbacks.onPageChange?.(page)
      },
      setZoom(value) {
        this.zooms.push(value)
        callbacks.onScaleChange?.(value === 'page' ? 0.75 : value === 'fit' ? 4 / 3 : value)
      },
      async destroy() { this.destroyed = true },
    }
    viewers.push(viewer)
    return viewer
  })
  return viewers
}

beforeEach(() => {
  mocks.get.mockResolvedValue({ data: new ArrayBuffer(8) })
})

afterEach(() => {
  document.querySelectorAll('[data-test-toolbar]').forEach(node => node.remove())
})

describe('PDF preview component', () => {
  it('component keeps portal toolbar pagination, fit modes, zoom and reset without download', async () => {
    const viewers = configureViewer()
    const toolbar = document.createElement('div')
    toolbar.dataset.testToolbar = 'true'
    document.body.append(toolbar)
    const user = userEvent.setup()
    const view = render(<PdfPreview fileId={27} toolbarContainer={toolbar} />)

    await waitFor(() => expect(viewers).toHaveLength(1))
    expect(mocks.get).toHaveBeenCalledWith('/files/27/content', expect.objectContaining({ responseType: 'arraybuffer' }))
    expect(view.container.querySelector('iframe')).not.toBeInTheDocument()
    expect(within(toolbar).getByRole('group', { name: 'PDF 阅读控制' })).toBeVisible()
    expect(viewers[0].previews[0].data.byteLength).toBe(8)

    await user.click(within(toolbar).getByRole('button', { name: '下一页' }))
    expect(viewers[0].pages.at(-1)).toBe(2)
    const pageInput = within(toolbar).getByRole('spinbutton', { name: 'PDF 页码' })
    fireEvent.change(pageInput, { target: { value: '99' } })
    fireEvent.blur(pageInput)
    expect(viewers[0].pages.at(-1)).toBe(3)
    await user.click(within(toolbar).getByRole('combobox', { name: 'PDF 缩放' }))
    fireEvent.click(await screen.findByText('适合宽度'))
    expect(viewers[0].zooms.at(-1)).toBe('fit')
    await user.click(within(toolbar).getByRole('button', { name: '放大 PDF' }))
    expect(typeof viewers[0].zooms.at(-1)).toBe('number')
    await user.click(within(toolbar).getByRole('button', { name: '缩小 PDF' }))
    await user.click(within(toolbar).getByRole('button', { name: '重置 PDF 缩放' }))
    expect(viewers[0].zooms.at(-1)).toBe('page')
    expect(view.container).not.toHaveTextContent('下载')

    view.unmount()
    expect(viewers[0].destroyed).toBe(true)
  })

  it('switching files and closing abort requests, destroy viewers and ignore late responses', async () => {
    const viewers = configureViewer()
    const requests: Array<ReturnType<typeof deferred<{ data: ArrayBuffer }>> & { signal: AbortSignal }> = []
    mocks.get.mockImplementation((_url, options) => {
      const request = { ...deferred<{ data: ArrayBuffer }>(), signal: options.signal }
      requests.push(request)
      return request.promise
    })
    const view = render(<PdfPreview fileId={1} />)
    await waitFor(() => expect(requests).toHaveLength(1))
    view.rerender(<PdfPreview fileId={2} />)
    expect(requests[0].signal.aborted).toBe(true)
    requests[0].resolve({ data: new ArrayBuffer(1) })
    await requests[0].promise
    await Promise.resolve()
    expect(viewers).toHaveLength(0)

    requests[1].resolve({ data: new ArrayBuffer(2) })
    await waitFor(() => expect(viewers).toHaveLength(1))
    view.rerender(<PdfPreview fileId={3} />)
    expect(viewers[0].destroyed).toBe(true)
    await waitFor(() => expect(requests).toHaveLength(3))
    view.unmount()
    expect(requests[2].signal.aborted).toBe(true)
    requests[2].resolve({ data: new ArrayBuffer(3) })
    await requests[2].promise
    await Promise.resolve()
    expect(viewers).toHaveLength(1)

    const pendingViewers = configureViewer({ pendingPreview: true })
    mocks.get.mockResolvedValue({ data: new ArrayBuffer(4) })
    const parsing = render(<PdfPreview fileId={4} />)
    await waitFor(() => expect(pendingViewers).toHaveLength(1))
    parsing.unmount()
    expect(pendingViewers[0].destroyed).toBe(true)
    pendingViewers[0].callbacks.onDocumentLoaded?.(3)
    pendingViewers[0].previews[0].resolve()
    await pendingViewers[0].previews[0].promise
    expect(screen.queryByRole('group', { name: 'PDF 阅读控制' })).not.toBeInTheDocument()
  })

  it('load and page errors stay visible and retry creates a fresh owned viewer', async () => {
    const loadingViewers = configureViewer({ failPreview: true })
    const user = userEvent.setup()
    const loading = render(<PdfPreview fileId={27} />)

    expect(await screen.findByRole('alert')).toHaveTextContent('PDF 加载失败')
    await user.click(screen.getByRole('button', { name: '重试 PDF 预览' }))
    await waitFor(() => expect(mocks.get).toHaveBeenCalledTimes(2))
    expect(loadingViewers[0].destroyed).toBe(true)
    loading.unmount()

    const pageViewers = configureViewer()
    const page = render(<PdfPreview fileId={28} />)
    await waitFor(() => expect(pageViewers).toHaveLength(1))
    pageViewers[0].callbacks.onPageError?.(new Error('decode'), 1)
    expect(await screen.findByRole('alert')).toHaveTextContent('PDF 页面渲染失败')
    page.unmount()
  })
})
