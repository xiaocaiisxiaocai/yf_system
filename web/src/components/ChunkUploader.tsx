import { useEffect, useRef, useState } from 'react'
import { Button, Checkbox, Modal, Progress, Typography, Message, Space } from '@arco-design/web-react'
import { IconUpload, IconClose } from '@arco-design/web-react/icon'
import { fmtSize } from '../api/types'
import { createChunkHasher, fileMd5, uploadFingerprint } from '../api/file-hash'
import {
  abortUpload,
  initUpload,
  mergeUpload,
  putUploadChunk,
  submitUploadMd5,
} from '../api/uploads'

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
  | 'queued' | 'interrupted' | 'hashing' | 'uploading' | 'merging'
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
  /** 已开始但在等待并发名额，尚未计算校验值。 */
  waiting?: boolean
}

let nextEntryKey = 0

/** 同一批次最多同时上传的文件数；其余文件排队，避免挤占浏览器连接与主线程。 */
const MAX_CONCURRENT_FILES = 2
/** 所有文件合计的分片请求上限，给普通 API 与实时连接保留浏览器连接。 */
const MAX_CONCURRENT_CHUNKS = 4
/** 单个分片遇到网络抖动等暂时性错误时的总尝试次数（含首次）。 */
const CHUNK_ATTEMPTS = 3

type Release = () => void

/** 可中止的并发名额：等待中的尝试被取消时立即返回 undefined，不占用名额。 */
function createUploadSlots(limit: number) {
  let active = 0
  const waiters = new Set<() => void>()
  const release = () => {
    active--
    const next = waiters.values().next().value
    if (next) { waiters.delete(next); next() }
  }
  const grant = (): Release => {
    active++
    let released = false
    return () => { if (!released) { released = true; release() } }
  }
  return {
    /** immediate 为 false 时表示需要排队等待。 */
    acquire(signal: AbortSignal, onWait: () => void): Promise<Release | undefined> {
      if (signal.aborted) return Promise.resolve(undefined)
      if (active < limit) return Promise.resolve(grant())
      onWait()
      return new Promise((resolve) => {
        const onAbort = () => { waiters.delete(waiter); resolve(undefined) }
        const waiter = () => { signal.removeEventListener('abort', onAbort); resolve(grant()) }
        waiters.add(waiter)
        signal.addEventListener('abort', onAbort, { once: true })
      })
    },
  }
}

/** 断网、超时、限流和服务端错误可以重试；业务拒绝（4xx）与主动取消不重试。分片写入在服务端是幂等的。 */
function isTransientUploadError(error: unknown): boolean {
  const failure = error as { code?: string; response?: { status?: number } }
  if (failure?.code === 'ERR_CANCELED') return false
  const status = failure?.response?.status
  if (typeof status === 'number') return status === 408 || status === 429 || status >= 500
  return failure?.code === 'ERR_NETWORK' || failure?.code === 'ECONNABORTED' || failure?.code === 'ETIMEDOUT'
}

function abortableDelay(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve) => {
    const done = () => { clearTimeout(timer); signal.removeEventListener('abort', done); resolve() }
    const timer = setTimeout(done, ms)
    signal.addEventListener('abort', done, { once: true })
  })
}

function isDefinitiveIntegrityFailure(error: unknown): boolean {
  const response = (error as { response?: { status?: number; data?: { message?: unknown } } })?.response
  const message = response?.data?.message
  return response?.status === 400
    && typeof message === 'string'
    && /^(合并文件大小不符|文件 MD5 校验失败)/.test(message)
}

