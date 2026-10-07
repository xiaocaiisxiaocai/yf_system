import { forwardRef, useEffect, useImperativeHandle, useRef, useState } from 'react'
import { Button, Message, Progress, Space, Typography } from '@arco-design/web-react'
import { IconClose, IconRefresh, IconUpload } from '@arco-design/web-react/icon'
import { createChunkHasher, fileMd5, type ChunkHasher } from '../../api/file-hash'
import { fmtSize } from '../../api/types'
import { isQuietedError } from '../api/quietErrors'
import { useOem } from '../OemContext'

type Phase = 'queued' | 'hashing' | 'uploading' | 'merging' | 'cancelling' | 'done' | 'failed'

interface Entry {
  key: number
  file: File
  phase: Phase
  percent: number
  error?: string
  md5?: string
  session?: {
    id: string
    chunkSize: number
    totalChunks: number
    uploadedChunks: number[]
    mergeReady: boolean
  }
}

export interface OemUploaderHandle {
  /** Uploads queued and failed files only. Successfully merged files are never repeated. */
  uploadAll(transferId?: number): Promise<boolean>
  /** Forgets server sessions after the draft was deleted; every local file is queued again. */
  resetForNewDraft(): void
  hasFiles(): boolean
  isBusy(): boolean
}

interface Props {
  /** Detail pages provide an existing draft id. Creation dialogs provide it to uploadAll after creating the draft. */
  transferId?: number
  deferred?: boolean
  disabled?: boolean
  onUploaded?: () => void
  onBusyChange?: (busy: boolean) => void
  onQueueChange?: (fileCount: number) => void
}

let nextKey = 0

const CHUNK_RETRIES = 2
const CHUNK_RETRY_BASE_MS = 500
const TRANSIENT_CODES = new Set(['ERR_NETWORK', 'ECONNABORTED', 'ETIMEDOUT', 'ECONNRESET'])

function statusOf(error: unknown): number | undefined {
  return (error as { response?: { status?: number } })?.response?.status
}

/** Network drops, timeouts and gateway/server hiccups are worth an automatic chunk retry. */
function isTransient(error: unknown): boolean {
  const status = statusOf(error)
  if (status !== undefined) return status === 408 || status === 429 || status >= 500
  return TRANSIENT_CODES.has((error as { code?: string })?.code ?? '')
}

/**
 * The server rejected the chunk because its bytes did not match the declared `X-Chunk-SHA256`
 * (corrupted in transit or the local file changed while being read). Worth one re-read and resend.
 */
function isDigestMismatch(error: unknown): boolean {
  const response = (error as { response?: { status?: number; data?: { message?: string } } })?.response
  return response?.status === 400 && /SHA-?256.*(校验失败|不一致|不匹配|mismatch)/i.test(response.data?.message ?? '')
}

/** The server no longer knows the upload session (expired and purged, or its draft is gone). */
function isSessionGone(error: unknown): boolean {
  const status = statusOf(error)
  return status === 404 || status === 410
}

function wait(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal.aborted) {
      reject(new Error('aborted'))
      return
    }
    const timer = setTimeout(() => {
      signal.removeEventListener('abort', onAbort)
      resolve()
    }, ms)
    const onAbort = () => {
      clearTimeout(timer)
      reject(new Error('aborted'))
    }
    signal.addEventListener('abort', onAbort, { once: true })
  })
}

function errorMessage(error: unknown): string {
  const message = (error as { response?: { data?: { message?: string } } })?.response?.data?.message
  return message || (error instanceof Error ? error.message : '') || '上传失败，可重试同一文件并从断点续传'
}

/**
 * Resumable upload queue. Creation dialogs keep files local until the user submits;
 * existing draft pages upload immediately. Both paths retry only failed files and
 * let the server identify already uploaded chunks by the whole-file MD5.
 */
