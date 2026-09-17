import { useEffect, useRef, useState } from 'react'
import { Button, Checkbox, Modal, Progress, Typography, Message, Space } from '@arco-design/web-react'
import { IconUpload, IconClose } from '@arco-design/web-react/icon'
import http from '../api/client'
import { fmtSize } from '../api/types'
import { fileMd5 } from '../api/file-hash'

interface Props {
  projectId: number
  visible: boolean
  onClose: () => void
  /** 每个文件合并成功后触发一次，用于刷新文件列表。 */
  onDone: () => void
  /** 整批文件全部确认上传成功、且没有取消或移除时触发一次。 */
  onAllUploaded?: () => void
  onSubmitForAcceptance?: () => Promise<void>
}

type Phase =
  | 'queued' | 'hashing' | 'uploading' | 'merging'
  | 'merge-uncertain' | 'merge-invalid' | 'cancelling' | 'cancel-failed'
  | 'done' | 'cancelled'

const LOCKING_PHASES: Phase[] = ['merging', 'cancelling']

interface Attempt {
  running: boolean
  cancelled: boolean
  cancelling: boolean
  merging: boolean
  mergePending?: boolean
  mergeInvalid?: boolean
  sessionId?: string
  controller: AbortController
  finished: Promise<void>
  finish: () => void
}

interface Entry {
  key: string
  file: File
  phase: Phase
  percent: number
}

let nextEntryKey = 0

function isDefinitiveIntegrityFailure(error: unknown): boolean {
  const response = (error as { response?: { status?: number; data?: { message?: unknown } } })?.response
  const message = response?.data?.message
  return response?.status === 400
    && typeof message === 'string'
    && /^(合并文件大小不符|文件 MD5 校验失败)/.test(message)
}

function phaseLabel(phase: Phase, percent: number): string {
  switch (phase) {
    case 'queued': return '待上传'
    case 'hashing': return '正在校验文件内容…'
    case 'uploading': return `分片上传中 ${percent}%（中断后可续传）`
    case 'merging': return '服务端合并校验中…'
    case 'merge-uncertain': return '结果待确认，重试不会重复上传'
    case 'merge-invalid': return '完整性校验失败，需清理后重新上传'
    case 'cancelling': return '正在取消…'
    case 'cancel-failed': return '取消未确认，请重试'
    case 'done': return '已完成'
    case 'cancelled': return '已取消'
    default: return ''
  }
}

