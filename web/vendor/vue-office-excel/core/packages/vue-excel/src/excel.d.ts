import type { Workbook } from 'exceljs'

export interface SpreadSheetData {
  styles: Array<{ font: { size: number } }>
  rows: Record<number, { height: number; cells: Record<number, { style: number }> }> & { len: number }
  cols: Record<number, { width: number } | undefined> & { len: number }
}

export function readExcelData(buffer: Uint8Array | ArrayBuffer, xls: boolean): Promise<Workbook>
export function transferExcelToSpreadSheet(
  workbook: Workbook,
  options: { minRowLength: number; minColLength: number },
): {
  workbookData: SpreadSheetData[]
  workbookSource: Workbook & { _worksheets: Array<{ _columns: unknown[] }> }
}
