import * as ExcelJS from 'exceljs'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { fitDimensions } from '../../../../vendor/vue-office-excel/auto-fit.js'
import {
  copyCellText,
  selectedCellContent,
} from '../../../../vendor/vue-office-excel/cell-content.js'
import {
  readExcelData,
  transferExcelToSpreadSheet,
} from '../../../../vendor/vue-office-excel/core/packages/vue-excel/src/excel.js'
import {
  calcPosition,
  renderImage,
  toImageBytes,
} from '../../../../vendor/vue-office-excel/core/packages/vue-excel/src/media.js'
import Resizer from '../../../../vendor/vue-office-excel/core/packages/vue-excel/src/x-spreadsheet/component/resizer.js'
import {
  selectorSet,
  sheetInitEvents,
} from '../../../../vendor/vue-office-excel/core/packages/vue-excel/src/x-spreadsheet/component/sheet.js'
import { h } from '../../../../vendor/vue-office-excel/core/packages/vue-excel/src/x-spreadsheet/component/element.js'
import { getFontSizePxByPt } from '../../../../vendor/vue-office-excel/core/packages/vue-excel/src/x-spreadsheet/core/font.js'

describe('Excel vendor behavior migrated from node:test', () => {
  afterEach(() => {
    document.body.replaceChildren()
    vi.unstubAllGlobals()
    vi.restoreAllMocks()
  })

  it('corner and Ctrl+A select every row and column while text fields keep native Ctrl+A', () => {
    const selection = {
      range: { sri: 4, sci: 2, eri: 4, eci: 2 },
      set(ri: number, ci: number) {
        this.range = {
          sri: Math.max(0, ri),
          sci: Math.max(0, ci),
          eri: ri < 0 ? 79 : ri,
          eci: ci < 0 ? 11 : ci,
        }
      },
    }
    const overlayerEl = h('div')
    document.body.append(overlayerEl.el)
    const sheet = {
      selector: selection,
      data: { getCell: () => null },
      focusing: true,
      contextMenu: { setMode() {}, hide() {} },
      trigger() {},
      overlayerEl,
      rowResizer: {},
      colResizer: {},
      verticalScrollbar: {},
      horizontalScrollbar: {},
      editor: {},
      modalValidation: {},
      toolbar: {},
      sortFilter: {},
      reload() {},
    }

    selectorSet.call(sheet, false, -1, -1)
    expect(selection.range).toEqual({ sri: 0, sci: 0, eri: 79, eci: 11 })

    sheetInitEvents.call(sheet)
    selection.set(2, 3)
    const gridSelect = new KeyboardEvent('keydown', {
      key: 'a', ctrlKey: true, bubbles: true, cancelable: true,
    })
    Object.defineProperty(gridSelect, 'keyCode', { value: 65 })
    window.dispatchEvent(gridSelect)
    expect(selection.range).toEqual({ sri: 0, sci: 0, eri: 79, eci: 11 })
    expect(gridSelect.defaultPrevented).toBe(true)

    sheet.focusing = false
    const nativeSelect = new KeyboardEvent('keydown', {
      key: 'a', ctrlKey: true, bubbles: true, cancelable: true,
    })
    Object.defineProperty(nativeSelect, 'keyCode', { value: 65 })
    window.dispatchEvent(nativeSelect)
    expect(nativeSelect.defaultPrevented).toBe(false)
  })

  it('auto-fit handles wrapped content, line breaks, merged titles, hidden cells and scoped columns', () => {
    const merged = { sri: 0, eri: 0, sci: 0, eci: 3 }
    const data = {
      rows: {
        _: {
          0: { cells: { 0: { text: '合并标题'.repeat(100) }, 1: { text: '重复主格' } } },
          1: { cells: { 0: { text: '长段落'.repeat(100) }, 1: { text: '甲\n乙\n丙' }, 2: { text: '隐藏列内容' } } },
        },
        isHide: () => false,
        getHeight: () => 24,
      },
      cols: {
        isHide: () => false,
        getWidth: (ci: number) => ci === 2 ? 0.1 : 100,
      },
      merges: { getFirstIncludes: (ri: number, ci: number) => ri === 0 && ci < 4 ? merged : null },
      getCellStyleOrDefault: () => ({ textwrap: true, font: { size: 12, name: 'Arial' } }),
    }
    const context = { font: '', measureText: (text: string) => ({ width: text.length * 10 }) }

    const all = fitDimensions(data, context)
    expect(all.widths.get(0)).toBe(360)
    expect(all.widths.get(1)).toBe(60)
    expect(all.widths.has(2)).toBe(false)
    expect(all.widths.has(3)).toBe(false)
    expect(all.heights.get(1)).toBeGreaterThan(60)
    expect(all.heights.get(0)).toBeGreaterThan(24)

    const scoped = fitDimensions(data, context, 'col', 1, 1)
    expect(Array.from(scoped.widths.keys())).toEqual([1])
    expect(scoped.heights.size).toBe(0)
    const rows = fitDimensions(data, context, 'row', 1, 1)
    expect(rows.widths.size).toBe(0)
    expect(Array.from(rows.heights.keys())).toEqual([1])
  })

  it('preview resize uses CSS pointer coordinates and keeps the minimum size', () => {
    for (const vertical of [false, true]) {
      const resizer = Object.create(Resizer.prototype)
      let size: number | undefined
      Object.assign(resizer, {
        vertical,
        minDistance: 24,
        cRect: { left: 0, top: 0, width: 108, height: 32 },
        el: h('div'),
        lineEl: h('div'),
        unhideHoverEl: h('div'),
        hide: Resizer.prototype.hide,
        finishedFn(_rect: unknown, value: number) { size = value },
      })
      document.body.append(resizer.el.el, resizer.lineEl.el)

      resizer.mousedownHandler(new MouseEvent('mousedown', { clientX: 100, clientY: 100 }))
      window.dispatchEvent(new MouseEvent('mousemove', {
        buttons: 1, clientX: 140, clientY: 140,
      }))
      window.dispatchEvent(new MouseEvent('mouseup'))
      expect(size).toBe(vertical ? 148 : 72)

      resizer.mousedownHandler(new MouseEvent('mousedown', { clientX: 100, clientY: 100 }))
      window.dispatchEvent(new MouseEvent('mousemove', {
        buttons: 1, clientX: -500, clientY: -500,
      }))
      window.dispatchEvent(new MouseEvent('mouseup'))
      expect(size).toBe(24)
    }
  })

  it('double-click copy writes complete text and always cleans up its temporary selection', () => {
    for (const fail of [false, true]) {
      const previous = document.createElement('button')
      document.body.append(previous)
      previous.focus()
      let copied: string | undefined
      const execCommand = vi.fn(() => {
        if (fail) throw new Error('clipboard unavailable')
        const event = new Event('copy', { bubbles: true, cancelable: true })
        Object.defineProperty(event, 'clipboardData', {
          value: { setData(type: string, value: string) {
            expect(type).toBe('text/plain')
            copied = value
          } },
        })
        window.dispatchEvent(event)
        return true
      })
      Object.defineProperty(document, 'execCommand', {
        configurable: true,
        value: execCommand,
      })

      const content = '<b>原文</b>\n含制表符\t' + '长文本'.repeat(100)
      expect(copyCellText(content)).toBe(!fail)
      if (!fail) expect(copied).toBe(content)
      expect(document.querySelectorAll('textarea')).toHaveLength(0)
      expect(document.activeElement).toBe(previous)
      expect(execCommand).toHaveBeenCalledWith('copy')
      previous.remove()
    }
  })

  it('selected cell content resolves merged masters and preserves complete text and falsy values', () => {
    const book = new ExcelJS.Workbook()
    const sheet = book.addWorksheet('内容')
    sheet.mergeCells('A1:C2')
    const fullText = '<b>完整原文</b>\n' + '长内容'.repeat(300)
    sheet.getCell('A1').value = fullText
    const cells: Record<string, { text: string | number | boolean }> = {
      '0:0': { text: fullText },
      '2:0': { text: 0 },
      '3:0': { text: false },
    }
    const viewer = {
      sheetIndex: 0,
      workbookDataSource: { _worksheets: [sheet] },
      xs: { sheet: { data: { getCell: (row: number, column: number) => cells[`${row}:${column}`] } } },
    }

    expect(selectedCellContent(viewer, 1, 2)).toMatchObject({ address: 'A1', text: fullText })
    expect(selectedCellContent(viewer, 2, 0).text).toBe(0)
    expect(selectedCellContent(viewer, 3, 0).text).toBe(false)
    expect(selectedCellContent(viewer, 4, 0).text).toBe('')
    expect(selectedCellContent(viewer, -1, 0).address).toBe('')
  })

  it('ExcelJS conversion keeps point fonts, converts row height to CSS pixels, and preserves image/style-only geometry', async () => {
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

    const sourceBytes = Uint8Array.from(Object.values(await sourceBook.xlsx.writeBuffer()))
    const workbook = await readExcelData(sourceBytes, false)
    const { workbookData, workbookSource } = transferExcelToSpreadSheet(workbook, {
      minRowLength: 0,
      minColLength: 0,
    })
    const data = workbookData[0]
    const a1Style = data.styles[data.rows[0].cells[0].style]

    expect(data.rows[0].height).toBe(24)
    expect(a1Style.font.size).toBe(18)
    expect(data.cols[9]?.width).toBe(120)
    expect(data.cols.len).toBe(15)
    expect(data.rows.len).toBe(7)
    expect(Object.keys(data.rows).filter(key => key !== 'len')).toEqual(['0'])
    expect(data.cols[14]).toBeUndefined()
    expect(workbookSource._worksheets[0]._columns.length).toBeGreaterThanOrEqual(10)
  })

  it('font fallback converts unmapped point sizes to CSS pixels', () => {
    expect(getFontSizePxByPt(18)).toBe(24)
    expect(getFontSizePxByPt(13)).toBe(13 * 4 / 3)
  })

  it('media geometry handles one-cell and two-cell anchors in CSS pixels', () => {
    const oneCell = calcPosition(
      {},
      { tl: { nativeCol: 0, nativeRow: 0 }, ext: { width: 120, height: 80 } },
      null,
      { pixelRatio: 2 },
    )
    expect({ x: oneCell.x, y: oneCell.y, width: oneCell.width, height: oneCell.height })
      .toEqual({ x: 120, y: 50, width: 240, height: 160 })

    const sheet = {
      _columns: [{ width: 10 }, { width: 20 }],
      _rows: [{ height: 18 }, { height: 30 }],
    }
    const twoCell = calcPosition(sheet, {
      tl: { nativeCol: 0, nativeColOff: 9525, nativeRow: 0, nativeRowOff: 9525 },
      br: { nativeCol: 2, nativeRow: 2 },
    }, null, { pixelRatio: 1, widthOffset: 2, heightOffset: 3 })
    expect(twoCell).toMatchObject({ x: 61, y: 26, width: 183, height: 69 })
  })

  it('media image bytes retain typed-array boundaries', () => {
    const backing = new Uint8Array([1, 2, 3, 4, 5])
    expect(Array.from(toImageBytes({ buffer: backing.subarray(1, 4) }))).toEqual([2, 3, 4])
  })

  it('late image completion cannot draw over the current sheet and object URLs are revoked', async () => {
    const pending: Array<{ onload?: () => void; onerror?: () => void }> = []
    class ControlledImage {
      width = 10
      height = 10
      onload?: () => void
      onerror?: () => void

      set src(_value: string) {
        pending.push(this)
      }
    }
    const revoked: string[] = []
    let objectUrl = 0
    vi.stubGlobal('Image', ControlledImage)
    Object.defineProperty(URL, 'createObjectURL', {
      configurable: true,
      value: () => `blob:${++objectUrl}`,
    })
    Object.defineProperty(URL, 'revokeObjectURL', {
      configurable: true,
      value: (value: string) => revoked.push(value),
    })

    const draws: unknown[][] = []
    const context = { drawImage: (...args: unknown[]) => draws.push(args) }
    const range = { tl: {}, ext: { width: 10, height: 10 } }
    const first = { buffer: new Uint8Array([1]), extension: 'png' }
    const second = { buffer: new Uint8Array([2]), extension: 'png' }

    renderImage(context, [first], { _media: [{ type: 'image', imageId: 0, range }] })
    renderImage(context, [second], { _media: [{ type: 'image', imageId: 0, range }] })
    pending[0].onload?.()
    await Promise.resolve()
    expect(draws).toHaveLength(0)
    pending[1].onload?.()
    await Promise.resolve()
    expect(draws).toHaveLength(1)
    expect(revoked).toEqual(['blob:1', 'blob:2'])
  })
})
