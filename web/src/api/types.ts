export interface PageResp<T> {
  list: T[]
  total: number
  page: number
  pageSize: number
}

export interface Project {
  id: number
  code: string
  name: string
  description?: string
  supplierId: number
  supplierName?: string
  supplierCode?: string
  status: 'DRAFT' | 'IN_PROGRESS' | 'COMPLETED' | 'TERMINATED'
  createdBy: number
  createdByName?: string
  createdAt: string
  updatedAt: string
}

export interface Round {
  id: number
  projectId: number
  roundNo: number
  title?: string
  remark?: string
  confirmSide: 'COMPANY' | 'SUPPLIER'
  status: 'PENDING' | 'CONFIRMED' | 'REJECTED' | 'CANCELLED'
  decidedBy?: number | null
  decidedByName?: string | null
  decidedAt?: string | null
  rejectReason?: string | null
  createdBy: number
  createdByName?: string
  createdAt: string
  logs?: RoundLog[]
}

export interface RoundLog {
  id: number
  fromStatus?: string | null
  toStatus: string
  reason?: string | null
  operatorId: number
  operatorName?: string
  createdAt: string
}

export interface FileItem {
  id: number
  projectId: number
  roundId: number
  roundNo?: number
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
  roundId?: number | null
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
  username: string
  realName: string
  deptName?: string | null
  createdAt: string
}

export const PROJECT_STATUS: Record<string, { text: string; color: string }> = {
  DRAFT: { text: '草稿', color: 'gray' },
  IN_PROGRESS: { text: '进行中', color: 'arcoblue' },
  COMPLETED: { text: '已完成', color: 'green' },
  TERMINATED: { text: '已终止', color: 'red' },
}

export const ROUND_STATUS: Record<string, { text: string; color: string }> = {
  PENDING: { text: '待确认', color: 'orange' },
  CONFIRMED: { text: '已确认', color: 'green' },
  REJECTED: { text: '已驳回', color: 'red' },
  CANCELLED: { text: '已撤销', color: 'gray' },
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
