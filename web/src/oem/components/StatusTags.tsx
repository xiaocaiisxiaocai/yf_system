import { Tag } from '@arco-design/web-react'
import type { ApprovalStatus, Direction, Lifecycle, PayloadStatus, ScanStatus } from '../api/types'

type TagColor = 'gray' | 'blue' | 'green' | 'orange' | 'red' | 'arcoblue' | 'purple'

const LIFECYCLE: Record<Lifecycle, [string, TagColor]> = {
  DRAFT: ['草稿', 'gray'],
  SEALED: ['已发送', 'arcoblue'],
  RELEASED: ['已发布', 'green'],
  REJECTED: ['已驳回', 'red'],
  BLOCKED: ['安全阻断', 'red'],
  CANCELLED: ['已终止', 'orange'],
  ABANDONED: ['已废弃', 'gray'],
}

const APPROVAL: Record<ApprovalStatus, [string, TagColor]> = {
  NOT_REQUIRED: ['无需审批', 'gray'],
  WAITING_SCAN: ['等待扫描后审批', 'blue'],
  PENDING: ['审批中', 'arcoblue'],
  APPROVAL_BLOCKED: ['审批阻断', 'orange'],
  APPROVED: ['审批通过', 'green'],
  SKIPPED: ['按策略免审', 'purple'],
  REJECTED: ['审批驳回', 'red'],
  CANCELLED: ['审批已取消', 'gray'],
}

const SCAN: Record<ScanStatus, [string, TagColor]> = {
  PENDING: ['待扫描', 'gray'],
  SCANNING: ['扫描中', 'blue'],
  CLEAN: ['安全', 'green'],
  INFECTED: ['发现威胁', 'red'],
  ERROR: ['扫描失败', 'red'],
  UNSCANNABLE: ['无法扫描', 'red'],
}

const PAYLOAD: Record<PayloadStatus, [string, TagColor]> = {
  QUARANTINED: ['隔离中', 'gray'],
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

function StatusTag<T extends string>({ map, value }: { map: Record<T, [string, TagColor]>; value: T | null | undefined }) {
  if (!value) return null
  const [label, color] = map[value] ?? [value, 'gray']
  return <Tag color={color} size="small">{label}</Tag>
}

export const LifecycleTag = ({ value }: { value: Lifecycle }) => <StatusTag map={LIFECYCLE} value={value} />
export const ApprovalTag = ({ value }: { value: ApprovalStatus | null }) => <StatusTag map={APPROVAL} value={value} />
export const ScanTag = ({ value }: { value: ScanStatus | null }) => <StatusTag map={SCAN} value={value} />
export const PayloadTag = ({ value }: { value: PayloadStatus }) => <StatusTag map={PAYLOAD} value={value} />

export const LIFECYCLE_OPTIONS = Object.entries(LIFECYCLE).map(([value, [label]]) => ({ value, label }))
