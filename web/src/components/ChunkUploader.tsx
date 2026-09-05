import { useEffect, useRef, useState } from 'react'
import { Button, Modal, Progress, Select, Typography, Message } from '@arco-design/web-react'
import { IconUpload } from '@arco-design/web-react/icon'
import http from '../api/client'
import { fmtSize } from '../api/types'
import { fileMd5 } from '../api/file-hash'

interface Props {
  projectId: number
  roundId: number
  rounds?: { id: number; roundNo: number }[]
  onRoundChange?: (id: number) => void
  visible: boolean
  onClose: () => void
  onDone: () => void
}

type Phase = 'pick' | 'hashing' | 'uploading' | 'merging' | 'cancelling' | 'cancel-failed' | 'done'
interface Attempt {
  cancelled: boolean
  cancelling: boolean
  merging: boolean
  sessionId?: string
  controller: AbortController
  finished: Promise<void>
  finish: () => void
}

export default function ChunkUploader({ projectId, roundId, rounds, onRoundChange, visible, onClose, onDone }: Props) {
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
        roundId,
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
      attempt.merging = true
      setPhase('merging')
      await http.post(`/uploads/${sid}/merge`)
      if (!isCurrent()) return
      setPhase('done')
      Message.success('上传完成')
      onDone()
      finishAndClose()
    } catch {
      if (isCurrent()) {
        // 错误信息由拦截器提示；保留进度便于重试
        setPhase('pick')
        Message.warning('上传中断，可点击开始后从断点续传')
      }
    } finally {
      attempt.finish()
      if (attemptRef.current === attempt && !attempt.cancelled) attemptRef.current = null
    }
  }

  return (
    <Modal
      title="上传文件（分片）"
      visible={visible}
      onCancel={close}
      closable={phase !== 'merging' && phase !== 'cancelling'}
      maskClosable={phase !== 'merging' && phase !== 'cancelling'}
      escToExit={phase !== 'merging' && phase !== 'cancelling'}
      footer={
        phase === 'merging' || phase === 'cancelling' ? (
          <Button disabled>{phase === 'merging' ? '合并校验中，请稍候' : '正在取消上传…'}</Button>
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
      {rounds && rounds.length > 1 && onRoundChange && (
        <div style={{ marginBottom: 12 }}>
          <Typography.Text type="secondary" style={{ marginRight: 8 }}>上传到轮次：</Typography.Text>
          <Select
            value={roundId}
            style={{ width: 160 }}
            disabled={busy}
            onChange={(v) => onRoundChange(v as number)}
          >
            {rounds.map((r) => (
              <Select.Option key={r.id} value={r.id}>
                第 {r.roundNo} 轮
              </Select.Option>
            ))}
          </Select>
        </div>
      )}
      <input
        type="file"
        disabled={busy}
        onChange={(e) => {
          setFile(e.target.files?.[0] || null)
          setPercent(0)
        }}
      />
      {file && (
        <div style={{ marginTop: 16 }}>
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
                {phase === 'done' && '完成'}
              </Typography.Text>
            </>
          )}
        </div>
      )}
    </Modal>
  )
}
