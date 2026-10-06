import { useEffect, useRef, useState } from 'react'
import { Alert, Button, Checkbox, Modal, Progress, Typography, Message, Space } from '@arco-design/web-react'
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
import { companyMaterialNameError, isStepFile } from '../utils/uploadMaterialRules'
import UploadMaterialGuide, { type UploadDirection } from './UploadMaterialGuide'

interface Props {
  projectId: number
  visible: boolean
  onClose: () => void
  /** 每个文件合并成功后触发一次，用于刷新文件列表。 */
  onDone: () => void
  /** 整批文件全部确认上传成功、且没有取消或移除时触发一次。 */
  onAllUploaded?: () => void
  onSubmitForAcceptance?: () => Promise<void>
  /** 上传方向：C2S（公司发给供应商）校验资料要求，S2C（供应商发给公司）只展示资料示例。 */
  direction?: UploadDirection
  /**
   * 子项目已有可用的公司发给供应商 STEP 文件时为 true，用于隐藏“本批没有 STEP”提示。
   * 仅是前端尽力判断（来自文件列表），服务端在初始化与合并时仍会权威校验。
   */
  hasCompanyStep?: boolean
}

type Phase =
  | 'queued' | 'interrupted' | 'hashing' | 'uploading' | 'merging'
  | 'merge-uncertain' | 'merge-invalid' | 'cancelling' | 'cancel-failed'
  | 'done' | 'cancelled' | 'rejected'

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
  /** 等待同批 STEP 文件建立上传会话（公司发给供应商时，服务端要求先有 STEP）。 */
  awaitingStep?: boolean
  /** 服务端业务规则拒绝（初始化或合并时的 4xx）的提示；此状态不可续传。 */
  rejectedMessage?: string
}

interface RunOptions {
  /** 非 STEP 文件在此之后才初始化上传。 */
  stepGate?: Promise<void>
  /** 非 STEP 文件在此之后才请求合并：服务端合并时只认已合并的 STEP 文件，不认进行中的 STEP 会话。 */
  mergeGate?: Promise<void>
  /** STEP 文件的上传会话建立后调用，放行同批其它文件。 */
  onInitialized?: () => void
}

