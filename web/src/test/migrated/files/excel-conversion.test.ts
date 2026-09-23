import { describe, expect, it } from 'vitest'
import * as XLSX from 'xlsx'

import { readExcelData, transferExcelToSpreadSheet } from '../../../../vendor/vue-office-excel/core/packages/vue-excel/src/excel.js'

describe('Excel source conversion', () => {
  it('source Excel conversion retains empty sheets and populated sheet navigation data', async () => {
    const book = XLSX.utils.book_new()
    XLSX.utils.book_append_sheet(book, {}, '空白')
    XLSX.utils.book_append_sheet(book, XLSX.utils.aoa_to_sheet([['可见内容']]), '内容')

    const source = XLSX.write(book, { type: 'array', bookType: 'xlsx' }) as unknown as ArrayBuffer
    const workbook = await readExcelData(source, false)
    const { workbookData } = transferExcelToSpreadSheet(workbook, { minRowLength: 30, minColLength: 10 })
    const sheets = workbookData as unknown as Array<{
      name: string
      rows: Record<number, { cells: Record<number, { text: string }> }> & { len: number }
    }>

    expect(sheets.map((sheet) => sheet.name)).toEqual(['空白', '内容'])
    expect(sheets[0].rows.len).toBe(30)
    expect(sheets[1].rows[0].cells[0].text).toBe('可见内容')
  })
})
