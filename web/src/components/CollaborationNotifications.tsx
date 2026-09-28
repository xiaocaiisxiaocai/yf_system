import { useCallback, useEffect, useRef, useState } from 'react'
import { Alert, Badge, Button, Drawer, Empty, Pagination, Spin, Tabs, Tag, Typography } from '@arco-design/web-react'
import { IconNotification } from '@arco-design/web-react/icon'
import { useNavigate } from 'react-router-dom'
import { useShallow } from 'zustand/react/shallow'
import http, { type QuietRequestConfig } from '../api/client'
import { fmtTime } from '../api/types'
import { useAuth } from '../store/auth'
import {
  type CollaborationNotification,
  parseCollaborationNotificationPage,
  startCollaborationPolling,
  useCollaboration,
} from '../store/collaboration'
import '../styles/collaboration.css'
import type { ApiResponses } from '../api/types'

const PAGE_SIZE = 20

const TYPE_LABEL: Record<CollaborationNotification['type'], string> = {
  FILE: '文件',
  MESSAGE: '留言',
  PROJECT: '项目',
}

const ACTION_LABEL: Record<string, string> = {
  CREATE: '创建',
  CREATED: '创建',
  UPLOAD: '上传',
  DELETE: '删除',
  SEND: '发送',
  START: '开始项目',
  RESTART: '重新开始',
  SUBMIT: '提交验收',
  CONFIRM: '验收通过',
  REJECT: '验收驳回',
  WITHDRAW: '撤回验收申请',
  TERMINATE: '终止项目',
  UPDATE: '更新',
}

function actionLabel(item: CollaborationNotification): string {
  if (item.type === 'MESSAGE' && item.action === 'CREATE') return '发送留言'
  return ACTION_LABEL[item.action] || item.action
}

function notificationRoute(item: CollaborationNotification): string {
  if (item.type === 'PROJECT') return `/projects/${item.projectId}`
  if (!item.targetAvailable || item.targetId === null) return `/projects/${item.projectId}?tab=activity`
  const tab = item.type === 'FILE' ? 'files' : 'messages'
  return `/projects/${item.projectId}?tab=${tab}&target=${item.targetId}`
}