function waitForGate(gate: Promise<void>, signal: AbortSignal): Promise<void> {
  return new Promise((resolve) => {
    if (signal.aborted) return resolve()
    const done = () => { signal.removeEventListener('abort', done); resolve() }
    signal.addEventListener('abort', done, { once: true })
    void gate.then(done)
  })
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

/** 服务端按业务规则拒绝的错误：初始化或合并阶段出现即终止，续传不会改变结果。 */
class UploadRejectedError extends Error {}

const REJECTION_STATUSES = new Set([400, 403, 404, 413, 422])

/** 初始化或合并返回的业务拒绝（4xx，不含会话冲突、限流、超时和完整性失败）；返回服务端提示。 */
function businessRejectionMessage(error: unknown, stage: 'init' | 'merge'): string | undefined {
  const failure = error as { code?: string; response?: { status?: number; data?: { message?: unknown } } }
  if (failure?.code === 'ERR_CANCELED') return undefined
  const status = failure?.response?.status
  if (typeof status !== 'number' || !REJECTION_STATUSES.has(status)) return undefined
  // 合并时 404 表示会话状态未知（可能已过期或已提交），保留“结果待确认”流程。
  if (stage === 'merge' && status === 404) return undefined
  if (isDefinitiveIntegrityFailure(error)) return undefined
  const message = failure.response?.data?.message
  return typeof message === 'string' && message.trim() ? message : '服务端拒绝了该文件'
}

function phaseLabel(phase: Phase, percent: number, waiting = false, awaitingStep = false): string {
  switch (phase) {
    case 'queued':
      if (awaitingStep) return '等待同批 STEP 文件开始上传'
      return waiting ? '排队中，前面的文件完成后自动开始' : '待上传'
    case 'interrupted': return '上传中断，可点击重新上传从断点续传'
    case 'hashing': return `正在校验文件内容 ${percent}%`
    case 'uploading':
      if (awaitingStep) return '等待同批 STEP 文件上传完成'
      return `分片上传中 ${percent}%（中断后可续传）`
    case 'merging': return '服务端合并校验中…'
    case 'merge-uncertain': return '结果待确认，重试不会重复上传'
    case 'merge-invalid': return '完整性校验失败，需清理后重新上传'
    case 'cancelling': return '正在取消…'
    case 'cancel-failed': return '取消未确认，请重试'
    case 'done': return '已完成'
    case 'cancelled': return '已取消'
    case 'rejected': return '服务端拒绝，未上传'
    default: return ''
  }
}

export default function ChunkUploader({ projectId, visible, onClose, onDone, onAllUploaded, onSubmitForAcceptance, direction, hasCompanyStep = false }: Props) {
  const [entries, setEntries] = useState<Entry[]>([])
  const attemptsRef = useRef<Map<string, Attempt>>(new Map())
  const fileInputRef = useRef<HTMLInputElement>(null)
  const batchNotifiedRef = useRef(false)
  const batchStartedRef = useRef(false)
  const batchCancelledRef = useRef(false)
  const closingRef = useRef(false)
  const mountedRef = useRef(true)
  /** 本批已提示过的拒绝原因：同一原因只弹一次，逐个文件的原因显示在各自行内。 */
  const rejectionToastsRef = useRef<Set<string>>(new Set())
  const [closing, setClosing] = useState(false)
  const [submitForAcceptance, setSubmitForAcceptance] = useState(false)
  const uploadSlots = useRef(createUploadSlots(MAX_CONCURRENT_FILES)).current
  const chunkUploadSlots = useRef(createUploadSlots(MAX_CONCURRENT_CHUNKS)).current

  const locking = closing || entries.some((e) => LOCKING_PHASES.includes(e.phase))
  const companyRules = direction === 'C2S'
  const nameError = (entry: Entry) => (companyRules ? companyMaterialNameError(entry.file.name) : null)
  const pending = entries.filter((e) => e.phase === 'queued' || e.phase === 'interrupted')
  const hasQueued = pending.length > 0
  const hasNameErrors = pending.some((e) => nameError(e) !== null)
  const missingStepInBatch = companyRules && !hasCompanyStep && hasQueued
    && !entries.some((e) => isStepFile(e.file.name) && e.phase !== 'cancelled' && e.phase !== 'rejected')
  const canAddMore = !locking && entries.every((e) => e.phase === 'queued' || e.phase === 'interrupted' || e.phase === 'rejected')

  const patchEntry = (key: string, patch: Partial<Entry>) =>
    setEntries((list) => list.map((e) => (e.key === key ? { ...e, ...patch } : e)))

  const resetAll = () => {
    if (fileInputRef.current) fileInputRef.current.value = ''
    attemptsRef.current.clear()
    batchNotifiedRef.current = false
    batchStartedRef.current = false
    batchCancelledRef.current = false
    rejectionToastsRef.current.clear()
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

  const reject = (key: string, message: string) => {
    patchEntry(key, { phase: 'rejected', rejectedMessage: message, waiting: false, awaitingStep: false })
    if (rejectionToastsRef.current.has(message)) return
    rejectionToastsRef.current.add(message)
    Message.error(message)
  }

  const confirmMerge = async (key: string, attempt: Attempt, fileName: string) => {
    if (attemptsRef.current.get(key) !== attempt || attempt.cancelled || attempt.merging || !attempt.sessionId) return
    attempt.merging = true
    attempt.mergePending = true
    attempt.mergeInvalid = false
    patchEntry(key, { phase: 'merging' })
    try {
      await mergeUpload(attempt.sessionId, { quietClientError: true })
      if (attemptsRef.current.get(key) !== attempt || attempt.cancelled) return
      attempt.mergePending = false
      patchEntry(key, { phase: 'done', percent: 100 })
      Message.success(`「${fileName}」上传完成`)
      onDone()
    } catch (error) {
      if (attemptsRef.current.get(key) === attempt && !attempt.cancelled) {
        const rejection = businessRejectionMessage(error, 'merge')
        if (rejection !== undefined) {
          // 合并时服务端复核业务规则（如 STEP 要求）失败：终态拒绝，不提供重试确认。
          attempt.mergePending = false
          reject(key, rejection)
        } else if (isDefinitiveIntegrityFailure(error)) {
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

  const runEntry = async (key: string, file: File, options: RunOptions = {}) => {
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
      if (options.stepGate) {
        patchEntry(key, { awaitingStep: true })
        await waitForGate(options.stepGate, attempt.controller.signal)
        patchEntry(key, { awaitingStep: false })
        if (!isCurrent()) return
      }
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
      }, { quietClientError: true }).catch((error: unknown) => {
        const rejection = businessRejectionMessage(error, 'init')
        if (rejection === undefined) throw error
        if (isCurrent()) reject(key, rejection)
        throw new UploadRejectedError(rejection)
      })
      const sid: string = init.data.sessionId
      attempt.sessionId = sid
      options.onInitialized?.()
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
      if (options.mergeGate) {
        // 等待期间释放并发名额，避免排队中的同批 STEP 拿不到名额而互相等待。
        release?.()
        release = undefined
        patchEntry(key, { awaitingStep: true })
        await waitForGate(options.mergeGate, attempt.controller.signal)
        patchEntry(key, { awaitingStep: false })
        if (!isCurrent()) return
      }
      await confirmMerge(key, attempt, file.name)
    } catch (error) {
      attempt.controller.abort()
      if (isCurrent() && !(error instanceof UploadRejectedError)) {
        patchEntry(key, { phase: 'interrupted' })
      }
    } finally {
      chunkHasher?.dispose()
      release?.()
      patchEntry(key, { waiting: false, awaitingStep: false })
      attempt.running = false
      attempt.finish()
    }
  }

  const startAll = async () => {
    if (closingRef.current || locking || hasNameErrors) return
    batchStartedRef.current = true
    rejectionToastsRef.current.clear()
    const runnable = entries.filter((entry) => entry.phase === 'queued' || entry.phase === 'interrupted')
    const steps = companyRules ? runnable.filter((entry) => isStepFile(entry.file.name)) : []
    if (steps.length === 0 || steps.length === runnable.length) {
      await Promise.all(runnable.map((entry) => runEntry(entry.key, entry.file)))
      return
    }
    // 服务端要求公司发给供应商的其它文件初始化时已有 STEP 文件或正在上传的 STEP 会话，合并时已有
    // 合并完成的 STEP 文件：同批 STEP 先建立会话，其余文件随后开始上传，并在同批 STEP 全部结束后
    // 再合并；STEP 全部失败时也放行，由服务端给出明确提示。
    let openGate!: () => void
    const stepGate = new Promise<void>((resolve) => { openGate = resolve })
    const stepRuns = steps.map((entry) => runEntry(entry.key, entry.file, { onInitialized: openGate }))
    const mergeGate = Promise.allSettled(stepRuns).then(() => openGate())
    await Promise.all([
      ...stepRuns,
      ...runnable.filter((entry) => !isStepFile(entry.file.name))
        .map((entry) => runEntry(entry.key, entry.file, { stepGate, mergeGate })),
    ])
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
            <Button type="primary" aria-label="上传所选文件" disabled={!hasQueued || hasNameErrors} onClick={startAll} icon={<IconUpload />}>
              上传所选文件{hasQueued ? `（${pending.length}）` : ''}
            </Button>
          </>
        )
      }
    >
      {direction && <UploadMaterialGuide direction={direction} />}
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
      <div aria-live="polite">
        {missingStepInBatch && (
          <Alert type="warning" style={{ marginTop: 12 }}
            content="本批没有 STEP 文件。若本子项目还没有上传过 STEP 3D 图，其它文件会被拒绝，请一并选择 .step/.stp 文件。" />
        )}
      </div>
      <div aria-live="polite">
        {hasNameErrors && (
          <Alert type="error" style={{ marginTop: 12 }} content="有文件名不符合要求，请移除后按规则重命名再选择。" />
        )}
      </div>
      {totalCount > 0 && (
        <div style={{ marginTop: 16, display: 'flex', flexDirection: 'column', gap: 12 }}>
          {entries.map((entry) => {
            const entryError = entry.phase === 'rejected'
              ? entry.rejectedMessage ?? null
              : (entry.phase === 'queued' || entry.phase === 'interrupted') ? nameError(entry) : null
            const errorId = `upload-entry-error-${entry.key}`
            return (
            <div key={entry.key} style={{ overflowWrap: 'anywhere' }} role="group" aria-label={entry.file.name}
              aria-describedby={entryError ? errorId : undefined}>
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
                  {(entry.phase === 'queued' || entry.phase === 'interrupted' || entry.phase === 'rejected') && (
                    <Button size="mini" icon={<IconClose />} aria-label={`移除「${entry.file.name}」`}
                      aria-describedby={entryError ? errorId : undefined}
                      onClick={() => removeQueued(entry.key)} />
                  )}
                </Space>
              </div>
              {entry.phase !== 'queued' && entry.phase !== 'cancelled' && (
                <Progress
                  percent={entry.percent}
                  status={entry.phase === 'merge-invalid' || entry.phase === 'rejected' ? 'error' : entry.phase === 'done' ? 'success' : undefined}
                  style={{ marginTop: 4 }}
                />
              )}
              <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                {phaseLabel(entry.phase, entry.percent, entry.waiting, entry.awaitingStep)}
              </Typography.Text>
              <div aria-live="polite">
                {entryError && (
                  <Typography.Text className="upload-entry-error" id={errorId}>{entryError}</Typography.Text>
                )}
              </div>
            </div>
            )
          })}
        </div>
      )}
    </Modal>
  )
}
