import { useEffect, useState } from 'react'
import { Button, Card, Space, Spin, Tag } from '@arco-design/web-react'
import { useQueryClient } from '@tanstack/react-query'
import { Link, useLocation, useParams } from 'react-router-dom'
import { fmtTime } from '../api/types'
import LoadError from '../components/LoadError'
import { useOem } from '../oem/OemContext'
import { getAnnouncement, recordAnnouncementRead, type AnnouncementContent } from './announcementReaderApi'

function inaccessible(error: unknown) {
  const status = (error as { response?: { status?: number } } | null)?.response?.status
  return status === 403 || status === 404
}

export function OpenAnnouncement({ id }: { id: string }) {
  const { api, realm } = useOem()
  const queryClient = useQueryClient()
  const [content, setContent] = useState<AnnouncementContent | null>(null)
  const [failure, setFailure] = useState<'access' | 'load' | 'record' | null>(null)
  const [attempt, setAttempt] = useState(0)
  const [recordAttempt, setRecordAttempt] = useState(0)

  useEffect(() => {
    const controller = new AbortController()
    void getAnnouncement(api.http, realm, id, controller.signal).then((result) => {
      if (!controller.signal.aborted) setContent(result)
    }).catch((error: unknown) => {
      if (!controller.signal.aborted) setFailure(inaccessible(error) ? 'access' : 'load')
    })
    return () => controller.abort()
  }, [api, realm, id, attempt])

  // Record only after React has committed the body to the reader's page.
  useEffect(() => {
    if (!content) return
    let active = true
    void recordAnnouncementRead(api.http, realm, content.id).then(() => {
      // The reader may already have returned to the inbox while the read was being saved.
      return queryClient.invalidateQueries({ queryKey: ['announcement-inbox'] })
    }).catch((error: unknown) => {
      if (active) setFailure(inaccessible(error) ? 'access' : 'record')
    })
    return () => { active = false }
  }, [api, realm, content, recordAttempt, queryClient])

  if (failure === 'access') return <p role="alert">此公告不可查看</p>
  if (failure === 'load') return <LoadError message="公告正文加载失败" onRetry={() => {
    setFailure(null)
    setAttempt((value) => value + 1)
  }} />
  if (!content) return <Spin tip="正在加载公告…" />
  return <article>
    <h1 style={{ overflowWrap: 'anywhere' }}>{content.title}</h1>
    <Space wrap>
      {content.pinnedAt && <Tag color="orange">置顶</Tag>}
      <span>{content.departmentName}</span>
      <time dateTime={content.publishedAt ?? undefined}>{fmtTime(content.publishedAt)}</time>
    </Space>
    <p style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{content.body}</p>
    {failure === 'record' && <Space>
      <span role="alert">阅读时间记录失败</span>
      <Button size="small" onClick={() => {
        setFailure(null)
        setRecordAttempt((value) => value + 1)
      }}>重试</Button>
    </Space>}
  </article>
}

export default function AnnouncementDetail() {
  const { id } = useParams()
  const { base, queryScope } = useOem()
  const location = useLocation()
  return <Card className="page-card">
    <Link className="table-link" to={`${base}/announcements${location.search}`}>返回公告中心</Link>
    {id && /^[1-9]\d*$/.test(id)
      ? <OpenAnnouncement key={JSON.stringify([queryScope, id, location.key])} id={id} />
      : <p role="alert">此公告不可查看</p>}
  </Card>
}
