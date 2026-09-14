import { useCallback, useEffect, useRef, useState } from 'react'
import { Button, Empty, Select, Spin, Tag, Timeline, Typography } from '@arco-design/web-react'
import http from '../api/client'
import { type ConfirmSide, PROJECT_STATUS, fmtTime } from '../api/types'

type ActivityType = 'PROJECT' | 'FILE' | 'MESSAGE'

export interface ProjectActivity {
  id: number
  type: ActivityType
  action: string
  actorName: string
  occurredAt: string
  title: string
  summary: string | null
  targetId: number | null
  targetAvailable: boolean
}

interface ActivitySummary {
  status: string
  pendingConfirmation: boolean
  confirmSide: ConfirmSide | null
  lastActivityAt: string | null
}

interface ActivityResponse {
  list: ProjectActivity[]
  nextCursor: string | null
  summary: ActivitySummary
}

interface Props {
  projectId: number
  active?: boolean
  revision?: string
  onNavigate?: (type: ActivityType, targetId: number) => void
}

const EMPTY_SUMMARY: ActivitySummary = {
  status: '',
  pendingConfirmation: false,
  confirmSide: null,
  lastActivityAt: null,
}

const ACTIVITY_TYPES: Array<{ label: string; value: ActivityType }> = [
  { label: '项目', value: 'PROJECT' },
  { label: '文件', value: 'FILE' },
  { label: '留言', value: 'MESSAGE' },
]

const ACTIVITY_TYPE_LABEL: Record<ActivityType, string> = {
  PROJECT: '项目',
  FILE: '文件',
  MESSAGE: '留言',
}

const PROJECT_ACTION_LABEL: Record<string, string> = {
  CREATE: '创建项目',
  CREATED: '创建项目',
  START: '开始项目',
  RESTART: '重新开始项目',
  SUBMIT: '提交验收',
  CONFIRM: '验收通过',
  REJECT: '验收驳回',
  WITHDRAW: '撤回验收申请',
  TERMINATE: '终止项目',
  UPDATE: '更新项目',
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null
}

function isNullablePositiveNumber(value: unknown): value is number | null {
  return value === null || (typeof value === 'number' && Number.isSafeInteger(value) && value > 0)
}

function parseActivity(value: unknown): ProjectActivity | null {
  if (!isRecord(value)) return null
  const type = value.type
  if (typeof value.id !== 'number' || !Number.isSafeInteger(value.id) || value.id <= 0 || typeof type !== 'string') return null
  if (type !== 'PROJECT' && type !== 'FILE' && type !== 'MESSAGE') return null
  if (typeof value.action !== 'string' || typeof value.actorName !== 'string' || typeof value.occurredAt !== 'string') return null
  if (typeof value.title !== 'string' || !isNullablePositiveNumber(value.targetId) || typeof value.targetAvailable !== 'boolean') return null
  if (value.summary !== null && typeof value.summary !== 'string') return null
  return {
    id: value.id,
    type,
    action: value.action,
    actorName: value.actorName,
    occurredAt: value.occurredAt,
    title: value.title,
    summary: value.summary,
    targetId: value.targetId,
    targetAvailable: value.targetAvailable,
  }
}

function parseResponse(value: unknown): ActivityResponse {
  if (!isRecord(value)) throw new Error('项目动态响应格式错误')
  if (!Array.isArray(value.list) || !isRecord(value.summary)) throw new Error('项目动态响应格式错误')
  if (value.nextCursor !== null && typeof value.nextCursor !== 'string') throw new Error('项目动态响应格式错误')
  const parsedList = value.list.map(parseActivity)
  if (parsedList.some((item) => item === null)) throw new Error('项目动态条目格式错误')
  const list = parsedList.filter((item): item is ProjectActivity => item !== null)
  const rawSummary = value.summary
  if (typeof rawSummary.status !== 'string' || typeof rawSummary.pendingConfirmation !== 'boolean'
    || (rawSummary.confirmSide !== null && rawSummary.confirmSide !== 'COMPANY' && rawSummary.confirmSide !== 'SUPPLIER')
    || (rawSummary.lastActivityAt !== null && typeof rawSummary.lastActivityAt !== 'string')) {
    throw new Error('项目动态概览格式错误')
  }
  return {
    list,
    nextCursor: value.nextCursor && value.nextCursor.length > 0 ? value.nextCursor : null,
    summary: {
      status: rawSummary.status,
      pendingConfirmation: rawSummary.pendingConfirmation,
      confirmSide: rawSummary.confirmSide as ConfirmSide | null,
      lastActivityAt: rawSummary.lastActivityAt,
    },
  }
}

