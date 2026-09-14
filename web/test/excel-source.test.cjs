const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const vm = require('node:vm')
const ts = require('typescript')
const ExcelJS = require('exceljs')

function loadTs(relativePath, mocks = {}, globals = {}) {
  const filename = path.resolve(__dirname, '..', relativePath)
  const source = ts.transpileModule(fs.readFileSync(filename, 'utf8'), {
    compilerOptions: {
      module: ts.ModuleKind.CommonJS,
      target: ts.ScriptTarget.ES2022,
      esModuleInterop: true,
    },
  }).outputText
  const exports = {}
  vm.runInNewContext(source, {
    exports,
    module: { exports },
    console,
    setTimeout,
    clearTimeout,
    URL,
    URLSearchParams,
    AbortController,
    ArrayBuffer,
    Uint8Array,
    ...globals,
    require: name => {
      if (name in mocks) return mocks[name]
      return require(name)
    },
  }, { filename })
  return exports
}

function loadExcelSource() {
  const color = loadTs('vendor/vue-office-excel/core/packages/vue-excel/src/color.js')
  const media = loadTs(
    'vendor/vue-office-excel/core/packages/vue-excel/src/media.js',
    {},
    { window: { devicePixelRatio: 1 }, WeakMap },
  )
  return loadTs('vendor/vue-office-excel/core/packages/vue-excel/src/excel.js', {
    '../../../utils/url': {},
    './color': color,
    './media': media,
  })
}

test('auto-fit handles wrapped content, line breaks, merged titles, hidden cells and scoped columns', () => {
  const { fitDimensions } = loadTs('vendor/vue-office-excel/auto-fit.js', {
    './core/packages/vue-excel/src/x-spreadsheet/core/font.js': { getFontSizePxByPt: pt => pt * 4 / 3 },
  })
  const merged = { sri: 0, eri: 0, sci: 0, eci: 3 }
  const data = {
    rows: { _: { 0: { cells: { 0: { text: '合并标题'.repeat(100) }, 1: { text: '重复主格' } } }, 1: { cells: { 0: { text: '长段落'.repeat(100) }, 1: { text: '甲\n乙\n丙' }, 2: { text: '隐藏列内容' } } } }, isHide: () => false, getHeight: () => 24 },
    cols: { isHide: () => false, getWidth: ci => ci === 2 ? 0.1 : 100 },
    merges: { getFirstIncludes: (ri, ci) => ri === 0 && ci < 4 ? merged : null },
    getCellStyleOrDefault: () => ({ textwrap: true, font: { size: 12, name: 'Arial' } }),
  }
  const context = { measureText: text => ({ width: text.length * 10 }) }
  const all = fitDimensions(data, context)
  assert.equal(all.widths.get(0), 360)
  assert.equal(all.widths.get(1), 60)
  assert.equal(all.widths.has(2), false, 'hidden columns stay hidden')
  assert.equal(all.widths.has(3), false, 'merged titles do not widen every covered column')
  assert.ok(all.heights.get(1) > 60, 'long paragraphs get enough wrapped lines')
  assert.ok(all.heights.get(0) > 24, 'merged title is measured across combined widths')
  const scoped = fitDimensions(data, context, 'col', 1, 1)
  assert.deepEqual(Array.from(scoped.widths.keys()), [1])
  assert.equal(scoped.heights.size, 0)
  const rows = fitDimensions(data, context, 'row', 1, 1)
  assert.equal(rows.widths.size, 0)
  assert.deepEqual(Array.from(rows.heights.keys()), [1])
})

test('preview resize uses CSS pointer coordinates and keeps the minimum size', () => {
  let move, up
  const Resizer = loadTs('vendor/vue-office-excel/core/packages/vue-excel/src/x-spreadsheet/component/resizer.js', {
    './element': {}, '../config': {},
    './event': { mouseMoveUp(_window, onMove, onUp) { move = onMove; up = onUp } },
  }, { window: {} }).default
  for (const vertical of [false, true]) {
    const resizer = Object.create(Resizer.prototype)
    let size
    Object.assign(resizer, {
      vertical, minDistance: 24, cRect: { left: 0, top: 0, width: 108, height: 32 },
      el: { css() {} }, lineEl: { show() {}, hide() {} }, hide() {},
      finishedFn(_rect, value) { size = value },
    })
    resizer.mousedownHandler({ clientX: 100, clientY: 100 })
    move({ buttons: 1, clientX: 140, clientY: 140, movementX: 80, movementY: 80 })
    up()
    assert.equal(size, vertical ? 148 : 72, 'physical movement units must not double the CSS resize delta')
    resizer.mousedownHandler({ clientX: 100, clientY: 100 })
    move({ buttons: 1, clientX: -500, clientY: -500 })
    up()
    assert.equal(size, 24)
  }
})

