import { useEffect, type RefObject } from 'react'

/**
 * 在表格正文里按住鼠标拖选文字时只选中起点所在的那一列（如只选工单号的前几行），
 * 否则浏览器会把拖过的整行所有单元格一起选中。
 * 起点所在列记在宿主元素的 `data-select-col`，由 index.css 把其他列设为不可选；下一次按下鼠标前保持不变，选区可以直接复制。
 */
export function useColumnTextSelection(hostRef: RefObject<HTMLElement>) {
  useEffect(() => {
    const host = hostRef.current
    if (!host) return undefined
    const onMouseDown = (event: MouseEvent) => {
      const target = event.target as Element | null
      const cell = event.button === 0 && !event.shiftKey ? target?.closest('tbody td.arco-table-td') : null
      const row = cell?.parentElement
      if (!cell || !row || target?.closest('input, textarea, select, button, a')) {
        host.removeAttribute('data-select-col')
        return
      }
      host.setAttribute('data-select-col', String(Array.prototype.indexOf.call(row.children, cell) + 1))
    }
    host.addEventListener('mousedown', onMouseDown)
    return () => host.removeEventListener('mousedown', onMouseDown)
  }, [hostRef])
}
