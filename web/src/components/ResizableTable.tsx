import { useMemo, useRef, type ReactNode } from 'react'
import { Table, type TableColumnProps, type TableProps } from '@arco-design/web-react'
import { useAuth } from '../store/auth'
import { ColumnResizeProvider } from './columnResize'
import { TABLE_PAGINATION } from './tablePagination'
import type { ColumnLayoutSpec } from './tableColumnLayout'
import { useAdjustableColumns } from './useAdjustableColumns'

/** 列没写宽度时的初始宽度；拖动后按用户记在浏览器。 */
const RESIZABLE_DEFAULT_WIDTH = 120


/** Marks header cells as column headers for screen readers (Arco renders the `th` itself). */
const columnHeaderProps = () => ({ scope: 'col' }) as unknown as ReturnType<NonNullable<TableColumnProps['onHeaderCell']>>

export type ResizableTableProps<T> = Omit<TableProps<T>, 'columns' | 'components'> & {
  /** 浏览器里保存列宽用的表格标识，全站唯一。 */
  tableId: string
  columns: TableColumnProps<T>[]
  /** 屏幕有余量时多出的宽度全给这一列（如文件名）；不传则各列平分。 */
  growKey?: string
  /**
   * 始终显示、不能在「显示列」里隐藏的列。默认是第一列（记录的识别列）、固定在左右两侧的列和「操作」列；
   * 标题不是纯文字的列也始终显示。
   */
  lockedKeys?: string[]
}

type KeyedColumn<T> = TableColumnProps<T> & { key: string; originalTitle: ReactNode }

function classNames(value: string | string[] | undefined) {
  return (Array.isArray(value) ? value : [value ?? '']).flatMap((item) => item.split(/\s+/)).filter(Boolean)
}

function keyOf<T>(column: TableColumnProps<T>, index: number) {
  if (column.key !== undefined && column.key !== null && column.key !== '') return String(column.key)
  if (typeof column.dataIndex === 'string' && column.dataIndex) return column.dataIndex
  // A render-only column keeps its saved width and visibility even when columns before it come and go.
  if (typeof column.title === 'string' && column.title) return column.title
  return `col-${index}`
}

function lockedByDefault<T>(column: TableColumnProps<T>, index: number) {
  return index === 0 || Boolean(column.fixed) || column.title === '操作' || typeof column.title !== 'string'
}

/**
 * Arco 表格加上可拖动的列宽，宽度按表格和用户记在浏览器里（与项目列表等页面同一套机制）。
 * 列宽规格直接取自各列已写的 `width`；双击表头分隔线恢复该列默认宽度。
 */
function FlatResizableTable<T extends object>({ tableId, columns, growKey, lockedKeys, className, scroll, pagination, ...rest }: ResizableTableProps<T>) {
  const userId = useAuth((state) => state.user?.id)
  const hostRef = useRef<HTMLDivElement>(null)
  const keyed: (KeyedColumn<T> & { locked: boolean })[] = columns.map((column, index) => ({
    onHeaderCell: columnHeaderProps,
    ...column,
    key: keyOf(column, index),
    locked: lockedKeys ? lockedKeys.includes(keyOf(column, index)) : lockedByDefault(column, index),
    // The layout swaps titles for its plain-text spec titles; the original (possibly rich) title is put back below.
    originalTitle: column.title,
    title: typeof column.title === 'string' ? column.title : keyOf(column, index),
  }))
  // Specs must stay stable across renders: rebuild them only when a key, title or written width changes.
  const signature = JSON.stringify(keyed.map((column) => [column.key, String(column.title),
    typeof column.width === 'number' && column.width > 0 ? column.width : RESIZABLE_DEFAULT_WIDTH, column.locked]))
  const specs = useMemo<ColumnLayoutSpec[]>(() => (JSON.parse(signature) as Array<[string, string, number, boolean]>)
    .map(([key, title, defaultWidth, locked]) => ({
      key,
      title,
      defaultWidth,
      minWidth: Math.min(72, defaultWidth),
      locked,
    })), [signature])
  // Arco adds its own 40px checkbox / expand columns; leave room for them so the table never overflows its card.
  const selection = rest.rowSelection ? (typeof rest.rowSelection.columnWidth === 'number' ? rest.rowSelection.columnWidth : 40) : 0
  const reservedWidth = selection + (rest.expandedRowRender ? 40 : 0)
  const table = useAdjustableColumns(tableId, userId, specs, keyed, growKey ?? '', hostRef, true, Boolean(growKey), reservedWidth)
  const sized = table.columns.map(({ originalTitle, locked: _locked, ...column }) => ({ ...column, title: originalTitle }))
  return (
    <ColumnResizeProvider actions={table.actions} picker={table.picker}>
      {/* A list-page table (`page-table`) must stay in the card's flex column, or its pagination stops sticking to the bottom. */}
      <div ref={hostRef} className={[table.hostClassName, classNames(className).includes('page-table') && 'page-table-host'].filter(Boolean).join(' ')}>
        <Table<T>
          {...rest}
          className={[...classNames(className), table.tableClassName].join(' ')}
          columns={sized as TableColumnProps<T>[]}
          components={table.components}
          pagination={pagination && typeof pagination === 'object' ? { ...TABLE_PAGINATION, ...pagination } : pagination}
          // A column pinned to the left takes the checkbox column with it, or the two overlap while scrolling sideways.
          rowSelection={rest.rowSelection && sized.some((column) => column.fixed === 'left') ? { ...rest.rowSelection, fixed: true } : rest.rowSelection}
          // A page's own minimum table width stays the floor, so narrow screens still scroll instead of squeezing.
          scroll={{ ...scroll, x: Math.max(table.scrollX + reservedWidth, typeof scroll?.x === 'number' ? scroll.x : 0) }}
        />
      </div>
    </ColumnResizeProvider>
  )
}