function phaseLabel(phase: Phase, percent: number, waiting = false): string {
  switch (phase) {
    case 'queued': return waiting ? '排队中，前面的文件完成后自动开始' : '待上传'
    case 'interrupted': return '上传中断，可点击重新上传从断点续传'
    case 'hashing': return `正在校验文件内容 ${percent}%`
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
  const uploadSlots = useRef(createUploadSlots(MAX_CONCURRENT_FILES)).current
  const chunkUploadSlots = useRef(createUploadSlots(MAX_CONCURRENT_CHUNKS)).current

  const locking = closing || entries.some((e) => LOCKING_PHASES.includes(e.phase))
  const hasQueued = entries.some((e) => e.phase === 'queued' || e.phase === 'interrupted')
  const canAddMore = !locking && entries.every((e) => e.phase === 'queued' || e.phase === 'interrupted')

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
    const attempts = attemptsRef.current
    return () => {
      mountedRef.current = false
      // 离开页面时中止所有进行中的尝试，但保留服务端会话以便其他页面续传。
      for (const attempt of attempts.values()) {
        attempt.cancelled = true
        attempt.controller.abort()
      }
      attempts.clear()
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
      await mergeUpload(attempt.sessionId)
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
    let release: Release | undefined
    let chunkHasher: ReturnType<typeof createChunkHasher> | undefined
    try {
      release = await uploadSlots.acquire(attempt.controller.signal, () => patchEntry(key, { waiting: true }))
      if (!release || !isCurrent()) return
      chunkHasher = createChunkHasher()
      const activeChunkHasher = chunkHasher
      patchEntry(key, { phase: 'hashing', percent: 0, waiting: false })
      let hashFailure: unknown
      let showHashProgress = true
      const fullMd5 = fileMd5(
        file,
        () => !isCurrent() || attempt.controller.signal.aborted,
        (fraction) => {
          if (showHashProgress && isCurrent()) patchEntry(key, { percent: Math.round(fraction * 100) })
        },
      ).catch((error) => {
        hashFailure = error
        return undefined
      })
      const fingerprint = await uploadFingerprint(file)
      if (!isCurrent()) return
      showHashProgress = false
      patchEntry(key, { phase: 'uploading', percent: 0 })
      const init = await initUpload({
        projectId,
        fileName: file.name,
        fileSize: file.size,
        fileLastModified: file.lastModified,
        fileFingerprint: fingerprint,
      })
      const sid: string = init.data.sessionId
      attempt.sessionId = sid
      if (!isCurrent()) return
      const chunkSize: number = init.data.chunkSize
      const total: number = init.data.totalChunks
      const remoteDigests = new Map(
        (init.data.uploadedChunks || []).map((chunk) => [chunk.index, chunk.sha256.toLowerCase()]),
      )
      const uploaded = new Set<number>()
      const chunks = Array.from({ length: total }, (_, i) => i)
      let cursor = 0
      let failed = false
      let firstFailure: { reason: unknown } | undefined
      const worker = async () => {
        while (cursor < chunks.length && !failed && isCurrent()) {
          const i = chunks[cursor++]
          const blob = file.slice(i * chunkSize, Math.min((i + 1) * chunkSize, file.size))
          try {
            const chunkDigest = await activeChunkHasher.sha256(blob)
            if (remoteDigests.get(i) !== chunkDigest) {
              for (let attemptNo = 1; ; attemptNo++) {
                let releaseChunk: Release | undefined
                try {
                  releaseChunk = await chunkUploadSlots.acquire(attempt.controller.signal, () => undefined)
                  if (!releaseChunk || !isCurrent()) throw new Error('上传已取消')
                  await putUploadChunk(sid, i, blob, chunkDigest, {
                    signal: attempt.controller.signal,
                    // 仍会自动重试时不弹出错误提示，最后一次失败才提示。
                    quietNetworkError: attemptNo < CHUNK_ATTEMPTS,
                  })
                  break
                } catch (error) {
                  if (attemptNo >= CHUNK_ATTEMPTS || failed || !isCurrent() || !isTransientUploadError(error)) throw error
                  await abortableDelay(1000 * 2 ** (attemptNo - 1), attempt.controller.signal)
                  if (failed || !isCurrent()) throw error
                } finally {
                  releaseChunk?.()
                }
              }
            }
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
      const results = await Promise.allSettled(Array.from({ length: Math.min(3, chunks.length) }, () => worker()))
      if (!isCurrent()) return
      if (firstFailure) throw firstFailure.reason
      const failure = results.find((result) => result.status === 'rejected')
      if (failure?.status === 'rejected') throw failure.reason
      const digest = await fullMd5
      if (hashFailure) throw hashFailure
      if (!digest) throw new Error('文件 MD5 计算失败')
      if (!isCurrent()) return
      await submitUploadMd5(sid, digest)
      if (!isCurrent()) return
      await confirmMerge(key, attempt, file.name)
    } catch {
      attempt.controller.abort()
      if (isCurrent()) {
        patchEntry(key, { phase: 'interrupted' })
      }
    } finally {
      chunkHasher?.dispose()
      release?.()
      patchEntry(key, { waiting: false })
      attempt.running = false
      attempt.finish()
    }
  }

  const startAll = async () => {
    if (closingRef.current || locking) return
    batchStartedRef.current = true
    await Promise.all(entries
      .filter((entry) => entry.phase === 'queued' || entry.phase === 'interrupted')
      .map((entry) => runEntry(entry.key, entry.file)))
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
      if (attempt.sessionId) await abortUpload(attempt.sessionId)
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
      await abortUpload(attempt.sessionId)
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
              上传所选文件{hasQueued ? `（${entries.filter((e) => e.phase === 'queued' || e.phase === 'interrupted').length}）` : ''}
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
                  {(entry.phase === 'queued' || entry.phase === 'interrupted') && (
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
                {phaseLabel(entry.phase, entry.percent, entry.waiting)}
              </Typography.Text>
            </div>
          ))}
        </div>
      )}
    </Modal>
  )
}
