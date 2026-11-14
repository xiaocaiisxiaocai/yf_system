import { Tag } from '@arco-design/web-react'

/** Status of an account, role, supplier, vendor, contact or dictionary row: 启用 or 停用, the same everywhere. */
export default function StatusTag({ status, size }: { status: string; size?: 'small' | 'default' }) {
  const active = status === 'ACTIVE'
  return <Tag color={active ? 'green' : 'gray'} size={size}>{active ? '启用' : '停用'}</Tag>
}
