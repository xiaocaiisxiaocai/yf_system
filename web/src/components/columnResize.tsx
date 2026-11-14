import { createContext, useContext, type ReactNode } from 'react'
import { Button, Checkbox, Popover } from '@arco-design/web-react'
import { IconSettings } from '@arco-design/web-react/icon'
import type { ColumnLayoutSpec } from './tableColumnLayout'

interface ColumnResizeActions {
  beginResize: (key: string, clientX: number) => void
  setWidth: (key: string, width: number | null) => void
  widthOf: (key: string) => number
  /** 该列允许的宽度范围，供读屏报告当前值。 */
  limitsOf?: (key: string) => { min: number }
}

/** Column visibility state of one table; the picker sits in the header of its last shown column. */
export interface ColumnPickerState {
  specs: readonly ColumnLayoutSpec[]
  hidden: readonly string[]
  lastKey?: string
  toggle: (key: string, shown: boolean) => void
  showAll: () => void
  hideOptional: () => void
  reset: () => void
}

const ColumnResizeContext = createContext<ColumnResizeActions | null>(null)
const ColumnPickerContext = createContext<ColumnPickerState | null>(null)

export function ResizableHeaderCell({ className, column, children }: {
  className?: string
  column?: { key?: string | number; title?: ReactNode }
  children?: ReactNode
}) {
  const actions = useContext(ColumnResizeContext)
  const picker = useContext(ColumnPickerContext)
  const columnKey = typeof column?.key === 'string' ? column.key : undefined
  const label = typeof column?.title === 'string' ? column.title : columnKey
  const withPicker = Boolean(picker && columnKey && columnKey === picker.lastKey && picker.specs.some((spec) => !spec.locked))
  return (
    <div className={withPicker ? `${className ?? ''} table-th-with-picker` : className}>
      {children}
      {withPicker && picker && <ColumnVisibilityPicker specs={picker.specs} hidden={picker.hidden} onToggle={picker.toggle}
        onShowAll={picker.showAll} onHideOptional={picker.hideOptional} onReset={picker.reset} />}
      {actions && columnKey && label && (
        <div
          role="separator"
          tabIndex={0}
          aria-orientation="vertical"
          aria-valuenow={actions.widthOf(columnKey)}
          aria-valuemin={actions.limitsOf?.(columnKey).min}
          className="table-col-resize"
          aria-label={`调整${label}列宽`}
          title="拖动或用左右方向键调整列宽，双击或按 Home 键恢复默认"
          onPointerDown={(event) => {
            if (event.button !== 0) return
            event.preventDefault()
            event.stopPropagation()
            actions.beginResize(columnKey, event.clientX)
          }}
          onDoubleClick={(event) => {
            event.preventDefault()
            event.stopPropagation()
            actions.setWidth(columnKey, null)
          }}
          onKeyDown={(event) => {
            if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
              event.preventDefault()
              actions.setWidth(columnKey, actions.widthOf(columnKey) + (event.key === 'ArrowRight' ? 16 : -16))
            } else if (event.key === 'Home') {
              event.preventDefault()
              actions.setWidth(columnKey, null)
            }
          }}
        />
      )}
    </div>
  )
}

/** Every resizable table goes through here: column widths, plus the "显示列" picker in the header when given. */
export function ColumnResizeProvider({ actions, picker, children }: {
  actions: ColumnResizeActions; picker?: ColumnPickerState; children: ReactNode
}) {
  return <ColumnResizeContext.Provider value={actions}>
    <ColumnPickerContext.Provider value={picker ?? null}>{children}</ColumnPickerContext.Provider>
  </ColumnResizeContext.Provider>
}

function ColumnVisibilityPicker({ specs, hidden, onToggle, onShowAll, onHideOptional, onReset }: {
  specs: readonly ColumnLayoutSpec[]
  hidden: readonly string[]
  onToggle: (key: string, shown: boolean) => void
  onShowAll: () => void
  onHideOptional: () => void
  onReset: () => void
}) {
  const hiddenKeys = new Set(hidden)
  const optional = specs.filter((spec) => !spec.locked)
  const visibleOptional = optional.filter((spec) => !hiddenKeys.has(spec.key)).length
  return (
    <Popover
      trigger="click"
      position="br"
      content={(
        <div className="table-column-picker" role="group" aria-label="显示列">
          <Checkbox
            checked={visibleOptional === optional.length}
            indeterminate={visibleOptional > 0 && visibleOptional < optional.length}
            onChange={(checked) => { if (checked) onShowAll(); else onHideOptional() }}
          >全部可选列</Checkbox>
          <div className="table-column-picker-list">
            {specs.map((spec) => (
              <Checkbox
                key={spec.key}
                checked={spec.locked || !hiddenKeys.has(spec.key)}
                disabled={spec.locked}
                onChange={(checked) => onToggle(spec.key, checked)}
              >{spec.title}</Checkbox>
            ))}
          </div>
          <p className="table-column-picker-note">{specs.some((spec) => spec.locked) ? `${specs.filter((spec) => spec.locked).map((spec) => spec.title).join('、')}始终显示。` : ''}拖动表头右缘调整列宽，双击恢复该列默认宽度。</p>
          <div className="table-column-picker-actions">
            <Button type="text" size="mini" onClick={onReset}>恢复默认</Button>
          </div>
        </div>
      )}
    >
      <Button className="table-column-picker-trigger" type="text" size="mini" icon={<IconSettings />} aria-label="显示列" title="显示列"
        onClick={(event) => event.stopPropagation()} onPointerDown={(event) => event.stopPropagation()} />
    </Popover>
  )
}
