import { useLayoutEffect, useMemo, useState, type ReactNode, type RefObject } from 'react'
import { ResizableHeaderCell } from './columnResize'
import { columnLayoutStorageKey, measureTableWidth, withFlexibleColumn, type ColumnLayoutSpec } from './tableColumnLayout'
import { useColumnLayout } from './useColumnLayout'

/**
 * 列宽、显示列和铺满剩余宽度。`specs` 必须是稳定常量。
 * 指定列保持设定宽度，其余列平分容器多出来的宽度。
 */
export function useAdjustableColumns<T extends { key: string; title?: ReactNode }>(
  tableId: string,
  userId: number | null | undefined,
  specs: readonly ColumnLayoutSpec[],
  columns: readonly T[],
  fixedKey: string,
  hostRef: RefObject<HTMLDivElement | null>,
  active = true,
) {
  const layout = useColumnLayout(columnLayoutStorageKey(tableId, userId ?? undefined), specs)
  const [tableWidth, setTableWidth] = useState(0)
  useLayoutEffect(() => {
    if (!active) return
    const host = hostRef.current
    if (!host) return
    let frame = 0
    const measure = () => {
      const next = measureTableWidth(host)
      setTableWidth((current) => (current === next ? current : next))
    }
    measure()
    const observer = new ResizeObserver(() => {
      cancelAnimationFrame(frame)
      frame = requestAnimationFrame(measure)
    })
    observer.observe(host)
    const container = host.querySelector('.arco-table-container')
    if (container) observer.observe(container)
    return () => {
      cancelAnimationFrame(frame)
      observer.disconnect()
    }
  }, [active, hostRef])
  const components = useMemo(() => ({ header: { cell: ResizableHeaderCell } }), [])
  const sized = withFlexibleColumn(layout.apply([...columns]), tableWidth, fixedKey)
  const scrollX = sized.reduce((sum, column) => sum + column.width, 0)
  return {
    hostClassName: 'adjustable-table-host',
    tableClassName: 'page-table--resizable',
    columns: sized,
    components,
    scrollX,
    actions: layout.actions,
    hidden: layout.hidden,
    toggle: layout.toggle,
    showAll: layout.showAll,
    hideOptional: layout.hideOptional,
    reset: layout.reset,
  }
}
