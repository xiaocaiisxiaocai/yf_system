import { useEffect, useRef, useState } from 'react'
import { Button, Result, Spin } from '@arco-design/web-react'
import http, { type QuietRequestConfig } from '../api/client'

type MediaSession = { url: string; expiresInSeconds: number }

// 续期失败时在当前授权到期前按退避重试；只有授权真的到期（留出余量）才判定失败，
// 期间不打断正在播放的视频。
const RENEW_RETRY_BASE_MS = 5_000
const RENEW_RETRY_MAX_MS = 30_000
const EXPIRY_MARGIN_MS = 2_000

class InvalidMediaSessionError extends Error {}

function VideoDocument({ fileId }: { fileId: number }) {
  const player = useRef<HTMLVideoElement>(null)
  const [attempt, setAttempt] = useState(0)
  const [source, setSource] = useState('')
  const [state, setState] = useState<'loading' | 'ready' | 'error'>('loading')
  const loadTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)

  useEffect(() => {
    let active = true
    let renewal: ReturnType<typeof setTimeout> | undefined
    const controller = new AbortController()
    const video = player.current
    let expiresAt = 0
    let retryDelay = RENEW_RETRY_BASE_MS
    const fail = () => {
      if (!active) return
      clearTimeout(loadTimer.current)
      clearTimeout(renewal)
      video?.pause()
      setSource('')
      setState('error')
    }
    const authorize = async () => {
      try {
        const { data } = await http.post<MediaSession>(`/files/${fileId}/media-session`, null,
          { signal: controller.signal, quietNetworkError: true } as QuietRequestConfig)
        if (!active) return
        // Only the same-origin, file-scoped endpoint may receive the media cookie.
        const url = new URL(data.url, window.location.origin)
        if (url.origin !== window.location.origin || url.pathname !== `/api/v1/files/${fileId}/media`
          || url.search || url.hash || !Number.isFinite(data.expiresInSeconds) || data.expiresInSeconds < 10) {
          throw new InvalidMediaSessionError('Invalid media session')
        }
        expiresAt = Date.now() + data.expiresInSeconds * 1000
        retryDelay = RENEW_RETRY_BASE_MS
        setSource(url.pathname)
        renewal = setTimeout(() => { void authorize() }, Math.min(data.expiresInSeconds * 500, 240000))
      } catch (error) {
        if (!active) return
        // 首次授权失败或服务端返回了不可信的地址：立即失败。
        if (!expiresAt || error instanceof InvalidMediaSessionError) {
          fail()
          return
        }
        const remaining = expiresAt - Date.now() - EXPIRY_MARGIN_MS
        if (remaining <= 0) {
          fail()
          return
        }
        const delay = Math.min(retryDelay, remaining)
        retryDelay = Math.min(retryDelay * 2, RENEW_RETRY_MAX_MS)
        renewal = setTimeout(() => { void authorize() }, delay)
      }
    }
    loadTimer.current = setTimeout(fail, 60000)
    void authorize()
    return () => {
      active = false
      controller.abort()
      clearTimeout(renewal)
      clearTimeout(loadTimer.current)
      video?.pause()
      video?.removeAttribute('src')
      video?.load()
    }
  }, [fileId, attempt])

  return <div className="video-preview">
    <video ref={player} key={attempt} src={source || undefined} controls playsInline preload="metadata"
      controlsList="nodownload noremoteplayback" disablePictureInPicture
      aria-label="视频预览" onContextMenu={event => event.preventDefault()}
      onLoadedMetadata={() => { clearTimeout(loadTimer.current); setState('ready') }}
      onError={() => { if (source) { clearTimeout(loadTimer.current); setState('error') } }}
      style={{ visibility: state === 'error' ? 'hidden' : 'visible' }} />
    {state === 'loading' && <div className="video-preview-status" role="status"><Spin />正在加载视频…</div>}
    {state === 'error' && <div className="video-preview-status" role="alert"><Result status="error" title="视频无法播放"
      subTitle="请重试；若仍无法播放，文件可能损坏或视频编码不受当前浏览器支持。"
      extra={<Button onClick={() => { setSource(''); setState('loading'); setAttempt(value => value + 1) }}>重试播放</Button>} /></div>}
  </div>
}

export default function VideoPreview({ fileId }: { fileId: number }) {
  return <VideoDocument key={fileId} fileId={fileId} />
}
