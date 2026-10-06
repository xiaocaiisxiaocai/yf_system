import type * as Api from './generated/api-types'

export type { ApiResponses, ApiRoute } from './generated/api-types'

export type PageResp<T> = Omit<Api.PageResponseOfAuditLogResponse, 'list'> & { list: T[] }

/**
 * 以下业务类型由后端 OpenAPI 生成的 ./generated/api-types 推导（npm run generate:api-types），
 * 只在生成类型之上收窄状态等枚举字段；后端字段改名或删除时，前端使用处会在编译期报错。
 */
export type ProjectStatus = 'DRAFT' | 'IN_PROGRESS' | 'PENDING_CONFIRMATION' | 'COMPLETED' | 'TERMINATED'

/** 新申请固定 COMPANY；SUPPLIER 仅用于读取升级前保留的历史数据。 */
export type ConfirmSide = 'COMPANY' | 'SUPPLIER'

type WithProjectEnums<T> = Omit<T, 'status' | 'confirmSide'> & {
  status: ProjectStatus
  /** 当前内部验收方；待验收时固定为 COMPANY，项目回到进行中后由后端清空。 */
  confirmSide: ConfirmSide | null
}

/**
 * 子项目的摘要字段（后端 ProjectResponse），用于主项目详情的子项目列表、复制结果等批量场景。
 * 不包含 rejectReason / latestSubmitterId —— 只有 GET /projects/{id} 详情接口才会附带。
 */
export type ProjectSummary = WithProjectEnums<Api.ProjectResponse>

/** 子项目详情，仅 GET /projects/{id} 返回；比 ProjectSummary 多两个单独查询的字段。 */
export type Project = WithProjectEnums<Api.ProjectDetailResponse>

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

export type ProjectDictionaryType = 'PRIORITY'

export type ProjectDictionaryOption = Omit<Api.ProjectDictionaryResponse, 'type'> & { type: ProjectDictionaryType }

export type RobotPart = Api.RobotPartResponse

export type FileItem = Omit<Api.FileListItem, 'direction'> & { direction: 'C2S' | 'S2C' }

export type Message = Omit<Api.MessageResponse, 'senderType'> & { senderType: 'INTERNAL' | 'SUPPLIER' }

type ProjectStatusView = { text: string; color: string }
export const PROJECT_STATUS: Record<ProjectStatus, ProjectStatusView> & Partial<Record<string, ProjectStatusView>> = {
  DRAFT: { text: '草稿', color: 'gray' },
  IN_PROGRESS: { text: '进行中', color: 'arcoblue' },
  PENDING_CONFIRMATION: { text: '待内部验收', color: 'orange' },
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
