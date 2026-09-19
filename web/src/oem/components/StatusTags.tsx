import { Tag } from '@arco-design/web-react'
import type { ApprovalStatus, Lifecycle, PayloadStatus, ScanStatus } from '../api/types'
import { APPROVAL, LIFECYCLE, PAYLOAD, SCAN, type TagColor } from './statusLabels'

function StatusTag<T extends string>({ map, value }: { map: Record<T, [string, TagColor]>; value: T | null | undefined }) {
  if (!value) return null
  const [label, color] = map[value] ?? [value, 'gray']
  return <Tag color={color} size="small">{label}</Tag>
}

export const LifecycleTag = ({ value }: { value: Lifecycle }) => <StatusTag map={LIFECYCLE} value={value} />
export const ApprovalTag = ({ value }: { value: ApprovalStatus | null }) => <StatusTag map={APPROVAL} value={value} />
export const ScanTag = ({ value }: { value: ScanStatus | null }) => <StatusTag map={SCAN} value={value} />
export const PayloadTag = ({ value }: { value: PayloadStatus }) => <StatusTag map={PAYLOAD} value={value} />
