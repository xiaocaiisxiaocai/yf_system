import { useRef, useState } from 'react'
import { Button, Progress, Space, Typography } from '@arco-design/web-react'
import { IconUpload } from '@arco-design/web-react/icon'
import { fileMd5 } from '../../api/file-hash'
import { fmtSize } from '../../api/types'
import { useOem } from '../OemContext'

interface Entry {
  key: number
  name: string
  size: number
  phase: 'hashing' | 'uploading' | 'merging' | 'done' | 'failed'
  percent: number
  error?: string
}

let nextKey = 0

/**
 * Resumable chunked upload into the draft's quarantine. A content MD5 lets an
 * interrupted upload of the same file resume from the chunks the server already has.
 * Files are uploaded one after another to respect the per-vendor concurrency limit.
 */
export default function OemUploader({ transferId, onUploaded }: { transferId: number; onUploaded: () => void }) {
  const { api } = useOem()
  const input = useRef<HTMLInputElement>(null)
  const [entries, setEntries] = useState<Entry[]>([])
  const busy = entries.some((entry) => entry.phase === 'hashing' || entry.phase === 'uploading' || entry.phase === 'merging')

  const patch = (key: number, value: Partial<Entry>) =>
    setEntries((list) => list.map((entry) => (entry.key === key ? { ...entry, ...value } : entry)))

  const uploadOne = async (file: File, key: number) => {
    try {
      patch(key, { phase: 'hashing' })
      const md5 = await fileMd5(file)
      patch(key, { phase: 'uploading' })
      const init = await api.initUpload(transferId, { fileName: file.name, fileSize: file.size, fileMd5: md5 })
      const done = new Set(init.uploadedChunks)
      for (let index = 0; index < init.totalChunks; index++) {
        if (!done.has(index)) {
          const blob = file.slice(index * init.chunkSize, Math.min(file.size, (index + 1) * init.chunkSize))
          await api.putChunk(init.sessionId, index, blob)
          done.add(index)
        }
        patch(key, { percent: Math.round((done.size / init.totalChunks) * 100) })
      }
      patch(key, { phase: 'merging' })
      await api.merge(init.sessionId)
      patch(key, { phase: 'done', percent: 100 })
      onUploaded()
    } catch (error) {
      const message = (error as { response?: { data?: { message?: string } } })?.response?.data?.message
      patch(key, { phase: 'failed', error: message || '上传失败，可重新选择同一文件从断点续传' })
    }
  }

  const choose = async (files: FileList | null) => {
    if (!files?.length) return
    const queued = Array.from(files).map((file) => ({ file, key: ++nextKey }))
    setEntries((list) => [...list.filter((entry) => entry.phase !== 'done'),
      ...queued.map(({ file, key }) => ({ key, name: file.name, size: file.size, phase: 'hashing' as const, percent: 0 }))])
    for (const { file, key } of queued) await uploadOne(file, key)
    if (input.current) input.current.value = ''
  }

  const label = (entry: Entry) => ({
    hashing: '正在校验文件…',
    uploading: `上传中 ${entry.percent}%`,
    merging: '服务端合并中…',
    done: '已上传，等待安全扫描',
    failed: entry.error ?? '上传失败',
  })[entry.phase]

  return (
    <div className="oem-uploader">
      <input ref={input} type="file" multiple hidden onChange={(event) => void choose(event.target.files)} />
      <Button type="primary" icon={<IconUpload />} loading={busy} onClick={() => input.current?.click()}>
        上传附件
      </Button>
      <Space direction="vertical" style={{ width: '100%', marginTop: entries.length ? 12 : 0 }}>
        {entries.map((entry) => (
          <div key={entry.key}>
            <Typography.Text>{entry.name}</Typography.Text>
            <Typography.Text type="secondary" style={{ marginLeft: 8 }}>{fmtSize(entry.size)}</Typography.Text>
            <Progress
              percent={entry.phase === 'done' ? 100 : entry.percent}
              status={entry.phase === 'failed' ? 'error' : entry.phase === 'done' ? 'success' : 'normal'}
              formatText={() => label(entry)}
            />
          </div>
        ))}
      </Space>
    </div>
  )
}
