import type { CellObject, WorkSheet } from 'xlsx'
import type { ContactColumnShape } from './contactsMerge'

const MAX_BYTES = 10 * 1024 * 1024
const MAX_ROWS = 2000
const MAX_COLUMNS = 64

export interface ContactImportCellPayload {
  colKey: string
  textValue: string | null
  phonePairText: string | null
}

export interface ContactImportRowPayload {
  rowNumber: number
  visibilityText: string | null
  cells: ContactImportCellPayload[]
}

function cellText(cell: CellObject | undefined): string {
  if (!cell) return ''
  if (cell.t === 'e') return cell.w ?? ''
  if (cell.v == null) return ''
  if (typeof cell.w === 'string' && cell.t === 'n' && !Number.isInteger(cell.v)) {
    return cell.w.trim()
  }
  return String(cell.v).trim()
}

function expandSheetMerges(sheet: WorkSheet, grid: (CellObject | undefined)[][]) {
  for (const { s, e } of sheet['!merges'] ?? []) {
    const origin = grid[s.r]?.[s.c]
    if (!origin) continue
    for (let r = s.r; r <= e.r; r += 1) {
      const row = grid[r] ?? (grid[r] = [])
      for (let c = s.c; c <= e.c; c += 1) {
        row[c] = origin
      }
    }
  }
}

export async function parseContactsWorkbook(
  file: File,
  columns: readonly ContactColumnShape[],
): Promise<ContactImportRowPayload[]> {
  if (columns.length === 0) throw new Error('请先配置至少一列后再导入')
  if (!file.name.toLowerCase().endsWith('.xlsx')) throw new Error('仅支持 .xlsx 文件')
  if (file.size === 0 || file.size > MAX_BYTES) {
    throw new Error(`文件大小必须为 1–${Math.floor(MAX_BYTES / 1024 / 1024)} MiB`)
  }

  const XLSX = await import('xlsx')
  let workbook: ReturnType<typeof XLSX.read>
  try {
    workbook = XLSX.read(await file.arrayBuffer(), {
      type: 'array',
      cellFormula: true,
      cellDates: false,
      sheetRows: MAX_ROWS + 2,
    })
  } catch {
    throw new Error('Excel 文件损坏或格式不受支持')
  }

  const firstSheetName = workbook.SheetNames[0]
  if (!firstSheetName) throw new Error('工作簿不包含任何工作表')
  const sheet = workbook.Sheets[firstSheetName]
  if (!sheet?.['!ref']) throw new Error('工作表为空')

  const range = XLSX.utils.decode_range(sheet['!ref'])
  const fullRange = sheet['!fullref'] ? XLSX.utils.decode_range(sheet['!fullref']) : range
  const totalCols = fullRange.e.c - fullRange.s.c + 1
  const totalRows = fullRange.e.r - fullRange.s.r + 1
  if (totalCols > MAX_COLUMNS || totalRows > MAX_ROWS + 2) {
    throw new Error(`工作表最多支持 ${MAX_ROWS} 条数据、${MAX_COLUMNS} 列`)
  }

  const grid: (CellObject | undefined)[][] = []
  for (let r = range.s.r; r <= range.e.r; r += 1) {
    const row: (CellObject | undefined)[] = []
    for (let c = range.s.c; c <= range.e.c; c += 1) {
      const cell = sheet[XLSX.utils.encode_cell({ r, c })] as CellObject | undefined
      if (r > range.s.r) {
        if (cell?.f !== undefined) throw new Error(`第 ${r + 1} 行包含公式，请粘贴为值后再导入`)
        if (cell?.t === 'e') throw new Error(`第 ${r + 1} 行包含 Excel 错误值`)
      }
      row.push(cell)
    }
    grid.push(row)
  }

  expandSheetMerges(sheet, grid)

  const headerRow = (grid[0] ?? []).map(cellText)
  if (headerRow.every((h) => !h)) throw new Error('首行表头不能为空')

  const textColIndex = new Map<string, number>()
  const phoneColIndex = new Map<string, number>()
  let visibilityIdx: number | null = null

  const colByHeader = new Map<string, { col: ContactColumnShape; isPhone: boolean }>()
  for (const col of columns) {
    colByHeader.set(col.title.trim(), { col, isPhone: false })
    if (col.dataType === 'PERSON' && col.personDisplay === 'SPLIT') {
      const phoneHeader = (col.phoneTitle?.trim() || `${col.title}电话`).trim()
      colByHeader.set(phoneHeader, { col, isPhone: true })
    }
  }

  headerRow.forEach((header, idx) => {
    if (!header) return
    if (header === '可见范围' || header === '给谁看' || header === '给谁看（可见范围）') {
      visibilityIdx = idx
      return
    }
    const matched = colByHeader.get(header)
    if (!matched) return
    if (matched.isPhone) phoneColIndex.set(matched.col.colKey, idx)
    else textColIndex.set(matched.col.colKey, idx)
  })

  if (textColIndex.size === 0) {
    throw new Error('未识别到与当前表格匹配的列标题，请下载最新模板后再导入')
  }

  const rows: ContactImportRowPayload[] = []
  for (let r = 1; r < grid.length; r += 1) {
    const row = grid[r] ?? []
    const texts = row.map(cellText)
    if (texts.every((v) => !v)) continue
    const cells: ContactImportCellPayload[] = []
    for (const col of columns) {
      const tIdx = textColIndex.get(col.colKey)
      const pIdx = phoneColIndex.get(col.colKey)
      if (tIdx === undefined && pIdx === undefined) continue
      cells.push({
        colKey: col.colKey,
        textValue: tIdx !== undefined ? texts[tIdx] : null,
        phonePairText: pIdx !== undefined ? texts[pIdx] : null,
      })
    }
    rows.push({
      rowNumber: range.s.r + r + 1,
      visibilityText: visibilityIdx !== null ? texts[visibilityIdx] : null,
      cells,
    })
  }

  if (rows.length === 0) throw new Error('工作表没有可导入的数据行')
  if (rows.length > MAX_ROWS) throw new Error(`一次最多导入 ${MAX_ROWS} 行`)
  return rows
}
