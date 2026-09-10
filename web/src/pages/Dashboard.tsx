import { useCallback, useEffect, useRef, useState } from 'react'
import { Button, Card, Empty, Grid, List, Pagination, Spin, Statistic, Tag, Typography } from '@arco-design/web-react'
import { IconRight } from '@arco-design/web-react/icon'
import { Link, useNavigate } from 'react-router-dom'
import http from '../api/client'
import { useAuth } from '../store/auth'
import { fmtTime } from '../api/types'

interface Summary {
  projectCount: number
  activeProjectCount: number
  pendingConfirmations: number
  unreadMessages: number
  recentMessages: {
    id: number
    projectId: number
    projectName?: string
    content: string
    senderName?: string
    createdAt: string
    unread: boolean
  }[]
}

interface PendingProject {
  id: number
  name: string
  status: 'PENDING_CONFIRMATION'
  confirmSide: 'COMPANY' | 'SUPPLIER'
  updatedAt: string
}

interface PendingProjectPage {
  list: PendingProject[]
  total: number
  page: number
  pageSize: number
}

export default function Dashboard() {
  const [data, setData] = useState<Summary | null>(null)
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState(false)
  const [reloadKey, setReloadKey] = useState(0)
  const loadSeq = useRef(0)
  const [pendingData, setPendingData] = useState<PendingProjectPage>({ list: [], total: 0, page: 1, pageSize: 10 })
  const [pendingPage, setPendingPage] = useState(1)
  const [pendingLoading, setPendingLoading] = useState(true)
  const [pendingError, setPendingError] = useState(false)
  const [pendingReloadKey, setPendingReloadKey] = useState(0)
  const pendingSeq = useRef(0)
  const user = useAuth((s) => s.user)
  const nav = useNavigate()

  const load = useCallback(() => {
    setLoading(true)
    setLoadError(false)
    setReloadKey((value) => value + 1)
  }, [])

  useEffect(() => {
    const seq = ++loadSeq.current
    let active = true
    http.get('/dashboard/summary')
      .then((r) => {
        if (active && seq === loadSeq.current) {
          setData(r.data)
          setLoadError(false)
        }
      })
      .catch(() => {
        if (active && seq === loadSeq.current) setLoadError(true)
      })
      .finally(() => {
        if (active && seq === loadSeq.current) setLoading(false)
      })
    return () => {
      active = false
    }
  }, [reloadKey])

  const reloadPending = useCallback(() => {
    setPendingLoading(true)
    setPendingError(false)
    setPendingReloadKey((value) => value + 1)
  }, [])

  const refreshDashboard = () => {
    load()
    reloadPending()
  }

  useEffect(() => {
    const seq = ++pendingSeq.current
    let correctingPage = false
    http.get('/dashboard/pending-projects', { params: { page: pendingPage, pageSize: 10 } })
      .then((r) => {
        if (seq !== pendingSeq.current) return
        const next = r.data as PendingProjectPage
        const lastPage = Math.max(1, Math.ceil(next.total / (next.pageSize || 10)))
        if (pendingPage > lastPage) {
          correctingPage = true
          setPendingPage(lastPage)
          return
        }
        setPendingData(next)
        setPendingError(false)
      })
      .catch(() => {
        if (seq === pendingSeq.current) setPendingError(true)
      })
      .finally(() => {
        if (seq === pendingSeq.current && !correctingPage) setPendingLoading(false)
      })
    return () => {
      pendingSeq.current += 1
    }
  }, [pendingPage, pendingReloadKey])

  const cards = [
    { title: '可见项目', value: data?.projectCount },
    { title: '进行中项目', value: data?.activeProjectCount },
    { title: '待确认项目', value: data?.pendingConfirmations },
    { title: '未读留言', value: data?.unreadMessages },
  ]

  return (
    <div>
      <div className="page-heading">
        <div>
          <h1>工作台{user ? ` · ${user.realName}` : ''}</h1>
        </div>
      </div>
      <Grid.Row className="dashboard-stats" gutter={[16, 16]}>
        {cards.map((c) => (
          <Grid.Col xs={24} sm={12} md={6} key={c.title}>
            <Card className="dashboard-stat-card">
              <Statistic title={c.title} value={c.value ?? '-'} />
            </Card>
          </Grid.Col>
        ))}
      </Grid.Row>
      <Card
        className="dashboard-pending"
        style={{ marginTop: 16 }}
        title={
          <div className="section-heading">
            <div>
              <h2>待我方确认的项目</h2>
            </div>
          </div>
        }
        extra={<Button size="small" loading={pendingLoading} onClick={refreshDashboard}>刷新</Button>}
      >
        {pendingLoading ? (
          <Spin loading style={{ width: '100%', minHeight: 80 }} />
        ) : pendingError ? (
          <div className="dashboard-pending-feedback">
            <Typography.Text type="error">待确认项目加载失败</Typography.Text>
            <Button size="small" onClick={reloadPending}>重试</Button>
          </div>
        ) : pendingData.list.length > 0 ? (
          <>
            <List
              className="dashboard-pending-list"
              dataSource={pendingData.list}
              render={(project) => (
                <List.Item
                  key={project.id}
                  extra={<Typography.Text type="secondary">更新于 {fmtTime(project.updatedAt)}</Typography.Text>}
                >
                  <List.Item.Meta
                    title={<Link className="dashboard-pending-link" to={`/projects/${project.id}`}>{project.name}</Link>}
                    description={(
                      <Tag color="orange">
                        {project.confirmSide === 'COMPANY' ? '待公司确认' : '待供应商确认'}
                      </Tag>
                    )}
                  />
                </List.Item>
              )}
            />
            {pendingData.total > 10 && (
              <div className="dashboard-pending-pagination">
                <Pagination
                  current={pendingPage}
                  pageSize={10}
                  total={pendingData.total}
                  onChange={(page) => {
                    setPendingLoading(true)
                    setPendingError(false)
                    setPendingPage(page)
                  }}
                />
              </div>
            )}
          </>
        ) : (
          <Empty description="暂无待确认项目" />
        )}
      </Card>
      <Card
        className="dashboard-latest"
        style={{ marginTop: 16 }}
        title={
          <div className="section-heading">
            <div>
              <h2>最新留言</h2>
            </div>
          </div>
        }
        extra={<span className="dashboard-latest-limit">近 5 条</span>}
      >
        {loading ? (
          <Spin loading style={{ width: '100%', minHeight: 80 }} />
        ) : loadError ? (
          <div style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '24px 0' }}>
            <Typography.Text type="error">加载失败</Typography.Text>
            <Button size="small" onClick={load}>重试</Button>
          </div>
        ) : data && data.recentMessages.length > 0 ? (
          <List
            dataSource={data.recentMessages}
            render={(m) => (
              <List.Item
                key={m.id}
                className={`dashboard-message-item${m.unread ? ' dashboard-message-item--unread' : ''}`}
                role="link"
                tabIndex={0}
                onClick={() => nav(`/projects/${m.projectId}?tab=messages`)}
                onKeyDown={(event) => {
                  if (event.key === 'Enter' || event.key === ' ') {
                    event.preventDefault()
                    nav(`/projects/${m.projectId}?tab=messages`)
                  }
                }}
                extra={(
                  <div className="dashboard-message-extra">
                    <Typography.Text type="secondary">{fmtTime(m.createdAt)}</Typography.Text>
                    <IconRight />
                  </div>
                )}
              >
                <List.Item.Meta
                  title={
                    <span className="dashboard-message-title">
                      {m.unread && <span className="dashboard-message-unread" aria-label="未读" title="未读" />}
                      <Tag color="arcoblue">
                        {m.projectName || `项目#${m.projectId}`}
                      </Tag>
                      <span className="dashboard-message-sender">{m.senderName || '未知用户'}</span>
                    </span>
                  }
                  description={m.content}
                />
              </List.Item>
            )}
          />
        ) : (
          <Empty description="暂无留言" />
        )}
      </Card>
    </div>
  )
}
