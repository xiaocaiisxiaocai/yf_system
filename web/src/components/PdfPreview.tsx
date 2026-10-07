import { useEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { Button, InputNumber, Select, Spin } from '@arco-design/web-react'
import http, { type QuietRequestConfig } from '../api/client'
import { init } from '../../vendor/vue-office-pdf/src/main'
import type { PdfPreviewInstance, PdfZoom } from '../../vendor/vue-office-pdf/src/main'

function PreviewError({ message, onRetry }: { message: string; onRetry: () => void }) {
  return <div className="pdf-preview-status" role="alert">
    <span>{message}</span>
    <Button onClick={onRetry} aria-label="重试 PDF 预览">重试</Button>
  </div>
}

function parseZoom(value: string): PdfZoom {
  return value === 'page' || value === 'fit' ? value : Number(value)
}

/** Loads the PDF bytes for a non-collaboration source (e.g. OEM files); defaults to `/files/{id}/content`. */
export type PdfContentLoader = (signal: AbortSignal) => Promise<ArrayBuffer>

function PdfDocument({ fileId, loadContent, onRetry, toolbarContainer }: {
  fileId: number
  loadContent?: PdfContentLoader
  onRetry: () => void
  toolbarContainer?: HTMLElement | null
}) {
  const [error, setError] = useState('')
  const [pageNumber, setPageNumber] = useState(1)
  const [pageCount, setPageCount] = useState(0)
  const [zoom, setZoom] = useState('page')
  const [scale, setScale] = useState(1)
  const [ready, setReady] = useState(false)
  const viewportRef = useRef<HTMLDivElement>(null)
  const viewerRef = useRef<PdfPreviewInstance | null>(null)
  const scaleRef = useRef(1)

  useEffect(() => {
    const container = viewportRef.current
    if (!container) return
    const previewContainer: HTMLElement = container
    let active = true
    let viewer: PdfPreviewInstance | null = null
    const controller = new AbortController()

    async function load() {
      try {
        const [data, engine] = await Promise.all([
          loadContent
            ? loadContent(controller.signal)
            : http.get<ArrayBuffer>(`/files/${fileId}/content`, {
              responseType: 'arraybuffer',
              signal: controller.signal,
              quietNetworkError: true,
            } as QuietRequestConfig).then(response => response.data),
          import('./pdfEngine'),
        ])
        if (!active) return
        viewer = init(previewContainer, {
          openPdf: engine.openPdf,
          onDocumentLoaded: total => {
            if (!active) return
            setPageCount(total)
            setReady(true)
          },
          onPageChange: page => {
            if (active) setPageNumber(page)
          },
          onScaleChange: value => {
            if (active) {
              scaleRef.current = value
              setScale(value)
            }
          },
          onPageError: () => {
            if (active) setError('PDF 页面渲染失败，请重试。')
          },
        })
        viewerRef.current = viewer
        await viewer.preview(new Uint8Array(data))
      } catch (cause) {
        if (active) {
          setError(cause instanceof Error && cause.name === 'PasswordException'
            ? '此 PDF 受密码保护，暂不支持在线预览。'
            : 'PDF 加载失败，文件可能损坏或网络暂时不可用，请重试。')
        }
      }
    }

    void load()
    return () => {
      active = false
      controller.abort()
      if (viewerRef.current === viewer) viewerRef.current = null
      void viewer?.destroy()
    }
    // The loader is fixed for a mounted document; PdfPreview remounts it per file and retry.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [fileId])

  const changePage = (value: number | undefined) => {
    if (pageCount === 0 || value === undefined || !Number.isFinite(value)) return
    viewerRef.current?.goToPage(value)
  }
  const changeZoom = (value: string) => {
    setZoom(value)
    setError('')
    viewerRef.current?.setZoom(parseZoom(value))
  }
  const stepZoom = (direction: number) => {
    const next = Math.min(4, Math.max(0.1,
      Math.round(scaleRef.current * (direction > 0 ? 1.25 : 0.8) * 100) / 100))
    changeZoom(String(next))
  }
  const toolbar = ready && !error && <div className="pdf-preview-toolbar" role="group" aria-label="PDF 阅读控制">
      <Button size="small" aria-label="上一页" disabled={pageNumber <= 1} onClick={() => changePage(pageNumber - 1)}>上一页</Button>
      <InputNumber size="small" aria-label="PDF 页码" min={1} max={pageCount} precision={0} value={pageNumber} onChange={changePage} style={{ width: 76 }} />
      <span role="status">/ {pageCount} 页</span>
      <Button size="small" aria-label="下一页" disabled={pageNumber >= pageCount} onClick={() => changePage(pageNumber + 1)}>下一页</Button>
      <Button size="small" aria-label="缩小 PDF" disabled={scale <= 0.1} onClick={() => stepZoom(-1)}>缩小</Button>
      <Select size="small" aria-label="PDF 缩放" value={zoom} onChange={changeZoom} style={{ width: 120 }}
        getPopupContainer={trigger => trigger.closest<HTMLElement>('.file-preview-modal') ?? trigger.parentElement ?? trigger}
        options={[{ value: 'page', label: '适合整页' }, { value: 'fit', label: '适合宽度' },
          ...[...new Set([0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4, ...(Number.isFinite(Number(zoom)) ? [Number(zoom)] : [])])]
            .sort((a, b) => a - b).map(value => ({ value: String(value), label: `${Math.round(value * 100)}%` }))]} />
      <Button size="small" aria-label="放大 PDF" disabled={scale >= 4} onClick={() => stepZoom(1)}>放大</Button>
      <Button size="small" aria-label="重置 PDF 缩放" onClick={() => changeZoom('page')}>重置</Button>
    </div>

  return <section className="pdf-preview" aria-label="PDF 预览">
    {toolbarContainer ? createPortal(toolbar, toolbarContainer) : toolbar}
    <div className="pdf-preview-viewport" tabIndex={0} role="region" aria-label="PDF 页面内容" style={{ position: 'relative' }}>
      <div ref={viewportRef} style={{ height: '100%', minHeight: 0 }} />
      {error
        ? <div style={{ background: 'var(--color-fill-2)', inset: 0, position: 'absolute', zIndex: 1 }}><PreviewError message={error} onRetry={onRetry} /></div>
        : !ready && <div className="pdf-preview-status" role="status" style={{ inset: 0, position: 'absolute', zIndex: 1 }}><Spin />正在加载 PDF…</div>}
    </div>
  </section>
}

export default function PdfPreview({ fileId, loadContent, toolbarContainer }: {
  fileId: number
  /** Optional byte source replacing the collaboration content endpoint; `fileId` still keys the document. */
  loadContent?: PdfContentLoader
  toolbarContainer?: HTMLElement | null
}) {
  const [attempt, setAttempt] = useState(0)
  // Every file/retry owns a fresh request, loading task, virtual canvas list and worker.
  return <PdfDocument key={`${fileId}:${attempt}`} fileId={fileId} loadContent={loadContent} toolbarContainer={toolbarContainer} onRetry={() => setAttempt(value => value + 1)} />
}
