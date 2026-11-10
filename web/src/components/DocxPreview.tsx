import { useEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { Button, Result, Select, Spin } from '@arco-design/web-react'
import http, { type QuietRequestConfig } from '../api/client'
import viewerHtml from '../../generated/docx-viewer.html?raw'
import './PptxPreview.css'
import { createPreviewDeadline, createRenderDeadline, type PreviewContentLoader } from './previewDeadline'

type PreviewState = 'loading' | 'ready' | 'error'

function randomChannel() {
  return Array.from(crypto.getRandomValues(new Uint8Array(16)), byte => byte.toString(16).padStart(2, '0')).join('')
}

function validScale(value: unknown) {
  const number = Number(value)
  return Number.isFinite(number) && number >= 0.1 && number <= 4 ? number : undefined
}

function DocxDocument({ fileId, loadContent, toolbarContainer, onRetry }: {
  fileId: number
  loadContent?: PreviewContentLoader
  toolbarContainer?: HTMLElement | null
  onRetry: () => void
}) {
  const frameRef = useRef<HTMLIFrameElement>(null)
  const frameLoaded = useRef(false)
  const sendBuffer = useRef<() => void>(() => {})
  const [channel] = useState(randomChannel)
  const [state, setState] = useState<PreviewState>('loading')
  const [message, setMessage] = useState('')
  const [zoom, setZoom] = useState('fit')
  const [scale, setScale] = useState(1)
  const html = viewerHtml.replaceAll('YF_VIEWER_NONCE', channel)

  useEffect(() => {
    let active = true
    let buffer: ArrayBuffer | undefined
    let sent = false
    const controller = new AbortController()
    const fail = (text: string) => {
      deadline.clear()
      setMessage(text)
      setState('error')
    }
    const timedOut = () => {
      controller.abort()
      if (active) fail('Word 文档加载或渲染超时，请重试。')
    }
    const deadline = loadContent ? createRenderDeadline(timedOut) : createPreviewDeadline(timedOut)
    const receive = (event: MessageEvent) => {
      if (!active || event.source !== frameRef.current?.contentWindow || event.data?.channel !== channel) return
      if (event.data.type === 'docx:rendered' || event.data.type === 'docx:scale-changed') {
        const nextScale = validScale(event.data.scale)
        if (!nextScale) {
          if (event.data.type === 'docx:rendered') fail('Word 渲染器返回了无效结果，请重试。')
          return
        }
        setScale(nextScale)
        setZoom(event.data.zoom === 'fit' ? 'fit' : String(nextScale))
        if (event.data.type === 'docx:rendered') { deadline.clear(); setState('ready') }
      } else if (event.data.type === 'docx:error') {
        fail('Word 文档加载失败，文件可能损坏、受密码保护或包含暂不支持的内容。')
      }
    }
    window.addEventListener('message', receive)
    sendBuffer.current = () => {
      if (!active || !frameLoaded.current || !buffer || sent) return
      const signature = new Uint8Array(buffer, 0, Math.min(buffer.byteLength, 4))
      if (signature[0] !== 0x50 || signature[1] !== 0x4b) {
        fail('文件不是有效的 DOCX 文档（旧版 .doc 请下载后查看）。')
        return
      }
      sent = true
      frameRef.current?.contentWindow?.postMessage({ type: 'docx:load', channel, buffer }, '*', [buffer])
      buffer = undefined
    }
    void (loadContent ? loadContent(controller.signal) : http.get<ArrayBuffer>(`/files/${fileId}/content`, {
      responseType: 'arraybuffer',
      signal: controller.signal,
      quietNetworkError: true,
      timeout: 0,
      onDownloadProgress: (event) => deadline.progress(event.loaded),
    } as QuietRequestConfig).then(response => response.data)).then(data => {
      if (active) { deadline.downloaded(); buffer = data; sendBuffer.current() }
    }).catch(() => {
      if (active) fail('Word 文件读取失败，请检查网络后重试。')
    })
    return () => {
      active = false
      deadline.clear()
      controller.abort()
      window.removeEventListener('message', receive)
      sendBuffer.current = () => {}
    }
  }, [fileId, channel, loadContent])

  const changeZoom = (value: number | string) => {
    if (state !== 'ready') return
    frameRef.current?.contentWindow?.postMessage({ type: 'docx:command', command: 'zoom', channel, value }, '*')
  }
  const stepZoom = (direction: number) => {
    changeZoom(Math.min(4, Math.max(0.1, Math.round(scale * (direction > 0 ? 1.25 : 0.8) * 100) / 100)))
  }
  const toolbar = state === 'ready' && <div className="pptx-preview-toolbar" role="group" aria-label="Word 阅读控制">
    <Button size="small" aria-label="缩小 Word 文档" disabled={scale <= 0.1} onClick={() => stepZoom(-1)}>缩小</Button>
    <Select size="small" aria-label="Word 缩放" value={zoom} onChange={changeZoom} style={{ width: 120 }}
      getPopupContainer={trigger => trigger.closest<HTMLElement>('.file-preview-modal') ?? trigger.parentElement ?? trigger}
      options={[{ value: 'fit', label: '适合宽度' },
        ...[...new Set([0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4, ...(Number.isFinite(Number(zoom)) ? [Number(zoom)] : [])])]
          .sort((a, b) => a - b).map(value => ({ value: String(value), label: `${Math.round(value * 100)}%` }))]} />
    <Button size="small" aria-label="放大 Word 文档" disabled={scale >= 4} onClick={() => stepZoom(1)}>放大</Button>
  </div>

  return <section className="pptx-preview" aria-label="Word 预览">
    {toolbarContainer ? createPortal(toolbar, toolbarContainer) : toolbar}
    <div className="pptx-preview-viewport">
      <iframe ref={frameRef} className="pptx-preview-frame" title="Word 预览内容"
        sandbox="allow-scripts" referrerPolicy="no-referrer" srcDoc={html}
        onLoad={() => { frameLoaded.current = true; sendBuffer.current() }}
        style={{ visibility: state === 'ready' ? 'visible' : 'hidden' }} />
      {state === 'loading' && <div className="pptx-preview-status" role="status"><Spin />正在渲染文档…</div>}
      {state === 'error' && <div className="pptx-preview-status" role="alert"><Result status="error" title="Word 预览失败" subTitle={message}
        extra={<Button aria-label="重试 Word 预览" onClick={onRetry}>重试</Button>} /></div>}
    </div>
  </section>
}

export default function DocxPreview({ fileId, loadContent, toolbarContainer }: {
  fileId: number
  loadContent?: PreviewContentLoader
  toolbarContainer?: HTMLElement | null
}) {
  const [attempt, setAttempt] = useState(0)
  return <DocxDocument key={`${fileId}:${attempt}`} fileId={fileId} loadContent={loadContent} toolbarContainer={toolbarContainer} onRetry={() => setAttempt(value => value + 1)} />
}
