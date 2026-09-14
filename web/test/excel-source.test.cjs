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
