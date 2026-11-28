import { useEffect, useRef, type Dispatch, type SetStateAction } from 'react'
import { Button, Space } from '@arco-design/web-react'
import { IconUpload } from '@arco-design/web-react/icon'
import { fmtSize } from '../api/types'
import { requestErrorMessage } from '../api/requestError'
import { newClientUuid } from '../utils/clientUuid'
import { ATTACHMENT_ACCEPT, type AnnouncementAsset } from './announcementUpload'
import { AnnouncementAttachmentActions } from './AnnouncementRichBody'

export type AttachmentEntry =
  | { key: string; status: 'ready'; asset: AnnouncementAsset }
  | { key: string; status: 'pending' | 'sending' | 'failed'; file: File; progress: number; error?: string }

interface Props {
  entries: AttachmentEntry[]
  setEntries: Dispatch<SetStateAction<AttachmentEntry[]>>
  upload: (file: File, key: string, signal: AbortSignal, progress: (percent: number) => void) => Promise<AnnouncementAsset>
  disabled: boolean
  announcementId: number | null
}

export default function AnnouncementAttachmentsEditor({ entries, setEntries, upload, disabled, announcementId }: Props) {
  const input = useRef<HTMLInputElement>(null)
  const controllers = useRef(new Map<string, AbortController>())
  useEffect(() => {
    const active = controllers.current
    return () => { active.forEach(value => value.abort()); active.clear() }
  }, [])

  function replace(key: string, next: AttachmentEntry) {
    setEntries(previous => previous.map(row => row.key === key ? next : row))
  }
  async function send(file: File, key: string, controller: AbortController) {
    if (controller.signal.aborted) return
    const progress = (percent: number) => {
      if (!controller.signal.aborted) replace(key, { key, file, status: 'sending', progress: percent })
    }
    progress(0)
    try {
      const asset = await upload(file, key, controller.signal, progress)
      if (!controller.signal.aborted) replace(key, { key, status: 'ready', asset })
    } catch (cause) {
      if (!controller.signal.aborted) replace(key, {
        key, file, progress: 0, status: 'failed', error: requestErrorMessage(cause, '上传失败'),
      })
    } finally {
      controllers.current.delete(key)
    }
  }
  async function add(files: File[]) {
    const tasks = files.map(file => {
      const key = newClientUuid()
      const controller = new AbortController()
      controllers.current.set(key, controller)
      return { key, file, controller }
    })
    setEntries(previous => [...previous, ...tasks.map(({ key, file }) => ({
      key, file, status: 'pending' as const, progress: 0,
    }))])
    for (const task of tasks) await send(task.file, task.key, task.controller)
  }
  function retry(file: File, key: string) {
    if (controllers.current.has(key)) return
    const controller = new AbortController()
    controllers.current.set(key, controller)
    void send(file, key, controller)
  }
  function remove(key: string) {
    controllers.current.get(key)?.abort()
    controllers.current.delete(key)
    setEntries(previous => previous.filter(row => row.key !== key))
  }
  function move(key: string, distance: number) {
    setEntries(previous => {
      const index = previous.findIndex(row => row.key === key)
      const to = index + distance
      if (index < 0 || to < 0 || to >= previous.length) return previous
      const next = [...previous]
      next.splice(to, 0, ...next.splice(index, 1))
      return next
    })
  }
  return <div className="announcement-attachments-editor">
    <input ref={input} aria-label="添加公告附件" type="file" accept={ATTACHMENT_ACCEPT} multiple hidden disabled={disabled}
      onChange={event => {
        const files = Array.from(event.currentTarget.files ?? [])
        event.currentTarget.value = ''
        if (files.length) void add(files)
      }} />
    <Button size="small" disabled={disabled} icon={<IconUpload />} onClick={() => input.current?.click()}>添加附件</Button>
    {entries.length > 0 && <ol aria-label="公告附件顺序">
      {entries.map((entry, index) => {
        const name = entry.status === 'ready' ? entry.asset.fileName : entry.file.name
        const bytes = entry.status === 'ready' ? entry.asset.sizeBytes : entry.file.size
        return <li key={entry.key}>
          <div className="announcement-file-meta"><span>{name}</span><small>{fmtSize(bytes)}</small>
            {entry.status !== 'ready' && <small role={entry.status === 'failed' ? 'alert' : 'status'}>
              {entry.status === 'failed' ? entry.error : entry.status === 'pending' ? '等待上传' : `上传中 ${entry.progress}%`}
            </small>}
          </div>
          <Space size={2} wrap>
            {announcementId != null && entry.status === 'ready' &&
              <AnnouncementAttachmentActions announcementId={announcementId} access="manage" asset={entry.asset} />}
            {entry.status === 'failed' && <Button size="mini" disabled={disabled} onClick={() => retry(entry.file, entry.key)}>重试</Button>}
            <Button size="mini" type="text" aria-label={'上移 ' + name} disabled={disabled || index === 0}
              onClick={() => move(entry.key, -1)}>上移</Button>
            <Button size="mini" type="text" aria-label={'下移 ' + name} disabled={disabled || index === entries.length - 1}
              onClick={() => move(entry.key, 1)}>下移</Button>
            <Button size="mini" type="text" status="danger" aria-label={'移除 ' + name} disabled={disabled}
              onClick={() => remove(entry.key)}>移除</Button>
          </Space>
        </li>
      })}
    </ol>}
  </div>
}
