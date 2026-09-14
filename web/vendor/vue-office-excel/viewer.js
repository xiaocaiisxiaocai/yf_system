import { init } from './core/packages/js-excel/src/main.js'
import Spreadsheet from './core/packages/vue-excel/src/x-spreadsheet/index.js'
import zh from './core/packages/vue-excel/src/x-spreadsheet/locale/zh-cn.js'
import { renderImage } from './core/packages/vue-excel/src/media.js'
import './core/packages/js-excel/index.css'

Spreadsheet.locale('zh-cn', zh)
let viewer
let channel
let loaded = false
const host = document.getElementById('viewer')
const notify = type => parent.postMessage({ type, channel }, '*')
addEventListener('message', async event => {
  if (event.source !== parent || event.data?.type !== 'excel:load' || loaded) return
  if (!(event.data.buffer instanceof ArrayBuffer) || typeof event.data.channel !== 'string') return
  loaded = true
  channel = event.data.channel
  try {
    viewer = init(host, { xls: event.data.xls === true, minColLength: 10, minRowLength: 30, showContextmenu: false })
    // The authenticated bytes are supplied by the parent. Never fetch a document
    // URL or expose the library's save/export API from this isolated viewer.
    await viewer.renderExcel(event.data.buffer)
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
addEventListener('contextmenu', event => event.preventDefault())
addEventListener('keydown', event => {
  const key = event.key.toLowerCase()
  if (['delete', 'backspace'].includes(key) || (event.ctrlKey || event.metaKey) && ['b', 'i', 'u', 'z', 'y', 'x', 'v', 's', 'p'].includes(key)) {
    event.preventDefault()
    event.stopImmediatePropagation()
  }
}, true)