export default function CollaborationNotifications() {
  const nav = useNavigate()
  const { revision, unreadCount, status, refresh } = useCollaboration(useShallow((state) => ({
    revision: state.revision,
    unreadCount: state.unreadCount,
    status: state.status,
    refresh: state.refresh,
  })))
  const [visible, setVisible] = useState(false)
  const [unreadOnly, setUnreadOnly] = useState(true)
  const [list, setList] = useState<CollaborationNotification[]>([])
  const [page, setPage] = useState(1)
  const [total, setTotal] = useState(0)
  const [loading, setLoading] = useState(false)
  const [listFailed, setListFailed] = useState(false)
  const [marking, setMarking] = useState<Set<number>>(() => new Set())
  const [newContentAvailable, setNewContentAvailable] = useState(false)
  const knownRevision = useRef('')
  const bellRef = useRef<HTMLButtonElement | null>(null)
  const requestSequence = useRef(0)
  const listController = useRef<AbortController | null>(null)
  const markingIds = useRef(new Set<number>())
  // 标记已查看会改变未查看数，从而改变概览 revision；这期间的 revision 变化由 markViewed 自己判定，
  // 避免把“自己刚标记已查看”误报成“有新的协作动态”。
  const markingDepth = useRef(0)

  useEffect(() => startCollaborationPolling(), [])

  useEffect(() => {
    if (!visible || !revision || markingDepth.current > 0) return
    if (!knownRevision.current) {
      knownRevision.current = revision
      return
    }
    if (knownRevision.current !== revision) setNewContentAvailable(true)
  }, [revision, visible])

  useEffect(() => () => {
    requestSequence.current += 1
    listController.current?.abort()
  }, [])

  const loadList = useCallback(async function loadPage(nextPage: number, nextUnreadOnly: boolean) {
    const sequence = ++requestSequence.current
    const requestGeneration = useAuth.getState().generation
    const revisionAtStart = useCollaboration.getState().revision
    listController.current?.abort()
    const controller = new AbortController()
    listController.current = controller
    setLoading(true)
    setListFailed(false)
    try {
      const response = await http.get<ApiResponses['GET /collaboration/notifications']>('/collaboration/notifications', {
        params: { page: nextPage, pageSize: PAGE_SIZE, unreadOnly: nextUnreadOnly },
        signal: controller.signal,
        quietNetworkError: true,
      } as QuietRequestConfig)
      const result = parseCollaborationNotificationPage(response.data)
      if (controller.signal.aborted || sequence !== requestSequence.current
        || useAuth.getState().generation !== requestGeneration) return
      const lastPage = Math.max(1, Math.ceil(result.total / result.pageSize))
      if (result.page > lastPage) {
        await loadPage(lastPage, nextUnreadOnly)
        return
      }
      setList(result.list)
      setPage(result.page)
      setTotal(result.total)
      const currentRevision = useCollaboration.getState().revision
      if (revisionAtStart && currentRevision && revisionAtStart !== currentRevision) {
        knownRevision.current = revisionAtStart
        setNewContentAvailable(true)
      } else {
        knownRevision.current = revisionAtStart || currentRevision
        setNewContentAvailable(false)
      }
    } catch {
      if (!controller.signal.aborted && sequence === requestSequence.current
        && useAuth.getState().generation === requestGeneration) setListFailed(true)
    } finally {
      if (sequence === requestSequence.current) setLoading(false)
    }
  }, [])

  const openDrawer = () => {
    knownRevision.current = revision
    setNewContentAvailable(false)
    setVisible(true)
    setUnreadOnly(true)
    setPage(1)
    void loadList(1, true)
  }

  const closeDrawer = () => {
    setVisible(false)
    requestSequence.current += 1
    listController.current?.abort()
    setLoading(false)
    window.requestAnimationFrame(() => bellRef.current?.focus())
  }

  const markViewed = useCallback(async (ids: number[]): Promise<boolean> => {
    const pendingIds = [...new Set(ids)]
      .filter((id) => Number.isSafeInteger(id) && id > 0 && !markingIds.current.has(id))
      .slice(0, 100)
    if (pendingIds.length === 0) return true
    const requestGeneration = useAuth.getState().generation
    const before = useCollaboration.getState()
    // 标记前列表已与概览同步（没有待刷新的新动态）时，标记后的 revision 变化若没有带来更新的通知，
    // 就只是自己的已查看状态变化，直接同步 knownRevision。
    const syncedBefore = Boolean(knownRevision.current) && knownRevision.current === before.revision
    pendingIds.forEach((id) => markingIds.current.add(id))
    markingDepth.current += 1
    setMarking((current) => new Set([...current, ...pendingIds]))
    try {
      await http.post<ApiResponses['POST /collaboration/reads']>('/collaboration/reads', { ids: pendingIds })
      if (useAuth.getState().generation !== requestGeneration) return false
      const selected = new Set(pendingIds)
      setList((current) => current.map((item) => selected.has(item.id) ? { ...item, read: true } : item))
      await useCollaboration.getState().refresh()
      const after = useCollaboration.getState()
      if (syncedBefore && after.revision && after.latestId <= before.latestId) knownRevision.current = after.revision
      return true
    } catch {
      return false
    } finally {
      markingDepth.current -= 1
      if (markingDepth.current === 0 && useAuth.getState().generation === requestGeneration) {
        const current = useCollaboration.getState().revision
        if (knownRevision.current && current && knownRevision.current !== current) setNewContentAvailable(true)
      }
      if (useAuth.getState().generation === requestGeneration) {
        const selected = new Set(pendingIds)
        setMarking((current) => new Set([...current].filter((id) => !selected.has(id))))
      }
      pendingIds.forEach((id) => markingIds.current.delete(id))
    }
  }, [])

  const openNotification = async (item: CollaborationNotification) => {
    const requestGeneration = useAuth.getState().generation
    if (!item.read) await markViewed([item.id])
    if (useAuth.getState().generation !== requestGeneration) return
    closeDrawer()
    nav(notificationRoute(item))
  }

  const unreadOnPage = list.filter((item) => !item.read).map((item) => item.id)
  const retrySummary = () => { void refresh() }

  return (
    <>
      <div className="collaboration-header-actions">
        {status === 'error' && (
          <div className="collaboration-poll-status" role="status">
            <span>更新暂时中断</span>
            <Button type="text" size="mini" aria-label="重新获取协作通知" onClick={retrySummary}>重试</Button>
          </div>
        )}
        <Badge count={status === 'idle' || status === 'loading' ? 0 : unreadCount} maxCount={99} dot={false}>
          <Button
            ref={bellRef}
            className="collaboration-bell"
            type="text"
            shape="circle"
            aria-label={unreadCount > 0 ? `协作动态通知，${unreadCount} 条未查看` : '协作动态通知'}
            icon={<IconNotification />}
            onClick={openDrawer}
          />
        </Badge>
      </div>

      <Drawer
        className="collaboration-drawer"
        title="协作动态通知"
        width={480}
        visible={visible}
        footer={null}
        unmountOnExit
        onCancel={closeDrawer}
      >
        <div className="collaboration-drawer-content" data-collaboration-drawer="true">
          <Alert
            className="collaboration-meaning-note"
            type="info"
            content="通知“已查看”仅表示已查看这条协作动态，不等同于留言已读；留言需进入项目后查看。未查看通知仅统计近30天。"
          />
          {newContentAvailable && (
            <Alert
              className="collaboration-new-content"
              type="info"
              content={(
                <div className="collaboration-new-content-inner">
                  <span>有新的协作动态</span>
                  <Button type="text" size="small" onClick={() => loadList(1, unreadOnly)}>刷新列表</Button>
                </div>
              )}
            />
          )}

          <div className="collaboration-list-toolbar">
            <Tabs
              activeTab={unreadOnly ? 'unread' : 'all'}
              type="rounded"
              onChange={(key) => {
                const nextUnreadOnly = key === 'unread'
                setUnreadOnly(nextUnreadOnly)
                setPage(1)
                void loadList(1, nextUnreadOnly)
              }}
            >
              <Tabs.TabPane key="unread" title="未查看" />
              <Tabs.TabPane key="all" title="全部" />
            </Tabs>
            <Button
              size="small"
              disabled={unreadOnPage.length === 0 || unreadOnPage.every((id) => marking.has(id))}
              loading={unreadOnPage.some((id) => marking.has(id))}
              onClick={() => { void markViewed(unreadOnPage) }}
            >
              将本页标记为已查看
            </Button>
          </div>

          {listFailed ? (
            <div className="collaboration-list-state" role="alert">
              <Empty description="通知列表加载失败" />
              <Button type="primary" onClick={() => loadList(page, unreadOnly)}>重试</Button>
            </div>
          ) : (
            <Spin loading={loading && list.length === 0} className="collaboration-list-spinner">
              {list.length === 0 && !loading ? (
                <Empty description={unreadOnly ? '暂无未查看通知' : '暂无协作通知'} />
              ) : (
                <div className="collaboration-list" aria-busy={loading}>
                  {list.map((item) => (
                    <article
                      className={`collaboration-notification${item.read ? ' is-read' : ' is-unread'}`}
                      data-notification-id={item.id}
                      data-read={String(item.read)}
                      key={item.id}
                    >
                      <div className="collaboration-notification-heading">
                        <div className="collaboration-notification-tags">
                          <Tag size="small" color={item.type === 'MESSAGE' ? 'arcoblue' : item.type === 'FILE' ? 'green' : 'orange'}>
                            {TYPE_LABEL[item.type]}
                          </Tag>
                          <span>{actionLabel(item)}</span>
                        </div>
                        {!item.read && <span className="collaboration-unread-mark" aria-label="未查看" />}
                      </div>
                      <Button
                        className="collaboration-notification-title"
                        type="text"
                        disabled={marking.has(item.id)}
                        aria-label={`查看通知：${item.title}`}
                        onClick={() => { void openNotification(item) }}
                      >
                        {item.title}
                      </Button>
                      {item.summary && (
                        <Typography.Paragraph className="collaboration-notification-summary" ellipsis={{ rows: 3 }}>
                          {item.summary}
                        </Typography.Paragraph>
                      )}
                      <div className="collaboration-notification-meta">
                        <span>{item.actorName}</span>
                        <span title={item.projectGroupName ? `${item.projectGroupName} / ${item.projectName}` : item.projectName}>
                          {item.projectGroupName ? `${item.projectGroupName} / ${item.projectName}` : item.projectName}
                        </span>
                        <time dateTime={item.occurredAt}>{fmtTime(item.occurredAt)}</time>
                      </div>
                      {!item.read && (
                        <Button
                          className="collaboration-mark-viewed"
                          type="text"
                          size="mini"
                          loading={marking.has(item.id)}
                          aria-label={`标记为已查看：${item.title}`}
                          onClick={() => { void markViewed([item.id]) }}
                        >
                          标记为已查看
                        </Button>
                      )}
                    </article>
                  ))}
                </div>
              )}
            </Spin>
          )}

          {total > PAGE_SIZE && (
            <Pagination
              className="collaboration-pagination"
              current={page}
              pageSize={PAGE_SIZE}
              total={total}
              sizeCanChange={false}
              showTotal
              onChange={(nextPage) => {
                setPage(nextPage)
                void loadList(nextPage, unreadOnly)
              }}
            />
          )}
        </div>
      </Drawer>
    </>
  )
}
