import type { ApprovalStatus, Direction, Lifecycle, PayloadStatus, ValidationStatus } from '../api/types'

/** Display labels and colours for OEM statuses (shared by tags, filters and detail views). */
export type TagColor = 'gray' | 'blue' | 'green' | 'orange' | 'red' | 'arcoblue' | 'purple'

export const LIFECYCLE: Record<Lifecycle, [string, TagColor]> = {
  DRAFT: ['草稿', 'gray'],
  SEALED: ['待发送', 'arcoblue'],
  RELEASED: ['已发送', 'green'],
  REJECTED: ['已驳回', 'red'],
  BLOCKED: ['文件阻断', 'red'],
  CANCELLED: ['已终止', 'orange'],
  ABANDONED: ['已废弃', 'gray'],
}

export const APPROVAL: Record<ApprovalStatus, [string, TagColor]> = {
  NOT_REQUIRED: ['无需审批', 'gray'],
  WAITING_FILES: ['等待文件校验', 'blue'],
  PENDING: ['审批中', 'arcoblue'],
  APPROVAL_BLOCKED: ['审批阻断', 'orange'],
  APPROVED: ['审批通过', 'green'],
  SKIPPED: ['按策略免审', 'purple'],
  REJECTED: ['审批驳回', 'red'],
  CANCELLED: ['审批已取消', 'gray'],
}

export const VALIDATION: Record<ValidationStatus, [string, TagColor]> = {
  PENDING: ['待校验', 'gray'],
  VALIDATING: ['校验中', 'blue'],
  VALID: ['校验通过', 'green'],
  INVALID: ['校验未通过', 'red'],
  ERROR: ['校验失败', 'red'],
}

export const PAYLOAD: Record<PayloadStatus, [string, TagColor]> = {
  QUARANTINED: ['暂存中', 'gray'],
  PROMOTING: ['入库中', 'blue'],
  AVAILABLE: ['可用', 'green'],
  PURGE_PENDING: ['待删除', 'orange'],
  PURGED: ['已删除', 'gray'],
  STORAGE_LOST: ['存储丢失', 'red'],
  MISSING_UNVERIFIED: ['原因待核实', 'red'],
}

export const DIRECTION_LABEL: Record<Direction, string> = {
  INTERNAL_TO_OEM: '公司 → OEM',
  OEM_TO_INTERNAL: 'OEM → 公司',
}

export const LIFECYCLE_OPTIONS = Object.entries(LIFECYCLE).map(([value, [label]]) => ({ value, label }))
