import http, { type QuietRequestConfig } from './client'
import type {
  CreateProjectCopyJobRequest,
  ProjectCopyJob,
  ProjectCopyJobResponse,
  ProjectCopyJobsResponse,
} from './types'

export type CopyJobResponseBody = ProjectCopyJobResponse

const isSafePositiveInteger = (value: unknown): value is number =>
  typeof value === 'number' && Number.isSafeInteger(value) && value > 0
const isSafeNonNegativeInteger = (value: unknown): value is number =>
  typeof value === 'number' && Number.isSafeInteger(value) && value >= 0
const isTimestamp = (value: unknown, nullable: boolean): value is string | null =>
  (nullable && value === null) || (typeof value === 'string' && value.length > 0 && !Number.isNaN(new Date(value).getTime()))

export function isCopyJob(value: unknown): value is ProjectCopyJob {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false
  const candidate = value as Partial<ProjectCopyJob>
  const result = candidate.result
  const validResult = result === null || (
    typeof result === 'object'
    && isSafePositiveInteger(result.projectId)
    && isSafeNonNegativeInteger(result.copyFileCount)
  )
  return isSafePositiveInteger(candidate.jobId)
    && isSafePositiveInteger(candidate.sourceProjectId)
    && isSafePositiveInteger(candidate.projectGroupId)
    && typeof candidate.targetName === 'string'
    && candidate.targetName.trim().length > 0
    && (candidate.status === 'pending' || candidate.status === 'running' || candidate.status === 'succeeded' || candidate.status === 'failed')
    && isSafeNonNegativeInteger(candidate.filesTotal)
    && isSafeNonNegativeInteger(candidate.filesCopied)
    && candidate.filesCopied <= candidate.filesTotal
    && isSafeNonNegativeInteger(candidate.bytesTotal)
    && isSafeNonNegativeInteger(candidate.bytesCopied)
    && candidate.bytesCopied <= candidate.bytesTotal
    && (candidate.error === null || typeof candidate.error === 'string')
    && validResult
    && (candidate.status !== 'succeeded' || candidate.result !== null)
    && isTimestamp(candidate.createdAt, false)
    && isTimestamp(candidate.startedAt, true)
    && isTimestamp(candidate.completedAt, true)
}

/** 202 响应采用后端 ProjectCopyJobResponse 的扁平任务记录。 */
export function unwrapCopyJob(value: unknown): ProjectCopyJob {
  if (!isCopyJob(value)) throw new Error('复制任务响应格式无效')
  return value
}

export function createProjectCopyJob(sourceId: number, request: CreateProjectCopyJobRequest, signal?: AbortSignal) {
  const name = typeof request.name === 'string' ? request.name.trim() : ''
  const idempotencyKey = typeof request.idempotencyKey === 'string' ? request.idempotencyKey.trim() : ''
  if (!isSafePositiveInteger(sourceId) || [...name].length < 1 || [...name].length > 128) throw new Error('复制任务名称或来源无效')
  if (idempotencyKey.length < 1 || idempotencyKey.length > 64 || !/^[A-Za-z0-9_.:-]+$/.test(idempotencyKey)) {
    throw new Error('复制任务幂等键无效')
  }
  return http.post<CopyJobResponseBody>(
    `/projects/${sourceId}/copy`,
    { ...request, name, idempotencyKey },
    { signal, quietNetworkError: true } as QuietRequestConfig,
  )
}

export function listProjectCopyJobs(groupId: number, signal?: AbortSignal) {
  return http.get<ProjectCopyJobsResponse>(
    `/project-groups/${groupId}/copy-jobs`,
    { signal, quietNetworkError: true } as QuietRequestConfig,
  )
}

export function getProjectCopyJob(jobId: number, signal?: AbortSignal) {
  return http.get<ProjectCopyJobResponse>(
    `/project-copy-jobs/${jobId}`,
    { signal, quietNetworkError: true } as QuietRequestConfig,
  )
}

export function isCopyJobAccessError(error: unknown): boolean {
  const status = (error as { response?: { status?: unknown } } | null)?.response?.status
  return status === 403 || status === 404
}

export function isCopyJobAbort(error: unknown): boolean {
  return (error as { code?: unknown } | null)?.code === 'ERR_CANCELED'
}
