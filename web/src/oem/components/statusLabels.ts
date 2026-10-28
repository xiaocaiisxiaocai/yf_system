import type { ApprovalStatus, Direction, Lifecycle, PayloadStatus, ValidationStatus } from '../api/types'

/** Display labels and colours for OEM statuses (shared by tags, filters and detail views). */
export type TagColor = 'gray' | 'green' | 'orange' | 'red' | 'arcoblue' | 'purple'

export const LIFECYCLE: Record<Lifecycle, [string, TagColor]> = {
  DRAFT: ['草稿', 'gray'],
  SEALED: ['待发送', 'arcoblue'],
  RELEASED: ['已发送', 'green'],
  REJECTED: ['已驳回', 'red'],
  BLOCKED: ['文件阻断', 'red'],
  CANCELLED: ['已终止', 'red'],
  ABANDONED: ['已废弃', 'gray'],
}

export const APPROVAL: Record<ApprovalStatus, [string, TagColor]> = {
  NOT_REQUIRED: ['无需审批', 'gray'],
  WAITING_FILES: ['等待文件校验', 'arcoblue'],
  PENDING: ['审批中', 'arcoblue'],
  APPROVAL_BLOCKED: ['审批异常', 'orange'],
  APPROVED: ['审批通过', 'green'],
  SKIPPED: ['按策略免审', 'purple'],
  REJECTED: ['审批驳回', 'red'],
  CANCELLED: ['审批已取消', 'gray'],
}

export const VALIDATION: Record<ValidationStatus, [string, TagColor]> = {
  PENDING: ['待校验', 'gray'],
  VALIDATING: ['校验中', 'arcoblue'],
  VALID: ['校验通过', 'green'],
  INVALID: ['校验未通过', 'red'],
  ERROR: ['校验失败', 'red'],
}

export const PAYLOAD: Record<PayloadStatus, [string, TagColor]> = {
  QUARANTINED: ['暂存中', 'gray'],
  PROMOTING: ['入库中', 'arcoblue'],
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

/** Notice shown when a draft the user is still editing has already left DRAFT. */
export function transferClosedNotice(summary: { lifecycleStatus: Lifecycle; direction: Direction }): string | null {
  switch (summary.lifecycleStatus) {
    case 'DRAFT':
      return null
    case 'SEALED':
      return summary.direction === 'INTERNAL_TO_OEM' ? '传递单已提交，正在等待审批或文件处理' : '文件正在处理中'
    case 'RELEASED':
      return '文件已发送'
    case 'REJECTED':
      return '传递单已被驳回'
    case 'BLOCKED':
      return '传递单因文件校验未通过而终止'
    case 'CANCELLED':
      return '传递单已终止'
    case 'ABANDONED':
      return '草稿已删除'
  }
}
