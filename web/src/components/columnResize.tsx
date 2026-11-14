import { createContext, useContext, useState, type ReactNode } from 'react'
import { Button, Checkbox, Popover } from '@arco-design/web-react'
import { IconDragDotVertical, IconPushpin, IconSettings } from '@arco-design/web-react/icon'
import type { ColumnLayoutSpec } from './tableColumnLayout'

interface ColumnResizeActions {
  beginResize: (key: string, clientX: number) => void
  setWidth: (key: string, width: number | null) => void
  widthOf: (key: string) => number
  /** 该列允许的宽度范围，供读屏报告当前值。 */
  limitsOf?: (key: string) => { min: number }
}

export interface ColumnPickerEntry {
  key: string
  title: string
  /** 始终显示（不能取消勾选）。 */
  locked: boolean
  /** 固定在左侧。 */
  pinned: boolean
  /** 固定在右侧的列（如操作）不参与排序和固定。 */
  movable: boolean
}

/** Column visibility, order and pinning of one table; the picker sits in the header of its last shown column. */
export interface ColumnPickerState {
  specs: readonly ColumnLayoutSpec[]
  hidden: readonly string[]
  lastKey?: string
  /** All columns in their current order, hidden ones included. */
  entries: readonly ColumnPickerEntry[]
  toggle: (key: string, shown: boolean) => void
  showAll: () => void
  hideOptional: () => void
  /** Moves `key` to where `target` is now. */
  move: (key: string, target: string) => void
  pin: (key: string, pinned: boolean) => void
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
  const withPicker = Boolean(picker && columnKey && columnKey === picker.lastKey
    && (picker.entries.some((entry) => !entry.locked) || picker.entries.filter((entry) => entry.movable).length > 1))
  return (
    <div className={withPicker ? `${className ?? ''} table-th-with-picker` : className}>
      {children}
      {withPicker && picker && <ColumnVisibilityPicker picker={picker} />}
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

function ColumnVisibilityPicker({ picker }: { picker: ColumnPickerState }) {
  const [dragging, setDragging] = useState<string | null>(null)
  const hiddenKeys = new Set(picker.hidden)
  const optional = picker.entries.filter((entry) => !entry.locked)
  const visibleOptional = optional.filter((entry) => !hiddenKeys.has(entry.key)).length
  const movable = picker.entries.filter((entry) => entry.movable)
  const neighbour = (key: string, step: -1 | 1) => {
    const index = movable.findIndex((entry) => entry.key === key)
    return movable[index + step]?.key
  }
  return (
    <Popover
      trigger="click"
      position="br"
      content={(
        <div className="table-column-picker" role="group" aria-label="显示列">
          <Checkbox
            checked={optional.length > 0 && visibleOptional === optional.length}
            indeterminate={visibleOptional > 0 && visibleOptional < optional.length}
            disabled={optional.length === 0}
            onChange={(checked) => { if (checked) picker.showAll(); else picker.hideOptional() }}
          >全部可选列</Checkbox>
          <ul className="table-column-picker-list">
            {picker.entries.map((entry) => (
              <li
                key={entry.key}
                className={[
                  'table-column-picker-item',
                  entry.pinned && 'is-pinned',
                  dragging === entry.key && 'is-dragging',
                ].filter(Boolean).join(' ')}
                draggable={entry.movable}
                onDragStart={(event) => {
                  setDragging(entry.key)
                  event.dataTransfer.effectAllowed = 'move'
                  event.dataTransfer.setData('text/plain', entry.key)
                }}
                onDragEnd={() => setDragging(null)}
                onDragOver={(event) => { if (entry.movable) event.preventDefault() }}
                onDrop={(event) => {
                  event.preventDefault()
                  const key = dragging ?? event.dataTransfer.getData('text/plain')
                  if (key && entry.movable) picker.move(key, entry.key)
                  setDragging(null)
                }}
              >
                {entry.movable
                  ? (
                    <button type="button" className="table-column-picker-handle" aria-label={`移动${entry.title}列`}
                      title="拖动或用上下方向键调整顺序"
                      onKeyDown={(event) => {
                        const step = event.key === 'ArrowUp' ? -1 : event.key === 'ArrowDown' ? 1 : 0
                        const target = step ? neighbour(entry.key, step) : undefined
                        if (!target) return
                        event.preventDefault()
                        picker.move(entry.key, target)
                      }}
                    ><IconDragDotVertical /></button>
                  )
                  : <span className="table-column-picker-handle is-static" aria-hidden="true" />}
                <Checkbox
                  checked={entry.locked || !hiddenKeys.has(entry.key)}
                  disabled={entry.locked}
                  onChange={(checked) => picker.toggle(entry.key, checked)}
                >{entry.title}</Checkbox>
                {entry.movable && (
                  <Button type="text" size="mini" className="table-column-picker-pin" icon={<IconPushpin />}
                    aria-label={entry.pinned ? `取消固定${entry.title}列` : `固定${entry.title}列`} aria-pressed={entry.pinned}
                    title={entry.pinned ? '取消固定' : '固定在左侧'} onClick={() => picker.pin(entry.key, !entry.pinned)} />
                )}
              </li>
            ))}
          </ul>
          <div className="table-column-picker-actions">
            <Button type="text" size="mini" onClick={picker.reset}>恢复默认</Button>
          </div>
        </div>
      )}
    >
      <Button className="table-column-picker-trigger" type="text" size="mini" icon={<IconSettings />} aria-label="显示列" title="显示列"
        onClick={(event) => event.stopPropagation()} onPointerDown={(event) => event.stopPropagation()} />
    </Popover>
  )
}
