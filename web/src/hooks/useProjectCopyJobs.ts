import { useCallback, useEffect, useReducer, useRef } from 'react'
import {
  isCopyJob, isCopyJobAbort, isCopyJobAccessError, listProjectCopyJobs,
} from '../api/copyJobs'
import type { ProjectCopyJob } from '../api/types'

export const COPY_JOB_POLL_INTERVAL_MS = 1500

export function isCopyJobActive(job: ProjectCopyJob): boolean {
  return job.status === 'pending' || job.status === 'running'
}

export interface ProjectCopyJobsState {
  jobs: ProjectCopyJob[]
  loading: boolean
  error: boolean
  unavailable: boolean
  ready: boolean
  refresh: () => Promise<void>
  upsert: (job: ProjectCopyJob) => void
}

interface CopyJobsSnapshot {
  scopeKey: string
  jobs: ProjectCopyJob[]
  loading: boolean
  error: boolean
  unavailable: boolean
  ready: boolean
}

type CopyJobsAction =
  | { type: 'reset'; scopeKey: string; enabled: boolean }
  | { type: 'loading' }
  | { type: 'success'; jobs: ProjectCopyJob[] }
  | { type: 'error'; accessDenied: boolean }
  | { type: 'upsert'; job: ProjectCopyJob }

function copyJobsReducer(state: CopyJobsSnapshot, action: CopyJobsAction): CopyJobsSnapshot {
  switch (action.type) {
    case 'reset':
      return {
        scopeKey: action.scopeKey,
        jobs: [],
        loading: action.enabled,
        error: false,
        unavailable: false,
        ready: false,
      }
    case 'loading':
      return { ...state, loading: true }
    case 'success':
      return { ...state, jobs: action.jobs, loading: false, error: false, unavailable: false, ready: true }
    case 'error':
      if (action.accessDenied) {
        return { ...state, jobs: [], loading: false, error: false, unavailable: true, ready: false }
      }
      // A transient failure keeps the last authoritative snapshot visible. On
      // the first load, ready remains false but loading is still false so the
      // user can invoke the explicit retry action.
      return { ...state, loading: false, error: true }
    case 'upsert': {
      const index = state.jobs.findIndex((item) => item.jobId === action.job.jobId)
      if (index < 0) return { ...state, jobs: [action.job, ...state.jobs], loading: false, error: false, unavailable: false }
      const jobs = state.jobs.slice()
      jobs[index] = action.job
      return { ...state, jobs, loading: false, error: false, unavailable: false }
    }
  }
}

/**
 * 按主项目作用域加载并轮询持久化复制任务。generation 变化时会先清空旧
 * 快照并 abort 旧请求，避免路由、权限或刷新后的迟到响应污染当前页面。
 */
export function useProjectCopyJobs(
  groupId: number,
  enabled: boolean,
  generation: number,
): ProjectCopyJobsState {
  const scopeKey = `${groupId}:${generation}:${enabled ? 'enabled' : 'disabled'}`
  const [snapshot, dispatch] = useReducer(copyJobsReducer, {
    scopeKey,
    jobs: [],
    loading: enabled,
    error: false,
    unavailable: false,
    ready: false,
  })
  const requestRef = useRef<{ sequence: number; controller: AbortController } | null>(null)
  const sequenceRef = useRef(0)
  const upsertVersionRef = useRef(0)

  const refresh = useCallback(async () => {
    if (!enabled || requestRef.current) return
    const sequence = ++sequenceRef.current
    const upsertVersion = upsertVersionRef.current
    const controller = new AbortController()
    requestRef.current = { sequence, controller }
    dispatch({ type: 'loading' })
    try {
      const response = await listProjectCopyJobs(groupId, controller.signal)
      if (controller.signal.aborted || sequence !== sequenceRef.current) return
      const next = response.data?.jobs
      if (!Array.isArray(next) || !next.every(isCopyJob)) throw new Error('复制任务列表响应格式无效')
      // POST upsert 后，忽略开始于旧快照的迟到列表；下一轮轮询再以服务端
      // 的完整结果为权威，避免旧响应覆盖刚接受的任务。
      if (upsertVersionRef.current !== upsertVersion) return
      dispatch({ type: 'success', jobs: next })
    } catch (requestError) {
      if (controller.signal.aborted || sequence !== sequenceRef.current || isCopyJobAbort(requestError)) return
      if (upsertVersionRef.current !== upsertVersion) return
      dispatch({ type: 'error', accessDenied: isCopyJobAccessError(requestError) })
    } finally {
      if (sequence === sequenceRef.current) {
        requestRef.current = null
      }
    }
  }, [enabled, groupId])

  useEffect(() => {
    sequenceRef.current += 1
    requestRef.current?.controller.abort()
    requestRef.current = null
    upsertVersionRef.current = 0
    dispatch({ type: 'reset', scopeKey, enabled })
    if (!enabled) return undefined
    void refresh()
    return () => {
      sequenceRef.current += 1
      requestRef.current?.controller.abort()
      requestRef.current = null
    }
  }, [enabled, scopeKey, refresh])

  const scopeMatches = snapshot.scopeKey === scopeKey
  const ready = enabled && scopeMatches && snapshot.ready
  const jobs = ready && !snapshot.unavailable ? snapshot.jobs : []
  const active = jobs.some(isCopyJobActive)
  useEffect(() => {
    if (!ready || !active) return undefined
    const timer = window.setInterval(() => { void refresh() }, COPY_JOB_POLL_INTERVAL_MS)
    return () => window.clearInterval(timer)
  }, [active, ready, refresh])

  const upsert = useCallback((job: ProjectCopyJob) => {
    if (!enabled) return
    upsertVersionRef.current += 1
    dispatch({ type: 'upsert', job })
  }, [enabled])

  return {
    jobs,
    loading: enabled && (!scopeMatches || snapshot.loading || (!snapshot.ready && !snapshot.error)),
    error: scopeMatches ? snapshot.error : false,
    unavailable: scopeMatches && snapshot.unavailable,
    ready,
    refresh,
    upsert,
  }
}
