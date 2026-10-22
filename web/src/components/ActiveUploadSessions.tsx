import { useCallback, useEffect, useState } from 'react'
import { Button, Popconfirm, Space, Typography } from '@arco-design/web-react'
import http, { type QuietRequestConfig } from '../api/client'
import { abortUpload } from '../api/uploads'
import { fmtSize } from '../api/types'
import { useCoalescedRefresh } from '../hooks/useCoalescedRefresh'
import { useCollaboration } from '../store/collaboration'
import type { ApiResponses } from '../api/types'

type UploadSessionList = ApiResponses['GET /projects/{projectId}/upload-sessions']
type UploadSessionItem = UploadSessionList['items'][number]

interface Props {
  projectId: number
  /** Bumped by the parent after a submit/terminate failure or a sibling abort. */
  refreshToken?: number
  compact?: boolean
  /** Opens the existing chunk uploader. The user re-selects the same file; there is no resume API. */
  onResume?: () => void
  onChanged?: () => void
}

function readList(data: unknown): UploadSessionList {
  if (!data || typeof data !== 'object' || !Array.isArray((data as { items?: unknown }).items)) {
    return { total: 0, items: [] }
  }
  const body = data as { total?: unknown; items: UploadSessionItem[] }
  const total = typeof body.total === 'number' ? body.total : body.items.length
  return { total, items: body.items }
}

export default function ActiveUploadSessions({
  projectId, refreshToken = 0, compact = false, onResume, onChanged,
}: Props) {
  const liveActivity = useCollaboration((state) => state.activityRevisions?.[projectId] ?? 0)
  const { refreshKey: liveRefreshKey, begin: beginLive, end: endLive } = useCoalescedRefresh(liveActivity)
  const [sessions, setSessions] = useState<UploadSessionList>({ total: 0, items: [] })
  const [busyId, setBusyId] = useState<string | null>(null)

  const load = useCallback(async (signal?: AbortSignal) => {
    const response = await http.get<UploadSessionList>(`/projects/${projectId}/upload-sessions`, {
      signal,
      quietNetworkError: true,
    } as QuietRequestConfig)
    return readList(response.data)
  }, [projectId])

  useEffect(() => {
    const controller = new AbortController()
    let active = true
    beginLive()
    load(controller.signal).then((next) => {
      if (active) setSessions(next)
    }).catch(() => {
      if (active && !controller.signal.aborted) setSessions({ total: 0, items: [] })
    }).finally(() => {
      if (active) endLive()
    })
    return () => {
      active = false
      controller.abort()
    }
  }, [beginLive, endLive, liveRefreshKey, load, refreshToken])

  const abort = async (sessionId: string) => {
    if (busyId) return
    setBusyId(sessionId)
    try {
      await abortUpload(sessionId)
      const next = await load()
      setSessions(next)
      onChanged?.()
    } catch {
      // 响应拦截器已提示。列表保持不动，便于再次取消。
    } finally {
      setBusyId(null)
    }
  }

  if (sessions.total <= 0) return null
  if (compact) {
    return <Typography.Text type="warning">有 {sessions.total} 个未完成的上传</Typography.Text>
  }
  return (
    <div className="active-upload-sessions" aria-label="未完成的上传">
      <Typography.Text type="warning">有 {sessions.total} 个未完成的上传。继续需要重新选择同一个文件。</Typography.Text>
      <ul>
        {sessions.items.map((item) => (
          <li key={item.sessionId}>
            <span title={item.fileName}>{item.fileName}</span>
            <Typography.Text type="secondary">
              {item.status === 'MERGING' ? '合并中' : '上传中'}
              {' · '}{item.receivedChunks}/{item.totalChunks} 分片 · {fmtSize(item.fileSize)}
              {' · '}{item.uploaderName}
            </Typography.Text>
            <Space>
              {item.canResume && onResume && (
                <Button size="mini" onClick={onResume}>继续</Button>
              )}
              {item.canAbort && (
                <Popconfirm title="取消后已上传的分片会被丢弃，确认取消？" onOk={() => abort(item.sessionId)}>
                  <Button size="mini" status="danger" loading={busyId === item.sessionId}>取消</Button>
                </Popconfirm>
              )}
            </Space>
          </li>
        ))}
      </ul>
    </div>
  )
}
