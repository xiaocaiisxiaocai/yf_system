import { useCallback, useContext, useEffect, useRef, useState } from 'react'
import { Badge, Button, Empty, Spin, Tabs, Typography } from '@arco-design/web-react'
import { IconLaunch } from '@arco-design/web-react/icon'
import { isAxiosError } from 'axios'
import type { IDockviewPanelProps } from 'dockview-react'
import { useNavigate } from 'react-router-dom'
import http, { type QuietRequestConfig } from '../../api/client'
import type { ApiResponses, Project } from '../../api/types'
import FileTable from '../../components/FileTable'
import MessagePanel from '../../components/MessagePanel'
import ProjectActivityPanel from '../../components/ProjectActivityPanel'
import ProjectWorkflowPanel from '../../components/ProjectWorkflowPanel'
import { useDisconnectedPoll } from '../../hooks/useDisconnectedPoll'
import { useCollaboration } from '../../store/collaboration'
import { SubprojectDockContext } from './subprojectDockContext'
import type { SubprojectPanelParams } from './subprojectDockLayout'

type PaneTab = 'files' | 'messages' | 'activity'

interface Summary {
  unreadMessages: number
  activityRevision?: string
}

export default function SubprojectPane({ params, api }: IDockviewPanelProps<SubprojectPanelParams>) {
  // 面板被复用到另一个子项目时重建局部状态，旧请求不能覆盖新项目。
  return <SubprojectPaneContent key={params.projectId} projectId={params.projectId} panelApi={api} />
}

