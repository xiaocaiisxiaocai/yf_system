import { init } from './core/packages/js-excel/src/main.js'
import Spreadsheet from './core/packages/vue-excel/src/x-spreadsheet/index.js'
import zh from './core/packages/vue-excel/src/x-spreadsheet/locale/zh-cn.js'
import { renderImage } from './core/packages/vue-excel/src/media.js'
import { createCellContentBar, selectedCellContent } from './cell-content.js'
import './core/packages/js-excel/index.css'
import './viewer.css'

Spreadsheet.locale('zh-cn', zh)
let viewer
let channel
let loaded = false
const host = document.getElementById('viewer')
const contentBar = createCellContentBar(host)
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
host.addEventListener('dblclick', () => contentBar.expand())
addEventListener('contextmenu', event => event.preventDefault())
addEventListener('keydown', event => {
  if (event.target.closest?.('.excel-cell-content')) return
  const key = event.key.toLowerCase()
  if (['delete', 'backspace'].includes(key) || (event.ctrlKey || event.metaKey) && ['b', 'i', 'u', 'z', 'y', 'x', 'v', 's', 'p'].includes(key)) {
    event.preventDefault()
    event.stopImmediatePropagation()
  }
}, true)
