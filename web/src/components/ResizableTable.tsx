import { useMemo, useRef, type ReactNode } from 'react'
import { Table, type TableColumnProps, type TableProps } from '@arco-design/web-react'
import { useAuth } from '../store/auth'
import { ColumnResizeProvider } from './columnResize'
import type { ColumnLayoutSpec } from './tableColumnLayout'
import { useAdjustableColumns } from './useAdjustableColumns'

/** 列没写宽度时的初始宽度；拖动后按用户记在浏览器。 */
const RESIZABLE_DEFAULT_WIDTH = 120

export type ResizableTableProps<T> = Omit<TableProps<T>, 'columns' | 'components'> & {
  /** 浏览器里保存列宽用的表格标识，全站唯一。 */
  tableId: string
  columns: TableColumnProps<T>[]
  /** 屏幕有余量时多出的宽度全给这一列（如文件名）；不传则各列平分。 */
  growKey?: string
}

type KeyedColumn<T> = TableColumnProps<T> & { key: string; originalTitle: ReactNode }

function classNames(value: string | string[] | undefined) {
  return (Array.isArray(value) ? value : [value ?? '']).flatMap((item) => item.split(/\s+/)).filter(Boolean)
}

function keyOf<T>(column: TableColumnProps<T>, index: number) {
  if (column.key !== undefined && column.key !== null && column.key !== '') return String(column.key)
  if (typeof column.dataIndex === 'string' && column.dataIndex) return column.dataIndex
  return `col-${index}`
}

/**
 * Arco 表格加上可拖动的列宽，宽度按表格和用户记在浏览器里（与项目列表等页面同一套机制）。
 * 列宽规格直接取自各列已写的 `width`；双击表头分隔线恢复该列默认宽度。
 */
export default function ResizableTable<T extends object>({ tableId, columns, growKey, className, scroll, ...rest }: ResizableTableProps<T>) {
  const userId = useAuth((state) => state.user?.id)
  const hostRef = useRef<HTMLDivElement>(null)
  const keyed: KeyedColumn<T>[] = columns.map((column, index) => ({
    ...column,
    key: keyOf(column, index),
    // The layout swaps titles for its plain-text spec titles; the original (possibly rich) title is put back below.
    originalTitle: column.title,
    title: typeof column.title === 'string' ? column.title : keyOf(column, index),
  }))
  // Specs must stay stable across renders: rebuild them only when a key, title or written width changes.
  const signature = JSON.stringify(keyed.map((column) => [column.key, String(column.title),
    typeof column.width === 'number' && column.width > 0 ? column.width : RESIZABLE_DEFAULT_WIDTH]))
  const specs = useMemo<ColumnLayoutSpec[]>(() => (JSON.parse(signature) as Array<[string, string, number]>)
    .map(([key, title, defaultWidth]) => ({
      key,
      title,
      defaultWidth,
      minWidth: Math.min(72, defaultWidth),
      maxWidth: Math.max(480, defaultWidth * 2),
      locked: true,
    })), [signature])
  // Arco adds its own 40px checkbox / expand columns; leave room for them so the table never overflows its card.
  const selection = rest.rowSelection ? (typeof rest.rowSelection.columnWidth === 'number' ? rest.rowSelection.columnWidth : 40) : 0
  const reservedWidth = selection + (rest.expandedRowRender ? 40 : 0)
  const table = useAdjustableColumns(tableId, userId, specs, keyed, growKey ?? '', hostRef, true, Boolean(growKey), reservedWidth)
  const sized = table.columns.map(({ originalTitle, ...column }) => ({ ...column, title: originalTitle }))
  return (
    <ColumnResizeProvider actions={table.actions}>
      {/* A list-page table (`page-table`) must stay in the card's flex column, or its pagination stops sticking to the bottom. */}
      <div ref={hostRef} className={[table.hostClassName, classNames(className).includes('page-table') && 'page-table-host'].filter(Boolean).join(' ')}>
        <Table<T>
          {...rest}
          className={[...classNames(className), table.tableClassName].join(' ')}
          columns={sized as TableColumnProps<T>[]}
          components={table.components}
          // A page's own minimum table width stays the floor, so narrow screens still scroll instead of squeezing.
          scroll={{ ...scroll, x: Math.max(table.scrollX + reservedWidth, typeof scroll?.x === 'number' ? scroll.x : 0) }}
        />
      </div>
    </ColumnResizeProvider>
  )
}
