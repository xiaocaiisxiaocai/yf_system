import { useEffect, useRef, useState, type ClipboardEvent } from 'react'
import { Button, Message, Modal, Result, Spin, Tooltip } from '@arco-design/web-react'
import { IconClose, IconDelete, IconImage } from '@arco-design/web-react/icon'
import http, { type QuietRequestConfig } from '../api/client'
import ImagePreview from './ImagePreview'
import './MessageImages.css'

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

interface MessageImageComposerProps {
  files: File[]
  onChange: (files: File[]) => void
  disabled?: boolean
}

interface MessageImagesProps {
  messageId: number
  images: MessageImageItem[]
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

function LocalImageThumbnail({ file, onRemove, disabled }: {
  file: File
  onRemove: () => void
  disabled?: boolean
}) {
  const [source, setSource] = useState('')

  useEffect(() => {
    const objectUrl = URL.createObjectURL(file)
    setSource(objectUrl)
    return () => URL.revokeObjectURL(objectUrl)
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
        key={`${file.name}-${file.size}-${file.lastModified}-${index}`}
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

function RemoteImageThumbnail({ messageId, image, onOpen }: {
  messageId: number
  image: MessageImageItem
  onOpen: () => void
}) {
  const rootRef = useRef<HTMLDivElement>(null)
  const [visible, setVisible] = useState(() => typeof IntersectionObserver === 'undefined')
  const [source, setSource] = useState('')
  const [status, setStatus] = useState<'idle' | 'loading' | 'ready' | 'error'>('idle')
  const [attempt, setAttempt] = useState(0)

  useEffect(() => {
    if (visible) return
    const element = rootRef.current
    if (!element || typeof IntersectionObserver === 'undefined') {
      setVisible(true)
      return
    }
    const observer = new IntersectionObserver((entries) => {
      if (entries.some((entry) => entry.isIntersecting)) {
        setVisible(true)
        observer.disconnect()
      }
    }, { rootMargin: '160px' })
    observer.observe(element)
    return () => observer.disconnect()
  }, [visible])

  useEffect(() => {
    if (!visible) return
    let active = true
    let objectUrl = ''
    const controller = new AbortController()
    setSource('')
    setStatus('loading')
    void http.get<Blob>(`/messages/${messageId}/images/${image.id}`, {
      responseType: 'blob',
      signal: controller.signal,
      quietNetworkError: true,
    } as QuietRequestConfig).then((response) => {
      if (!active) return
      objectUrl = URL.createObjectURL(response.data)
      setSource(objectUrl)
    }).catch(() => {
      if (active) setStatus('error')
    })
    return () => {
      active = false
      controller.abort()
      if (objectUrl) URL.revokeObjectURL(objectUrl)
    }
  }, [visible, messageId, image.id, attempt])

  return <div ref={rootRef} className="message-image-card">
    {status === 'error' ? <Result
      status="error"
      title="加载失败"
      extra={<Button size="mini" onClick={() => setAttempt((value) => value + 1)}>重试</Button>}
    /> : <button
      type="button"
      className="message-image-open"
      aria-label={`预览图片：${image.name}`}
      disabled={status !== 'ready'}
      onClick={onOpen}
    >
      {source && <img
        src={source}
        alt={image.name}
        onContextMenu={(event) => event.preventDefault()}
        onLoad={() => setStatus('ready')}
        onError={() => setStatus('error')}
      />}
      {(status === 'idle' || status === 'loading') && <span className="message-image-loading" role="status"><Spin /></span>}
    </button>}
    <div className="message-image-meta">
      <span title={image.name}>{image.name}</span>
      <small>{formatBytes(image.sizeBytes)}</small>
    </div>
  </div>
}

export function MessageImages({ messageId, images }: MessageImagesProps) {
  const [preview, setPreview] = useState<MessageImageItem | null>(null)
  const [previewToolbar, setPreviewToolbar] = useState<HTMLDivElement | null>(null)

  if (!images.length) return null

  return <>
    <div className="message-images" aria-label={`留言图片，共 ${images.length} 张`}>
      {images.map((image) => <RemoteImageThumbnail
        key={image.id}
        messageId={messageId}
        image={image}
        onOpen={() => setPreview(image)}
      />)}
    </div>
    <Modal
      className="file-preview-modal message-image-preview-modal"
      alignCenter
      closable={false}
      title={preview ? <div className="file-preview-heading">
        <span className="file-preview-name" title={preview.name}>预览：{preview.name}</span>
        <div className="file-preview-controls" ref={setPreviewToolbar} />
        <Button
          className="file-preview-close"
          type="text"
          aria-label="关闭图片预览"
          icon={<IconClose />}
          onClick={() => setPreview(null)}
        />
      </div> : ''}
      visible={!!preview}
      onCancel={() => setPreview(null)}
      footer={null}
      style={{ display: 'inline-flex', width: 'calc(100vw - 24px)', maxWidth: 'none', height: 'calc(100dvh - 24px)' }}
      unmountOnExit
    >
      {preview && <ImagePreview
        contentUrl={`/messages/${messageId}/images/${preview.id}`}
        name={preview.name}
        toolbarContainer={previewToolbar}
      />}
    </Modal>
  </>
}
