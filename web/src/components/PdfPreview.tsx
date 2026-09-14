import { useEffect, useRef, useState } from 'react'
import { Button, InputNumber, Select, Spin } from '@arco-design/web-react'
import type { PDFDocumentProxy, PDFDocumentLoadingTask, RenderTask } from 'pdfjs-dist'
import http from '../api/client'

function PreviewError({ message, onRetry }: { message: string; onRetry: () => void }) {
  return <div className="pdf-preview-status" role="alert">
    <span>{message}</span>
    <Button onClick={onRetry} aria-label="重试 PDF 预览">重试</Button>
  </div>
}

function PdfPage({ document, pageNumber, zoom, width, height, onScale, onRetry }: {
  document: PDFDocumentProxy; pageNumber: number; zoom: string; width: number; height: number; onScale: (scale: number) => void; onRetry: () => void
}) {
  const canvasRef = useRef<HTMLCanvasElement>(null)
  const [ready, setReady] = useState(false)
  const [failed, setFailed] = useState(false)
  useEffect(() => {
    const canvas = canvasRef.current
    if (!canvas) return
    let active = true
    let renderTask: RenderTask | undefined
    async function render() {
      try {
        const page = await document.getPage(pageNumber)
        if (!active || !canvas) return
        const base = page.getViewport({ scale: 1 })
        const scale = zoom === 'page' ? Math.min(width / base.width, height / base.height)
          : zoom === 'fit' ? width / base.width : Number(zoom)
        onScale(scale)
        const viewport = page.getViewport({ scale })
        // Bound backing-store memory for engineering drawings and high-DPI screens.
        const density = Math.min(window.devicePixelRatio || 1, 2,
          Math.sqrt(16_000_000 / (viewport.width * viewport.height)),
          8192 / viewport.width, 8192 / viewport.height)
        canvas.width = Math.max(1, Math.floor(viewport.width * density))
        canvas.height = Math.max(1, Math.floor(viewport.height * density))
        canvas.style.width = `${viewport.width}px`
        canvas.style.height = `${viewport.height}px`
        renderTask = page.render({ canvas, viewport, transform: [density, 0, 0, density, 0, 0], background: '#ffffff' })
        await renderTask.promise
        if (active) setReady(true)
      } catch {
        if (active) setFailed(true)
      }
    }
    void render()
    return () => {
      active = false
      renderTask?.cancel()
      canvas.width = 0
      canvas.height = 0
    }
  }, [document, pageNumber, zoom, width, height, onScale])
  return <>
    {failed ? <PreviewError message="PDF 页面渲染失败，请重试。" onRetry={onRetry} />
      : !ready && <div className="pdf-preview-status" role="status"><Spin />正在渲染第 {pageNumber} 页…</div>}
    <canvas ref={canvasRef} role="img" aria-label={`PDF 第 ${pageNumber} 页`} style={{ display: ready && !failed ? 'block' : 'none' }} />
  </>
}

