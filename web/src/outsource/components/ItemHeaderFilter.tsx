import { useState } from 'react'
import { AutoComplete, Button, DatePicker, Input, Select } from '@arco-design/web-react'
import ColumnFilterPanel from './ColumnFilterPanel'

export type FilterControl = 'text' | 'number' | 'date' | 'range' | 'choice' | 'customer' | 'workOrders'
export type FilterOption = { label: string; value: string }
export interface HeaderFilterProps {
  label: string
  control: FilterControl
  values: string[]
  options?: FilterOption[]
  onApply: (values: string[]) => void
}

// Keep nested pickers inside the filter popup so choosing an option does not dismiss the column filter.
const popupParent = (node: HTMLElement) => node.parentElement ?? node

export default function ItemHeaderFilter({ label, control, values, options = [], onApply }: HeaderFilterProps) {
  const [draft, setDraft] = useState(values)
  const value = draft[0] ?? ''
  const setValue = (next?: string) => setDraft(next ? [next] : [])
  const name = `筛选${label}`
  if (control === 'workOrders') {
    return <ColumnFilterPanel label={label} value={value} onApply={text => onApply(text ? [text] : [])} />
  }

  const apply = () => onApply(draft)
  let input
  if (control === 'choice') {
    input = <Select aria-label={name} placeholder="全部" allowClear showSearch options={options}
      value={value || undefined} onChange={setValue} getPopupContainer={popupParent} />
  } else if (control === 'customer') {
    input = <AutoComplete inputProps={{ 'aria-label': name }} placeholder="输入或选择客户" allowClear value={value}
      data={options.map(option => ({ name: option.label, value: option.value }))}
      filterOption={(text, option) => String(option.props.children).toLowerCase().includes(text.toLowerCase())}
      onChange={setValue} getPopupContainer={popupParent} />
  } else if (control === 'range') {
    input = <DatePicker.RangePicker aria-label={name} placeholder={['开始日期', '结束日期']} getPopupContainer={popupParent}
      value={draft.length === 2 ? [draft[0], draft[1]] : undefined} onChange={next => setDraft(next ?? [])} />
  } else if (control === 'date') {
    input = <DatePicker aria-label={name} placeholder="选择日期" value={value || undefined}
      onChange={setValue} getPopupContainer={popupParent} />
  } else {
    input = <Input aria-label={name} autoFocus allowClear placeholder={control === 'number' ? '输入数值' : '输入关键字'}
      type={control === 'number' ? 'number' : 'text'} value={value} onChange={setValue} onPressEnter={apply} />
  }
  return <div className={`outsource-column-filter${control === 'range' ? ' outsource-column-filter--range' : ''}`}>
    {input}
    <div className="outsource-column-filter-actions">
      <Button size="mini" onClick={() => onApply([])}>重置</Button>
      <Button size="mini" type="primary" onClick={apply}>筛选</Button>
    </div>
  </div>
}