const OemUploader = forwardRef<OemUploaderHandle, Props>(function OemUploader({
  transferId,
  deferred = false,
  disabled = false,
  onUploaded,
  onBusyChange,
  onQueueChange,
}, ref) {
  const { api } = useOem()
  const input = useRef<HTMLInputElement>(null)
  const entriesRef = useRef<Entry[]>([])
  const chooseLocked = useRef(false)
  const [entries, setEntries] = useState<Entry[]>([])
  const [dragging, setDragging] = useState(false)
  const busy = entries.some((entry) => ['hashing', 'uploading', 'merging', 'cancelling'].includes(entry.phase))

  useEffect(() => { entriesRef.current = entries }, [entries])
  useEffect(() => { onBusyChange?.(busy) }, [busy, onBusyChange])
  useEffect(() => { onQueueChange?.(entries.length) }, [entries.length, onQueueChange])

  // One controller per mounted uploader: unmounting aborts in-flight chunk uploads and backoff waits.
  const unmounted = useRef(false)
  const abort = useRef<AbortController>(new AbortController())
  useEffect(() => {
    unmounted.current = false
    const controller = new AbortController()
    abort.current = controller
    return () => {
      unmounted.current = true
      controller.abort()
    }
  }, [])

  const patch = (key: number, value: Partial<Entry>) => {
    if (unmounted.current) return
    setEntries((list) => {
      const next = list.map((entry) => (entry.key === key ? { ...entry, ...value } : entry))
      entriesRef.current = next
      return next
    })
  }

  /**
   * Sends one chunk with its SHA-256 digest. Transient failures back off and retry; a digest
   * mismatch re-reads the slice from the file, re-hashes and resends once. Every attempt is
   * quiet: the global HTTP toast stays silent and uploadOne reports the item's final failure once.
   */
  const putChunkWithRetry = async (
    sessionId: string, index: number, file: File, start: number, end: number, hasher: ChunkHasher, signal: AbortSignal,
  ) => {
    let transientRetries = 0
    let digestRetried = false
    for (;;) {
      const blob = file.slice(start, end)
      const sha256 = await hasher.sha256(blob)
      if (signal.aborted) throw new Error('aborted')
      try {
        await api.putChunk(sessionId, index, blob, sha256, signal, { quietNetworkError: true, quietClientError: true })
        return
      } catch (error) {
        if (signal.aborted) throw error
        if (isDigestMismatch(error) && !digestRetried) {
          digestRetried = true
          continue
        }
        if (transientRetries >= CHUNK_RETRIES || !isTransient(error)) throw error
        await wait(CHUNK_RETRY_BASE_MS * 2 ** transientRetries, signal)
        transientRetries++
      }
    }
  }

  const uploadOne = async (file: File, key: number, draftId: number): Promise<boolean> => {
    const signal = abort.current.signal
    const saved = entriesRef.current.find((entry) => entry.key === key)
    let md5 = saved?.md5
    let session = saved?.session
    // A resumed session the server no longer knows (404/410) is dropped and re-initialised once.
    // Merged-ready sessions are never re-created: their merge may already have produced the file.
    let mayRecreateSession = Boolean(session && !session.mergeReady)
    let hasher: ChunkHasher | undefined
    try {
      for (;;) {
        try {
          if (!md5) {
            patch(key, { phase: 'hashing', error: undefined, percent: 0 })
            md5 = await fileMd5(file)
            if (signal.aborted) return false
            patch(key, { md5 })
          }
          if (!session) {
            patch(key, { phase: 'uploading', error: undefined })
            const init = await api.initUpload(draftId, { fileName: file.name, fileSize: file.size, fileMd5: md5 })
            session = {
              id: init.sessionId,
              chunkSize: init.chunkSize,
              totalChunks: init.totalChunks,
              uploadedChunks: [...init.uploadedChunks],
              mergeReady: false,
            }
            patch(key, { session })
          }
          const done = new Set(session.uploadedChunks)
          if (!session.mergeReady) {
            patch(key, { phase: 'uploading', error: undefined })
            for (let index = 0; index < session.totalChunks; index++) {
              if (!done.has(index)) {
                hasher ??= createChunkHasher()
                await putChunkWithRetry(session.id, index, file, index * session.chunkSize,
                  Math.min(file.size, (index + 1) * session.chunkSize), hasher, signal)
                done.add(index)
                session = { ...session, uploadedChunks: [...done] }
                patch(key, { session })
              }
              patch(key, { percent: session.totalChunks === 0 ? 100 : Math.round((done.size / session.totalChunks) * 100) })
            }
            session = { ...session, mergeReady: true }
            patch(key, { session })
          }
          patch(key, { phase: 'merging' })
          // Merge is idempotent for a session. Reusing this id is essential when its response is lost.
          await api.merge(session.id)
          if (signal.aborted) return false
          patch(key, { phase: 'done', percent: 100 })
          onUploaded?.()
          return true
        } catch (error) {
          if (signal.aborted) return false
          if (mayRecreateSession && isSessionGone(error)) {
            mayRecreateSession = false
            session = undefined
            patch(key, { session: undefined, percent: 0 })
            continue
          }
          const message = errorMessage(error)
          // Quieted chunk requests left the user-facing report to us: one toast per failed item.
          if (isQuietedError(error) && !unmounted.current) Message.error(`「${file.name}」${message}`)
          patch(key, { phase: 'failed', error: message })
          return false
        }
      }
    } finally {
      hasher?.dispose()
    }
  }

  const uploadSelected = async (selected: Entry[], draftId: number) => {
    for (const entry of selected) await uploadOne(entry.file, entry.key, draftId)
  }

  const uploadAll = async (draftId = transferId): Promise<boolean> => {
    if (!draftId) throw new Error('请先创建草稿')
    if (entriesRef.current.some((entry) => ['hashing', 'uploading', 'merging', 'cancelling'].includes(entry.phase))) return false
    const pending = entriesRef.current.filter((entry) => entry.phase === 'queued' || entry.phase === 'failed')
    let ok = true
    for (const entry of pending) {
      if (!await uploadOne(entry.file, entry.key, draftId)) ok = false
    }
    return ok
  }

  const resetForNewDraft = () => {
    if (unmounted.current) return
    setEntries((list) => {
      const next = list.map((entry) => ({ key: entry.key, file: entry.file, md5: entry.md5, phase: 'queued' as const, percent: 0 }))
      entriesRef.current = next
      return next
    })
  }

  useImperativeHandle(ref, () => ({
    uploadAll,
    resetForNewDraft,
    hasFiles: () => entriesRef.current.length > 0,
    isBusy: () => entriesRef.current.some((entry) => ['hashing', 'uploading', 'merging', 'cancelling'].includes(entry.phase)),
  }))

  const choose = async (files: FileList | File[] | null) => {
    if (!files?.length || chooseLocked.current || busy || disabled) return
    chooseLocked.current = true
    try {
      const queued = Array.from(files).map((file) => ({ key: ++nextKey, file, phase: 'queued' as const, percent: 0 }))
      const next = [...entriesRef.current, ...queued]
      entriesRef.current = next
      setEntries(next)
      if (input.current) input.current.value = ''
      if (!deferred && transferId) await uploadSelected(queued, transferId)
    } finally {
      chooseLocked.current = false
    }
  }

  const removeLocal = (key: number) => {
    if (unmounted.current) return
    setEntries((list) => {
      const next = list.filter((entry) => entry.key !== key)
      entriesRef.current = next
      return next
    })
  }

  const remove = async (entry: Entry) => {
    if (entry.session) {
      patch(entry.key, { phase: 'cancelling', error: undefined })
      try {
        await api.abortUpload(entry.session.id)
      } catch (error) {
        // A session the server no longer knows has nothing left to cancel.
        if (!isSessionGone(error)) {
          // A completed merge is rejected here; the merged file stays on the draft and can be removed there.
          patch(entry.key, { phase: 'failed', error: `取消上传失败：${errorMessage(error)}` })
          return
        }
      }
    }
    removeLocal(entry.key)
  }

  const retry = (entry: Entry) => {
    if (transferId && !busy && !disabled) void uploadSelected([entry], transferId)
  }

  const label = (entry: Entry) => ({
    queued: '等待提交',
    hashing: '正在校验文件…',
    uploading: `上传中 ${entry.percent}%`,
    merging: '服务端合并中…',
    cancelling: '正在取消上传…',
    done: '上传完成',
    failed: entry.error ?? '上传失败',
  })[entry.phase]

  const canChoose = !busy && !disabled
  return (
    <div className="oem-uploader">
      <input ref={input} aria-label="选择文件" type="file" multiple hidden disabled={!canChoose}
        onChange={(event) => void choose(event.target.files)} />
      <div
        role="button"
        tabIndex={canChoose ? 0 : -1}
        aria-label="拖放或点击选择文件"
        aria-disabled={!canChoose}
        onClick={() => { if (canChoose) input.current?.click() }}
        onKeyDown={(event) => {
          if (canChoose && (event.key === 'Enter' || event.key === ' ')) {
            event.preventDefault()
            input.current?.click()
          }
        }}
        onDragOver={(event) => { event.preventDefault(); if (canChoose) setDragging(true) }}
        onDragLeave={() => setDragging(false)}
        onDrop={(event) => {
          event.preventDefault()
          setDragging(false)
          if (canChoose) void choose(event.dataTransfer.files)
        }}
        style={{
          border: `1px dashed ${dragging ? 'rgb(var(--primary-6))' : 'var(--color-border-3)'}`,
          borderRadius: 6,
          padding: '18px 16px',
          textAlign: 'center',
          cursor: canChoose ? 'pointer' : 'not-allowed',
          background: dragging ? 'var(--color-primary-light-1)' : 'var(--color-fill-1)',
          opacity: disabled ? 0.65 : 1,
        }}
      >
        <IconUpload style={{ marginRight: 8 }} />
        <Typography.Text>{busy ? '正在上传，请稍候' : '拖放文件到这里，或点击选择多个文件'}</Typography.Text>
      </div>
      <Space direction="vertical" style={{ width: '100%', marginTop: entries.length ? 12 : 0 }}>
        {entries.map((entry) => (
          <div key={entry.key}>
            <Space style={{ width: '100%', justifyContent: 'space-between' }}>
              <span>
                <Typography.Text>{entry.file.name}</Typography.Text>
                <Typography.Text type="secondary" style={{ marginLeft: 8 }}>{fmtSize(entry.file.size)}</Typography.Text>
              </span>
              {(entry.phase === 'queued' || entry.phase === 'failed') && !busy && !disabled && (
                <Space size={4}>
                  {entry.phase === 'failed' && !deferred && transferId && (
                    <Button type="text" size="mini" icon={<IconRefresh />}
                      aria-label={`重试「${entry.file.name}」`} onClick={(event) => { event.stopPropagation(); retry(entry) }}>重试</Button>
                  )}
                  <Button type="text" size="mini" status="danger" icon={<IconClose />}
                    aria-label={`移除「${entry.file.name}」`} onClick={(event) => { event.stopPropagation(); void remove(entry) }} />
                </Space>
              )}
            </Space>
            <Progress
              percent={entry.phase === 'done' ? 100 : entry.percent}
              status={entry.phase === 'failed' ? 'error' : entry.phase === 'done' ? 'success' : 'normal'}
              formatText={() => label(entry)}
            />
            {entry.phase === 'failed' && entry.session?.mergeReady && (
              <Typography.Text type="secondary">合并结果尚未确认，请重试；关闭窗口后也可从草稿详情继续。移除前请确认附件列表中没有该文件。</Typography.Text>
            )}
          </div>
        ))}
      </Space>
    </div>
  )
})

export default OemUploader