export default function ChunkUploader({ projectId, visible, onClose, onDone, onAllUploaded, onSubmitForAcceptance }: Props) {
  const [entries, setEntries] = useState<Entry[]>([])
  const attemptsRef = useRef<Map<string, Attempt>>(new Map())
  const fileInputRef = useRef<HTMLInputElement>(null)
  const batchNotifiedRef = useRef(false)
  const batchStartedRef = useRef(false)
  const batchCancelledRef = useRef(false)
  const closingRef = useRef(false)
  const mountedRef = useRef(true)
  const [closing, setClosing] = useState(false)
  const [submitForAcceptance, setSubmitForAcceptance] = useState(false)

  const locking = closing || entries.some((e) => LOCKING_PHASES.includes(e.phase))
  const hasQueued = entries.some((e) => e.phase === 'queued')
  const canAddMore = !locking && entries.every((e) => e.phase === 'queued')

  const patchEntry = (key: string, patch: Partial<Entry>) =>
    setEntries((list) => list.map((e) => (e.key === key ? { ...e, ...patch } : e)))

  const resetAll = () => {
    if (fileInputRef.current) fileInputRef.current.value = ''
    attemptsRef.current.clear()
    batchNotifiedRef.current = false
    batchStartedRef.current = false
    batchCancelledRef.current = false
    setSubmitForAcceptance(false)
    setEntries([])
  }

  useEffect(() => {
    mountedRef.current = true
    return () => {
      mountedRef.current = false
      // 离开页面时中止所有进行中的尝试，但保留服务端会话以便其他页面续传。
      for (const attempt of attemptsRef.current.values()) {
        attempt.cancelled = true
        attempt.controller.abort()
      }
      attemptsRef.current.clear()
    }
  }, [])

  // 只有确认成功的文件可以计入整批完成；取消、清理或结果未知不等于交付成功。
  useEffect(() => {
    if (entries.length === 0 || batchNotifiedRef.current || closingRef.current) return
    if (entries.every((e) => e.phase === 'done')) {
      batchNotifiedRef.current = true
      if (!batchCancelledRef.current) onAllUploaded?.()
      if (!batchCancelledRef.current && submitForAcceptance && onSubmitForAcceptance) {
        closingRef.current = true
        setClosing(true)
        void onSubmitForAcceptance().catch(() => {
          Message.warning('文件已上传，但提交验收失败，请重试提交验收')
        }).finally(() => {
          closingRef.current = false
          if (!mountedRef.current) return
          setClosing(false)
          resetAll()
          onClose()
        })
        return
      }
      resetAll()
      onClose()
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [entries])

  const confirmMerge = async (key: string, attempt: Attempt, fileName: string) => {
    if (attemptsRef.current.get(key) !== attempt || attempt.cancelled || attempt.merging || !attempt.sessionId) return
    attempt.merging = true
    attempt.mergePending = true
    attempt.mergeInvalid = false
    patchEntry(key, { phase: 'merging' })
    try {
      await http.post(`/uploads/${attempt.sessionId}/merge`)
      if (attemptsRef.current.get(key) !== attempt || attempt.cancelled) return
      attempt.mergePending = false
      patchEntry(key, { phase: 'done', percent: 100 })
      Message.success(`「${fileName}」上传完成`)
      onDone()
    } catch (error) {
      if (attemptsRef.current.get(key) === attempt && !attempt.cancelled) {
        if (isDefinitiveIntegrityFailure(error)) {
          attempt.mergePending = false
          attempt.mergeInvalid = true
          patchEntry(key, { phase: 'merge-invalid' })
          Message.warning(`「${fileName}」完整性校验失败，请清理后重新上传`)
        } else {
          patchEntry(key, { phase: 'merge-uncertain' })
          Message.warning(`「${fileName}」上传结果待确认，请重试确认`)
        }
      }
    } finally {
      attempt.merging = false
    }
  }

  const runEntry = async (key: string, file: File) => {
    const previous = attemptsRef.current.get(key)
    if (closingRef.current || previous?.running || previous?.merging || previous?.cancelling || previous?.mergePending) return
    let finish!: () => void
    const finished = new Promise<void>((resolve) => { finish = resolve })
    const attempt: Attempt = { running: true, sessionId: previous?.sessionId, cancelled: false, cancelling: false, merging: false, controller: new AbortController(), finished, finish }
    attemptsRef.current.set(key, attempt)
    const isCurrent = () => attemptsRef.current.get(key) === attempt && !attempt.cancelled
    patchEntry(key, { phase: 'hashing', percent: 0 })
    try {
      const digest = await fileMd5(file, () => !isCurrent())
      if (!isCurrent()) return
      patchEntry(key, { phase: 'uploading' })
      const init = await http.post('/uploads/init', {
        projectId,
        fileName: file.name,
        fileSize: file.size,
        fileMd5: digest,
      })
      const sid: string = init.data.sessionId
      attempt.sessionId = sid
      if (!isCurrent()) return
      const chunkSize: number = init.data.chunkSize
      const total: number = init.data.totalChunks
      const uploaded: Set<number> = new Set(init.data.uploadedChunks || [])
      patchEntry(key, { percent: Math.round((uploaded.size / total) * 100) })
      const missing = Array.from({ length: total }, (_, i) => i).filter((i) => !uploaded.has(i))
      let cursor = 0
      let failed = false
      let firstFailure: { reason: unknown } | undefined
      const worker = async () => {
        while (cursor < missing.length && !failed && isCurrent()) {
          const i = missing[cursor++]
          const blob = file.slice(i * chunkSize, Math.min((i + 1) * chunkSize, file.size))
          try {
            await http.put(`/uploads/${sid}/chunks/${i}`, blob, {
              headers: { 'Content-Type': 'application/octet-stream' },
              timeout: 300000,
              signal: attempt.controller.signal,
            })
          } catch (error) {
            failed = true
            if (!firstFailure) {
              firstFailure = { reason: error }
              // 一个分片失败即视为本轮并发批次不可用；尽快中止同伴请求，已完成分片保留给下次续传。
              attempt.controller.abort()
            }
            throw error
          }
          if (!isCurrent()) return
          uploaded.add(i)
          patchEntry(key, { percent: Math.round((uploaded.size / total) * 100) })
        }
      }
      const results = await Promise.allSettled(Array.from({ length: Math.min(3, missing.length) }, () => worker()))
      if (!isCurrent()) return
      if (firstFailure) throw firstFailure.reason
      const failure = results.find((result) => result.status === 'rejected')
      if (failure?.status === 'rejected') throw failure.reason
      await confirmMerge(key, attempt, file.name)
    } catch {
      if (isCurrent()) {
        patchEntry(key, { phase: 'queued' })
        Message.warning(`「${file.name}」上传中断，可点击重新上传从断点续传`)
      }
    } finally {
      attempt.running = false
      attempt.finish()
    }
  }

  const startAll = async () => {
    if (closingRef.current || locking) return
    batchStartedRef.current = true
    await Promise.all(entries.filter((entry) => entry.phase === 'queued').map((entry) => runEntry(entry.key, entry.file)))
  }

  const cancelEntry = async (key: string): Promise<boolean> => {
    if (batchStartedRef.current) batchCancelledRef.current = true
    const attempt = attemptsRef.current.get(key)
    if (!attempt) {
      setEntries((list) => list.filter((e) => e.key !== key))
      return true
    }
    if (attempt.merging || attempt.cancelling) return false
    if (attempt.mergePending) {
      // 结果未知时只刷新列表；保留可恢复会话，不取消，也不计为成功。
      onDone()
      return true
    }
    attempt.cancelled = true
    attempt.cancelling = true
    attempt.controller.abort()
    patchEntry(key, { phase: 'cancelling' })
    await attempt.finished
    if (attemptsRef.current.get(key) !== attempt) return false
    try {
      if (attempt.sessionId) await http.delete(`/uploads/${attempt.sessionId}`)
      if (attemptsRef.current.get(key) !== attempt) return false
      attemptsRef.current.delete(key)
      patchEntry(key, { phase: 'cancelled' })
      return true
    } catch {
      if (attemptsRef.current.get(key) === attempt) {
        attempt.cancelling = false
        patchEntry(key, { phase: 'cancel-failed' })
        Message.warning('无法确认取消结果，请重试取消')
      }
      return false
    }
  }

  const discardInvalid = async (key: string) => {
    const attempt = attemptsRef.current.get(key)
    if (!attempt || !attempt.mergeInvalid || attempt.merging || attempt.cancelling || !attempt.sessionId) return
    batchCancelledRef.current = true
    attempt.cancelled = true
    attempt.cancelling = true
    attempt.controller.abort()
    patchEntry(key, { phase: 'cancelling' })
    await attempt.finished
    if (attemptsRef.current.get(key) !== attempt) return
    try {
      await http.delete(`/uploads/${attempt.sessionId}`)
      if (attemptsRef.current.get(key) !== attempt) return
      attemptsRef.current.delete(key)
      setEntries((list) => list.filter((e) => e.key !== key))
      Message.info('损坏的上传会话已清理')
    } catch {
      if (attemptsRef.current.get(key) === attempt) {
        attempt.cancelling = false
        patchEntry(key, { phase: 'merge-invalid' })
        Message.warning('清理失败，请重试')
      }
    }
  }

  const removeQueued = async (key: string) => {
    if (closingRef.current) return
    if (await cancelEntry(key)) setEntries((list) => list.filter((e) => e.key !== key))
  }

  const closeAll = async () => {
    if (closingRef.current || locking || [...attemptsRef.current.values()].some((a) => a.merging || a.cancelling)) return
    closingRef.current = true
    batchCancelledRef.current = true
    setClosing(true)
    try {
      const cancellable = entries.filter((e) => e.phase !== 'done' && e.phase !== 'cancelled')
      const results = await Promise.all(cancellable.map((e) => cancelEntry(e.key)))
      if (!mountedRef.current || results.some((ok) => !ok)) return
      resetAll()
      onClose()
    } finally {
      closingRef.current = false
      if (mountedRef.current) setClosing(false)
    }
  }

  const addFiles = (files: FileList | null) => {
    if (!canAddMore || closingRef.current || !files || files.length === 0) return
    if (entries.length === 0) {
      batchStartedRef.current = false
      batchCancelledRef.current = false
    }
    const additions: Entry[] = Array.from(files).map((file) => ({
      key: `f${++nextEntryKey}`, file, phase: 'queued', percent: 0,
    }))
    setEntries((list) => [...list, ...additions])
    batchNotifiedRef.current = false
  }

  const totalCount = entries.length
  const doneCount = entries.filter((e) => e.phase === 'done').length

  return (
    <Modal
      title={totalCount > 0 ? `上传文件（${doneCount}/${totalCount}）` : '上传文件'}
      visible={visible}
      onCancel={closeAll}
      closable={!locking}
      maskClosable={!locking}
      escToExit={!locking}
      footer={
        locking ? (
          <Button disabled>处理中，请稍候</Button>
        ) : (
          <>
            <Button onClick={closeAll}>关闭</Button>
            <Button type="primary" aria-label="上传所选文件" disabled={!hasQueued} onClick={startAll} icon={<IconUpload />}>
              上传所选文件{hasQueued ? `（${entries.filter((e) => e.phase === 'queued').length}）` : ''}
            </Button>
          </>
        )
      }
    >
      <input
        ref={fileInputRef}
        type="file"
        multiple
        className="upload-file-input"
        aria-label="选择上传文件"
        hidden
        disabled={!canAddMore}
        onChange={(e) => {
          addFiles(e.target.files)
          if (fileInputRef.current) fileInputRef.current.value = ''
        }}
      />
      <Button icon={<IconUpload />} aria-label="选择文件" disabled={!canAddMore} onClick={() => fileInputRef.current?.click()}>
        {totalCount > 0 ? '继续选择文件' : '选择文件（可多选）'}
      </Button>
      {onSubmitForAcceptance && <div style={{ marginTop: 12 }}>
        <Checkbox checked={submitForAcceptance} disabled={!canAddMore}
          onChange={setSubmitForAcceptance}>全部上传成功后提交验收</Checkbox>
      </div>}
      {totalCount > 0 && (
        <div style={{ marginTop: 16, display: 'flex', flexDirection: 'column', gap: 12 }}>
          {entries.map((entry) => (
            <div key={entry.key} style={{ overflowWrap: 'anywhere' }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                <Typography.Text style={{ flex: 1 }}>
                  {entry.file.name}（{fmtSize(entry.file.size)}）
                </Typography.Text>
                <Space size={4}>
                  {entry.phase === 'merge-uncertain' && (
                    <Button size="mini" type="primary" onClick={() => {
                      const attempt = attemptsRef.current.get(entry.key)
                      if (attempt) return confirmMerge(entry.key, attempt, entry.file.name)
                    }}>重试确认</Button>
                  )}
                  {entry.phase === 'merge-invalid' && (
                    <Button size="mini" type="primary" status="danger" onClick={() => discardInvalid(entry.key)}>
                      清理并移除
                    </Button>
                  )}
                  {entry.phase === 'cancel-failed' && (
                    <Button size="mini" status="danger" onClick={() => cancelEntry(entry.key)}>重试取消</Button>
                  )}
                  {(entry.phase === 'hashing' || entry.phase === 'uploading') && (
                    <Button size="mini" status="danger" onClick={() => cancelEntry(entry.key)}>取消</Button>
                  )}
                  {entry.phase === 'queued' && (
                    <Button size="mini" icon={<IconClose />} aria-label={`移除「${entry.file.name}」`}
                      onClick={() => removeQueued(entry.key)} />
                  )}
                </Space>
              </div>
              {entry.phase !== 'queued' && entry.phase !== 'cancelled' && (
                <Progress
                  percent={entry.percent}
                  status={entry.phase === 'merge-invalid' ? 'error' : entry.phase === 'done' ? 'success' : undefined}
                  style={{ marginTop: 4 }}
                />
              )}
              <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                {phaseLabel(entry.phase, entry.percent)}
              </Typography.Text>
            </div>
          ))}
        </div>
      )}
    </Modal>
  )
}
