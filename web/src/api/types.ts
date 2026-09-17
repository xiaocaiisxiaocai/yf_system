export interface PageResp<T> {
  list: T[]
  total: number
  page: number
  pageSize: number
}

/**
 * 子项目的摘要字段，由 ProjectJson.Project（后端）序列化，
 * 用于主项目详情的子项目列表（siblings/表格）、复制结果等批量/列表场景。
 * 不包含 rejectReason / latestSubmitterId —— 这两个字段需要按项目单独查询
 * project_status_logs，只有 GET /projects/{id} 详情接口才会附带，
 * 批量场景为避免逐行查询没有提供。需要这两个字段时使用 Project 类型。
 */
export interface ProjectSummary {
  id: number
  projectGroupId: number
  projectGroupName: string
  name: string
  description?: string
  supplierId: number
  supplierName?: string
  workOrderNos: string[]
  machineModel?: string | null
  robotVendorId?: number | null
  robotVendorName?: string | null
  robotModelId?: number | null
  robotModelName?: string | null
  responsibleUserId?: number | null
  responsibleUserEmployeeNo?: string | null
  responsibleUserName?: string | null
  sectionId?: number | null
  sectionName?: string | null
  priorityId?: number | null
  priorityName?: string | null
  /** 仅包含日期，不进行时区转换。 */
  expectedCompletionDate?: string | null
  status: 'DRAFT' | 'IN_PROGRESS' | 'PENDING_CONFIRMATION' | 'COMPLETED' | 'TERMINATED'
  /** 当前内部验收方；待验收时固定为 COMPANY，项目回到进行中后由后端清空。 */
  confirmSide?: ConfirmSide | null
  /** 当前待验收提交的不可变版本标识；确认、驳回和撤回必须回传。 */
  latestSubmissionId?: number | null
  createdBy: number
  createdByName?: string
  createdAt: string
  updatedAt: string
  /** 当前项目存在可查看或受限的复制关系。 */
  hasCopyHistory?: boolean
  unreadMessages?: number
  /** 当前项目的可见复制来源；来源无权访问时由后端置空。 */
  copySource?: { projectId: number; name: string } | null
}

/** 子项目详情，仅 GET /projects/{id} 返回；比 ProjectSummary 多两个单独查询的字段。 */
export interface Project extends ProjectSummary {
  /** 当前待确认提交的提交人；撤回仅由提交人或内部全量查看者发起。 */
  latestSubmitterId?: number | null
  rejectReason?: string | null
}

export interface ProjectGroup {
  id: number
  name: string
  description?: string | null
  supplierId: number
  supplierName?: string | null
  workOrderNos: string[]
  machineModel?: string | null
  robotVendorId?: number | null
  robotVendorName?: string | null
  robotModelId?: number | null
  robotModelName?: string | null
  responsibleUserId?: number | null
  responsibleUserEmployeeNo?: string | null
  responsibleUserName?: string | null
  sectionId?: number | null
  sectionName?: string | null
  priorityId?: number | null
  priorityName?: string | null
  expectedCompletionDate?: string | null
  status: 'DRAFT' | 'IN_PROGRESS' | 'COMPLETED' | 'TERMINATED'
  completedAt?: string | null
  createdBy: number
  createdByName?: string | null
  createdAt: string
  updatedAt: string
  subprojectCount: number
  completedCount: number
  pendingCount: number
  terminatedCount: number
  unreadMessages: number
}

export interface ProjectGroupDetail {
  group: ProjectGroup
  projects: ProjectSummary[]
}

export interface ProjectCopySummary {
  copyId: number
  /** 从当前项目视角关联的另一项目 ID；仅在关联项目可见时返回。 */
  projectId: number
  name: string
  fileCount: number
  totalBytes: number
  copiedByName?: string | null
  createdAt: string
}

export interface ProjectCopyHistory {
  source: ProjectCopySummary | null
  copies: ProjectCopySummary[]
  hasRestrictedRelations: boolean
}

export interface ProjectCopyFileMapping {
  sourceFileId: number
  sourceFileName: string
  sourceDeleted: boolean
  targetFileId: number
  targetFileName: string
  targetDeleted: boolean
}

export interface ProjectCopyResult {
  project: ProjectSummary
  copy: {
    copyId: number
    sourceProjectId: number
    targetProjectId: number
    fileCount: number
    totalBytes: number
    createdAt: string
  }
}

export type ProjectDictionaryType = 'ROBOT_VENDOR' | 'ROBOT_MODEL' | 'PRIORITY'

export interface ProjectDictionaryOption {
  id: number
  type: ProjectDictionaryType
  name: string
  parentId?: number | null
  sortNo: number
  enabled: boolean
}

export interface ProjectOwnerOption {
  id: number
  employeeNo: string
  realName: string
  sectionId?: number | null
  sectionName?: string | null
}

/** 新申请固定 COMPANY；SUPPLIER 仅用于读取升级前保留的历史数据。 */
export type ConfirmSide = 'COMPANY' | 'SUPPLIER'

export interface FileItem {
  id: number
  projectId: number
  uploaderId: number
  uploaderName?: string
  direction: 'C2S' | 'S2C'
  originalName: string
  ext: string
  sizeBytes: number
  mimeType?: string
  createdAt: string
  canDelete?: boolean
  /** 该文件由项目复制产生，不暴露不可见来源的文件标识。 */
  isCopiedReference?: boolean
}

export interface Message {
  id: number
  projectId: number
  content: string
  images?: { id: number; name: string; sizeBytes: number; mimeType: string }[]
  senderId: number
  senderName: string
  senderType: 'INTERNAL' | 'SUPPLIER'
  createdAt: string
  readCount: number
  totalCount: number
  readByMe: boolean
}

export const PROJECT_STATUS: Record<string, { text: string; color: string }> = {
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