type Leaf<T> = { column: TableColumnProps<T>; groupKey?: string; group?: TableColumnProps<T> }

const flattenColumns = <T,>(columns: TableColumnProps<T>[]): Leaf<T>[] => columns.flatMap((column) => column.children?.length
  ? column.children.map((child) => ({ column: child, groupKey: String(column.key ?? column.title), group: column }))
  : [{ column }])

/**
 * 带分组表头（`children`）的表格：叶子列和其他表格一样可拖动列宽、在「显示列」里隐藏、排序和固定；
 * 表头分组按当前显示的相邻同组列重新合并（同组的列被拆开后，各段各有一个分组表头）。
 */
function GroupedTable<T extends object>({ tableId, columns, lockedKeys, className, scroll, pagination, ...rest }: ResizableTableProps<T>) {
  const userId = useAuth((state) => state.user?.id)
  const hostRef = useRef<HTMLDivElement>(null)
  const leaves = flattenColumns(columns)
  const keyed: (KeyedColumn<T> & { locked: boolean; groupKey?: string; group?: TableColumnProps<T> })[] = leaves.map(({ column, groupKey, group }, index) => {
    const key = keyOf(column, index)
    const label = typeof column.title === 'string' ? column.title : key
    return {
      onHeaderCell: columnHeaderProps, ...column, key, groupKey, group,
      locked: lockedKeys ? lockedKeys.includes(key) : lockedByDefault(column, index),
      originalTitle: column.title,
      // Leaf names repeat across groups (完成占比, 责任人), so the picker and the layout use "group-leaf".
      title: group && typeof group.title === 'string' ? `${group.title}-${label}` : label,
    }
  })
  const signature = JSON.stringify(keyed.map((column) => [column.key, String(column.title),
    typeof column.width === 'number' && column.width > 0 ? column.width : RESIZABLE_DEFAULT_WIDTH, column.locked]))
  const specs = useMemo<ColumnLayoutSpec[]>(() => (JSON.parse(signature) as Array<[string, string, number, boolean]>)
    .map(([key, title, defaultWidth, locked]) => ({ key, title, defaultWidth, minWidth: Math.min(72, defaultWidth), locked })), [signature])
  const table = useAdjustableColumns(tableId, userId, specs, keyed, '', hostRef, true, false, 0)
  const tree: TableColumnProps<T>[] = []
  for (const { originalTitle, locked: _locked, groupKey, group, ...column } of table.columns) {
    const leaf = { ...column, title: originalTitle } as TableColumnProps<T>
    const last = tree.at(-1)
    if (!group) tree.push(leaf)
    else if (last?.children && (last as { groupKey?: string }).groupKey === groupKey) last.children.push(leaf)
    else tree.push({ ...group, key: `${groupKey}:${column.key}`, groupKey, children: [leaf] } as TableColumnProps<T>)
  }
  return (
    // A wide grouped table scrolls sideways for ages, so the picker sits on the first column, which the page pins in view.
    <ColumnResizeProvider actions={table.actions} picker={{ ...table.picker, lastKey: table.columns[0]?.key }}>
      <div ref={hostRef} className={[table.hostClassName, classNames(className).includes('page-table') && 'page-table-host'].filter(Boolean).join(' ')}>
        <Table<T>
          {...rest}
          className={[...classNames(className), table.tableClassName].join(' ')}
          columns={tree}
          components={table.components}
          pagination={pagination && typeof pagination === 'object' ? { ...TABLE_PAGINATION, ...pagination } : pagination}
          scroll={{ ...scroll, x: Math.max(table.scrollX, typeof scroll?.x === 'number' ? scroll.x : 0) }}
        />
      </div>
    </ColumnResizeProvider>
  )
}

export default function ResizableTable<T extends object>(props: ResizableTableProps<T>) {
  return props.columns.some((column) => column.children?.length) ? <GroupedTable<T> {...props} /> : <FlatResizableTable<T> {...props} />
}
