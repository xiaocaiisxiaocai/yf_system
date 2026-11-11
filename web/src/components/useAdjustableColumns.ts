import { useLayoutEffect, useMemo, useRef, useState, type ReactNode, type RefObject } from 'react'
import { ResizableHeaderCell } from './columnResize'
import { columnLayoutStorageKey, measureTableWidth, withFlexibleColumn, type ColumnLayoutSpec } from './tableColumnLayout'
import { useColumnLayout } from './useColumnLayout'

/**
 * 列宽、显示列和铺满剩余宽度。`specs` 必须是稳定常量。
 * 指定列保持设定宽度，其余列平分容器多出来的宽度；`growFixed` 时多出的宽度全部给指定列。
 * `growFixed` 下一旦开始拖动列宽，指定列（左边）按当前宽度固定住、不再吸收变化：被拖的列从实际宽度开始跟手，
 * 它右边的列随之位移（总宽超出时出现横向滚动）；恢复默认或双击该列表头边缘后重新铺满。
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
    const container = host.querySelector('.arco-table-container')
    if (container) observer.observe(container)
    return () => {
      cancelAnimationFrame(frame)
      observer.disconnect()
    }
  }, [active, hostRef, reservedWidth])
  const components = useMemo(() => ({ header: { cell: ResizableHeaderCell } }), [])
  // 指定列有了手动宽度就不再把多余宽度分给它。
  const growPinned = growFixed && fixedKey in layout.widths
  const sized = withFlexibleColumn(layout.apply([...columns]), growPinned ? 0 : tableWidth, fixedKey, growFixed)
  const scrollX = sized.reduce((sum, column) => sum + column.width, 0)
  const sizedRef = useRef(sized)
  sizedRef.current = sized
  const { actions: layoutActions } = layout
  const actions = useMemo(() => {
    if (!growFixed) return layoutActions
    const shown = (key: string) => sizedRef.current.find((column) => column.key === key)?.width
    const pinGrowColumn = (changing: string) => {
      const grown = shown(fixedKey)
      if (changing !== fixedKey && grown !== undefined && !(fixedKey in layout.widths)) layoutActions.setWidth(fixedKey, grown)
    }
    return {
      ...layoutActions,
      widthOf: (key: string) => shown(key) ?? layoutActions.widthOf(key),
      setWidth: (key: string, width: number | null) => {
        if (width !== null) pinGrowColumn(key)
        layoutActions.setWidth(key, width)
      },
      beginResize: (key: string, clientX: number) => {
        pinGrowColumn(key)
        layoutActions.beginResize(key, clientX, shown(key))
      },
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps -- layout.widths is read at call time through the latest render's closure
  }, [growFixed, fixedKey, layoutActions, layout.widths])
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