test('double-click copy writes complete text and always cleans up its temporary selection', () => {
  for (const fail of [false, true]) {
    let listener, removed = false, focused = false, copied
    const helper = { style: {}, focus() {}, select() {}, remove() { removed = true } }
    const browserWindow = {
      addEventListener(type, next, capture) { assert.equal(type, 'copy'); assert.equal(capture, true); listener = next },
      removeEventListener(type, current) { assert.equal(type, 'copy'); assert.equal(current, listener); listener = undefined },
    }
    const browserDocument = {
      activeElement: { focus() { focused = true } },
      createElement: () => helper,
      body: { append() {} },
      execCommand(command) {
        assert.equal(command, 'copy')
        if (fail) throw new Error('clipboard unavailable')
        listener({ clipboardData: { setData(type, value) { assert.equal(type, 'text/plain'); copied = value } }, preventDefault() {}, stopImmediatePropagation() {} })
        return true
      },
    }
    const { copyCellText } = loadTs('vendor/vue-office-excel/cell-content.js', {}, { window: browserWindow, document: browserDocument })
    const content = '<b>原文</b>\n含制表符\t' + '长文本'.repeat(100)
    assert.equal(copyCellText(content), !fail)
    if (!fail) assert.equal(copied, content)
    assert.equal(removed, true)
    assert.equal(focused, true)
    assert.equal(listener, undefined)
  }
})

test('selected cell content resolves merged masters and preserves complete text and falsy values', () => {
  const { selectedCellContent } = loadTs('vendor/vue-office-excel/cell-content.js')
  const book = new ExcelJS.Workbook()
  const sheet = book.addWorksheet('内容')
  sheet.mergeCells('A1:C2')
  const fullText = '<b>完整原文</b>\n' + '长内容'.repeat(300)
  sheet.getCell('A1').value = fullText
  const cells = { '0:0': { text: fullText }, '2:0': { text: 0 }, '3:0': { text: false } }
  const viewer = { sheetIndex: 0, workbookDataSource: { _worksheets: [sheet] }, xs: { sheet: { data: { getCell: (r, c) => cells[`${r}:${c}`] } } } }
  assert.equal(selectedCellContent(viewer, 1, 2).address, 'A1')
  assert.equal(selectedCellContent(viewer, 1, 2).text, fullText)
  assert.equal(selectedCellContent(viewer, 2, 0).text, 0)
  assert.equal(selectedCellContent(viewer, 3, 0).text, false)
  assert.equal(selectedCellContent(viewer, 4, 0).text, '')
  assert.equal(selectedCellContent(viewer, -1, 0).address, '')
})

test('ExcelJS conversion keeps point fonts, converts row height to CSS pixels, and preserves image/style-only geometry', async () => {
  const sourceBook = new ExcelJS.Workbook()
  const sheet = sourceBook.addWorksheet('格式和图片')
  sheet.getCell('A1').value = '18pt'
  sheet.getCell('A1').font = { name: 'Arial', size: 18, bold: true }
  sheet.getRow(1).height = 18
  sheet.getColumn(10).width = 20
  sheet.getColumn(10).font = { name: 'Calibri', size: 14 }
  const imageId = sourceBook.addImage({
    base64: 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=',
    extension: 'png',
  })
  sheet.addImage(imageId, {
    tl: { col: 12, row: 4 },
    ext: { width: 180, height: 60 },
  })

  const engine = loadExcelSource()
  const workbook = await engine.readExcelData(await sourceBook.xlsx.writeBuffer(), false)
  const { workbookData, workbookSource } = engine.transferExcelToSpreadSheet(workbook, {
    minRowLength: 0,
    minColLength: 0,
  })
  const data = workbookData[0]
  const a1Style = data.styles[data.rows[0].cells[0].style]

  assert.equal(data.rows[0].height, 24)
  assert.equal(a1Style.font.size, 18, 'font remains in points for table.js to convert exactly once')
  assert.equal(data.cols[9].width, 120, 'a style-only column retains its declared geometry')
  assert.equal(data.cols.len, 15, 'the full one-cell image extent remains horizontally reachable')
  assert.equal(data.rows.len, 7, 'the full one-cell image extent remains vertically reachable')
  assert.deepEqual(
    Object.keys(data.rows).filter(key => key !== 'len'),
    ['0'],
    'image bounds extend scroll geometry without materializing a dense rectangle',
  )
  assert.equal(data.cols[14], undefined, 'image-only trailing columns remain sparse')
  assert.ok(workbookSource._worksheets[0]._columns.length >= 10, 'conversion does not truncate ExcelJS source columns')
})