function PdfDocument({ fileId, onRetry }: { fileId: number; onRetry: () => void }) {
  const [document, setDocument] = useState<PDFDocumentProxy | null>(null)
  const [error, setError] = useState('')
  const [pageNumber, setPageNumber] = useState(1)
  const [zoom, setZoom] = useState('page')
  const [scale, setScale] = useState(1)
  const [width, setWidth] = useState(0)
  const [height, setHeight] = useState(0)
  const viewportRef = useRef<HTMLDivElement>(null)
  useEffect(() => {
    let active = true
    let task: PDFDocumentLoadingTask | undefined
    const controller = new AbortController()
    async function load() {
      try {
        const [response, engine] = await Promise.all([
          http.get<ArrayBuffer>(`/files/${fileId}/content`, { responseType: 'arraybuffer', signal: controller.signal }),
          import('./pdfEngine'),
        ])
        if (!active) return
        task = engine.openPdf(new Uint8Array(response.data))
        const loaded = await task.promise
        if (active) setDocument(loaded)
      } catch (cause) {
        if (active) setError(cause instanceof Error && cause.name === 'PasswordException'
          ? '此 PDF 受密码保护，暂不支持在线预览。'
          : 'PDF 加载失败，文件可能损坏或网络暂时不可用，请重试。')
      }
    }
    void load()
    return () => {
      active = false
      controller.abort()
      // Destroy the worker as well as any pending parsing/rendering work.
      void task?.destroy().catch(() => undefined)
    }
  }, [fileId])
  useEffect(() => {
    const element = viewportRef.current
    if (!element) return
    const update = () => {
      setWidth(Math.max(1, element.clientWidth - 32))
      setHeight(Math.max(1, element.clientHeight - 32))
    }
    update()
    const observer = new ResizeObserver(update)
    observer.observe(element)
    return () => observer.disconnect()
  }, [])
  const changePage = (value: number | undefined) => {
    if (document && value !== undefined && Number.isFinite(value)) {
      setPageNumber(Math.min(document.numPages, Math.max(1, Math.trunc(value))))
      viewportRef.current?.scrollTo?.({ top: 0, left: 0 })
    }
  }
  const changeZoom = (value: string) => {
    setZoom(value)
    viewportRef.current?.scrollTo?.({ top: 0, left: 0 })
  }
  const stepZoom = (direction: number) => {
    setZoom(current => String(Math.min(4, Math.max(0.1,
      Math.round((Number.isFinite(Number(current)) ? Number(current) : scale) * (direction > 0 ? 1.25 : 0.8) * 100) / 100))))
  }
  return <section className="pdf-preview" aria-label="PDF 预览">
    {document && !error && <div className="pdf-preview-toolbar" role="group" aria-label="PDF 阅读控制">
      <Button size="small" aria-label="上一页" disabled={pageNumber <= 1} onClick={() => changePage(pageNumber - 1)}>上一页</Button>
      <InputNumber size="small" aria-label="PDF 页码" min={1} max={document.numPages} precision={0} value={pageNumber} onChange={changePage} style={{ width: 76 }} />
      <span role="status">/ {document.numPages} 页</span>
      <Button size="small" aria-label="下一页" disabled={pageNumber >= document.numPages} onClick={() => changePage(pageNumber + 1)}>下一页</Button>
      <Button size="small" aria-label="缩小 PDF" disabled={scale <= 0.1} onClick={() => stepZoom(-1)}>缩小</Button>
      <Select size="small" aria-label="PDF 缩放" value={zoom} onChange={changeZoom} style={{ width: 120 }}
        options={[{ value: 'page', label: '适合整页' }, { value: 'fit', label: '适合宽度' },
          ...[...new Set([0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4, ...(Number.isFinite(Number(zoom)) ? [Number(zoom)] : [])])]
            .sort((a, b) => a - b).map(value => ({ value: String(value), label: `${Math.round(value * 100)}%` }))]} />
      <Button size="small" aria-label="放大 PDF" disabled={scale >= 4} onClick={() => stepZoom(1)}>放大</Button>
      <Button size="small" aria-label="重置 PDF 缩放" onClick={() => changeZoom('page')}>重置</Button>
    </div>}
    <div ref={viewportRef} className="pdf-preview-viewport" tabIndex={0} aria-label="PDF 页面内容">
      {error ? <PreviewError message={error} onRetry={onRetry} />
        : document && width > 0 && height > 0
          ? <PdfPage key={`${pageNumber}:${zoom}:${width}:${height}`} document={document} pageNumber={pageNumber} zoom={zoom} width={width} height={height} onScale={setScale} onRetry={onRetry} />
          : <div className="pdf-preview-status" role="status"><Spin />正在加载 PDF…</div>}
    </div>
  </section>
}

export default function PdfPreview({ fileId }: { fileId: number }) {
  const [attempt, setAttempt] = useState(0)
  // Every file/retry owns fresh document, canvas, page and zoom state.
  return <PdfDocument key={`${fileId}:${attempt}`} fileId={fileId} onRetry={() => setAttempt(value => value + 1)} />
}
