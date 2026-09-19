import { useCallback, useEffect, useRef, useState } from 'react'
import { Badge, Button, Card, Descriptions, Drawer, Empty, Spin, Table, Tabs, Tag, Typography } from '@arco-design/web-react'
import { useNavigate, useParams, useSearchParams } from 'react-router-dom'
import http, { type QuietRequestConfig } from '../../api/client'
import {
  type PageResp,
  type Project,
  type ProjectCopyFileMapping,
  type ProjectCopyHistory,
  type ProjectCopySummary,
  type ProjectSummary,
  PROJECT_STATUS,
  fmtSize,
  fmtTime,
} from '../../api/types'
import FileTable from '../../components/FileTable'
import MessagePanel from '../../components/MessagePanel'
import ProjectActivityPanel from '../../components/ProjectActivityPanel'
import ProjectWorkflowPanel from '../../components/ProjectWorkflowPanel'
import { useCollaboration } from '../../store/collaboration'
import { isAxiosError } from 'axios'
import './ProjectDetail.css'

interface Summary {
  unreadMessages: number
  activityRevision?: string
}

function detailText(value?: string | null) {
  return value?.trim() || '-'
}

function expectCopyHistory(value: unknown): ProjectCopyHistory {
  if (!value || typeof value !== 'object' || !Array.isArray((value as ProjectCopyHistory).copies)) {
    throw new Error('复制履历接口返回格式错误')
  }
  return value as ProjectCopyHistory
}

function expectCopyFilePage(value: unknown): PageResp<ProjectCopyFileMapping> {
  if (!value || typeof value !== 'object' || !Array.isArray((value as PageResp<ProjectCopyFileMapping>).list)) {
    throw new Error('文件映射接口返回格式错误')
  }
  return value as PageResp<ProjectCopyFileMapping>
}

function ProjectCopyRecord({
  item,
  relation,
  onNavigate,
}: {
  item: ProjectCopySummary
  relation: 'source' | 'copy'
  onNavigate: (projectId: number) => void
}) {
  const [expanded, setExpanded] = useState(false)
  const [data, setData] = useState<PageResp<ProjectCopyFileMapping>>({ list: [], total: 0, page: 1, pageSize: 20 })
  const [page, setPage] = useState(1)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState(false)
  const [reloadKey, setReloadKey] = useState(0)
  const requestSeq = useRef(0)

  useEffect(() => {
    if (!expanded) return
    const seq = ++requestSeq.current
    const controller = new AbortController()
    // eslint-disable-next-line react/set-state-in-effect
    setLoading(true)
    setError(false)
    http.get(`/project-copies/${item.copyId}/files`, {
      params: { page, pageSize: 20 },
      signal: controller.signal,
      quietNetworkError: true,
    } as QuietRequestConfig)
      .then((response) => {
        if (seq !== requestSeq.current) return
        setData(expectCopyFilePage(response.data))
      })
      .catch(() => {
        if (controller.signal.aborted || seq !== requestSeq.current) return
        setError(true)
      })
      .finally(() => {
        if (seq === requestSeq.current) setLoading(false)
      })
    return () => {
      requestSeq.current += 1
      controller.abort()
    }
  }, [expanded, item.copyId, page, reloadKey])

  const sourceLabel = relation === 'source' ? '来源项目文件' : '当前项目文件'
  const targetLabel = relation === 'source' ? '当前项目文件' : '副本项目文件'

  return (
    <article className="project-copy-record">
      <div className="project-copy-record-heading">
        <div className="project-copy-record-title">
          <Tag color={relation === 'source' ? 'arcoblue' : 'purple'}>{relation === 'source' ? '复制来源' : '派生副本'}</Tag>
          <Button type="text" size="small" onClick={() => onNavigate(item.projectId)} title={item.name}>{item.name}</Button>
        </div>
        <Button size="small" type="text" onClick={() => setExpanded((value) => !value)} aria-expanded={expanded}>
          {expanded ? '收起文件映射' : '查看文件映射'}
        </Button>
      </div>
      <div className="project-copy-record-meta">
        <span>操作人：{detailText(item.copiedByName)}</span>
        <span>复制时间：{fmtTime(item.createdAt)}</span>
        <span>文件：{item.fileCount} 个 / {fmtSize(item.totalBytes)}</span>
      </div>
      {expanded && (
        <div className="project-copy-files">
          {error ? (
            <div className="project-copy-load-state" role="status">
              <Typography.Text type="error">文件映射加载失败</Typography.Text>
              <Button size="small" onClick={() => setReloadKey((value) => value + 1)}>重试</Button>
            </div>
          ) : (
            <Table
              size="small"
              rowKey="targetFileId"
              loading={loading}
              data={data.list}
              scroll={{ x: 520 }}
              columns={[
                {
                  title: sourceLabel,
                  dataIndex: 'sourceFileName',
                  width: 240,
                  ellipsis: true,
                  render: (name: string, row: ProjectCopyFileMapping) => (
                    <span className="copy-file-name" title={name}>{name}{row.sourceDeleted && <Tag color="gray">已删除</Tag>}</span>
                  ),
                },
                {
                  title: targetLabel,
                  dataIndex: 'targetFileName',
                  width: 240,
                  ellipsis: true,
                  render: (name: string, row: ProjectCopyFileMapping) => (
                    <span className="copy-file-name" title={name}>{name}{row.targetDeleted && <Tag color="gray">已删除</Tag>}</span>
                  ),
                },
              ]}
              pagination={data.total > data.pageSize ? {
                total: data.total,
                current: page,
                pageSize: data.pageSize || 20,
                sizeCanChange: false,
                onChange: (nextPage) => setPage(nextPage),
              } : false}
              noDataElement={<Empty description={item.fileCount ? '暂无可显示的文件映射' : '复制时没有项目文件'} />}
            />
          )}
        </div>
      )}
    </article>
  )
}

