import * as XLSX from 'xlsx'
import { describe, expect, it } from 'vitest'
import { createExcelWorkbook, readExcelTable } from '../utils/excelWorkbook'

const makeFile = (sheet: XLSX.WorkSheet, name = 'import.xlsx') => {
  const workbook = XLSX.utils.book_new()
  XLSX.utils.book_append_sheet(workbook, sheet, '数据')
  const bytes = XLSX.write(workbook, { type: 'array', bookType: 'xlsx' }) as ArrayBuffer
  return new File([bytes], name, { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' })
}

describe('Excel import workbook safety', () => {
  it('reads only an explicit table header and preserves source row numbers', async () => {
    const file = makeFile(XLSX.utils.aoa_to_sheet([
      ['账号', '姓名', '初始密码', '状态'],
      ['U001', '甲', 'First#2026', 'ACTIVE'],
      ['', '', '', ''],
      ['U002', '乙', 'Second#2026', 'DISABLED'],
    ]))

    const rows = await readExcelTable(file, {
      requiredHeaders: ['账号', '姓名', '初始密码'],
      optionalHeaders: ['状态'],
      maxRows: 10,
    })

    expect(rows.map(row => row.rowNumber)).toEqual([2, 4])
    expect(rows[0].values).toMatchObject({ 账号: 'U001', 姓名: '甲', 初始密码: 'First#2026', 状态: 'ACTIVE' })
  })

  it('rejects formulas and formula error cells', async () => {
    const formula = XLSX.utils.aoa_to_sheet([['账号', '姓名'], ['U001', '甲']])
    formula.B2 = { t: 'n', f: '1+1', v: 2 }
    await expect(readExcelTable(makeFile(formula), { requiredHeaders: ['账号', '姓名'], maxRows: 10 }))
      .rejects.toThrow('包含公式')
  })

  it('rejects unexpected sheets, duplicate headers, and duplicate input columns', async () => {
    const wrongSheetBook = XLSX.utils.book_new()
    XLSX.utils.book_append_sheet(wrongSheetBook, XLSX.utils.aoa_to_sheet([['账号']]), 'Sheet1')
    const wrongSheet = new File([XLSX.write(wrongSheetBook, { type: 'array', bookType: 'xlsx' }) as ArrayBuffer], 'import.xlsx')
    await expect(readExcelTable(wrongSheet, { requiredHeaders: ['账号'], maxRows: 10 })).rejects.toThrow('缺少“数据”')

    await expect(readExcelTable(makeFile(XLSX.utils.aoa_to_sheet([['账号', '账号'], ['U001', 'U001']])), {
      requiredHeaders: ['账号'], optionalHeaders: ['账号'], maxRows: 10,
    })).rejects.toThrow('表头不能重复')
  })

  it('rejects wrong templates and row counts', async () => {
    await expect(readExcelTable(makeFile(XLSX.utils.aoa_to_sheet([['账号'], ['U001']])), {
      requiredHeaders: ['工号'], maxRows: 10,
    })).rejects.toThrow('表头不匹配')

    await expect(readExcelTable(makeFile(XLSX.utils.aoa_to_sheet([['账号'], ['U001'], ['U002']])), {
      requiredHeaders: ['账号'], maxRows: 1,
    })).rejects.toThrow('最多支持 1 条数据')
  })

  it('reads wide configured work-order templates by header name and keeps the column cap for other imports', async () => {
    const optional = Array.from({ length: 64 }, (_, index) => `自定义列${index + 1}`)
    const file = makeFile(XLSX.utils.aoa_to_sheet([[...optional, '工令'], [...optional.map(() => ''), 'WO-65']]))
    const options = { requiredHeaders: ['工令'], optionalHeaders: optional, maxRows: 10 }
    const rows = await readExcelTable(file, { ...options, matchHeadersByName: true })
    expect(rows.map(row => [row.rowNumber, row.values['工令']])).toEqual([[2, 'WO-65']])
    await expect(readExcelTable(file, options)).rejects.toThrow('64 列')
  })

  it('exports values as text cells and round-trips formula-looking strings safely', async () => {
    const bytes = await createExcelWorkbook([{ name: '数据', headers: ['账号', '备注'], rows: [['0012', '=1+1']] }])
    const workbook = XLSX.read(bytes, { type: 'array', cellFormula: true })
    const sheet = workbook.Sheets['数据']
    expect(sheet.A2).toMatchObject({ t: 's', v: '0012' })
    expect(sheet.B2).toMatchObject({ t: 's', v: '=1+1' })
    expect(sheet.B2.f).toBeUndefined()
  })
})
