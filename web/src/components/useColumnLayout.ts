import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import {
  applyColumnLayout, hasStoredColumnLayout, normalizeColumnLayout, readColumnLayout, withColumnWidth, withHiddenColumns,
  withMovedColumn, withPinnedColumn, writeColumnLayout,
  type ColumnLayout, type ColumnLayoutSpec,
} from './tableColumnLayout'

const EMPTY_LAYOUT: ColumnLayout = { hidden: [], widths: {} }

const knownKeysOf = (specs: readonly ColumnLayoutSpec[]) => new Set(specs.map((spec) => spec.key))

const sameList = (left: readonly string[] | undefined, right: readonly string[] | undefined) =>
  left === right || (left !== undefined && right !== undefined && left.length === right.length && left.every((item, index) => item === right[index]))

function sameColumnLayout(left: ColumnLayout, right: ColumnLayout) {
  if (!sameList(left.hidden, right.hidden) || !sameList(left.order, right.order) || !sameList(left.pinned, right.pinned)) return false
  const leftKeys = Object.keys(left.widths)
  const rightKeys = Object.keys(right.widths)
  return leftKeys.length === rightKeys.length && leftKeys.every((key) => left.widths[key] === right.widths[key])
}

/**
 * 列宽与隐藏列保存在本机，按表格和用户分开。
 * 拖动期间先改内存，松手后再写入；首次挂载与动态列变更时不覆盖已存设置。
 */
export function useColumnLayout(storageKey: string, specs: readonly ColumnLayoutSpec[]) {
  const specsSignature = JSON.stringify(specs.map((spec) => [spec.key, spec.defaultWidth, spec.minWidth ?? null, Boolean(spec.locked)]))
  const [layout, setLayout] = useState(() => readColumnLayout(storageKey, specs))
  const specsRef = useRef(specs)
  const specsSignatureRef = useRef(specsSignature)
  const layoutRef = useRef(layout)
  const storageKeyRef = useRef(storageKey)
  const dragRef = useRef<{ key: string; startX: number; startWidth: number } | null>(null)
  const dragListenerRef = useRef<{ move: (event: PointerEvent) => void; stop: () => void } | null>(null)

  const commit = useCallback((next: ColumnLayout) => {
    if (next === layoutRef.current) return
    layoutRef.current = next
    setLayout(next)
    if (!dragRef.current) writeColumnLayout(storageKeyRef.current, next, knownKeysOf(specsRef.current))
  }, [])

  const setWidth = useCallback((key: string, width: number | null) => {
    commit(withColumnWidth(layoutRef.current, specsRef.current, key, width))
  }, [commit])

  /** Sets many widths at once (one render, one save). */
  const setWidths = useCallback((widths: Record<string, number>) => {
    commit(Object.entries(widths).reduce((next, [key, width]) => withColumnWidth(next, specsRef.current, key, width), layoutRef.current))
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
    writeColumnLayout(storageKeyRef.current, layoutRef.current, knownKeysOf(specsRef.current))
  }, [])

  /** `startWidth`：表格实际显示的宽度（含自动铺满分到的部分），不传则用已存或默认宽度。 */
  const beginResize = useCallback((key: string, clientX: number, startWidth?: number) => {
    const spec = specsRef.current.find((item) => item.key === key)
    if (!spec) return
    endDrag()
    dragRef.current = { key, startX: clientX, startWidth: startWidth ?? layoutRef.current.widths[key] ?? spec.defaultWidth }
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

  useEffect(() => () => endDrag(), [endDrag])

  useEffect(() => {
    specsRef.current = specs
    if (storageKeyRef.current !== storageKey) {
      endDrag()
      storageKeyRef.current = storageKey
      specsSignatureRef.current = specsSignature
      const loaded = readColumnLayout(storageKey, specs)
      layoutRef.current = loaded
      setLayout(loaded)
      return
    }
    if (specsSignatureRef.current !== specsSignature) {
      specsSignatureRef.current = specsSignature
      const loaded = hasStoredColumnLayout(storageKey)
        ? readColumnLayout(storageKey, specs)
        : normalizeColumnLayout(layoutRef.current, specs)
      const activeDrag = dragRef.current
      const next = activeDrag && activeDrag.key in layoutRef.current.widths
        ? withColumnWidth(loaded, specs, activeDrag.key, layoutRef.current.widths[activeDrag.key])
        : loaded
      if (!sameColumnLayout(layoutRef.current, next)) {
        layoutRef.current = next
        setLayout(next)
      }
    }
  }, [endDrag, specs, specsSignature, storageKey])

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

  /** `arranged`：当前显示顺序里全部可移动列的 key。 */
  const move = useCallback((key: string, target: string, arranged: readonly string[]) => {
    commit(withMovedColumn(layoutRef.current, arranged, key, target))
  }, [commit])

  const pin = useCallback((key: string, pinned: boolean, current: readonly string[]) => {
    commit(withPinnedColumn(layoutRef.current, current, key, pinned))
  }, [commit])

  const reset = useCallback(() => {
    endDrag()
    const next: ColumnLayout = { ...EMPTY_LAYOUT, hidden: [], widths: {} }
    layoutRef.current = next
    setLayout(next)
    writeColumnLayout(storageKeyRef.current, next)
  }, [endDrag])

  const limitsOf = useCallback((key: string) => {
    const spec = specsRef.current.find((item) => item.key === key)
    return { min: spec?.minWidth ?? 72 }
  }, [])

  const actions = useMemo(() => ({ beginResize, setWidth, setWidths, widthOf, limitsOf }), [beginResize, setWidth, setWidths, widthOf, limitsOf])

  const apply = <T extends { key: string; title?: ReactNode }>(
    columns: readonly T[],
  ): Array<T & { width: number }> =>
    applyColumnLayout(columns, specs, layout, import.meta.env.DEV)

  return { apply, actions, layout, widths: layout.widths, hidden: layout.hidden, toggle, showAll, hideOptional, move, pin, reset }
}
