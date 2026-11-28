import { useEffect, useMemo, useRef, useState } from 'react'
import type { AxiosInstance } from 'axios'
import { loadAnnouncementAsset, type AnnouncementAccess } from './announcementAssetContent'

export default function AnnouncementAssetImage({ announcementId, assetId, access, client, alt, thumbnail = false, onOpen }: {
  announcementId: number
  assetId: number
  access: AnnouncementAccess
  client?: AxiosInstance
  alt: string
  thumbnail?: boolean
  onOpen?: () => void
}) {
  const element = useRef<HTMLSpanElement>(null)
  const [visible, setVisible] = useState(() => typeof IntersectionObserver === 'undefined')
  const [attempt, setAttempt] = useState(0)
  const request = useMemo(() => ({ announcementId, assetId, client, access, thumbnail, attempt }),
    [announcementId, assetId, client, access, thumbnail, attempt])
  const [result, setResult] = useState<{ request: typeof request; url: string } | null>(null)
  useEffect(() => {
    const host = element.current
    if (!host || visible || typeof IntersectionObserver === 'undefined') return
    const observer = new IntersectionObserver(entries => {
      if (entries.some(entry => entry.isIntersecting)) setVisible(true)
    }, { rootMargin: '200px' })
    observer.observe(host)
    return () => observer.disconnect()
  }, [visible])
  useEffect(() => {
    if (!visible) return
    let url = ''
    const controller = new AbortController()
    void loadAnnouncementAsset(request.announcementId, request.assetId, controller.signal, request).then(blob => {
      if (controller.signal.aborted) return
      url = URL.createObjectURL(blob)
      setResult({ request, url })
    }).catch(() => {
      if (!controller.signal.aborted) setResult({ request, url: '' })
    })
    return () => {
      controller.abort()
      if (url) URL.revokeObjectURL(url)
    }
  }, [request, visible])
  const source = result?.request === request ? result.url : undefined
  const image = source && <img src={source} alt={alt} onError={() => setResult({ request, url: '' })} />
  return <span className={thumbnail ? 'announcement-thumbnail' : 'announcement-inline-image'} ref={element}>
    {source === '' ? <button type="button" onClick={(event) => {
      event.stopPropagation()
      setAttempt(value => value + 1)
    }}>重试图片</button>
      : image ? (onOpen ? <button type="button" className="announcement-zoom-image" onClick={onOpen}
        aria-label={`放大图片：${alt}`}>{image}</button> : image)
      : <span role="status" aria-label="正在加载图片">图片加载中</span>}
  </span>
}
