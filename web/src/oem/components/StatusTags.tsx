import { Tag } from '@arco-design/web-react'
import type { ApprovalStatus, Lifecycle, PayloadStatus, ValidationStatus } from '../api/types'
import { APPROVAL, LIFECYCLE, VALIDATION, fileStatus, type TagColor } from './statusLabels'

function StatusTag<T extends string>({ map, value }: { map: Record<T, [string, TagColor]>; value: T | null | undefined }) {
  if (!value) return null
  const [label, color] = map[value] ?? [value, 'gray']
  return <Tag color={color} size="small">{label}</Tag>
}

export const LifecycleTag = ({ value }: { value: Lifecycle }) => <StatusTag map={LIFECYCLE} value={value} />
export const ApprovalTag = ({ value }: { value: ApprovalStatus | null }) => <StatusTag map={APPROVAL} value={value} />
export const ValidationTag = ({ value }: { value: ValidationStatus | null }) => <StatusTag map={VALIDATION} value={value} />
/** The single status of an attached file (see fileStatus). */
export function FileStatusTag({ file }: { file: { validationStatus: ValidationStatus; payloadStatus: PayloadStatus } }) {
  const [label, color] = fileStatus(file)
  return <Tag color={color} size="small">{label}</Tag>
}
