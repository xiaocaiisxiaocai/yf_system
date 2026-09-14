export interface PageResp<T> {
  list: T[]
  total: number
  page: number
  pageSize: number
}

export interface Project {
  id: number
  name: string
  description?: string
  supplierId: number
  supplierName?: string
  status: 'DRAFT' | 'IN_PROGRESS' | 'PENDING_CONFIRMATION' | 'COMPLETED' | 'TERMINATED'
  /** 当前内部验收方；待验收时固定为 COMPANY，项目回到进行中后由后端清空。 */
  confirmSide?: ConfirmSide | null
  /** 当前待确认提交的提交人；撤回仅由提交人或内部全量查看者发起。 */
  latestSubmitterId?: number | null
  /** 当前待验收提交的不可变版本标识；确认、驳回和撤回必须回传。 */
  latestSubmissionId?: number | null
  rejectReason?: string | null
  createdBy: number
  createdByName?: string
  createdAt: string
  updatedAt: string
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
}

export interface Message {
  id: number
  projectId: number
  content: string
  senderId: number
  senderName: string
  senderType: 'INTERNAL' | 'SUPPLIER'
  createdAt: string
  readCount: number
  totalCount: number
  readByMe: boolean
}

export interface Member {
  userId: number
  employeeNo: string
  realName: string
  deptName?: string | null
  status: 'ACTIVE' | 'DISABLED'
  createdAt: string
}

export interface SupplierMember {
  userId: number
  employeeNo: string
  realName: string
  status: 'ACTIVE'
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
