import { useLayoutEffect, useMemo, useRef, useState, type ReactNode, type RefObject } from 'react'
import { ResizableHeaderCell } from './columnResize'
import { columnLayoutStorageKey, measureTableWidth, withFlexibleColumn, withTrailingFill, type ColumnLayoutSpec } from './tableColumnLayout'
import { useColumnLayout } from './useColumnLayout'

/**
 * 列宽、显示列和铺满剩余宽度。`specs` 必须是稳定常量。
 * 指定列保持设定宽度，其余列平分容器多出来的宽度；`growFixed` 时多出的宽度全部给指定列。
 * 一旦开始拖动某一列，其余列都按当前显示的宽度固定住，不再随之变化：只有被拖的列从实际宽度开始跟手，
 * 它右边的列整体位移（总宽超出时出现横向滚动，没有最大宽度限制）；合计比容器窄时多出的宽度给最后一列，右侧不留空白；
 * 恢复默认后重新铺满。
 */
export function useAdjustableColumns<T extends { key: string; title?: ReactNode }>(
  tableId: string,
  userId: number | null | undefined,
  specs: readonly ColumnLayoutSpec[],
  columns: readonly T[],
  fixedKey: string,
  hostRef: RefObject<HTMLDivElement | null>,
  active = true,
  growFixed = false,
  /** Width of columns the table adds itself (row selection, expand), left out when filling the container. */
  reservedWidth = 0,
) {
  const layout = useColumnLayout(columnLayoutStorageKey(tableId, userId ?? undefined), specs)
  const [tableWidth, setTableWidth] = useState(0)
  // The column the user last resized in this view; it never receives the trailing fill (see withTrailingFill).
  const [resizedKey, setResizedKey] = useState<string>()
  const observerRef = useRef<ResizeObserver | null>(null)
  useLayoutEffect(() => {
    if (!active) return
    const host = hostRef.current
    if (!host) return
    let frame = 0
    const measure = () => {
      const next = Math.max(0, measureTableWidth(host) - reservedWidth)
      setTableWidth((current) => (current === next ? current : next))
    }
    measure()
    const observer = new ResizeObserver(() => {
      cancelAnimationFrame(frame)
      frame = requestAnimationFrame(measure)
    })
    observer.observe(host)
    observerRef.current = observer
    return () => {
      cancelAnimationFrame(frame)
      observer.disconnect()
      observerRef.current = null
    }
  }, [active, hostRef, reservedWidth])
  // Arco replaces the container and body as data arrives, so watch the current ones after every render
  // (observing an element twice is a no-op). A vertical scrollbar that appears with the rows shrinks only
  // the body's content box, so without the body the columns would keep a width that overflows by the scrollbar.
  useLayoutEffect(() => {
    const host = hostRef.current
    const observer = observerRef.current
    if (!host || !observer) return
    for (const selector of ['.arco-table-container', '.arco-table-body']) {
      const element = host.querySelector(selector)
      if (element) observer.observe(element)
    }
  })
  const components = useMemo(() => ({ header: { cell: ResizableHeaderCell } }), [])
  // 有了手动宽度之后不再自动分配多余宽度，否则拖一列会带动别的列变宽或变窄。
  // 铺满只给指定的那一列（growFixed）时，它被固定后才停止；否则任何手动宽度都会停止铺满。
  const filling = growFixed ? !(fixedKey in layout.widths) : Object.keys(layout.widths).length === 0
  const applied = layout.apply([...columns])
  const sized = filling ? withFlexibleColumn(applied, tableWidth, fixedKey, growFixed) : withTrailingFill(applied, tableWidth, resizedKey)
  const scrollX = sized.reduce((sum, column) => sum + column.width, 0)
  const sizedRef = useRef(sized)
  sizedRef.current = sized
  const { actions: layoutActions } = layout
  const actions = useMemo(() => {
    const shown = (key: string) => sizedRef.current.find((column) => column.key === key)?.width
    // The first manual change freezes every other column at the width it is showing now.
    const pinOthers = (changing: string) => {
      if (Object.keys(layout.widths).length > 0) return
      for (const column of sizedRef.current) {
        if (column.key !== changing) layoutActions.setWidth(column.key, column.width)
      }
    }
    return {
      ...layoutActions,
      widthOf: (key: string) => shown(key) ?? layoutActions.widthOf(key),
      setWidth: (key: string, width: number | null) => {
        if (width !== null) {
          pinOthers(key)
          setResizedKey(key)
        }
        layoutActions.setWidth(key, width)
      },
      beginResize: (key: string, clientX: number) => {
        pinOthers(key)
        setResizedKey(key)
        layoutActions.beginResize(key, clientX, shown(key))
      },
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps -- layout.widths is read at call time through the latest render's closure
  }, [layoutActions, layout.widths])
  return {
    hostClassName: 'adjustable-table-host',
    tableClassName: 'page-table--resizable',
    columns: sized,
    components,
    scrollX,
    actions,
    hidden: layout.hidden,
    toggle: layout.toggle,
    showAll: layout.showAll,
    hideOptional: layout.hideOptional,
    reset: layout.reset,
  }
}