export default function ProjectDetail() {
  const { id } = useParams()
  // 切换项目时隔离操作后的刷新回调和表单状态，旧项目迟回不能覆盖新项目。
  return <ProjectDetailContent key={id} id={id} />
}

function ProjectDetailContent({ id }: { id?: string }) {
  const pid = Number(id)
  const validProjectId = Number.isSafeInteger(pid) && pid > 0
  const navigate = useNavigate()
  const [project, setProject] = useState<Project | null>(null)
  const [summary, setSummary] = useState<Summary>({ unreadMessages: 0 })
  const revision = useCollaboration((state) => state.revision)
  const syncStatus = useCollaboration((state) => state.status)
  const liveMessages = useCollaboration((state) => state.messageRevisions?.[pid] ?? 0)
  const liveReceipts = useCollaboration((state) => state.receiptRevisions?.[pid] ?? 0)
  const reconnected = useCollaboration((state) => state.reconnectRevision ?? 0)
  const realtimeConnected = useCollaboration((state) => state.realtimeStatus === 'connected')
  const projectSeq = useRef(0)
  const summarySeq = useRef(0)
  const historySeq = useRef(0)
  const [historyOpen, setHistoryOpen] = useState(false)
  const [copyHistory, setCopyHistory] = useState<ProjectCopyHistory | null>(null)
  const [historyLoading, setHistoryLoading] = useState(false)
  const [historyError, setHistoryError] = useState(false)
  const [historyReloadKey, setHistoryReloadKey] = useState(0)
  const [loadErrorFor, setLoadErrorFor] = useState<number | null>(null)
  const [siblings, setSiblings] = useState<ProjectSummary[]>([])
  const currentGroupId = project?.id === pid ? project.projectGroupId : undefined
  const [searchParams, setSearchParams] = useSearchParams()
  const requestedTab = searchParams.get('tab')
  const tab = requestedTab === 'files' || requestedTab === 'messages' || requestedTab === 'activity'
    ? requestedTab
    : 'files'
  const rawTargetId = searchParams.get('target')
  const parsedTargetId = rawTargetId ? Number(rawTargetId) : NaN
  const targetId = Number.isSafeInteger(parsedTargetId) && parsedTargetId > 0 ? parsedTargetId : undefined
  const filesTargetId = tab === 'files' ? targetId : undefined
  const messagesTargetId = tab === 'messages' ? targetId : undefined

  const fetchProject = useCallback(async (signal?: AbortSignal) => {
    const r = await http.get(`/projects/${pid}`, { signal })
    return r.data as Project
  }, [pid])

  const loadProject = useCallback(async () => {
    const seq = ++projectSeq.current
    try {
      const next = await fetchProject()
      if (seq !== projectSeq.current) return
      setProject(next)
      setLoadErrorFor(null)
    } catch (error: unknown) {
      if (seq !== projectSeq.current) return
      // 权限丢失或资源不存在时清除旧内容；瞬时错误保留快照并提供重试。
      setLoadErrorFor(pid)
      if (isAxiosError(error) && (error.response?.status === 403 || error.response?.status === 404)) setProject(null)
    }
  }, [fetchProject, pid])

  const fetchSummary = useCallback(async () => {
    const r = await http.get(`/projects/${pid}/summary`)
    return r.data as Summary
  }, [pid])

  const loadSummary = useCallback(async () => {
    const seq = ++summarySeq.current
    try {
      const next = await fetchSummary()
      if (seq === summarySeq.current) setSummary(next)
    } catch {
      /* 拦截器已提示 */
    }
  }, [fetchSummary])

  const handleActivityNavigate = useCallback((type: string, target: number) => {
    if (!Number.isSafeInteger(target) || target <= 0) return
    const nextTab = type === 'FILE' ? 'files' : type === 'MESSAGE' ? 'messages' : null
    if (!nextTab) return
    const next = new URLSearchParams(searchParams)
    next.set('tab', nextTab)
    next.set('target', String(target))
    setSearchParams(next)
  }, [searchParams, setSearchParams])

  const handleTabChange = useCallback((nextTab: string) => {
    const next = new URLSearchParams(searchParams)
    next.set('tab', nextTab)
    next.delete('target')
    setSearchParams(next)
  }, [searchParams, setSearchParams])

  const clearTarget = useCallback(() => {
    const next = new URLSearchParams(searchParams)
    next.delete('target')
    next.set('tab', tab)
    setSearchParams(next)
  }, [searchParams, setSearchParams, tab])

  useEffect(() => {
    if (!validProjectId) return
    let active = true
    const controller = new AbortController()
    const seq = ++projectSeq.current
    fetchProject(controller.signal)
      .then((next) => {
        if (!active || seq !== projectSeq.current) return
        setProject(next)
        setLoadErrorFor(null)
      })
      .catch((error: unknown) => {
        if (active && seq === projectSeq.current) {
          setLoadErrorFor(pid)
          if (isAxiosError(error) && (error.response?.status === 403 || error.response?.status === 404)) setProject(null)
        }
      })
    return () => {
      active = false
      if (projectSeq.current === seq) projectSeq.current += 1
      controller.abort()
    }
  }, [fetchProject, pid, validProjectId, revision, syncStatus])

  useEffect(() => {
    if (!currentGroupId) return
    let active = true
    const controller = new AbortController()
    http.get(`/project-groups/${currentGroupId}`, { signal: controller.signal, quietNetworkError: true } as QuietRequestConfig)
      .then((response) => {
        if (!active) return
        const data = response.data as { projects?: ProjectSummary[] }
        setSiblings(Array.isArray(data.projects) ? data.projects : [])
      })
      .catch(() => { if (active) setSiblings([]) })
    return () => { active = false; controller.abort() }
  }, [currentGroupId, revision, syncStatus])

  const switchProject = useCallback((nextId: number) => {
    if (nextId === pid) return
    navigate(tab === 'files' ? `/projects/${nextId}` : `/projects/${nextId}?tab=${tab}`, { replace: true })
  }, [navigate, pid, tab])

  useEffect(() => {
    if (project?.id !== pid) return
    // Summary is the external server state synchronized after the project becomes available.
    // eslint-disable-next-line react/set-state-in-effect
    void loadSummary()
    return () => {
      summarySeq.current += 1
    }
  }, [project?.id, pid, loadSummary, revision, syncStatus])

  useEffect(() => {
    if (!historyOpen || project?.id !== pid || !project.hasCopyHistory) return
    const seq = ++historySeq.current
    const controller = new AbortController()
    // eslint-disable-next-line react/set-state-in-effect
    setHistoryLoading(true)
    setHistoryError(false)
    http.get(`/projects/${pid}/copy-history`, {
      signal: controller.signal,
      quietNetworkError: true,
    } as QuietRequestConfig)
      .then((response) => {
        if (seq !== historySeq.current) return
        setCopyHistory(expectCopyHistory(response.data))
      })
      .catch(() => {
        if (controller.signal.aborted || seq !== historySeq.current) return
        setHistoryError(true)
      })
      .finally(() => {
        if (seq === historySeq.current) setHistoryLoading(false)
      })
    return () => {
      historySeq.current += 1
      controller.abort()
    }
  }, [historyOpen, historyReloadKey, pid, project?.hasCopyHistory, project?.id])

  if (!validProjectId) return (
    <div style={{ textAlign: 'center', padding: 32 }}>
      <Empty description="项目地址无效" />
      <Button type="primary" onClick={() => navigate('/projects')}>返回项目列表</Button>
    </div>
  )

  if (!project || project.id !== pid)
    return (
      <div style={{ textAlign: 'center', padding: 80 }}>
        {loadErrorFor === pid ? (
          <>
            <Empty description="项目加载失败或没有访问权限" />
            <Button
              type="primary"
              onClick={() => {
                setLoadErrorFor(null)
                loadProject()
              }}
            >
              重试
            </Button>
          </>
        ) : (
          <Spin size={36} />
        )}
      </div>
    )

  return (
    <div className="project-detail-page">
      {loadErrorFor === pid && (
        <div role="status" style={{ marginBottom: 12 }}>
          <Typography.Text type="warning">项目更新失败，当前显示上次获取的数据。</Typography.Text>
          <Button type="text" size="small" onClick={loadProject}>重新获取</Button>
        </div>
      )}
      <Card className="page-card project-detail-summary-card">
        <div className="detail-heading">
          <div>
            <Typography.Text type="secondary">{project.projectGroupName || '主项目'} / 子项目</Typography.Text>
            <h1>{project.name}</h1>
          </div>
          <div className="detail-actions">
            <Tag color={PROJECT_STATUS[project.status]?.color}>
              {PROJECT_STATUS[project.status]?.text || project.status}
            </Tag>
            <Button onClick={() => navigate(project.projectGroupId ? `/project-groups/${project.projectGroupId}` : '/projects')}>返回主项目</Button>
          </div>
        </div>
        {currentGroupId && siblings.length > 1 && (
          <div className="subproject-switch" role="tablist" aria-label="同一主项目下的子项目">
            {siblings.map((sibling) => (
              <button
                key={sibling.id}
                type="button"
                role="tab"
                aria-selected={sibling.id === pid}
                className={`subproject-switch-tab${sibling.id === pid ? ' is-active' : ''}`}
                onClick={() => switchProject(sibling.id)}
              >
                <span className="subproject-switch-name" title={sibling.name}>{sibling.name}</span>
                <Tag size="small" color={PROJECT_STATUS[sibling.status]?.color}>{PROJECT_STATUS[sibling.status]?.text || sibling.status}</Tag>
                {!!sibling.unreadMessages && <Badge count={sibling.unreadMessages} dot={false} />}
              </button>
            ))}
          </div>
        )}
        <Descriptions
          className="project-metadata"
          column={{ xs: 1, sm: 2, md: 3, lg: 4 }}
          data={[
            { label: '工令号', value: detailText(project.workOrderNos?.join('、')) },
            { label: '机型', value: detailText(project.machineModel) },
            { label: 'Robot 厂商', value: detailText(project.robotVendorName) },
            { label: 'Robot 型号', value: detailText(project.robotModelName) },
            {
              label: '负责人',
              value: project.responsibleUserName
                ? `${project.responsibleUserName}${project.responsibleUserEmployeeNo ? `（${project.responsibleUserEmployeeNo}）` : ''}`
                : '-',
            },
            { label: '课别', value: detailText(project.sectionName) },
            { label: '优先级', value: detailText(project.priorityName) },
            { label: '预计完成日期', value: detailText(project.expectedCompletionDate) },
            { label: '供应商', value: project.supplierName || '-' },
            { label: '创建人', value: project.createdByName || '-' },
            { label: '更新时间', value: fmtTime(project.updatedAt) },
          ]}
        />
        {project.hasCopyHistory && (
          <div className="project-copy-reference-bar">
            <div>
              <Typography.Text bold>复制履历</Typography.Text>
              <Typography.Text type="secondary">
                {project.copySource?.name ? `复制自「${project.copySource.name}」` : '可查看项目复制来源和派生副本'}
              </Typography.Text>
            </div>
            <Button size="small" onClick={() => setHistoryOpen(true)}>查看复制履历</Button>
          </div>
        )}
        {project.description && (
          <div className="project-summary-description">
            <span className="project-summary-description-label">项目说明</span>
            <Typography.Ellipsis className="project-description" rows={1} expandable={{ single: true }}
              expandRender={(expanded) => (
                <Button type="text" size="mini" aria-expanded={expanded}
                  aria-label={expanded ? '收起项目说明' : '展开项目说明'}>
                  {expanded ? '收起' : '展开'}
                </Button>
              )}>
              {project.description}
            </Typography.Ellipsis>
          </div>
        )}
        <ProjectWorkflowPanel
          compact
          project={project}
          onChanged={() => {
            loadProject()
            loadSummary()
          }}
        />
      </Card>

      <Card className="page-card project-detail-tabs-card">
        {targetId && tab === 'files' && (
          <div className="project-target-notice">
            <Typography.Text type="secondary">已定位到目标内容</Typography.Text>
            <Button type="text" size="small" onClick={clearTarget}>显示全部</Button>
          </div>
        )}
        <Tabs activeTab={tab} onChange={handleTabChange}>
          <Tabs.TabPane key="files" title="文件">
            <FileTable
              key={`${pid}:${filesTargetId ?? 'all'}`}
              projectId={pid}
              projectStatus={project.status}
              targetId={filesTargetId}
              onOpenCopyHistory={project.hasCopyHistory ? () => setHistoryOpen(true) : undefined}
              onProjectChanged={() => {
                loadProject()
                loadSummary()
              }}
            />
          </Tabs.TabPane>
          <Tabs.TabPane
            key="messages"
            title={
              summary.unreadMessages > 0 ? (
                <span>
                  留言 <Badge count={summary.unreadMessages} dot={false} />
                </span>
              ) : (
                '留言'
              )
            }
          >
            <MessagePanel
              key={pid}
              active={tab === 'messages'}
              projectId={pid}
              projectStatus={project.status}
              targetId={messagesTargetId}
              onRead={loadSummary}
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
              active={tab === 'activity'}
              revision={summary.activityRevision}
              onNavigate={handleActivityNavigate}
            />
          </Tabs.TabPane>
        </Tabs>
      </Card>

      <Drawer
        className="project-copy-history-drawer"
        width="min(720px, 100vw)"
        title="项目复制履历"
        visible={historyOpen}
        onCancel={() => setHistoryOpen(false)}
        footer={null}
        unmountOnExit
      >
        <div className="project-copy-history">
          <div className="project-copy-history-note">
            复制项目会生成独立文件副本并保留复制履历；留言、流程及验收状态、已读回执和通知不会复制。
          </div>
          {historyLoading && !copyHistory ? (
            <div className="project-copy-load-state"><Spin size={28} /></div>
          ) : historyError ? (
            <div className="project-copy-load-state" role="status">
              <Typography.Text type="error">复制履历加载失败</Typography.Text>
              <Button size="small" onClick={() => setHistoryReloadKey((value) => value + 1)}>重试</Button>
            </div>
          ) : copyHistory ? (
            <>
              {copyHistory.hasRestrictedRelations && (
                <div className="project-copy-restricted" role="status">
                  部分复制关联项目当前无权查看，已隐藏其名称和文件信息。
                </div>
              )}
              {copyHistory.source && (
                <section className="project-copy-history-section">
                  <h2>复制来源</h2>
                  <ProjectCopyRecord
                    item={copyHistory.source}
                    relation="source"
                    onNavigate={(projectId) => navigate(`/projects/${projectId}`)}
                  />
                </section>
              )}
              {copyHistory.copies.length > 0 && (
                <section className="project-copy-history-section">
                  <h2>派生副本</h2>
                  <div className="project-copy-history-list">
                    {copyHistory.copies.map((item) => (
                      <ProjectCopyRecord
                        key={item.copyId}
                        item={item}
                        relation="copy"
                        onNavigate={(projectId) => navigate(`/projects/${projectId}`)}
                      />
                    ))}
                  </div>
                </section>
              )}
              {!copyHistory.source && copyHistory.copies.length === 0 && (
                <Empty description={copyHistory.hasRestrictedRelations ? '复制履历受访问权限限制' : '暂无复制履历'} />
              )}
            </>
          ) : (
            <Empty description="暂无复制履历" />
          )}
        </div>
      </Drawer>
    </div>
  )
}
