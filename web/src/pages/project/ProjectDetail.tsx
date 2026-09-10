import { useCallback, useEffect, useRef, useState } from 'react'
import { Badge, Button, Card, Descriptions, Empty, Spin, Tabs, Tag, Typography } from '@arco-design/web-react'
import { useNavigate, useParams, useSearchParams } from 'react-router-dom'
import http from '../../api/client'
import { type Project, PROJECT_STATUS, fmtTime } from '../../api/types'
import FileTable from '../../components/FileTable'
import MessagePanel from '../../components/MessagePanel'
import MemberPanel from '../../components/MemberPanel'
import ProjectActivityPanel from '../../components/ProjectActivityPanel'
import ProjectWorkflowPanel from '../../components/ProjectWorkflowPanel'

interface Summary {
  unreadMessages: number
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
  const [loadErrorFor, setLoadErrorFor] = useState<number | null>(null)
  const loadingProjectId = useRef<number | null>(null)
  const [searchParams, setSearchParams] = useSearchParams()
  const requestedTab = searchParams.get('tab')
  const tab = requestedTab === 'files' || requestedTab === 'messages' || requestedTab === 'members' || requestedTab === 'activity'
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
    if (loadingProjectId.current === pid) return
    loadingProjectId.current = pid
    try {
      setProject(await fetchProject())
      setLoadErrorFor(null)
    } catch {
      // 403/404/网络错误：拦截器已提示，这里只落错误态避免永久 Spin
      setLoadErrorFor(pid)
    } finally {
      if (loadingProjectId.current === pid) loadingProjectId.current = null
    }
  }, [fetchProject, pid])

  const fetchSummary = useCallback(async () => {
    const r = await http.get(`/projects/${pid}/summary`)
    return r.data as Summary
  }, [pid])

  const loadSummary = useCallback(async () => {
    try {
      setSummary(await fetchSummary())
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
    loadingProjectId.current = pid
    fetchProject(controller.signal)
      .then((next) => {
        if (!active) return
        setProject(next)
        setLoadErrorFor(null)
      })
      .catch(() => {
        if (active) setLoadErrorFor(pid)
      })
      .finally(() => {
        if (active && loadingProjectId.current === pid) loadingProjectId.current = null
      })
    return () => {
      active = false
      controller.abort()
    }
  }, [fetchProject, pid, validProjectId])

  useEffect(() => {
    if (project?.id !== pid) return
    let active = true
    fetchSummary().then((next) => {
      if (active) setSummary(next)
    }).catch(() => undefined)
    return () => {
      active = false
    }
  }, [project?.id, pid, fetchSummary])

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
    <div className={`project-detail-page${tab === 'activity' ? ' project-detail-page--activity' : ''}`}>
      <Card className="page-card project-detail-summary-card" style={{ marginBottom: 16 }}>
        <div className="detail-heading">
          <div>
            <h1>{project.name}</h1>
          </div>
          <div className="detail-actions">
            <Tag color={PROJECT_STATUS[project.status]?.color}>
              {PROJECT_STATUS[project.status]?.text || project.status}
            </Tag>
            <Button onClick={() => navigate('/projects')}>返回项目列表</Button>
          </div>
        </div>
        <Descriptions
          column={{ xs: 1, sm: 1, md: 2, lg: 3 }}
          data={[
            { label: '供应商', value: project.supplierName || '-' },
            { label: '创建人', value: project.createdByName || '-' },
            { label: '更新时间', value: fmtTime(project.updatedAt) },
            {
              label: '项目说明',
              value: project.description ? (
                <Typography.Ellipsis className="project-description" rows={3} expandable
                  expandRender={(expanded) => (
                    <Button
                      type="text"
                      size="mini"
                      aria-expanded={expanded}
                      aria-label={expanded ? '收起项目说明' : '展开项目说明'}
                    >
                      {expanded ? '收起' : '展开'}
                    </Button>
                  )}>
                  {project.description}
                </Typography.Ellipsis>
              ) : '-',
              span: 3,
            },
          ]}
        />
        <ProjectWorkflowPanel
          project={project}
          onChanged={() => {
            loadProject()
            loadSummary()
          }}
        />
      </Card>

      <Card className={`page-card project-detail-tabs-card${tab === 'activity' ? ' project-detail-tabs-card--activity' : ''}`}>
        {targetId && (tab === 'files' || tab === 'messages') && (
          <div className="project-target-notice">
            <Typography.Text type="secondary">已定位到目标内容</Typography.Text>
            <Button type="text" size="small" onClick={clearTarget}>显示全部</Button>
          </div>
        )}
        <Tabs activeTab={tab} onChange={handleTabChange}>
          <Tabs.TabPane key="files" title="文件">
            <FileTable
              key={`${pid}:${targetId ?? 'all'}`}
              projectId={pid}
              projectStatus={project.status}
              targetId={filesTargetId}
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
              key={`${pid}:${targetId ?? 'all'}`}
              projectId={pid}
              projectStatus={project.status}
              targetId={messagesTargetId}
              onRead={loadSummary}
            />
          </Tabs.TabPane>
          <Tabs.TabPane key="activity" title="项目动态">
            <ProjectActivityPanel
              projectId={pid}
              active={tab === 'activity'}
              onNavigate={handleActivityNavigate}
            />
          </Tabs.TabPane>
          <Tabs.TabPane key="members" title="成员">
            <MemberPanel projectId={pid} supplierName={project.supplierName} projectStatus={project.status} />
          </Tabs.TabPane>
        </Tabs>
      </Card>
    </div>
  )
}
