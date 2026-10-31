import { Tag } from '@arco-design/web-react'
import { overdueDays } from '../api/types'

/** 未完成且超过需求完成时间时显示“逾期 N 天”。 */
export default function OverdueTag({ date, status }: { date?: string | null; status?: string | null }) {
  const days = overdueDays(date, status)
  return days === null ? null : <Tag size="small" color="red">逾期 {days} 天</Tag>
}
