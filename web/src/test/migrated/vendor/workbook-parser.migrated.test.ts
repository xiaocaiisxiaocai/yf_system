import * as XLSX from 'xlsx'
import { describe, expect, it } from 'vitest'

describe('workbook parser regressions migrated from node:test', () => {
  it('workbook parser version includes published security fixes', () => {
    const version = XLSX.version.split('.').map(Number)
    expect(
      version[0] > 0 || version[1] > 20 || (version[1] === 20 && version[2] >= 2),
      'xlsx must be at least 0.20.2',
    ).toBe(true)
  })

  it('workbook parser retains merged cells and Chinese text', () => {
    const sheet = XLSX.utils.aoa_to_sheet([['中文图纸', ''], [42, '供应商']])
    sheet['!merges'] = [{ s: { r: 0, c: 0 }, e: { r: 0, c: 1 } }]
    const workbook = XLSX.utils.book_new()
    XLSX.utils.book_append_sheet(workbook, sheet, '评审')
    const parsed = XLSX.read(
      XLSX.write(workbook, { type: 'buffer', bookType: 'xlsx' }),
      { type: 'buffer', cellStyles: true },
    )

    expect(parsed.Sheets['评审'].A1.v).toBe('中文图纸')
    expect(parsed.Sheets['评审']['!merges']![0].e.c).toBe(1)
  })
})
