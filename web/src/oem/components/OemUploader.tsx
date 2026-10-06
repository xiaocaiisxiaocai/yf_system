import { forwardRef, useEffect, useImperativeHandle, useRef, useState } from 'react'
import { Button, Progress, Space, Typography } from '@arco-design/web-react'
import { IconClose, IconUpload } from '@arco-design/web-react/icon'
import { fileMd5 } from '../../api/file-hash'
import { fmtSize } from '../../api/types'
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

  const patch = (key: number, value: Partial<Entry>) => {
    setEntries((list) => {
      const next = list.map((entry) => (entry.key === key ? { ...entry, ...value } : entry))
      entriesRef.current = next
      return next
    })
  }

  const uploadOne = async (file: File, key: number, draftId: number): Promise<boolean> => {
    try {
      const saved = entriesRef.current.find((entry) => entry.key === key)
      let md5 = saved?.md5
      if (!md5) {
        patch(key, { phase: 'hashing', error: undefined, percent: 0 })
        md5 = await fileMd5(file)
        patch(key, { md5 })
      }
      let session = saved?.session
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
            const blob = file.slice(index * session.chunkSize, Math.min(file.size, (index + 1) * session.chunkSize))
            await api.putChunk(session.id, index, blob)
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
      patch(key, { phase: 'done', percent: 100 })
      onUploaded?.()
      return true
    } catch (error) {
      patch(key, { phase: 'failed', error: errorMessage(error) })
      return false
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

  useImperativeHandle(ref, () => ({
    uploadAll,
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
    setEntries((list) => {
      const next = list.filter((entry) => entry.key !== key)
      entriesRef.current = next
      return next
    })
  }

  const remove = async (entry: Entry) => {
    if (entry.session?.mergeReady) return
    if (entry.session) {
      patch(entry.key, { phase: 'cancelling', error: undefined })
      try {
        await api.abortUpload(entry.session.id)
      } catch (error) {
        patch(entry.key, { phase: 'failed', error: `取消上传失败：${errorMessage(error)}` })
        return
      }
    }
    removeLocal(entry.key)
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
              {(entry.phase === 'queued' || (entry.phase === 'failed' && !entry.session?.mergeReady)) && !busy && !disabled && (
                <Button type="text" size="mini" status="danger" icon={<IconClose />}
                  aria-label={`移除「${entry.file.name}」`} onClick={(event) => { event.stopPropagation(); void remove(entry) }} />
              )}
            </Space>
            <Progress
              percent={entry.phase === 'done' ? 100 : entry.percent}
              status={entry.phase === 'failed' ? 'error' : entry.phase === 'done' ? 'success' : 'normal'}
              formatText={() => label(entry)}
            />
            {entry.phase === 'failed' && entry.session?.mergeReady && (
              <Typography.Text type="secondary">合并结果尚未确认，请重试；关闭窗口后也可从草稿详情继续。</Typography.Text>
            )}
          </div>
        ))}
      </Space>
    </div>
  )
})

export default OemUploader