test('font fallback converts unmapped point sizes to CSS pixels', () => {
  const font = loadTs(
    'vendor/vue-office-excel/core/packages/vue-excel/src/x-spreadsheet/core/font.js',
    { './_.prototypes': {} },
  )
  assert.equal(font.getFontSizePxByPt(18), 24)
  assert.equal(font.getFontSizePxByPt(13), 13 * 4 / 3)
})

test('media geometry handles one-cell and two-cell anchors in CSS pixels', () => {
  const media = loadTs(
    'vendor/vue-office-excel/core/packages/vue-excel/src/media.js',
    {},
    { window: { devicePixelRatio: 1 }, WeakMap },
  )
  const oneCell = media.calcPosition(
    {},
    { tl: { nativeCol: 0, nativeRow: 0 }, ext: { width: 120, height: 80 } },
    null,
    { pixelRatio: 2 },
  )
  assert.deepEqual(
    { x: oneCell.x, y: oneCell.y, width: oneCell.width, height: oneCell.height },
    { x: 120, y: 50, width: 240, height: 160 },
  )

  const sheet = {
    _columns: [{ width: 10 }, { width: 20 }],
    _rows: [{ height: 18 }, { height: 30 }],
  }
  const twoCell = media.calcPosition(sheet, {
    tl: { nativeCol: 0, nativeColOff: 9525, nativeRow: 0, nativeRowOff: 9525 },
    br: { nativeCol: 2, nativeRow: 2 },
  }, null, { pixelRatio: 1, widthOffset: 2, heightOffset: 3 })
  assert.equal(twoCell.x, 61)
  assert.equal(twoCell.y, 26)
  assert.equal(twoCell.width, 183)
  assert.equal(twoCell.height, 69)
})

test('media image bytes retain typed-array boundaries', () => {
  const media = loadTs(
    'vendor/vue-office-excel/core/packages/vue-excel/src/media.js',
    {},
    { window: { devicePixelRatio: 1 }, WeakMap },
  )
  const backing = new Uint8Array([1, 2, 3, 4, 5])
  assert.deepEqual(
    Array.from(media.toImageBytes({ buffer: backing.subarray(1, 4) })),
    [2, 3, 4],
  )
})

test('late image completion cannot draw over the current sheet and object URLs are revoked', async () => {
  const pending = []
  const revoked = []
  class FakeImage {
    constructor() {
      this.width = 10
      this.height = 10
    }

    set src(value) {
      this.url = value
      pending.push(this)
    }
  }
  class FakeBlob {
    constructor(parts) {
      this.parts = parts
    }
  }
  let objectUrl = 0
  const media = loadTs(
    'vendor/vue-office-excel/core/packages/vue-excel/src/media.js',
    {},
    {
      window: { devicePixelRatio: 1 },
      WeakMap,
      Image: FakeImage,
      Blob: FakeBlob,
      URL: {
        createObjectURL: () => `blob:${++objectUrl}`,
        revokeObjectURL: value => revoked.push(value),
      },
    },
  )
  const draws = []
  const ctx = { drawImage: (...args) => draws.push(args) }
  const range = { tl: {}, ext: { width: 10, height: 10 } }
  const first = { buffer: new Uint8Array([1]), extension: 'png' }
  const second = { buffer: new Uint8Array([2]), extension: 'png' }

  media.renderImage(ctx, [first], { _media: [{ type: 'image', imageId: 0, range }] })
  media.renderImage(ctx, [second], { _media: [{ type: 'image', imageId: 0, range }] })
  pending[0].onload()
  await Promise.resolve()
  assert.equal(draws.length, 0, 'the stale sheet is ignored')
  pending[1].onload()
  await Promise.resolve()
  assert.equal(draws.length, 1, 'the current sheet is drawn once')
  assert.deepEqual(revoked, ['blob:1', 'blob:2'])
})
