import { useCallback, useEffect, useRef, useState } from 'react'
import { Button, Card, Empty, Grid, List, Pagination, Result, Spin, Statistic, Tag, Typography } from '@arco-design/web-react'
import { IconRight } from '@arco-design/web-react/icon'
import { Link, useNavigate } from 'react-router-dom'
import http, { type QuietRequestConfig } from '../api/client'
import { fmtTime } from '../api/types'
import { useAuth } from '../store/auth'
import { useCollaboration } from '../store/collaboration'
import '../styles/dashboard-collaboration.css'

interface DashboardMessage {
  id: number
  projectId: number
  projectName?: string
  projectGroupName?: string
  content: string
  senderName?: string
  createdAt: string
  unread: boolean
}

interface Summary {
  projectCount: number
  activeProjectCount: number
  pendingConfirmations: number
  unreadMessages: number
  recentMessages: DashboardMessage[]
}

interface PendingProject {
  id: number
  name: string
  projectGroupName?: string
  status: 'PENDING_CONFIRMATION'
  confirmSide: 'COMPANY'
  updatedAt: string
}

interface PendingProjectPage {
  list: PendingProject[]
  total: number
  page: number
  pageSize: number
}

interface MessagePage {
  list: DashboardMessage[]
  total: number
  page: number
  pageSize: number
}

const PAGE_SIZE = 10

