import { saveBlob } from './saveBlob'

export interface ExcelImportRow {
  rowNumber: number
  values: Record<string, string>
  numericCells: ReadonlySet<string>
}

export interface ExcelTableOptions {
  sheetName?: string
  requiredHeaders: readonly string[]
  optionalHeaders?: readonly string[]
  /** Header names from older templates, mapped to the current name, so files filled in earlier still import. */
  headerAliases?: Readonly<Record<string, string>>
  maxRows: number
  maxBytes?: number
}

export interface ExcelWorkbookSheet {
  name: string
  headers: readonly string[]
  rows: readonly (readonly string[])[]
}

const DEFAULT_MAX_BYTES = 5 * 1024 * 1024

export async function createExcelWorkbook(sheets: readonly ExcelWorkbookSheet[]): Promise<ArrayBuffer> {
  if (sheets.length === 0 || sheets.some(sheet => !sheet.name || sheet.name.length > 31))
    throw new Error('工作表配置无效')
  const XLSX = await import('xlsx')
  const workbook = XLSX.utils.book_new()
  for (const sheet of sheets) {
    const rows = [sheet.headers, ...sheet.rows].map(row => row.map(value => value ?? ''))
    const worksheet = XLSX.utils.aoa_to_sheet(rows)
    XLSX.utils.book_append_sheet(workbook, worksheet, sheet.name)
  }
  return XLSX.write(workbook, { type: 'array', bookType: 'xlsx', compression: true }) as ArrayBuffer
}

/** Read one plain .xlsx data sheet. Formulas, duplicate headers, and unknown columns are rejected. */
export async function readExcelTable(file: File, options: ExcelTableOptions): Promise<ExcelImportRow[]> {
  const sheetName = options.sheetName ?? '数据'
  const maxBytes = options.maxBytes ?? DEFAULT_MAX_BYTES
  if (!file.name.toLowerCase().endsWith('.xlsx')) throw new Error('仅支持 .xlsx 文件')
  if (file.size === 0 || file.size > maxBytes) throw new Error(`文件大小必须为 1–${Math.floor(maxBytes / 1024 / 1024)} MiB`)
  if (!Number.isInteger(options.maxRows) || options.maxRows < 1) throw new Error('导入行数上限配置无效')

  const XLSX = await import('xlsx')
  let workbook: ReturnType<typeof XLSX.read>
  try {
    workbook = XLSX.read(await file.arrayBuffer(), {
      type: 'array', cellFormula: true, cellDates: false, sheetRows: options.maxRows + 1,
    })
  } catch {
    throw new Error('Excel 文件损坏或格式不受支持')
  }
  const sheet = workbook.Sheets[sheetName]
  if (!sheet) throw new Error(`缺少“${sheetName}”工作表`)
  if (!sheet['!ref']) throw new Error('工作表为空')

  const range = XLSX.utils.decode_range(sheet['!ref'])
  const fullRange = sheet['!fullref'] ? XLSX.utils.decode_range(sheet['!fullref']) : range
  if (fullRange.e.c >= 64 || fullRange.e.r > options.maxRows)
    throw new Error(`工作表最多支持 ${options.maxRows} 条数据、64 列`)
  for (let row = 1; row <= range.e.r; row++) {
    for (let column = 0; column <= range.e.c; column++) {
      const cell = sheet[XLSX.utils.encode_cell({ r: row, c: column })] as { f?: unknown; t?: string } | undefined
      if (cell?.f !== undefined) throw new Error(`第 ${row + 1} 行包含公式，请粘贴为值后再导入`)
      if (cell?.t === 'e') throw new Error(`第 ${row + 1} 行包含 Excel 错误值`)
    }
  }

  const matrix = XLSX.utils.sheet_to_json<unknown[]>(sheet, { header: 1, raw: false, defval: '' })
  const stringCell = (value: unknown) => typeof value === 'string' ? value.trim() : value == null ? '' : String(value).trim()
  const headers = (matrix[0] ?? []).map(stringCell).map(header => options.headerAliases?.[header] ?? header)
  const allowed = new Set([...options.requiredHeaders, ...(options.optionalHeaders ?? [])])
  if (options.requiredHeaders.some((header, index) => headers[index] !== header))
    throw new Error(`表头不匹配，请使用系统模板：${options.requiredHeaders.join('、')}`)
  if (headers.some(header => !header || !allowed.has(header))) throw new Error('表头为空或包含不支持的列')
  if (new Set(headers).size !== headers.length) throw new Error('表头不能重复')

  const result: ExcelImportRow[] = []
  for (let index = 1; index < matrix.length; index++) {
    const values = matrix[index] ?? []
    if (values.every(value => stringCell(value) === '')) continue
    const rowNumber = index + 1
    if (result.length >= options.maxRows) throw new Error(`最多导入 ${options.maxRows} 条数据`)
    const row: Record<string, string> = {}
    const numericCells = new Set<string>()
    headers.forEach((header, column) => {
      row[header] = stringCell(values[column])
      const cell = sheet[XLSX.utils.encode_cell({ r: index, c: column })] as { t?: string } | undefined
      if (cell?.t === 'n') numericCells.add(header)
    })
    result.push({ rowNumber, values: row, numericCells })
  }
  if (result.length === 0) throw new Error('没有可导入的数据行')
  return result
}

/** Build a simple macro-free workbook with text cells only. */
export async function downloadExcelWorkbook(fileName: string, sheets: readonly ExcelWorkbookSheet[]): Promise<void> {
  const buffer = await createExcelWorkbook(sheets)
  saveBlob(new Blob([buffer], { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' }), fileName)
}
