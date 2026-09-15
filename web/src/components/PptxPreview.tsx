import { useEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { Button, InputNumber, Result, Select, Spin } from '@arco-design/web-react'
import http, { type QuietRequestConfig } from '../api/client'
import viewerHtml from '../../generated/pptx-viewer.html?raw'
import './PptxPreview.css'

type PreviewState = 'loading' | 'ready' | 'error'
type ZoomValue = 'page' | 'fit' | string

function randomChannel() {
  return Array.from(crypto.getRandomValues(new Uint8Array(16)), byte => byte.toString(16).padStart(2, '0')).join('')
}

function finiteNumber(value: unknown, minimum: number, maximum: number) {
  const number = Number(value)
  return Number.isFinite(number) && number >= minimum && number <= maximum ? number : undefined
}

function PptxDocument({ fileId, toolbarContainer, onRetry }: {
  fileId: number
  toolbarContainer?: HTMLElement | null
  onRetry: () => void
}) {
  const frameRef = useRef<HTMLIFrameElement>(null)
  const frameLoaded = useRef(false)
  const sendBuffer = useRef<() => void>(() => {})
  const pageCountRef = useRef(0)
  const [channel] = useState(randomChannel)
  const [state, setState] = useState<PreviewState>('loading')
  const [message, setMessage] = useState('')
  const [pageNumber, setPageNumber] = useState(1)
  const [pageCount, setPageCount] = useState(0)
  const [zoom, setZoom] = useState<ZoomValue>('page')
  const [scale, setScale] = useState(1)
  const html = viewerHtml.replaceAll('YF_VIEWER_NONCE', channel)

  useEffect(() => {
    let active = true
    let buffer: ArrayBuffer | undefined
    let sent = false
    const controller = new AbortController()
    const timer = setTimeout(() => {
      if (active) {
        setMessage('PPTX 渲染超时，请重试。')
        setState('error')
      }
    }, 60000)
    const fail = (text: string) => {
      clearTimeout(timer)
      setMessage(text)
      setState('error')
    }
    const receive = (event: MessageEvent) => {
      if (!active || event.source !== frameRef.current?.contentWindow || event.data?.channel !== channel) return
      if (event.data.type === 'pptx:rendered') {
        const count = finiteNumber(event.data.pageCount, 1, 10000)
        const page = finiteNumber(event.data.pageNumber, 1, count ?? 1)
        const nextScale = finiteNumber(event.data.scale, 0.1, 4)
        if (!count || !page || !nextScale) {
          fail('PPTX 渲染器返回了无效结果，请重试。')
          return
        }
        clearTimeout(timer)
        pageCountRef.current = Math.round(count)
        setPageCount(pageCountRef.current)
        setPageNumber(Math.round(page))
        setScale(nextScale)
        setZoom(event.data.zoom === 'fit' ? 'fit' : 'page')
        setState('ready')
      } else if (event.data.type === 'pptx:page-changed') {
        const page = finiteNumber(event.data.pageNumber, 1, pageCountRef.current || 10000)
        if (page) setPageNumber(Math.round(page))
      } else if (event.data.type === 'pptx:scale-changed') {
        const nextScale = finiteNumber(event.data.scale, 0.1, 4)
        if (!nextScale) return
        setScale(nextScale)
        setZoom(event.data.zoom === 'page' || event.data.zoom === 'fit' ? event.data.zoom : String(nextScale))
      } else if (event.data.type === 'pptx:error') {
        fail('PPTX 加载失败，文件可能损坏、受密码保护或包含暂不支持的内容。')
      }
    }
    window.addEventListener('message', receive)
    sendBuffer.current = () => {
      if (!active || !frameLoaded.current || !buffer || sent) return
      const signature = new Uint8Array(buffer, 0, Math.min(buffer.byteLength, 4))
      if (signature[0] !== 0x50 || signature[1] !== 0x4b) {
        fail('文件不是有效的 PPTX 文档。')
        return
      }
      sent = true
      frameRef.current?.contentWindow?.postMessage({ type: 'pptx:load', channel, buffer }, '*', [buffer])
      buffer = undefined
    }
    void http.get<ArrayBuffer>(`/files/${fileId}/content`, {
      responseType: 'arraybuffer',
      signal: controller.signal,
      quietNetworkError: true,
    } as QuietRequestConfig).then(response => {
      if (active) { buffer = response.data; sendBuffer.current() }
    }).catch(() => {
      if (active) fail('PPTX 文件读取失败，请检查网络后重试。')
    })
    return () => {
      active = false
      clearTimeout(timer)
      controller.abort()
      window.removeEventListener('message', receive)
      sendBuffer.current = () => {}
    }
  }, [fileId, channel])

  const command = (name: 'page' | 'zoom', value: number | string) => {
    if (state !== 'ready') return
    frameRef.current?.contentWindow?.postMessage({ type: 'pptx:command', command: name, channel, value }, '*')
  }
  const changePage = (value: number | undefined) => {
    if (value === undefined || !Number.isFinite(value) || pageCount < 1) return
    command('page', Math.min(pageCount, Math.max(1, Math.round(value))))
  }
  const changeZoom = (value: string) => command('zoom', value)
  const stepZoom = (direction: number) => {
    command('zoom', Math.min(4, Math.max(0.1, Math.round(scale * (direction > 0 ? 1.25 : 0.8) * 100) / 100)))
  }
  const toolbar = state === 'ready' && <div className="pptx-preview-toolbar" role="group" aria-label="PPTX 阅读控制">
    <Button size="small" aria-label="上一张幻灯片" disabled={pageNumber <= 1} onClick={() => changePage(pageNumber - 1)}>上一张</Button>
    <InputNumber size="small" aria-label="PPTX 幻灯片编号" min={1} max={pageCount} precision={0} value={pageNumber} onChange={changePage} style={{ width: 76 }} />
    <span role="status">/ {pageCount} 张</span>
    <Button size="small" aria-label="下一张幻灯片" disabled={pageNumber >= pageCount} onClick={() => changePage(pageNumber + 1)}>下一张</Button>
    <Button size="small" aria-label="缩小 PPTX" disabled={scale <= 0.1} onClick={() => stepZoom(-1)}>缩小</Button>
    <Select size="small" aria-label="PPTX 缩放" value={zoom} onChange={changeZoom} style={{ width: 120 }}
      getPopupContainer={trigger => trigger.closest<HTMLElement>('.file-preview-modal') ?? trigger.parentElement ?? trigger}
      options={[{ value: 'page', label: '适合整页' }, { value: 'fit', label: '适合宽度' },
        ...[...new Set([0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4, ...(Number.isFinite(Number(zoom)) ? [Number(zoom)] : [])])]
          .sort((a, b) => a - b).map(value => ({ value: String(value), label: `${Math.round(value * 100)}%` }))]} />
    <Button size="small" aria-label="放大 PPTX" disabled={scale >= 4} onClick={() => stepZoom(1)}>放大</Button>
    <Button size="small" aria-label="重置 PPTX 缩放" onClick={() => changeZoom('page')}>重置</Button>
  </div>

  return <section className="pptx-preview" aria-label="PPTX 预览">
    {toolbarContainer ? createPortal(toolbar, toolbarContainer) : toolbar}
    <div className="pptx-preview-viewport">
      <iframe ref={frameRef} className="pptx-preview-frame" title="PPTX 预览内容"
        sandbox="allow-scripts" referrerPolicy="no-referrer" srcDoc={html}
        onLoad={() => { frameLoaded.current = true; sendBuffer.current() }}
        style={{ visibility: state === 'ready' ? 'visible' : 'hidden' }} />
      {state === 'loading' && <div className="pptx-preview-status" role="status"><Spin />正在渲染幻灯片…</div>}
      {state === 'error' && <div className="pptx-preview-status" role="alert"><Result status="error" title="PPTX 预览失败" subTitle={message}
        extra={<Button aria-label="重试 PPTX 预览" onClick={onRetry}>重试</Button>} /></div>}
    </div>
  </section>
}

export default function PptxPreview({ fileId, toolbarContainer }: {
  fileId: number
  toolbarContainer?: HTMLElement | null
}) {
  const [attempt, setAttempt] = useState(0)
  return <PptxDocument key={`${fileId}:${attempt}`} fileId={fileId} toolbarContainer={toolbarContainer} onRetry={() => setAttempt(value => value + 1)} />
}