function displaySummary(summary: string | null): string {
  if (!summary) return ''
  const characters = Array.from(summary)
  return characters.length > 160 ? `${characters.slice(0, 160).join('')}…` : summary
}

function displayTitle(item: ProjectActivity): string {
  if (item.type === 'PROJECT') return PROJECT_ACTION_LABEL[item.action] || item.title || item.action || '项目动态'
  return item.title || item.action || '项目动态'
}

export default function ProjectActivityPanel({ projectId, active = true, onNavigate, revision = '' }: Props) {
  const [seenRevision, setSeenRevision] = useState(revision)
  const currentRevision = useRef(revision)
  useEffect(() => {
    currentRevision.current = revision
    // The first remote snapshot establishes the baseline for subsequent update notices.
    // eslint-disable-next-line react/set-state-in-effect
    if (!seenRevision && revision) setSeenRevision(revision)
  }, [revision, seenRevision])
  const [filter, setFilter] = useState<ActivityType | undefined>()
  const [list, setList] = useState<ProjectActivity[]>([])
  const [summary, setSummary] = useState<ActivitySummary>(EMPTY_SUMMARY)
  const [nextCursor, setNextCursor] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const [appending, setAppending] = useState(false)
  const [loadError, setLoadError] = useState(false)
  const [appendError, setAppendError] = useState(false)
  const mounted = useRef(true)
  const requestSeq = useRef(0)
  const cursor = useRef<string | null>(null)
  const inFlight = useRef<{ mode: 'initial' | 'append'; queryKey: string } | null>(null)
  const queryKey = `${projectId}:${filter ?? ''}`

  const load = useCallback(async (append: boolean) => {
    if (append && !cursor.current) return
    const currentRequest = inFlight.current
    if (append && currentRequest) return
    if (!append && currentRequest?.mode === 'initial' && currentRequest.queryKey === queryKey) return

    const seq = ++requestSeq.current
    const requestRevision = currentRevision.current
    inFlight.current = { mode: append ? 'append' : 'initial', queryKey }
    if (append) {
      setAppending(true)
      setAppendError(false)
    } else {
      setLoading(true)
      setLoadError(false)
      setAppendError(false)
      setList([])
      setNextCursor(null)
      cursor.current = null
    }

    try {
      const params: { pageSize: number; type?: ActivityType; cursor?: string } = { pageSize: 20 }
      if (filter) params.type = filter
      if (append && cursor.current) params.cursor = cursor.current
      const response = await http.get(`/projects/${projectId}/activities`, { params })
      if (!mounted.current || seq !== requestSeq.current) return
      const data = parseResponse(response.data)
      setList((current) => {
        if (!append) return data.list
        const ids = new Set(current.map((item) => item.id))
        return [...current, ...data.list.filter((item) => !ids.has(item.id))]
      })
      cursor.current = data.nextCursor
      setNextCursor(data.nextCursor)
      setSummary(data.summary)
      if (!append) setSeenRevision(requestRevision)
      setLoadError(false)
      setAppendError(false)
    } catch {
      if (mounted.current && seq === requestSeq.current) {
        if (append) setAppendError(true)
        else setLoadError(true)
      }
    } finally {
      if (mounted.current && seq === requestSeq.current) {
        inFlight.current = null
        setLoading(false)
        setAppending(false)
      }
    }
  }, [filter, projectId, queryKey])

  useEffect(() => {
    if (!active) {
      requestSeq.current += 1
      inFlight.current = null
      return
    }
    // Loading is the external synchronization triggered by entering this tab.
    // eslint-disable-next-line react/set-state-in-effect
    void load(false)
  }, [active, load])

  useEffect(() => {
    mounted.current = true
    return () => {
      mounted.current = false
      requestSeq.current += 1
      inFlight.current = null
    }
  }, [])

  const statusText = PROJECT_STATUS[summary.status]?.text || summary.status || '-'
  const targetLabel = (item: ProjectActivity) => {
    const objectSummary = displaySummary(item.summary) || `${ACTIVITY_TYPE_LABEL[item.type]}${item.targetAvailable ? '' : '已不可用'}`
    return objectSummary
  }

  const renderTarget = (item: ProjectActivity) => {
    if (item.type === 'PROJECT' && !item.summary) return null
    const label = targetLabel(item)
    if (item.type !== 'PROJECT' && item.targetAvailable && item.targetId !== null && onNavigate) {
      return (
        <Button
          type="text"
          size="mini"
          className="project-activity-target-link"
          aria-label={`查看${label}`}
          onClick={() => onNavigate(item.type, item.targetId!)}
        >
          {label}
        </Button>
      )
    }
    return <span className="project-activity-target-text">{label}</span>
  }

  return (
    <div className="project-activity">
      <div className="project-activity-summary" aria-label="项目动态概览">
        <div className="project-activity-summary-item">
          <span className="project-activity-summary-label">当前状态</span>
          <strong className="project-activity-summary-value">{statusText}</strong>
        </div>
        <div className="project-activity-summary-item">
          <span className="project-activity-summary-label">内部验收</span>
          <strong className="project-activity-summary-value">
            {summary.pendingConfirmation ? '待公司内部验收' : '无待验收申请'}
          </strong>
        </div>
        <div className="project-activity-summary-item">
          <span className="project-activity-summary-label">最近动态</span>
          <strong className="project-activity-summary-value project-activity-summary-value--time">
            {summary.lastActivityAt ? fmtTime(summary.lastActivityAt) : '暂无'}
          </strong>
        </div>
      </div>

      <div className="project-activity-toolbar">
        <Typography.Text type="secondary">按时间倒序</Typography.Text>
        <div className="project-activity-controls">
          <Select
            aria-label="动态类型"
            value={filter ?? ''}
            style={{ width: 144 }}
            onChange={(value) => setFilter(value ? value as ActivityType : undefined)}
          >
            <Select.Option value="">全部</Select.Option>
            {ACTIVITY_TYPES.map((item) => <Select.Option key={item.value} value={item.value}>{item.label}</Select.Option>)}
          </Select>
          <Button onClick={() => load(false)} loading={loading}>
            {revision && seenRevision && revision !== seenRevision ? '有新动态，点击查看' : '刷新'}
          </Button>
        </div>
      </div>

      <div className="project-activity-feed">
        {loadError ? (
          <div className="project-activity-error">
            <Typography.Text type="error">项目动态加载失败</Typography.Text>
            <Button size="small" onClick={() => load(false)}>重试</Button>
          </div>
        ) : (
          <Spin loading={loading && list.length === 0}>
            {list.length === 0 && !loading ? (
              <Empty description="暂无项目动态" />
            ) : (
              <>
                <Timeline className="project-activity-timeline">
                  {list.map((item) => (
                    <Timeline.Item key={item.id}>
                      <div className="project-activity-item" data-activity-id={item.id} data-activity-type={item.type}>
                        <div className="project-activity-meta">
                          <Tag size="small" color={item.type === 'FILE' ? 'green' : item.type === 'MESSAGE' ? 'purple' : 'gray'}>
                            {ACTIVITY_TYPE_LABEL[item.type]}
                          </Tag>
                          <Typography.Text className="project-activity-actor">{item.actorName}</Typography.Text>
                          <time className="project-activity-time" dateTime={item.occurredAt}>{fmtTime(item.occurredAt)}</time>
                        </div>
                        <div className="project-activity-title">{displayTitle(item)}</div>
                        {!(item.type === 'PROJECT' && !item.summary) && (
                          <div className="project-activity-target">
                            <span className="project-activity-target-label">对象：</span>
                            {renderTarget(item)}
                          </div>
                        )}
                      </div>
                    </Timeline.Item>
                  ))}
                </Timeline>
                {appendError && (
                  <div className="project-activity-error project-activity-append-error">
                    <Typography.Text type="error">加载失败</Typography.Text>
                    <Button size="small" onClick={() => load(true)}>重试</Button>
                  </div>
                )}
                {nextCursor && !appendError && (
                  <div className="project-activity-more">
                    <Button onClick={() => load(true)} loading={appending}>加载更多</Button>
                  </div>
                )}
              </>
            )}
          </Spin>
        )}
      </div>
    </div>
  )
}
