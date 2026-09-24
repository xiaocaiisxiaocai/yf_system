import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { Button, Result, Spin } from '@arco-design/web-react'
import http, { type QuietRequestConfig } from '../api/client'
import './ImagePreview.css'
import { wheelZoomFactor } from '../../vendor/preview-wheel.js'

function ImageDocument({ fileId, contentUrl, sourceUrl, name, toolbarContainer }: {
  fileId?: number; contentUrl?: string; sourceUrl?: string; name: string; toolbarContainer?: HTMLElement | null
}) {
  const viewport = useRef<HTMLDivElement>(null)
  const imageRef = useRef<HTMLImageElement>(null)
  const zoomAnchor = useRef<{ x: number; y: number; fractionX: number; fractionY: number } | null>(null)
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)
  const drag = useRef<{ x: number; y: number; left: number; top: number } | null>(null)
  const [source, setSource] = useState(() => sourceUrl ?? '')
  const [status, setStatus] = useState<'loading' | 'ready' | 'error'>('loading')
  const [attempt, setAttempt] = useState(0)
  const [natural, setNatural] = useState({ width: 0, height: 0 })
  const [size, setSize] = useState({ width: 0, height: 0 })
  const [zoom, setZoom] = useState<number | 'fit'>('fit')
  const fit = natural.width && natural.height && size.width && size.height
    ? Math.min(1, Math.max(1, size.width - 32) / natural.width, Math.max(1, size.height - 32) / natural.height) : 1
  const scale = zoom === 'fit' ? fit : zoom

  useEffect(() => {
    const element = viewport.current
    if (!element || status !== 'ready') return
    const wheel = (event: WheelEvent) => {
      const factor = wheelZoomFactor(event)
      if (factor === 1) return
      event.preventDefault(); event.stopPropagation()
      const rect = imageRef.current?.getBoundingClientRect()
      if (!rect?.width || !rect.height) return
      zoomAnchor.current = { x: event.clientX, y: event.clientY,
        fractionX: (event.clientX - rect.left) / rect.width, fractionY: (event.clientY - rect.top) / rect.height }
      setZoom(current => Math.max(0.01, Math.min(8, (current === 'fit' ? fit : current) * factor)))
    }
    element.addEventListener('wheel', wheel, { passive: false })
    return () => element.removeEventListener('wheel', wheel)
  }, [status, fit])

  useLayoutEffect(() => {
    const anchor = zoomAnchor.current
    const element = viewport.current
    const rect = imageRef.current?.getBoundingClientRect()
    zoomAnchor.current = null
    if (anchor && element && rect) {
      element.scrollLeft += rect.left + anchor.fractionX * rect.width - anchor.x
      element.scrollTop += rect.top + anchor.fractionY * rect.height - anchor.y
    }
  }, [scale])

  useEffect(() => {
    const element = viewport.current
    if (!element) return
    const measure = () => setSize({ width: element.clientWidth, height: element.clientHeight })
    const observer = new ResizeObserver(measure)
    observer.observe(element); measure()
    return () => observer.disconnect()
  }, [])

  useEffect(() => {
    let active = true
    let objectUrl = ''
    const controller = sourceUrl ? undefined : new AbortController()
    timer.current = setTimeout(() => {
      active = false; controller?.abort(); setSource(''); setStatus('error')
    }, 60000)
    if (!sourceUrl) {
      const requestUrl = contentUrl ?? `/files/${fileId}/content`
      void http.get<Blob>(requestUrl, {
        responseType: 'blob', signal: controller?.signal, quietNetworkError: true,
      } as QuietRequestConfig).then(response => {
        if (!active) return
        objectUrl = URL.createObjectURL(response.data)
        setSource(objectUrl)
      }).catch(() => { if (active) { clearTimeout(timer.current); setStatus('error') } })
    }
    return () => {
      active = false; controller?.abort(); clearTimeout(timer.current)
      if (objectUrl) URL.revokeObjectURL(objectUrl)
    }
  }, [fileId, contentUrl, sourceUrl, attempt])

  const changeZoom = (value: number | 'fit') => {
    zoomAnchor.current = null
    setZoom(value)
    // Center the newly sized image while leaving normal wheel/drag scrolling local.
    requestAnimationFrame(() => {
      const element = viewport.current
      if (element) {
        element.scrollLeft = (element.scrollWidth - element.clientWidth) / 2
        element.scrollTop = (element.scrollHeight - element.clientHeight) / 2
      }
    })
  }
  const toolbar = status === 'ready' && <div className="image-preview-toolbar" role="group" aria-label="图片预览控制">
    <Button size="small" aria-label="缩小图片" disabled={scale <= 0.01} onClick={() => changeZoom(Math.max(0.01, scale / 1.25))}>缩小</Button>
    <span aria-live="polite">{Math.round(scale * 100)}%</span>
    <Button size="small" aria-label="放大图片" disabled={scale >= 8} onClick={() => changeZoom(Math.min(8, scale * 1.25))}>放大</Button>
    <Button size="small" onClick={() => changeZoom(1)}>原始大小</Button>
    <Button size="small" aria-label="重置图片缩放" onClick={() => changeZoom('fit')}>重置</Button>
  </div>

  return <section className="image-preview">
    {toolbarContainer ? createPortal(toolbar, toolbarContainer) : toolbar}
    <div ref={viewport} className="image-preview-viewport" tabIndex={0} role="region" aria-label="图片预览内容"
      style={{ overflow: zoom === 'fit' ? 'hidden' : 'auto' }}
      onPointerDown={event => {
        if (event.pointerType !== 'mouse' || event.button !== 0 || status !== 'ready') return
        const element = event.currentTarget
        drag.current = { x: event.clientX, y: event.clientY, left: element.scrollLeft, top: element.scrollTop }
        element.setPointerCapture(event.pointerId); event.preventDefault()
      }} onPointerMove={event => {
        if (!drag.current) return
        event.currentTarget.scrollLeft = drag.current.left + drag.current.x - event.clientX
        event.currentTarget.scrollTop = drag.current.top + drag.current.y - event.clientY
      }} onPointerUp={() => { drag.current = null }} onPointerCancel={() => { drag.current = null }}
      onLostPointerCapture={() => { drag.current = null }}>
      {source && <div className="image-preview-stage" style={{
        width: Math.max(size.width, natural.width * scale + 32), height: Math.max(size.height, natural.height * scale + 32),
      }}>
        <img ref={imageRef} src={source} alt={name} draggable={false} onContextMenu={event => event.preventDefault()}
          style={{ width: natural.width ? natural.width * scale : undefined, height: natural.height ? natural.height * scale : undefined, visibility: status === 'ready' ? 'visible' : 'hidden' }}
          onLoad={event => {
            const image = event.currentTarget
            clearTimeout(timer.current)
            if (!image.naturalWidth || !image.naturalHeight) { setStatus('error'); return }
            setNatural({ width: image.naturalWidth, height: image.naturalHeight }); setStatus('ready')
          }} onError={() => { clearTimeout(timer.current); setStatus('error') }} />
      </div>}
    </div>
    {status === 'loading' && <div className="image-preview-status" role="status"><Spin />正在加载图片…</div>}
    {status === 'error' && <div className="image-preview-status" role="alert"><Result status="error" title="图片预览失败"
      subTitle="图片可能损坏或读取失败，请重试。" extra={<Button onClick={() => {
        setSource(sourceUrl ?? ''); setNatural({ width: 0, height: 0 }); setZoom('fit'); setStatus('loading'); setAttempt(value => value + 1)
      }}>重试图片预览</Button>} /></div>}
  </section>
}

type ImagePreviewProps = {
  name: string
  toolbarContainer?: HTMLElement | null
} & (
  | { fileId: number; contentUrl?: never }
  | { fileId?: never; contentUrl: string }
  | { fileId?: never; contentUrl?: never; sourceUrl: string }
)

export default function ImagePreview(props: ImagePreviewProps) {
  const sourceUrl = 'sourceUrl' in props ? props.sourceUrl : undefined
  return <ImageDocument key={sourceUrl ?? props.contentUrl ?? props.fileId} {...props} />
}
