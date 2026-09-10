import { useEffect, useRef, useState } from 'react'
import { Button, Modal, Progress, Typography, Message } from '@arco-design/web-react'
import { IconUpload } from '@arco-design/web-react/icon'
import http from '../api/client'
import { fmtSize } from '../api/types'
import { fileMd5 } from '../api/file-hash'

interface Props {
  projectId: number
  visible: boolean
  onClose: () => void
  onDone: () => void
}

type Phase = 'pick' | 'hashing' | 'uploading' | 'merging' | 'merge-uncertain' | 'merge-invalid' | 'cancelling' | 'cancel-failed' | 'done'
interface Attempt {
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

function isDefinitiveIntegrityFailure(error: unknown): boolean {
  const response = (error as { response?: { status?: number; data?: { message?: unknown } } })?.response
  const message = response?.data?.message
  return response?.status === 400
    && typeof message === 'string'
    && /^(合并文件大小不符|文件 MD5 校验失败)/.test(message)
}

export default function ChunkUploader({ projectId, visible, onClose, onDone }: Props) {
  const [file, setFile] = useState<File | null>(null)
  const [phase, setPhase] = useState<Phase>('pick')
  const [percent, setPercent] = useState(0)
  const attemptRef = useRef<Attempt | null>(null)
  const busy = phase !== 'pick' && phase !== 'done'

  useEffect(() => () => {
    const attempt = attemptRef.current
    if (attempt) {
      attempt.cancelled = true
      attempt.controller.abort()
      attemptRef.current = null
      // 离页后保留可续传会话，不让晚到的清理请求取消其他页面的新尝试。
    }
  }, [])

  const reset = () => {
    setFile(null)
    setPhase('pick')
    setPercent(0)
    attemptRef.current = null
  }

  const close = async () => {
    const attempt = attemptRef.current
    if (!attempt) {
      reset()
      onClose()
      return
    }
    if (attempt.merging || attempt.cancelling) return
    if (attempt.mergePending) {
      // The server may have committed already. Leaving must never abort that session.
      reset()
      onDone()
      onClose()
      return
    }
    attempt.cancelled = true
    attempt.cancelling = true
    attempt.controller.abort()
    setPhase('cancelling')
    // init 必须返回后才能确定要取消的会话；关闭前完成清理，禁止新尝试复用旧 SID。
    await attempt.finished
    if (attemptRef.current !== attempt) return
    try {
      if (attempt.sessionId) await http.delete(`/uploads/${attempt.sessionId}`)
      if (attemptRef.current !== attempt) return
      Message.info('已取消上传')
      reset()
      onClose()
    } catch {
      if (attemptRef.current === attempt) {
        attempt.cancelling = false
        setPhase('cancel-failed')
        Message.warning('无法确认取消结果，请重试取消')
      }
    }
  }

  const finishAndClose = () => {
    reset()
    onClose()
  }

  const confirmMerge = async (attempt: Attempt) => {
    if (attemptRef.current !== attempt || attempt.cancelled || attempt.merging || !attempt.sessionId) return
    attempt.merging = true
    attempt.mergePending = true
    attempt.mergeInvalid = false
    setPhase('merging')
    try {
      await http.post(`/uploads/${attempt.sessionId}/merge`)
      if (attemptRef.current !== attempt || attempt.cancelled) return
      attempt.mergePending = false
      setPhase('done')
      Message.success('上传完成')
      onDone()
      finishAndClose()
    } catch (error) {
      if (attemptRef.current === attempt && !attempt.cancelled) {
        if (isDefinitiveIntegrityFailure(error)) {
          attempt.mergePending = false
          attempt.mergeInvalid = true
          setPhase('merge-invalid')
          Message.warning('文件完整性校验失败，请清理后重新上传')
        } else {
          setPhase('merge-uncertain')
          Message.warning('上传结果待确认，请重试确认')
        }
      }
    } finally {
      attempt.merging = false
    }
  }

  const discardInvalid = async () => {
    const attempt = attemptRef.current
    if (!attempt || !attempt.mergeInvalid || attempt.merging || attempt.cancelling || !attempt.sessionId) return
    attempt.cancelled = true
    attempt.cancelling = true
    attempt.controller.abort()
    setPhase('cancelling')
    await attempt.finished
    if (attemptRef.current !== attempt) return
    try {
      await http.delete(`/uploads/${attempt.sessionId}`)
      if (attemptRef.current !== attempt) return
      Message.info('损坏的上传会话已清理，请重新选择文件')
      reset()
    } catch {
      if (attemptRef.current === attempt) {
        attempt.cancelling = false
        setPhase('merge-invalid')
        Message.warning('清理失败，请重试')
      }
    }
  }

  const start = async () => {
    if (!file || attemptRef.current) return
    let finish!: () => void
    const finished = new Promise<void>((resolve) => { finish = resolve })
    const attempt: Attempt = { cancelled: false, cancelling: false, merging: false, controller: new AbortController(), finished, finish }
    attemptRef.current = attempt
    const isCurrent = () => attemptRef.current === attempt && !attempt.cancelled
    setPhase('hashing')
    try {
      const digest = await fileMd5(file, () => !isCurrent())
      if (!isCurrent()) return
      setPhase('uploading')
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
      setPercent(Math.round((uploaded.size / total) * 100))
      const missing = Array.from({ length: total }, (_, i) => i).filter((i) => !uploaded.has(i))
      let cursor = 0
      let failed = false
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
            throw error
          }
          if (!isCurrent()) return
          uploaded.add(i)
          setPercent(Math.round((uploaded.size / total) * 100))
        }
      }
      const results = await Promise.allSettled(Array.from({ length: Math.min(3, missing.length) }, () => worker()))
      if (!isCurrent()) return
      const failure = results.find((result) => result.status === 'rejected')
      if (failure?.status === 'rejected') throw failure.reason
      await confirmMerge(attempt)
    } catch {
      if (isCurrent()) {
        // 错误信息由拦截器提示；保留进度便于重试
        setPhase('pick')
        Message.warning('上传中断，可点击开始后从断点续传')
      }
    } finally {
      attempt.finish()
      if (attemptRef.current === attempt && !attempt.cancelled && !attempt.mergePending && !attempt.mergeInvalid) attemptRef.current = null
    }
  }

  return (
    <Modal
      title="上传文件"
      visible={visible}
      onCancel={close}
      closable={phase !== 'merging' && phase !== 'cancelling'}
      maskClosable={phase !== 'merging' && phase !== 'cancelling'}
      escToExit={phase !== 'merging' && phase !== 'cancelling'}
      footer={
        phase === 'merging' || phase === 'cancelling' ? (
          <Button disabled>{phase === 'merging' ? '合并校验中，请稍候' : '正在取消上传…'}</Button>
        ) : phase === 'merge-uncertain' ? (
          <>
            <Button onClick={close}>关闭</Button>
            <Button type="primary" onClick={() => {
              if (attemptRef.current) return confirmMerge(attemptRef.current)
            }}>重试确认</Button>
          </>
        ) : phase === 'merge-invalid' ? (
          <>
            <Button onClick={close}>关闭</Button>
            <Button type="primary" status="danger" onClick={discardInvalid}>清理并重新选择</Button>
          </>
        ) : busy ? (
          <Button status="danger" onClick={close}>
            {phase === 'cancel-failed' ? '重试取消' : '取消上传'}
          </Button>
        ) : (
          <>
            <Button onClick={close}>关闭</Button>
            <Button type="primary" disabled={!file} onClick={start} icon={<IconUpload />}>
              上传所选文件
            </Button>
          </>
        )
      }
    >
      <input
        type="file"
        className="upload-file-input"
        aria-label="选择上传文件"
        style={{ maxWidth: '100%' }}
        disabled={busy}
        onChange={(e) => {
          setFile(e.target.files?.[0] || null)
          setPercent(0)
        }}
      />
      {file && (
        <div style={{ marginTop: 16, overflowWrap: 'anywhere' }}>
          <Typography.Text>
            {file.name}（{fmtSize(file.size)}）
          </Typography.Text>
          {phase === 'pick' ? (
            <div style={{ marginTop: 8 }}>
              <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                文件已选择，点击“上传所选文件”后开始上传。
              </Typography.Text>
            </div>
          ) : (
            <>
              <Progress
                percent={percent}
                status={phase === 'merging' ? 'normal' : undefined}
                style={{ marginTop: 8 }}
              />
              <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                {phase === 'hashing' && '正在校验文件内容…'}
                {phase === 'uploading' && `分片上传中 ${percent}%（中断后可续传）`}
                {phase === 'merging' && '服务端合并校验中…'}
                {phase === 'merge-uncertain' && '结果待确认，重试不会重复上传。'}
                {phase === 'merge-invalid' && '文件完整性校验失败，需清理当前会话后重新上传。'}
                {phase === 'done' && '完成'}
              </Typography.Text>
            </>
          )}
        </div>
      )}
    </Modal>
  )
}
