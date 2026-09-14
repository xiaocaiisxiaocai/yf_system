import { init } from './core/packages/js-excel/src/main.js'
import Spreadsheet from './core/packages/vue-excel/src/x-spreadsheet/index.js'
import zh from './core/packages/vue-excel/src/x-spreadsheet/locale/zh-cn.js'
import { renderImage } from './core/packages/vue-excel/src/media.js'
import { createCellContentBar, selectedCellContent, copyCellText } from './cell-content.js'
import { fitDimensions } from './auto-fit.js'
import './core/packages/js-excel/index.css'
import './viewer.css'

Spreadsheet.locale('zh-cn', zh)
let viewer
let channel
let loaded = false
const host = document.getElementById('viewer')
const contentBar = createCellContentBar(host)
const copyStatus = document.createElement('div')
copyStatus.className = 'excel-copy-status'
copyStatus.setAttribute('role', 'status')
copyStatus.hidden = true
document.body.append(copyStatus)
let copyStatusTimer
const fitPreview = (axis = 'all', index) => {
  const data = viewer.xs.sheet.data
  const range = data.selector.range
  let start = 0, end = Infinity
  if (axis !== 'all') {
    start = axis === 'row' ? range.sri : range.sci
    end = axis === 'row' ? range.eri : range.eci
    if (index < start || index > end) start = end = index
  }
  const measure = document.createElement('canvas').getContext('2d')
  const { widths, heights } = fitDimensions(data, measure, axis, start, end, devicePixelRatio || 1)
  const source = viewer.workbookDataSource._worksheets[viewer.sheetIndex]
  widths.forEach((width, ci) => { data.cols.setWidth(ci, width); source.getColumn(ci + 1).width = width / 6 })
  heights.forEach((height, ri) => { data.rows.setHeight(ri, height); source.getRow(ri + 1).height = height * 3 / 4 })
  viewer.xs.sheet.resetData(data)
  renderImage(viewer.ctx, viewer.mediasSource, source, viewer.offset)
  copyStatus.textContent = axis === 'all' ? '已自适应当前工作表行高和列宽' : axis === 'row' ? '已自适应行高' : '已自适应列宽'
  copyStatus.hidden = false
  clearTimeout(copyStatusTimer)
  copyStatusTimer = setTimeout(() => { copyStatus.hidden = true }, 2000)
}
const showCell = (rowIndex, columnIndex) => {
  const content = selectedCellContent(viewer, rowIndex, columnIndex)
  contentBar.show(content.address, content.text)
}
const notify = type => parent.postMessage({ type, channel }, '*')
addEventListener('message', async event => {
  if (event.source !== parent || event.data?.type !== 'excel:load' || loaded) return
  if (!(event.data.buffer instanceof ArrayBuffer) || typeof event.data.channel !== 'string') return
  loaded = true
  channel = event.data.channel
  try {
    viewer = init(host, { xls: event.data.xls === true, minColLength: 10, minRowLength: 30, showContextmenu: false,
      cellSelected: ({ rowIndex, columnIndex }) => showCell(rowIndex, columnIndex),
      cellsSelected: ({ startRowIndex, startColumnIndex }) => showCell(startRowIndex, startColumnIndex),
    })
    // The authenticated bytes are supplied by the parent. Never fetch a document
    // URL or expose the library's save/export API from this isolated viewer.
    await viewer.renderExcel(event.data.buffer)
    // Resize the in-memory preview only. Keep the image anchor geometry in sync
    // before the spreadsheet paints its updated rows/columns.
    for (const axis of ['row', 'col']) {
      const resizer = viewer.xs.sheet[`${axis}Resizer`]
      resizer.autoFitFn = rect => fitPreview(axis, axis === 'row' ? rect.ri : rect.ci)
      resizer.hoverEl.el.title = axis === 'row' ? '拖动调整行高，双击自适应' : '拖动调整列宽，双击自适应'
      const finish = resizer.finishedFn
      resizer.finishedFn = (rect, distance) => {
        const data = viewer.xs.sheet.data
        const range = data.selector.range
        const index = axis === 'row' ? rect.ri : rect.ci
        const start = axis === 'row' ? range.sri : range.sci
        const end = axis === 'row' ? range.eri : range.eci
        const inSelection = index >= start && index <= end
        const sheet = viewer.workbookDataSource._worksheets[viewer.sheetIndex]
        for (let i = inSelection ? start : index; i <= (inSelection ? end : index); i++) {
          if (axis === 'row') sheet.getRow(i + 1).height = distance * 3 / 4
          else sheet.getColumn(i + 1).width = distance / 6
        }
        finish(rect, distance)
        renderImage(viewer.ctx, viewer.mediasSource, sheet, viewer.offset)
      }
    }
    const swap = viewer.xs.bottombar.swapFunc
    viewer.xs.bottombar.swapFunc = function (...args) {
      swap.apply(this, args)
      contentBar.show('', '')
    }
    notify('excel:rendered')
  } catch {
    notify('excel:error')
  }
})
new ResizeObserver(() => {
  if (!viewer?.xs || !loaded) return
  viewer.xs.sheet.resetData(viewer.xs.sheet.data)
  renderImage(viewer.ctx, viewer.mediasSource, viewer.workbookDataSource._worksheets[viewer.sheetIndex], viewer.offset)
}).observe(host)
host.addEventListener('dblclick', event => {
  if (!viewer?.xs || !event.target.closest?.('.x-spreadsheet-overlayer')) return
  const grid = host.querySelector('.x-spreadsheet-overlayer-content').getBoundingClientRect()
  if (event.clientX < grid.left && event.clientY < grid.top) { fitPreview(); return }
  if (event.clientX < grid.left || event.clientY < grid.top) return
  const { ri, ci } = viewer.xs.sheet.data.selector
  if (ri < 0 || ci < 0) return
  const content = selectedCellContent(viewer, ri, ci)
  const copied = copyCellText(content.text)
  copyStatus.textContent = copied ? '已复制单元格内容' : '复制失败，请选中文字后按 Ctrl+C'
  copyStatus.hidden = false
  clearTimeout(copyStatusTimer)
  copyStatusTimer = setTimeout(() => { copyStatus.hidden = true }, 2000)
})
addEventListener('contextmenu', event => event.preventDefault())
addEventListener('keydown', event => {
  if (event.target.closest?.('.excel-cell-content')) return
  const key = event.key.toLowerCase()
  if (['delete', 'backspace'].includes(key) || (event.ctrlKey || event.metaKey) && ['b', 'i', 'u', 'z', 'y', 'x', 'v', 's', 'p'].includes(key)) {
    event.preventDefault()
    event.stopImmediatePropagation()
  }
}, true)
