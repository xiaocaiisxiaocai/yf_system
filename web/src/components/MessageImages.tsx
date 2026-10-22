import { lazy, Suspense, useEffect, useRef, useState, type ClipboardEvent } from 'react'
import { Button, Message, Modal, Result, Spin, Tooltip } from '@arco-design/web-react'
import { IconClose, IconDelete, IconImage } from '@arco-design/web-react/icon'
import http, { type QuietRequestConfig } from '../api/client'
import './MessageImages.css'
import { PreviewWatermark } from './PreviewWatermark'

// 与 FileTable 的图片预览共用同一按需加载分包，避免此处的静态引用使其失效
const ImagePreview = lazy(() => import('./ImagePreview'))

export const MESSAGE_IMAGE_ACCEPT = 'image/png,image/jpeg,image/gif,image/webp,image/bmp,.bmp'
export const MESSAGE_IMAGE_MAX_COUNT = 9
export const MESSAGE_IMAGE_MAX_BYTES = 50 * 1024 * 1024

const ACCEPTED_IMAGE_TYPES = new Set(['image/png', 'image/jpeg', 'image/gif', 'image/webp', 'image/bmp'])
const ACCEPTED_IMAGE_EXTENSIONS = new Set(['png', 'jpg', 'jpeg', 'gif', 'webp', 'bmp'])

export interface MessageImageItem {
  id: number
  name: string
  sizeBytes: number
  mimeType: string
}

function isAcceptedImage(file: File) {
  const type = file.type.toLowerCase()
  if (type) return ACCEPTED_IMAGE_TYPES.has(type)
  const extension = file.name.split('.').pop()?.toLowerCase()
  return !!extension && ACCEPTED_IMAGE_EXTENSIONS.has(extension)
}

function appendFiles(files: File[], candidates: File[]) {
  const next = [...files]
  let totalBytes = files.reduce((sum, file) => sum + file.size, 0)
  let unsupported = 0
  let countExceeded = false
  let totalExceeded = false

  for (const file of candidates) {
    if (!isAcceptedImage(file)) {
      unsupported += 1
    } else if (next.length >= MESSAGE_IMAGE_MAX_COUNT) {
      countExceeded = true
    } else if (totalBytes + file.size > MESSAGE_IMAGE_MAX_BYTES) {
      totalExceeded = true
    } else {
      next.push(file)
      totalBytes += file.size
    }
  }

  if (unsupported) Message.error('仅支持 PNG、JPEG、GIF、WebP 和 BMP 图片')
  if (countExceeded) Message.error(`每条留言最多添加 ${MESSAGE_IMAGE_MAX_COUNT} 张图片`)
  if (totalExceeded) Message.error('本次图片总量最多 50 MiB')
  return next
}

/** Attach this to the composer container. Text-only paste is deliberately left untouched. */
// eslint-disable-next-line react/only-export-components
export function pasteMessageImages(
  event: ClipboardEvent<HTMLElement>,
  files: File[],
  onChange: (files: File[]) => void,
) {
  const itemFiles = Array.from(event.clipboardData.items)
    .filter((item) => item.kind === 'file' && item.type.toLowerCase().startsWith('image/'))
    .map((item) => item.getAsFile())
    .filter((file): file is File => file !== null)
  const candidates = itemFiles.length
    ? itemFiles
    : Array.from(event.clipboardData.files).filter((file) => file.type.toLowerCase().startsWith('image/'))
  if (!candidates.length) return
  event.preventDefault()
  const next = appendFiles(files, candidates)
  if (next.length !== files.length) onChange(next)
}

interface MessageImageComposerProps {
  files: File[]
  onChange: (files: File[]) => void
  disabled?: boolean
}

interface MessageImagesProps {
  messageId: number
  images: MessageImageItem[]
  watermarkEmployeeNo?: string
  watermarkRealName?: string
}

const draftFileKeys = new WeakMap<File, string>()
let nextDraftFileKey = 0

function draftFileKey(file: File) {
  const existing = draftFileKeys.get(file)
  if (existing) return existing
  const key = `message-draft-${++nextDraftFileKey}`
  draftFileKeys.set(file, key)
  return key
}

