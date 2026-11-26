import { useState } from 'react'
import { Button, Input, Select } from '@arco-design/web-react'
import './ColumnFilterPanel.css'

interface Props {
  label: string
  value: string
  words?: Record<string, string>
  onApply: (text: string) => void
}

/** The header filter of one column: a word list for coded fields, otherwise "contains" text. */
export default function ColumnFilterPanel({ label, value, words, onApply }: Props) {
  const [text, setText] = useState(value)
  return <div className="outsource-column-filter">
    {words
      ? <Select mode="multiple" allowClear placeholder="全部" value={text.split(/\s+/).filter(Boolean)} onChange={(next: string[]) => setText(next.join(' '))}
        options={Object.entries(words).map(([code, label]) => ({ value: code, label }))} />
      : <Input.TextArea autoFocus autoSize={{ minRows: 1, maxRows: 6 }} placeholder={`筛选${label}，可粘贴多个`} value={text} onChange={setText}
        onPressEnter={(event) => { if (!event.shiftKey) { event.preventDefault(); onApply(text) } }} />}
    <div className="outsource-column-filter-actions">
      <Button size="mini" onClick={() => { setText(''); onApply('') }}>重置</Button>
      <Button size="mini" type="primary" onClick={() => onApply(text)}>筛选</Button>
    </div>
  </div>
}
