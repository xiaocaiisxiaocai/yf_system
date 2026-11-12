import { Select } from '@arco-design/web-react'
import type { RecipientChoice } from '../api/types'

export default function RecipientChoiceSelect({ choices, value, onChange, disabled, loading, style }: {
  choices: RecipientChoice[]
  value?: number[]
  onChange?: (value: number[]) => void
  disabled?: boolean
  loading?: boolean
  style?: React.CSSProperties
}) {
  return (
    <Select
      mode="multiple" allowClear showSearch aria-label="通知公司人员" placeholder="选择要通知的公司人员"
      style={style} loading={loading} disabled={disabled} value={value}
      onChange={(next) => onChange?.((next as number[] | undefined) ?? [])}
      options={choices.map((choice) => ({ value: choice.userId, label: choice.realName }))}
      filterOption={(input, option) => String((option as { props?: { children?: unknown } })?.props?.children ?? '')
        .toLowerCase().includes(input.trim().toLowerCase())}
    />
  )
}
