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
  const summarySeq = useRef(0)
  const summaryAbort = useRef<AbortController | null>(null)
  const rowUpdatedAt = row?.updatedAt

  // 隐藏在标签页后面的面板不能把留言标记为已读。
  useEffect(() => {
    const disposable = panelApi.onDidVisibilityChange((event) => setVisible(event.isVisible))
    return () => disposable.dispose()
  }, [panelApi])

  const loadProject = useCallback(async (signal?: AbortSignal) => {
    const seq = ++projectSeq.current
    try {
      const response = await http.get<ApiResponses['GET /projects/{id}']>(`/projects/${pid}`, { signal, quietNetworkError: true } as QuietRequestConfig)
      if (seq !== projectSeq.current) return
      setProject(response.data as Project)
      setMissing(false)
      setLoadError(false)
    } catch (error: unknown) {
      if (signal?.aborted || seq !== projectSeq.current) return
      const status = isAxiosError(error) ? error.response?.status : undefined
      // 权限丢失或资源不存在时清除旧内容；瞬时错误保留快照并提供重试。
      if (status === 403 || status === 404) { setProject(null); setMissing(true) }
      setLoadError(true)
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
  }, [liveActivity, loadProject, reconnected, rowUpdatedAt])

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
        <Typography.Text className="subproject-pane-description" type="secondary" title={row.description || undefined}>
          {row.description?.trim() || '暂无子项目说明'}
        </Typography.Text>
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