function LocalImageThumbnail({ file, onRemove, disabled }: {
  file: File
  onRemove: () => void
  disabled?: boolean
}) {
  const [source, setSource] = useState('')

  // 在 effect 里创建并在清理时释放：StrictMode 的模拟卸载/重挂会重新创建，不会留下已释放的地址。
  useEffect(() => {
    const url = URL.createObjectURL(file)
    // The object URL is an external browser resource owned by this effect.
    // eslint-disable-next-line react/set-state-in-effect
    setSource(url)
    return () => URL.revokeObjectURL(url)
  }, [file])

  return <div className="message-image-draft" title={file.name}>
    {source && <img src={source} alt={file.name} />}
    <Button
      className="message-image-remove"
      type="primary"
      status="danger"
      size="mini"
      shape="circle"
      icon={<IconDelete />}
      aria-label={`移除图片：${file.name}`}
      disabled={disabled}
      onClick={onRemove}
    />
  </div>
}

export function MessageImageComposer({ files, onChange, disabled = false }: MessageImageComposerProps) {
  const inputRef = useRef<HTMLInputElement>(null)

  return <div className="message-image-composer" aria-label="留言图片">
    <input
      ref={inputRef}
      className="message-image-input"
      type="file"
      accept={MESSAGE_IMAGE_ACCEPT}
      multiple
      disabled={disabled}
      onChange={(event) => {
        const candidates = Array.from(event.currentTarget.files ?? [])
        event.currentTarget.value = ''
        if (!candidates.length) return
        const next = appendFiles(files, candidates)
        if (next.length !== files.length) onChange(next)
      }}
    />
    <Tooltip content="支持 Ctrl+V 粘贴，最多 9 张，合计 50 MiB">
      <Button
        className="message-image-add"
        size="small"
        icon={<IconImage />}
        disabled={disabled || files.length >= MESSAGE_IMAGE_MAX_COUNT}
        onClick={() => inputRef.current?.click()}
      >
        添加图片{files.length ? ` ${files.length}/${MESSAGE_IMAGE_MAX_COUNT}` : ''}
      </Button>
    </Tooltip>
    {files.length > 0 && <div className="message-image-drafts" aria-label={`已添加 ${files.length} 张图片`}>
      {files.map((file, index) => <LocalImageThumbnail
        key={draftFileKey(file)}
        file={file}
        disabled={disabled}
        onRemove={() => onChange(files.filter((_, current) => current !== index))}
      />)}
    </div>}
  </div>
}

