import type * as Api from './generated/api-types'

export type { ApiResponses, ApiRoute } from './generated/api-types'

export type PageResp<T> = Omit<Api.PageResponseOfAuditLogResponse, 'list'> & { list: T[] }

/**
 * 以下业务类型由后端 OpenAPI 生成的 ./generated/api-types 推导（npm run generate:api-types），
 * 只在生成类型之上收窄状态等枚举字段；后端字段改名或删除时，前端使用处会在编译期报错。
 */
export type ProjectStatus = 'DRAFT' | 'IN_PROGRESS' | 'COMPLETED' | 'TERMINATED'

/** 进行中子项目的厂商回传资料状态；确认 OK 即完成子项目，因此没有“已确认”状态。 */
export type MaterialReviewStatus = 'NO_MATERIAL' | 'PENDING' | 'RETURNED'

type WithProjectEnums<T> = Omit<T, 'status' | 'materialReviewStatus'> & {
  status: ProjectStatus
  materialReviewStatus: MaterialReviewStatus | null
}

/** 子项目（后端 ProjectResponse），列表、详情、复制结果等场景共用。 */
export type ProjectSummary = WithProjectEnums<Api.ProjectResponse>

/** 子项目详情（GET /projects/{id}），字段与 ProjectSummary 相同。 */
export type Project = ProjectSummary

export type ProjectGroup = Omit<Api.ProjectGroupResponse, 'status'> & {
  status: 'DRAFT' | 'IN_PROGRESS' | 'COMPLETED' | 'TERMINATED'
}

export interface ProjectGroupDetail {
  group: ProjectGroup
  projects: ProjectSummary[]
}

/** 从当前项目视角关联的另一项目；仅在关联项目可见时返回。 */
export type ProjectCopySummary = Api.ProjectCopyHistoryItem

export type ProjectCopyHistory = Api.ProjectCopyHistoryResponse

export type ProjectCopyFileMapping = Api.FileCopyHistoryItem

/** 持久化项目复制任务；任务响应不依赖当前页面是否仍打开。 */
export type ProjectCopyJobStatus = 'pending' | 'running' | 'succeeded' | 'failed'

export type ProjectCopyJobResult = Api.ProjectCopyJobResult

/** Generated DTO with the status narrowed to the known lifecycle values. */
export type ProjectCopyJob = Omit<Api.ProjectCopyJobResponse, 'status'> & {
  status: ProjectCopyJobStatus
}

/** POST and single-job GET return this flat record; only the group list wraps { jobs }. */
export type ProjectCopyJobResponse = ProjectCopyJob

export type ProjectCopyJobsResponse = Omit<Api.ProjectCopyJobListResponse, 'jobs'> & {
  jobs: ProjectCopyJob[]
}

export type CreateProjectCopyJobRequest = Omit<Api.ProjectCopyRequest, 'name' | 'idempotencyKey'> & {
  name: string
  idempotencyKey: string
}

export type ProjectDictionaryType = 'PRIORITY' | 'ROBOT_TYPE'

export type ProjectDictionaryOption = Omit<Api.ProjectDictionaryResponse, 'type'> & { type: ProjectDictionaryType }

export type RobotPart = Api.RobotPartResponse

export type SupplierContactOption = Api.SupplierContactOption

export type SupplierContact = Omit<Api.SupplierContactResponse, 'status'> & { status: 'ACTIVE' | 'DISABLED' }

export type FileItem = Omit<Api.FileListItem, 'direction'> & { direction: 'C2S' | 'S2C' }

export type Message = Omit<Api.MessageResponse, 'senderType'> & { senderType: 'INTERNAL' | 'SUPPLIER' }

type ProjectStatusView = { text: string; color: string }
export const PROJECT_STATUS: Record<ProjectStatus, ProjectStatusView> & Partial<Record<string, ProjectStatusView>> = {
  DRAFT: { text: '草稿', color: 'gray' },
  IN_PROGRESS: { text: '进行中', color: 'arcoblue' },
  COMPLETED: { text: '已完成', color: 'green' },
  TERMINATED: { text: '已终止', color: 'red' },
}

export function fmtSize(n: number): string {
  if (n < 1024) return `${n} B`
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`
  if (n < 1024 * 1024 * 1024) return `${(n / 1024 / 1024).toFixed(1)} MB`
  return `${(n / 1024 / 1024 / 1024).toFixed(2)} GB`
}

export function fmtTime(s?: string | null): string {
  if (!s) return '-'
  // 后端统一存 UTC；无时区后缀的朴素时间串也按 UTC 解析，再转本地时区显示
  const iso = /[zZ]|[+-]\d{2}:?\d{2}$/.test(s) ? s : `${s}Z`
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return s.replace('T', ' ').slice(0, 19)
  const p = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`
}