function SubprojectPaneContent({ projectId: pid, panelApi }: { projectId: number; panelApi: IDockviewPanelProps['api'] }) {
  const navigate = useNavigate()
  const { projects, renderManageActions, onGroupChanged } = useContext(SubprojectDockContext)
  const row = projects.get(pid)
  const [visible, setVisible] = useState(panelApi.isVisible)
  const [project, setProject] = useState<Project | null>(null)
  const [missing, setMissing] = useState(false)
  const [loadError, setLoadError] = useState(false)
  const [summary, setSummary] = useState<Summary>({ unreadMessages: 0 })
  const [tab, setTab] = useState<PaneTab>('files')
  const [target, setTarget] = useState<{ tab: PaneTab; id: number } | null>(null)
  const liveMessages = useCollaboration((state) => state.messageRevisions?.[pid] ?? 0)
  const liveReceipts = useCollaboration((state) => state.receiptRevisions?.[pid] ?? 0)
  const liveActivity = useCollaboration((state) => state.activityRevisions?.[pid] ?? 0)
  const reconnected = useCollaboration((state) => state.reconnectRevision ?? 0)
  const realtimeConnected = useCollaboration((state) => state.realtimeStatus === 'connected')
  const projectSeq = useRef(0)
  const projectInFlight = useRef(0)
  const [projectSettled, setProjectSettled] = useState(0)
  const caughtUpRow = useRef<string | null>(null)
  const summarySeq = useRef(0)
  const summaryAbort = useRef<AbortController | null>(null)
  const rowUpdatedAt = row?.updatedAt
  // 面板不自行跳转：已加载后失去访问时只通知主项目刷新一次，由主项目页统一判断
  // 是整个主项目无权访问（提示并回到列表）还是仅该子项目被删除（由停靠区移除面板）。
  const everLoaded = useRef(false)
  const accessLossReported = useRef(false)
  const onGroupChangedRef = useRef(onGroupChanged)
  useEffect(() => { onGroupChangedRef.current = onGroupChanged })

  // 隐藏在标签页后面的面板不能把留言标记为已读。
  useEffect(() => {
    const disposable = panelApi.onDidVisibilityChange((event) => setVisible(event.isVisible))
    return () => disposable.dispose()
  }, [panelApi])

  const loadProject = useCallback(async (signal?: AbortSignal) => {
    const seq = ++projectSeq.current
    projectInFlight.current += 1
    try {
      const response = await http.get<ApiResponses['GET /projects/{id}']>(`/projects/${pid}`, {
        signal,
        quietNetworkError: true,
        // 已加载后的 403/404 由主项目页统一提示，不再叠加拦截器的逐条错误提示。
        quietClientError: everLoaded.current,
      } as QuietRequestConfig)
      if (seq !== projectSeq.current) return
      everLoaded.current = true
      setProject(response.data as Project)
      setMissing(false)
      setLoadError(false)
    } catch (error: unknown) {
      if (signal?.aborted || seq !== projectSeq.current) return
      const status = isAxiosError(error) ? error.response?.status : undefined
      // 权限丢失或资源不存在时清除旧内容；瞬时错误保留快照并提供重试。
      if (status === 403 || status === 404) {
        setProject(null); setMissing(true)
        if (everLoaded.current && !accessLossReported.current) {
          accessLossReported.current = true
          onGroupChangedRef.current()
        }
      }
      setLoadError(true)
    } finally {
      projectInFlight.current -= 1
      // 请求结束后重新比较主项目行与详情：请求期间到达的较新行不能因“请求进行中”被永久跳过。
      if (projectInFlight.current === 0) setProjectSettled((value) => value + 1)
    }
  }, [pid])

  const loadSummary = useCallback(async () => {
    summaryAbort.current?.abort()
    const controller = new AbortController()
    summaryAbort.current = controller
    const seq = ++summarySeq.current
    try {
      const response = await http.get<ApiResponses['GET /projects/{id}/summary']>(`/projects/${pid}/summary`, {
        signal: controller.signal,
        quietNetworkError: true,
        quietClientError: true,
      } as QuietRequestConfig)
      if (!controller.signal.aborted && seq === summarySeq.current) setSummary(response.data as Summary)
    } catch {
      // 概览是辅助状态；断网或被后续请求取消时保留现有快照。
    } finally {
      if (summaryAbort.current === controller) summaryAbort.current = null
    }
  }, [pid])

  useEffect(() => {
    const controller = new AbortController()
    // The subproject detail is external server state, refetched on realtime or row changes.
    // eslint-disable-next-line react/set-state-in-effect
    void loadProject(controller.signal)
    return () => {
      projectSeq.current += 1
      controller.abort()
    }
  }, [liveActivity, loadProject, reconnected])

  // 主项目列表里的行比已加载的详情新（例如其他人改了状态）时才补拉一次；
  // 本面板自己触发的 refreshAll 会同时刷新详情和主项目，行更新到达时详情已是最新或仍在请求中，不再重复拉取。
  // 同一行时间戳只补拉一次，服务端返回的详情仍旧时不会反复请求。
  const projectUpdatedAt = project?.updatedAt
  useEffect(() => {
    if (!rowUpdatedAt || !projectUpdatedAt || projectInFlight.current > 0) return
    if (!(Date.parse(rowUpdatedAt) > Date.parse(projectUpdatedAt))) return
    if (caughtUpRow.current === rowUpdatedAt) return
    caughtUpRow.current = rowUpdatedAt
    // The group row is newer than the loaded detail, so the detail is refetched from the server.
    // eslint-disable-next-line react/set-state-in-effect
    void loadProject()
  }, [loadProject, projectSettled, projectUpdatedAt, rowUpdatedAt])

  useEffect(() => {
    if (project?.id !== pid) return
    // Summary is the external server state synchronized after the project becomes available.
    // eslint-disable-next-line react/set-state-in-effect
    void loadSummary()
    return () => {
      summarySeq.current += 1
      summaryAbort.current?.abort()
    }
  }, [liveActivity, liveMessages, loadSummary, pid, project?.id, reconnected])

  const summaryRef = useRef(summary)
  useEffect(() => { summaryRef.current = summary })

  // 实时通道未连接时，只轮询可见面板的概览（退避节奏见 useDisconnectedPoll）；
  // 动态指纹变化时更新概览让留言面板重新拉取，并静默刷新子项目详情。
  const pollWhileDisconnected = useCallback(async (signal: AbortSignal) => {
    let response: { data: unknown }
    try {
      response = await http.get<ApiResponses['GET /projects/{id}/summary']>(`/projects/${pid}/summary`, {
        signal,
        quietNetworkError: true,
        quietClientError: true,
      } as QuietRequestConfig)
    } catch (error: unknown) {
      // 概览 403/404 可能意味着失去访问或子项目已删除；重新获取详情确认并通知主项目。
      const status = isAxiosError(error) ? error.response?.status : undefined
      if (!signal.aborted && (status === 403 || status === 404)) void loadProject()
      throw error
    }
    if (signal.aborted) return
    const next = response.data as Summary
    const previous = summaryRef.current
    if (next.activityRevision === previous.activityRevision && next.unreadMessages === previous.unreadMessages) return
    summarySeq.current += 1
    setSummary(next)
    if (next.activityRevision !== previous.activityRevision) await loadProject()
  }, [loadProject, pid])
  useDisconnectedPoll(visible && project?.id === pid && !realtimeConnected, pollWhileDisconnected)

  const refreshAll = useCallback(() => {
    void loadProject()
    void loadSummary()
    onGroupChanged()
  }, [loadProject, loadSummary, onGroupChanged])

  const handleTabChange = useCallback((next: string) => {
    setTab(next as PaneTab)
    setTarget(null)
  }, [])

  const handleActivityNavigate = useCallback((type: string, id: number) => {
    if (!Number.isSafeInteger(id) || id <= 0) return
    const next: PaneTab | null = type === 'FILE' ? 'files' : type === 'MESSAGE' ? 'messages' : null
    if (!next) return
    setTab(next)
    setTarget({ tab: next, id })
  }, [])

  const handleMessageRead = useCallback(() => {
    void loadSummary()
    onGroupChanged()
  }, [loadSummary, onGroupChanged])

  if (!row || missing) {
    return <div className="subproject-pane subproject-pane-state"><Empty description="子项目不存在或没有访问权限" /></div>
  }

  const status = project?.status ?? row.status
  const filesTarget = target?.tab === 'files' ? target.id : undefined
  const messagesTarget = target?.tab === 'messages' ? target.id : undefined
  const unread = project ? summary.unreadMessages : row.unreadMessages

  return (
    <div className="subproject-pane" data-subproject-id={pid}>
      {/* 说明、流程按钮与管理操作合并为一行，把高度留给文件和留言。 */}
      <div className="subproject-pane-toolbar">
        {row.description?.trim()
          ? <Typography.Text className="subproject-pane-description" type="secondary" title={row.description}>{row.description.trim()}</Typography.Text>
          : <span className="subproject-pane-spacer" aria-hidden="true" />}
        {project ? <ProjectWorkflowPanel compact project={project} onChanged={refreshAll} /> : !loadError && <Spin size={16} />}
        <div className="subproject-pane-actions">
          {renderManageActions(row)}
          <Button size="mini" type="text" icon={<IconLaunch />} title="在独立页面打开" aria-label={`在独立页面打开${row.name}`} onClick={() => navigate(`/projects/${pid}`)}>详情</Button>
        </div>
      </div>
      {loadError && (
        <div className="subproject-pane-error" role="status">
          <Typography.Text type="warning">子项目更新失败，当前显示上次数据。</Typography.Text>
          <Button type="text" size="mini" onClick={() => void loadProject()}>重新获取</Button>
        </div>
      )}
      <Tabs className="subproject-pane-tabs" size="small" activeTab={tab} onChange={handleTabChange}>
        <Tabs.TabPane key="files" title="文件">
          <FileTable
            key={`${pid}:${filesTarget ?? 'all'}`}
            projectId={pid}
            projectStatus={status}
            targetId={filesTarget}
            compact
            onProjectChanged={refreshAll}
          />
        </Tabs.TabPane>
        <Tabs.TabPane key="messages" title={unread > 0 ? <span>留言 <Badge count={unread} dot={false} /></span> : '留言'}>
          <MessagePanel
            key={`${pid}:${messagesTarget ?? 'all'}`}
            active={visible && tab === 'messages'}
            projectId={pid}
            projectStatus={status}
            targetId={messagesTarget}
            onRead={handleMessageRead}
            revision={realtimeConnected
              ? `live:${liveMessages}:${reconnected}`
              : `poll:${summary.activityRevision ?? ''}:${liveMessages}:${reconnected}`}
            receiptRevision={`${liveReceipts}:${reconnected}`}
            realtimeConnected={realtimeConnected}
          />
        </Tabs.TabPane>
        <Tabs.TabPane key="activity" title="项目动态">
          <ProjectActivityPanel
            projectId={pid}
            active={visible && tab === 'activity'}
            revision={summary.activityRevision}
            onNavigate={handleActivityNavigate}
          />
        </Tabs.TabPane>
      </Tabs>
    </div>
  )
}
