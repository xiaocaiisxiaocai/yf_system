import { useCallback, useEffect, useRef, useState } from 'react'
import { Badge, Button, Card, Descriptions, Empty, Space, Spin, Tabs, Tag, Typography } from '@arco-design/web-react'
import { useNavigate, useParams, useSearchParams } from 'react-router-dom'
import http from '../../api/client'
import { type Project, type Round, PROJECT_STATUS, fmtTime } from '../../api/types'
import RoundPanel from '../../components/RoundPanel'
import FileTable from '../../components/FileTable'
import MessagePanel from '../../components/MessagePanel'
import MemberPanel from '../../components/MemberPanel'

interface Summary {
  unreadMessages: number
  pendingRounds: number
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
  const [rounds, setRounds] = useState<Round[]>([])
  const [summary, setSummary] = useState<Summary>({ unreadMessages: 0, pendingRounds: 0 })
  const [loadErrorFor, setLoadErrorFor] = useState<number | null>(null)
  const loadingProjectId = useRef<number | null>(null)
  const [searchParams, setSearchParams] = useSearchParams()
  const tab = searchParams.get('tab') || 'rounds'

  const fetchProject = useCallback(async () => {
    const r = await http.get(`/projects/${pid}`)
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

  const fetchRounds = useCallback(async () => {
    const r = await http.get(`/projects/${pid}/rounds`)
    return r.data as Round[]
  }, [pid])

  const loadRounds = useCallback(async () => {
    try {
      setRounds(await fetchRounds())
    } catch {
      /* 拦截器已提示 */
    }
  }, [fetchRounds])

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

  useEffect(() => {
    if (!validProjectId) return
    let active = true
    loadingProjectId.current = pid
    fetchProject()
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
    }
  }, [fetchProject, pid, validProjectId])

  useEffect(() => {
    if (project?.id !== pid) return
    let active = true
    fetchRounds().then((next) => {
      if (active) setRounds(next)
    }).catch(() => undefined)
    fetchSummary().then((next) => {
      if (active) setSummary(next)
    }).catch(() => undefined)
    return () => {
      active = false
    }
  }, [project?.id, pid, fetchRounds, fetchSummary])

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
    <div>
      <Card className="page-card" style={{ marginBottom: 16 }}>
        <Space align="start" style={{ width: '100%', justifyContent: 'space-between' }}>
          <Descriptions
            column={3}
            title={
              <Space>
                <Typography.Title heading={5} style={{ margin: 0 }}>
                  {project.name}
                </Typography.Title>
                <Tag>{project.code}</Tag>
                <Tag color={PROJECT_STATUS[project.status]?.color}>
                  {PROJECT_STATUS[project.status]?.text || project.status}
                </Tag>
              </Space>
            }
            data={[
              { label: '供应商', value: project.supplierName || '-' },
              { label: '创建人', value: project.createdByName || '-' },
              { label: '更新时间', value: fmtTime(project.updatedAt) },
              { label: '项目说明', value: project.description || '-', span: 3 },
            ]}
          />
        </Space>
      </Card>

      <Card className="page-card">
        <Tabs activeTab={tab} onChange={(k) => setSearchParams({ tab: k })}>
          <Tabs.TabPane
            key="rounds"
            title={
              summary.pendingRounds > 0 ? (
                <span>
                  轮次 <Badge count={summary.pendingRounds} dot={false} />
                </span>
              ) : (
                '轮次'
              )
            }
          >
            <RoundPanel
              projectId={pid}
              projectStatus={project.status}
              onChanged={() => {
                loadRounds()
                loadSummary()
                loadProject()
              }}
            />
          </Tabs.TabPane>
          <Tabs.TabPane key="files" title="文件">
            <FileTable projectId={pid} projectStatus={project.status} rounds={rounds} />
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
              projectId={pid}
              projectStatus={project.status}
              rounds={rounds}
              onRead={loadSummary}
            />
          </Tabs.TabPane>
          <Tabs.TabPane key="members" title="成员">
            <MemberPanel projectId={pid} supplierName={project.supplierName} />
          </Tabs.TabPane>
        </Tabs>
      </Card>
    </div>
  )
}