function formatBytes(bytes: number) {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KiB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MiB`
}

// 缩图请求的是原图：同时只下载少量几张，离开视口后中止并释放，避免长留言历史占满内存。
const MAX_THUMBNAIL_LOADS = 3
let activeThumbnailLoads = 0
const waitingThumbnailLoads: Array<() => void> = []

function acquireThumbnailSlot(signal: AbortSignal): Promise<() => void> {
  return new Promise((resolve, reject) => {
    const grant = () => {
      activeThumbnailLoads += 1
      let released = false
      resolve(() => {
        if (released) return
        released = true
        activeThumbnailLoads -= 1
        waitingThumbnailLoads.shift()?.()
      })
    }
    if (activeThumbnailLoads < MAX_THUMBNAIL_LOADS) {
      grant()
      return
    }
    const onAbort = () => {
      const index = waitingThumbnailLoads.indexOf(entry)
      if (index >= 0) waitingThumbnailLoads.splice(index, 1)
      reject(new DOMException('Aborted', 'AbortError'))
    }
    const entry = () => {
      signal.removeEventListener('abort', onAbort)
      grant()
    }
    waitingThumbnailLoads.push(entry)
    signal.addEventListener('abort', onAbort, { once: true })
  })
}

function RemoteImageThumbnail({ messageId, image, pinned, onOpen }: {
  messageId: number
  image: MessageImageItem
  /** 预览打开时保留该图的地址，即使缩图滚出视口也不释放。 */
  pinned: boolean
  onOpen: (source: string) => void
}) {
  const rootRef = useRef<HTMLDivElement>(null)
  const [visible, setVisible] = useState(() => typeof IntersectionObserver === 'undefined')

  useEffect(() => {
    const element = rootRef.current
    if (!element || typeof IntersectionObserver === 'undefined') return
    const observer = new IntersectionObserver((entries) => {
      const entry = entries[entries.length - 1]
      if (entry) setVisible(entry.isIntersecting)
    }, { rootMargin: '160px' })
    observer.observe(element)
    return () => observer.disconnect()
  }, [])

  return <div ref={rootRef} className="message-image-card">
    {visible || pinned
      ? <LoadedImageThumbnail messageId={messageId} image={image} onOpen={onOpen} />
      : <button type="button" className="message-image-open" aria-label={`预览图片：${image.name}`} disabled>
        <span className="message-image-loading" role="status"><Spin /></span>
      </button>}
    <div className="message-image-meta">
      <span title={image.name}>{image.name}</span>
      <small>{formatBytes(image.sizeBytes)}</small>
    </div>
  </div>
}

/** 挂载时下载原图，卸载时中止请求并释放地址。 */
function LoadedImageThumbnail({ messageId, image, onOpen }: {
  messageId: number
  image: MessageImageItem
  onOpen: (source: string) => void
}) {
  const [source, setSource] = useState('')
  const [sourceAttempt, setSourceAttempt] = useState<number | null>(null)
  const [loadedAttempt, setLoadedAttempt] = useState<number | null>(null)
  const [failedAttempt, setFailedAttempt] = useState<number | null>(null)
  const [attempt, setAttempt] = useState(0)

  useEffect(() => {
    let active = true
    let objectUrl = ''
    let release: (() => void) | undefined
    const controller = new AbortController()
    void acquireThumbnailSlot(controller.signal).then((releaseSlot) => {
      release = releaseSlot
      if (!active) { releaseSlot(); return undefined }
      return http.get<Blob>(`/messages/${messageId}/images/${image.id}`, {
        responseType: 'blob',
        signal: controller.signal,
        quietNetworkError: true,
      } as QuietRequestConfig).then((response) => {
        if (!active) return
        objectUrl = URL.createObjectURL(response.data)
        setSource(objectUrl)
        setSourceAttempt(attempt)
      })
    }).catch(() => {
      if (active) setFailedAttempt(attempt)
    }).finally(() => release?.())
    return () => {
      active = false
      controller.abort()
      release?.()
      if (objectUrl) URL.revokeObjectURL(objectUrl)
    }
  }, [messageId, image.id, attempt])

  const ready = sourceAttempt === attempt && loadedAttempt === attempt && !!source
  const failed = failedAttempt === attempt

  return failed ? <Result
    status="error"
    title="加载失败"
    extra={<Button size="mini" onClick={() => setAttempt((value) => value + 1)}>重试</Button>}
  /> : <button
    type="button"
    className="message-image-open"
    aria-label={`预览图片：${image.name}`}
    disabled={!ready}
    onClick={() => onOpen(source)}
  >
    {source && sourceAttempt === attempt && <img
      src={source}
      alt={image.name}
      onContextMenu={(event) => event.preventDefault()}
      onLoad={() => setLoadedAttempt(attempt)}
      onError={() => setFailedAttempt(attempt)}
    />}
    {!ready && <span className="message-image-loading" role="status"><Spin /></span>}
  </button>
}

export function MessageImages({ messageId, images, watermarkEmployeeNo, watermarkRealName }: MessageImagesProps) {
  const [preview, setPreview] = useState<{ image: MessageImageItem; source: string } | null>(null)
  const [previewToolbar, setPreviewToolbar] = useState<HTMLDivElement | null>(null)

  if (!images.length) return null

  return <>
    <div className="message-images" aria-label={`留言图片，共 ${images.length} 张`}>
      {images.map((image) => <RemoteImageThumbnail
        key={image.id}
        messageId={messageId}
        image={image}
        pinned={preview?.image.id === image.id}
        onOpen={(source) => setPreview({ image, source })}
      />)}
    </div>
    {preview && <Modal
      className="file-preview-modal message-image-preview-modal"
      alignCenter
      closable={false}
      title={<div className="file-preview-heading">
        <span className="file-preview-name" title={preview.image.name}>预览：{preview.image.name}</span>
        <div className="file-preview-controls" ref={setPreviewToolbar} />
        <Button
          className="file-preview-close"
          type="text"
          aria-label="关闭图片预览"
          icon={<IconClose />}
          onClick={() => setPreview(null)}
        />
      </div>}
      visible
      onCancel={() => setPreview(null)}
      footer={null}
      style={{ display: 'inline-flex', width: 'calc(100vw - 24px)', maxWidth: 'none', height: 'calc(100dvh - 24px)' }}
      unmountOnExit
    >
      <div className="file-preview-surface">
        <Suspense fallback={<div role="status">加载图片预览…</div>}>
          <ImagePreview
            sourceUrl={preview.source}
            name={preview.image.name}
            toolbarContainer={previewToolbar}
          />
        </Suspense>
        <PreviewWatermark employeeNo={watermarkEmployeeNo} realName={watermarkRealName} />
      </div>
    </Modal>}
  </>
}