export default function Dashboard() {
  const [data, setData] = useState<Summary | null>(null)
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState(false)
  const [summaryRefreshError, setSummaryRefreshError] = useState(false)
  const summarySeq = useRef(0)
  const hasSummarySnapshot = useRef(false)

  const [pendingData, setPendingData] = useState<PendingProjectPage>({ list: [], total: 0, page: 1, pageSize: PAGE_SIZE })
  const [pendingPage, setPendingPage] = useState(1)
  const [pendingLoading, setPendingLoading] = useState(true)
  const [pendingError, setPendingError] = useState(false)
  const [pendingRefreshError, setPendingRefreshError] = useState(false)
  const pendingSeq = useRef(0)
  const hasPendingSnapshot = useRef(false)

  const [messageData, setMessageData] = useState<MessagePage>({ list: [], total: 0, page: 1, pageSize: PAGE_SIZE })
  const [messagePage, setMessagePage] = useState(1)
  const [unreadOnly, setUnreadOnly] = useState(true)
  const [messageLoading, setMessageLoading] = useState(true)
  const [messageError, setMessageError] = useState(false)
  const [messageRefreshError, setMessageRefreshError] = useState(false)
  const messageSeq = useRef(0)
  const hasMessageSnapshot = useRef(false)

  const mounted = useRef(true)
  const observedRevision = useRef<string | null>(null)
  const user = useAuth((s) => s.user)
  const hasDashboard = useAuth((s) => s.menus.includes('dashboard'))
  const hasOtherMenus = useAuth((s) => s.menus.some(menu => menu !== 'dashboard'))
  const collaborationRevision = useCollaboration((s) => s.revision)
  const collaborationStatus = useCollaboration((s) => s.status)
  const nav = useNavigate()
  const isSupplier = user?.userType === 'SUPPLIER'
  const acceptanceCopy = isSupplier ? {
    cardTitle: '待公司验收子项目',
    cardAction: '查看待公司验收',
    heading: '待公司验收子项目',
    headingEmpty: '已提交并等待公司处理的验收',
    error: '待公司验收子项目加载失败',
    empty: '暂无待公司验收子项目',
    status: '待公司验收',
  } : {
    cardTitle: '待内部验收子项目',
    cardAction: '查看待验收',
    heading: '公司内部待验收子项目',
    headingEmpty: '需要处理的内部验收',
    error: '内部待验收子项目加载失败',
    empty: '暂无内部待验收子项目',
    status: '待公司内部验收',
  }

  useEffect(() => {
    mounted.current = true
    return () => {
      mounted.current = false
      summarySeq.current += 1
      pendingSeq.current += 1
      messageSeq.current += 1
    }
  }, [])

  const fetchSummary = useCallback(async (quiet = false) => {
    if (!hasDashboard) return
    const seq = ++summarySeq.current
    if (!quiet) {
      setLoading(true)
      setLoadError(false)
      setSummaryRefreshError(false)
    }
    try {
      const config: QuietRequestConfig = { quietNetworkError: true }
      const response = await http.get('/dashboard/summary', config)
      if (!mounted.current || seq !== summarySeq.current) return
      setData(response.data as Summary)
      hasSummarySnapshot.current = true
      setLoadError(false)
      setSummaryRefreshError(false)
    } catch {
      if (mounted.current && seq === summarySeq.current) {
        if (quiet && hasSummarySnapshot.current) setSummaryRefreshError(true)
        else setLoadError(true)
      }
    } finally {
      if (mounted.current && seq === summarySeq.current) setLoading(false)
    }
  }, [hasDashboard])

  const fetchPending = useCallback(async (requestedPage: number, quiet = false) => {
    if (!hasDashboard) return
    const seq = ++pendingSeq.current
    if (!quiet) {
      setPendingLoading(true)
      setPendingError(false)
      setPendingRefreshError(false)
    }
    try {
      let page = requestedPage
      while (mounted.current && seq === pendingSeq.current) {
        const config: QuietRequestConfig = {
          params: { page, pageSize: PAGE_SIZE },
          quietNetworkError: true,
        }
        const response = await http.get('/dashboard/pending-projects', config)
        if (!mounted.current || seq !== pendingSeq.current) return
        const next = response.data as PendingProjectPage
        const lastPage = Math.max(1, Math.ceil(next.total / (next.pageSize || PAGE_SIZE)))
        if (page > lastPage) {
          page = lastPage
          setPendingPage(lastPage)
          continue
        }
        setPendingData(next)
        hasPendingSnapshot.current = true
        setPendingError(false)
        setPendingRefreshError(false)
        return
      }
    } catch {
      if (mounted.current && seq === pendingSeq.current) {
        if (quiet && hasPendingSnapshot.current) setPendingRefreshError(true)
        else setPendingError(true)
      }
    } finally {
      if (mounted.current && seq === pendingSeq.current) setPendingLoading(false)
    }
  }, [hasDashboard])

  const fetchMessages = useCallback(async (requestedPage: number, nextUnreadOnly: boolean, quiet = false) => {
    if (!hasDashboard) return
    const seq = ++messageSeq.current
    if (!quiet) {
      setMessageLoading(true)
      setMessageError(false)
      setMessageRefreshError(false)
    }
    try {
      let page = requestedPage
      while (mounted.current && seq === messageSeq.current) {
        const config: QuietRequestConfig = {
          params: { page, pageSize: PAGE_SIZE, unreadOnly: nextUnreadOnly },
          quietNetworkError: true,
        }
        const response = await http.get('/dashboard/messages', config)
        if (!mounted.current || seq !== messageSeq.current) return
        const next = response.data as MessagePage
        const lastPage = Math.max(1, Math.ceil(next.total / (next.pageSize || PAGE_SIZE)))
        if (page > lastPage) {
          page = lastPage
          setMessagePage(lastPage)
          continue
        }
        setMessageData(next)
        hasMessageSnapshot.current = true
        setMessageError(false)
        setMessageRefreshError(false)
        return
      }
    } catch {
      if (mounted.current && seq === messageSeq.current) {
        if (quiet && hasMessageSnapshot.current) setMessageRefreshError(true)
        else setMessageError(true)
      }
    } finally {
      if (mounted.current && seq === messageSeq.current) setMessageLoading(false)
    }
  }, [hasDashboard])

  useEffect(() => {
    if (!hasDashboard) {
      summarySeq.current += 1
      pendingSeq.current += 1
      messageSeq.current += 1
      return
    }
    // Establish the initial snapshots from the three independent server resources.
    // eslint-disable-next-line react/set-state-in-effect
    void fetchSummary()
    void fetchPending(1)
    void fetchMessages(1, true)
  }, [fetchMessages, fetchPending, fetchSummary, hasDashboard])

  useEffect(() => {
    if (!hasDashboard) return
    if (collaborationStatus === 'error') {
      observedRevision.current = ''
      return
    }
    if (observedRevision.current === null) {
      observedRevision.current = collaborationRevision
      return
    }
    if (observedRevision.current === collaborationRevision) return
    observedRevision.current = collaborationRevision
    void fetchSummary(true)
    void fetchPending(pendingPage, true)
    void fetchMessages(messagePage, unreadOnly, true)
  }, [collaborationRevision, collaborationStatus, fetchMessages, fetchPending, fetchSummary, hasDashboard, messagePage, pendingPage, unreadOnly])

  const refreshDashboard = () => {
    void fetchSummary()
    void fetchPending(pendingPage)
    void fetchMessages(messagePage, unreadOnly)
  }

  const changeMessageFilter = (nextUnreadOnly: boolean) => {
    if (nextUnreadOnly === unreadOnly) return
    setUnreadOnly(nextUnreadOnly)
    setMessagePage(1)
    void fetchMessages(1, nextUnreadOnly)
  }

  const cards = [
    { title: '可见主项目', value: data?.projectCount, to: '/projects', action: '查看主项目' },
    { title: '进行中主项目', value: data?.activeProjectCount, to: '/projects', action: '查看主项目' },
    { title: acceptanceCopy.cardTitle, value: data?.pendingConfirmations, href: '#dashboard-pending', action: acceptanceCopy.cardAction },
    { title: '未读留言', value: data?.unreadMessages, href: '#dashboard-messages', action: '查看留言' },
  ]

  if (!hasDashboard) {
    return (
      <Result
        status="403"
        title="工作台不可用"
        subTitle={hasOtherMenus
          ? '当前账号未分配工作台菜单权限，请从导航进入已授权功能，或在右上角维护个人资料。'
          : '请联系管理员分配功能权限，或在右上角维护个人资料。'}
      />
    )
  }

  return (
    <div className="dashboard-collaboration">
      <div className="page-heading dashboard-heading">
        <div>
          <h1>工作台{user ? ` · ${user.realName}` : ''}</h1>
          <Typography.Text type="secondary">{isSupplier ? '跟进待公司验收与未读留言' : '优先处理公司内部验收与未读留言'}</Typography.Text>
        </div>
        <Button size="small" loading={pendingLoading || messageLoading} onClick={refreshDashboard}>刷新工作台</Button>
      </div>

      <Grid.Row className="dashboard-workbench" gutter={[16, 16]}>
        <Grid.Col xs={24} lg={12}>
          <Card
            id="dashboard-pending"
            className="dashboard-task-card dashboard-pending"
            title={(
              <div className="dashboard-section-title">
                <div>
                  <h2>{acceptanceCopy.heading}</h2>
                  <span>{pendingData.total > 0 ? `共 ${pendingData.total} 项` : acceptanceCopy.headingEmpty}</span>
                </div>
                {pendingData.total > 0 && <Tag color="orange">{pendingData.total}</Tag>}
              </div>
            )}
            extra={<Button size="small" loading={pendingLoading} onClick={refreshDashboard}>刷新</Button>}
          >
            {pendingRefreshError && !pendingLoading && (
              <div className="dashboard-stale-notice" role="status">
                <span>更新暂时失败，显示上次数据</span>
                <Button size="mini" onClick={() => { void fetchPending(pendingPage) }}>重试</Button>
              </div>
            )}
            {pendingLoading ? (
              <Spin loading className="dashboard-card-loading" />
            ) : pendingError ? (
              <div className="dashboard-feedback">
                <Typography.Text type="error">{acceptanceCopy.error}</Typography.Text>
                <Button size="small" onClick={() => { void fetchPending(pendingPage) }}>重试</Button>
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
                        title={<Link className="dashboard-pending-link" to={`/projects/${project.id}`}>{project.projectGroupName ? `${project.projectGroupName} / ${project.name}` : project.name}</Link>}
                        description={<Tag color="orange">{acceptanceCopy.status}</Tag>}
                      />
                    </List.Item>
                  )}
                />
                {pendingData.total > pendingData.pageSize && (
                  <div className="dashboard-pagination">
                    <Pagination
                      current={pendingPage}
                      pageSize={pendingData.pageSize || PAGE_SIZE}
                      total={pendingData.total}
                      sizeCanChange={false}
                      onChange={(page) => {
                        setPendingPage(page)
                        void fetchPending(page)
                      }}
                    />
                  </div>
                )}
              </>
            ) : (
              <Empty description={acceptanceCopy.empty} />
            )}
          </Card>
        </Grid.Col>

        <Grid.Col xs={24} lg={12}>
          <Card
            id="dashboard-messages"
            className="dashboard-task-card dashboard-messages"
            title={(
              <div className="dashboard-section-title">
                <div>
                  <h2>项目留言</h2>
                  <span>{unreadOnly ? `共 ${messageData.total} 条未读` : `共 ${messageData.total} 条留言`}</span>
                </div>
              </div>
            )}
            extra={(
              <Button.Group>
                <Button size="mini" type={unreadOnly ? 'primary' : 'secondary'} aria-pressed={unreadOnly} onClick={() => changeMessageFilter(true)}>未读</Button>
                <Button size="mini" type={!unreadOnly ? 'primary' : 'secondary'} aria-pressed={!unreadOnly} onClick={() => changeMessageFilter(false)}>全部</Button>
              </Button.Group>
            )}
          >
            {messageRefreshError && !messageLoading && (
              <div className="dashboard-stale-notice" role="status">
                <span>更新暂时失败，显示上次数据</span>
                <Button size="mini" onClick={() => { void fetchMessages(messagePage, unreadOnly) }}>重试</Button>
              </div>
            )}
            {messageLoading ? (
              <Spin loading className="dashboard-card-loading" />
            ) : messageError ? (
              <div className="dashboard-feedback">
                <Typography.Text type="error">留言加载失败</Typography.Text>
                <Button size="small" onClick={() => { void fetchMessages(messagePage, unreadOnly) }}>重试</Button>
              </div>
            ) : messageData.list.length > 0 ? (
              <>
                <List
                  className="dashboard-message-list"
                  dataSource={messageData.list}
                  render={(message) => {
                    const childLabel = message.projectName || `项目#${message.projectId}`
                    const projectLabel = message.projectGroupName ? `${message.projectGroupName} / ${childLabel}` : childLabel
                    const target = `/projects/${message.projectId}?tab=messages&target=${message.id}`
                    return (
                      <List.Item
                        key={message.id}
                        className={`dashboard-message-item${message.unread ? ' dashboard-message-item--unread' : ''}`}
                        role="link"
                        tabIndex={0}
                        aria-label={`查看${projectLabel}的留言`}
                        onClick={() => nav(target)}
                        onKeyDown={(event) => {
                          if (event.key === 'Enter' || event.key === ' ') {
                            event.preventDefault()
                            nav(target)
                          }
                        }}
                        extra={(
                          <div className="dashboard-message-extra">
                            <Typography.Text type="secondary">{fmtTime(message.createdAt)}</Typography.Text>
                            <IconRight />
                          </div>
                        )}
                      >
                        <List.Item.Meta
                          title={(
                            <span className="dashboard-message-title">
                              {message.unread && <span className="dashboard-message-unread" aria-label="未读" title="未读" />}
                              <Tag color="arcoblue"><span className="dashboard-message-project" title={projectLabel}>{projectLabel}</span></Tag>
                              <span className="dashboard-message-sender" title={message.senderName || '未知用户'}>{message.senderName || '未知用户'}</span>
                            </span>
                          )}
                          description={<span className="dashboard-message-content">{message.content}</span>}
                        />
                      </List.Item>
                    )
                  }}
                />
                {messageData.total > messageData.pageSize && (
                  <div className="dashboard-pagination">
                    <Pagination
                      current={messagePage}
                      pageSize={messageData.pageSize || PAGE_SIZE}
                      total={messageData.total}
                      sizeCanChange={false}
                      onChange={(page) => {
                        setMessagePage(page)
                        void fetchMessages(page, unreadOnly)
                      }}
                    />
                  </div>
                )}
              </>
            ) : (
              <Empty description={unreadOnly ? '暂无未读留言' : '暂无留言'} />
            )}
          </Card>
        </Grid.Col>
      </Grid.Row>

      <section className="dashboard-overview" aria-labelledby="dashboard-overview-title">
        <div className="dashboard-overview-heading">
          <div>
            <h2 id="dashboard-overview-title">业务概览</h2>
            <span>主项目与子项目协作状态汇总</span>
          </div>
          {loadError && data && <Typography.Text type="error">概览刷新失败，当前显示上次结果</Typography.Text>}
          {summaryRefreshError && data && (
            <span className="dashboard-overview-stale" role="status">
              更新暂时失败，显示上次数据
              <Button size="mini" onClick={() => { void fetchSummary() }}>重试</Button>
            </span>
          )}
        </div>
        {loading && !data ? (
          <Spin loading className="dashboard-overview-loading" />
        ) : loadError && !data ? (
          <div className="dashboard-feedback dashboard-overview-error">
            <Typography.Text type="error">加载失败</Typography.Text>
            <Button size="small" onClick={() => { void fetchSummary() }}>重试</Button>
          </div>
        ) : (
          <Grid.Row className="dashboard-stats" gutter={[12, 12]}>
            {cards.map((card) => {
              const content = (
                <Card className="dashboard-stat-card">
                  <Statistic title={card.title} value={card.value ?? '-'} />
                  <span className="dashboard-stat-action">{card.action}<IconRight /></span>
                </Card>
              )
              return (
                <Grid.Col xs={12} xl={6} key={card.title}>
                  {card.to ? (
                    <Link className="dashboard-stat-link" to={card.to} aria-label={`${card.title}：${card.action}`}>{content}</Link>
                  ) : (
                    <a className="dashboard-stat-link" href={card.href} aria-label={`${card.title}：${card.action}`}>{content}</a>
                  )}
                </Grid.Col>
              )
            })}
          </Grid.Row>
        )}
      </section>
    </div>
  )
}
