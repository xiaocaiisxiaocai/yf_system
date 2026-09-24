import { useEffect, useMemo, useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Button, Card, Collapse, Empty, Grid, List, Pagination, Result, Spin, Statistic, Tag, Typography } from '@arco-design/web-react'
import { IconRight } from '@arco-design/web-react/icon'
import { Link, useNavigate } from 'react-router-dom'
import http, { type QuietRequestConfig } from '../api/client'
import { createSessionQueryScope, queryClient } from '../api/queryClient'
import { fmtTime } from '../api/types'
import { useAuth } from '../store/auth'
import { useCollaboration } from '../store/collaboration'
import '../styles/dashboard-collaboration.css'
import type { ApiResponses } from '../api/types'

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
  projectGroupId?: number
  projectGroupName?: string
  status: 'PENDING_CONFIRMATION'
  confirmSide: 'COMPANY'
  updatedAt: string
}

interface PendingProjectGroup {
  groupId: number
  groupName: string
  items: PendingProject[]
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

function isAuthorizationError(error: unknown): boolean {
  if (!error || typeof error !== 'object') return false
  const candidate = error as { status?: unknown; response?: { status?: unknown } }
  const status = candidate.response?.status ?? candidate.status
  return status === 401 || status === 403
}

export default function Dashboard() {
  const [pendingPage, setPendingPage] = useState(1)
  const [collapsedPendingGroups, setCollapsedPendingGroups] = useState<Set<number>>(() => new Set())
  const [messagePage, setMessagePage] = useState(1)
  const [unreadOnly, setUnreadOnly] = useState(true)
  const observedRevision = useRef<string | null>(null)
  const auth = useAuth()
  const [sessionUserId, sessionGeneration, sessionGrants] = createSessionQueryScope(auth)
  const sessionScope = useMemo(
    () => [sessionUserId, sessionGeneration, sessionGrants] as const,
    [sessionGeneration, sessionGrants, sessionUserId],
  )
  const user = auth.user
  const hasDashboardMenu = auth.menus.includes('dashboard')
  const hasProjectAccess = auth.permissions.includes('project:list')
  const hasDashboard = hasDashboardMenu && hasProjectAccess
  const hasOtherMenus = auth.menus.some(menu => menu !== 'dashboard')
  const collaborationRevision = useCollaboration((s) => s.revision)
  const collaborationStatus = useCollaboration((s) => s.status)
  const nav = useNavigate()
  const dashboardQueryKey = useMemo(() => ['dashboard', sessionScope] as const, [sessionScope])
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

  const summaryQuery = useQuery({
    queryKey: [...dashboardQueryKey, 'summary'],
    enabled: hasDashboard,
    queryFn: async ({ signal }) => {
      const config: QuietRequestConfig = { signal, quietNetworkError: true }
      const response = await http.get<ApiResponses['GET /dashboard/summary']>('/dashboard/summary', config)
      return response.data as Summary
    },
  }, queryClient)
  const pendingQuery = useQuery({
    queryKey: [...dashboardQueryKey, 'pending-projects', pendingPage],
    enabled: hasDashboard,
    queryFn: async ({ signal }) => {
      const config: QuietRequestConfig = {
        params: { page: pendingPage, pageSize: PAGE_SIZE }, signal, quietNetworkError: true,
      }
      const response = await http.get<ApiResponses['GET /dashboard/pending-projects']>('/dashboard/pending-projects', config)
      return response.data as PendingProjectPage
    },
  }, queryClient)
  const messageQuery = useQuery({
    queryKey: [...dashboardQueryKey, 'messages', messagePage, unreadOnly],
    enabled: hasDashboard,
    queryFn: async ({ signal }) => {
      const config: QuietRequestConfig = {
        params: { page: messagePage, pageSize: PAGE_SIZE, unreadOnly }, signal, quietNetworkError: true,
      }
      const response = await http.get<ApiResponses['GET /dashboard/messages']>('/dashboard/messages', config)
      return response.data as MessagePage
    },
  }, queryClient)

  const dashboardAuthorizationError = isAuthorizationError(summaryQuery.error)
    || isAuthorizationError(pendingQuery.error)
    || isAuthorizationError(messageQuery.error)
  const data = dashboardAuthorizationError ? undefined : summaryQuery.data
  const pendingData = !dashboardAuthorizationError && pendingQuery.data
    ? pendingQuery.data
    : { list: [], total: 0, page: pendingPage, pageSize: PAGE_SIZE }
  const messageData = !dashboardAuthorizationError && messageQuery.data
    ? messageQuery.data
    : { list: [], total: 0, page: messagePage, pageSize: PAGE_SIZE }
  const loading = summaryQuery.isFetching
  const loadError = summaryQuery.isError && !summaryQuery.data
  const summaryRefreshError = summaryQuery.isRefetchError && !!summaryQuery.data
  const pendingLoading = pendingQuery.isFetching
  const pendingError = pendingQuery.isError && !pendingQuery.data
  const pendingRefreshError = pendingQuery.isRefetchError && !!pendingQuery.data
  const messageLoading = messageQuery.isFetching
  const messageError = messageQuery.isError && !messageQuery.data
  const messageRefreshError = messageQuery.isRefetchError && !!messageQuery.data
  const refetchSummary = summaryQuery.refetch
  const refetchPending = pendingQuery.refetch
  const refetchMessages = messageQuery.refetch

  useEffect(() => {
    if (!dashboardAuthorizationError) return
    void queryClient.cancelQueries({ queryKey: dashboardQueryKey })
    queryClient.removeQueries({ queryKey: dashboardQueryKey })
  }, [dashboardAuthorizationError, dashboardQueryKey])

  const pendingGroups = useMemo<PendingProjectGroup[]>(() => {
    const order: number[] = []
    const groups = new Map<number, PendingProjectGroup>()
    for (const project of pendingData.list) {
      const groupId = project.projectGroupId ?? 0
      let group = groups.get(groupId)
      if (!group) {
        group = { groupId, groupName: project.projectGroupName || '未命名主项目', items: [] }
        groups.set(groupId, group)
        order.push(groupId)
      }
      group.items.push(project)
    }
    return order.map((groupId) => groups.get(groupId)!)
  }, [pendingData.list])
  const pendingOpenKeys = useMemo(
    () => pendingGroups
      .filter((group) => !collapsedPendingGroups.has(group.groupId))
      .map((group) => String(group.groupId)),
    [collapsedPendingGroups, pendingGroups],
  )

  useEffect(() => {
    const next = pendingQuery.data
    if (!next) return
    const lastPage = Math.max(1, Math.ceil(next.total / (next.pageSize || PAGE_SIZE)))
    // eslint-disable-next-line react-hooks/set-state-in-effect -- the server can shrink the last page between requests
    if (pendingPage > lastPage) setPendingPage(lastPage)
  }, [pendingPage, pendingQuery.data])

  useEffect(() => {
    const next = messageQuery.data
    if (!next) return
    const lastPage = Math.max(1, Math.ceil(next.total / (next.pageSize || PAGE_SIZE)))
    // eslint-disable-next-line react-hooks/set-state-in-effect -- the server can shrink the last page between requests
    if (messagePage > lastPage) setMessagePage(lastPage)
  }, [messagePage, messageQuery.data])

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
    void refetchSummary()
    void refetchPending()
    void refetchMessages()
  }, [collaborationRevision, collaborationStatus, hasDashboard, refetchMessages, refetchPending, refetchSummary])

  const refreshDashboard = () => {
    void refetchSummary()
    void refetchPending()
    void refetchMessages()
  }

  const changeMessageFilter = (nextUnreadOnly: boolean) => {
    if (nextUnreadOnly === unreadOnly) return
    setUnreadOnly(nextUnreadOnly)
    setMessagePage(1)
  }

  const cards = [
    { title: '可见主项目', value: data?.projectCount, to: '/projects', action: '查看主项目' },
    { title: '进行中主项目', value: data?.activeProjectCount, to: '/projects', action: '查看主项目' },
    { title: acceptanceCopy.cardTitle, value: data?.pendingConfirmations, href: '#dashboard-pending', action: acceptanceCopy.cardAction },
    { title: '近30天未读留言', value: data?.unreadMessages, href: '#dashboard-messages', action: '查看留言' },
  ]

  if (!hasDashboard) {
    return (
      <Result
        status="403"
        title="工作台不可用"
        subTitle={hasDashboardMenu && !hasProjectAccess
          ? '当前账号未分配项目查看权限，请从导航进入已授权功能，或联系管理员调整权限。'
          : hasOtherMenus
          ? '当前账号未分配工作台菜单权限，请从导航进入已授权功能，或在右上角维护个人资料。'
          : '请联系管理员分配功能权限，或在右上角维护个人资料。'}
      />
    )
  }

  if (dashboardAuthorizationError) {
    return (
      <Result
        status="403"
        title="工作台不可用"
        subTitle="当前会话无权读取工作台数据，请重新登录或联系管理员确认权限。"
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
                <Button size="mini" onClick={() => { void refetchPending() }}>重试</Button>
              </div>
            )}
            {pendingLoading ? (
              <Spin loading className="dashboard-card-loading" />
            ) : pendingError ? (
              <div className="dashboard-feedback">
                <Typography.Text type="error">{acceptanceCopy.error}</Typography.Text>
                <Button size="small" onClick={() => { void refetchPending() }}>重试</Button>
              </div>
            ) : pendingData.list.length > 0 ? (
              <>
                <Collapse
                  key={pendingPage}
                  className="dashboard-pending-groups"
                  bordered={false}
                  activeKey={pendingOpenKeys}
                  onChange={(_key, keys) => {
                    const open = new Set(keys)
                    setCollapsedPendingGroups(new Set(
                      pendingGroups
                        .filter((group) => !open.has(String(group.groupId)))
                        .map((group) => group.groupId),
                    ))
                  }}
                >
                  {pendingGroups.map((group) => (
                    <Collapse.Item
                      key={group.groupId}
                      name={String(group.groupId)}
                      header={(
                        <span className="dashboard-pending-group-header">
                          <span className="dashboard-pending-group-name">{group.groupName}</span>
                          <Tag color="orange">{group.items.length}</Tag>
                        </span>
                      )}
                    >
                      <List
                        className="dashboard-pending-list"
                        dataSource={group.items}
                        render={(project) => (
                          <List.Item
                            key={project.id}
                            extra={<Typography.Text type="secondary">更新于 {fmtTime(project.updatedAt)}</Typography.Text>}
                          >
                            <List.Item.Meta
                              title={(
                                <Link
                                  className="dashboard-pending-link"
                                  to={`/projects/${project.id}`}
                                  aria-label={project.projectGroupName ? `${project.projectGroupName} / ${project.name}` : project.name}
                                >
                                  {project.name}
                                </Link>
                              )}
                              description={<Tag color="orange">{acceptanceCopy.status}</Tag>}
                            />
                          </List.Item>
                        )}
                      />
                    </Collapse.Item>
                  ))}
                </Collapse>
                {pendingData.total > pendingData.pageSize && (
                  <div className="dashboard-pagination">
                    <Pagination
                      current={pendingPage}
                      pageSize={pendingData.pageSize || PAGE_SIZE}
                      total={pendingData.total}
                      sizeCanChange={false}
                      onChange={(page) => {
                        setPendingPage(page)
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
                  <span>{unreadOnly ? `近30天共 ${messageData.total} 条未读` : `近30天共 ${messageData.total} 条留言`}</span>
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
                <Button size="mini" onClick={() => { void refetchMessages() }}>重试</Button>
              </div>
            )}
            {messageLoading ? (
              <Spin loading className="dashboard-card-loading" />
            ) : messageError ? (
              <div className="dashboard-feedback">
                <Typography.Text type="error">留言加载失败</Typography.Text>
                <Button size="small" onClick={() => { void refetchMessages() }}>重试</Button>
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
              <Button size="mini" onClick={() => { void refetchSummary() }}>重试</Button>
            </span>
          )}
        </div>
        {loading && !data ? (
          <Spin loading className="dashboard-overview-loading" />
        ) : loadError && !data ? (
          <div className="dashboard-feedback dashboard-overview-error">
            <Typography.Text type="error">加载失败</Typography.Text>
            <Button size="small" onClick={() => { void refetchSummary() }}>重试</Button>
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
