import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import {
  readColumnLayout, withColumnWidth, withHiddenColumns, writeColumnLayout,
  type ColumnLayout, type ColumnLayoutSpec,
} from './tableColumnLayout'

/**
 * 列宽与隐藏列保存在本机，按表格和用户分开。`specs` 需为稳定常量。
 * 拖动期间先改内存，松手后再写入。
 */
export function useColumnLayout(storageKey: string, specs: readonly ColumnLayoutSpec[]) {
  const [layout, setLayout] = useState(() => readColumnLayout(storageKey, specs))
  const specsRef = useRef(specs)
  const layoutRef = useRef(layout)
  const storageKeyRef = useRef(storageKey)
  const dragRef = useRef<{ key: string; startX: number; startWidth: number } | null>(null)
  const dragListenerRef = useRef<{ move: (event: PointerEvent) => void; stop: () => void } | null>(null)

  const commit = useCallback((next: ColumnLayout) => {
    layoutRef.current = next
    setLayout(next)
  }, [])

  const setWidth = useCallback((key: string, width: number | null) => {
    commit(withColumnWidth(layoutRef.current, specsRef.current, key, width))
  }, [commit])

  const endDrag = useCallback(() => {
    const listener = dragListenerRef.current
    dragListenerRef.current = null
    if (listener) {
      window.removeEventListener('pointermove', listener.move)
      window.removeEventListener('pointerup', listener.stop)
      window.removeEventListener('pointercancel', listener.stop)
    }
    document.body.classList.remove('table-col-resizing')
    if (!dragRef.current) return
    dragRef.current = null
    writeColumnLayout(storageKeyRef.current, layoutRef.current)
  }, [])

  const beginResize = useCallback((key: string, clientX: number) => {
    const spec = specsRef.current.find((item) => item.key === key)
    if (!spec) return
    endDrag()
    dragRef.current = { key, startX: clientX, startWidth: layoutRef.current.widths[key] ?? spec.defaultWidth }
    document.body.classList.add('table-col-resizing')
    const move = (event: PointerEvent) => {
      const drag = dragRef.current
      if (!drag) return
      setWidth(drag.key, drag.startWidth + event.clientX - drag.startX)
    }
    const stop = () => endDrag()
    dragListenerRef.current = { move, stop }
    window.addEventListener('pointermove', move)
    window.addEventListener('pointerup', stop)
    window.addEventListener('pointercancel', stop)
  }, [endDrag, setWidth])

  const widthOf = useCallback((key: string) => {
    const spec = specsRef.current.find((item) => item.key === key)
    return layoutRef.current.widths[key] ?? spec?.defaultWidth ?? 72
  }, [])

  useEffect(() => { specsRef.current = specs }, [specs])
  useEffect(() => () => endDrag(), [endDrag])

  useEffect(() => {
    if (storageKeyRef.current !== storageKey) {
      endDrag()
      storageKeyRef.current = storageKey
      const loaded = readColumnLayout(storageKey, specsRef.current)
      layoutRef.current = loaded
      setLayout(loaded)
      return
    }
    if (dragRef.current) return
    writeColumnLayout(storageKey, layout)
  }, [endDrag, layout, storageKey])

  const toggle = useCallback((key: string, shown: boolean) => {
    const hidden = new Set(layoutRef.current.hidden)
    if (shown) hidden.delete(key)
    else hidden.add(key)
    commit(withHiddenColumns(layoutRef.current, specsRef.current, hidden))
  }, [commit])

  const showAll = useCallback(() => {
    commit(withHiddenColumns(layoutRef.current, specsRef.current, new Set()))
  }, [commit])

  const hideOptional = useCallback(() => {
    commit(withHiddenColumns(layoutRef.current, specsRef.current, new Set(specsRef.current.map((spec) => spec.key))))
  }, [commit])

  const reset = useCallback(() => {
    commit({ hidden: [], widths: {} })
  }, [commit])

  const actions = useMemo(() => ({ beginResize, setWidth, widthOf }), [beginResize, setWidth, widthOf])

  const apply = <T extends { key: string; title?: ReactNode }>(columns: T[]) => columns.flatMap((column) => {
    const spec = specs.find((item) => item.key === column.key)
    if (!spec) throw new Error(`缺少列配置：${column.key}`)
    if (typeof column.title === 'string' && column.title !== spec.title) throw new Error(`列标题不一致：${column.key}`)
    if (!spec.locked && layout.hidden.includes(column.key)) return []
    return [{ ...column, title: spec.title, width: layout.widths[column.key] ?? spec.defaultWidth }]
  })

  return { apply, actions, hidden: layout.hidden, toggle, showAll, hideOptional, reset }
}
