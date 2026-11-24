import type { CellObject, Range, WorkSheet } from 'xlsx'
import { isGroup, MES_CODE_TEXT, MES_FIXED_COLUMNS, MES_LAYOUT } from './mesColumns'

const MAX_BYTES = 10 * 1024 * 1024
const MAX_ROWS = 5000
const MAX_COLUMNS = 256
const MAX_HEADER_ROWS = 6
const EXAMPLE_WORK_ORDER = '示例工单'
const EXAMPLE_VALUES: Readonly<Record<string, string>> = {
  customerName: '示例客户',
  workOrder: EXAMPLE_WORK_ORDER,
  deviceModels: 'MX-100',
  planEndDate: '2026-12-15',
  customerEndDate: '2026-12-18',
  applyEndTime: '2026-11-23',
  bomChargeUser: '示例工程师',
  deviceNum: '2',
  deliveryDate: '2026-12-12',
}

const columnLayout = [...MES_FIXED_COLUMNS, ...MES_LAYOUT]
const columnsByLabel = new Map<string, { prop: string; group?: string }[]>()
const groupLabels = new Set(MES_LAYOUT.filter(isGroup).map((group) => group.label))
for (const item of columnLayout) {
  for (const column of isGroup(item) ? item.columns : [item]) {
    const candidates = columnsByLabel.get(column.label) ?? []
    candidates.push({ prop: column.prop, group: isGroup(item) ? item.label : undefined })
    columnsByLabel.set(column.label, candidates)
  }
}

const codesByText = new Map(Object.entries(MES_CODE_TEXT).map(([field, codes]) => [
  field, new Map(Object.entries(codes).map(([code, text]) => [text, code])),
]))

export interface MesExcelImportRow {
  rowNumber: number
  workOrder: string
  values: Record<string, string>
}

export interface MesExcelImportResult {
  rows: MesExcelImportRow[]
  invalid: { rowNumber: number; workOrder: string; reason: string }[]
  unknownHeaders: string[]
  exampleSkipped: boolean
}

export async function createMesImportTemplate(): Promise<ArrayBuffer> {
  const XLSX = await import('xlsx')
  const topHeader: string[] = []
  const leafHeader: (string | null)[] = []
  const exampleRow: (string | null)[] = []
  const merges: Range[] = []
  const widths: { wpx: number }[] = []

  for (const item of columnLayout) {
    const grouped = isGroup(item)
    const columns = grouped ? item.columns : [item]
    const start = widths.length
    topHeader[start] = item.label
    leafHeader.push(...columns.map((column) => grouped ? column.label : null))
    exampleRow.push(...columns.map(({ prop }) => EXAMPLE_VALUES[prop] ?? null))
    widths.push(...columns.map(({ width }) => ({ wpx: Math.max(85, width) })))
    merges.push({ s: { r: 0, c: start }, e: { r: grouped ? 0 : 1, c: widths.length - 1 } })
  }

  const sheet = XLSX.utils.aoa_to_sheet([topHeader, leafHeader, exampleRow])
  sheet['!merges'] = merges
  sheet['!cols'] = widths
  const workbook = XLSX.utils.book_new()
  XLSX.utils.book_append_sheet(workbook, sheet, 'IPMS资料')
  return XLSX.write(workbook, { type: 'array', bookType: 'xlsx', compression: true }) as ArrayBuffer
}

function textOf(cell: CellObject | undefined): string | null {
  const value = cell?.t === 'e' ? cell.w : cell?.v
  return value == null ? null : String(value).trim() || null
}

function expandMerges(sheet: WorkSheet, data: CellObject[][]) {
  for (const { s, e } of sheet['!merges'] ?? []) {
    const origin = data[s.r]?.[s.c]
    if (!origin) continue
    for (let row = s.r; row <= e.r; row += 1) {
      const cells = data[row] ?? (data[row] = [])
      for (let column = s.c; column <= e.c; column += 1) cells[column] = origin
    }
  }
}

function mapHeaders(headerRows: (string | null)[][]) {
  const source = (headerRows[0] ?? []).flatMap((_, column) => {
    const stack = headerRows.map((row) => row[column]).filter((text) => text !== null)
    const label = stack.at(-1)
    return label && label !== '序号' ? [{ column, label, group: stack.at(-2) }] : []
  })

  // Reserve explicit group matches first. A group can contain the same leaf more than once (e.g. 研发机构).
  const grouped = new Map<number, string>()
  const used = new Set<string>()
  for (const { column, label, group } of source) {
    const candidates = columnsByLabel.get(label)
    if (!group || !candidates || candidates.length < 2) continue
    const match = candidates.find((item) => item.group === group && !used.has(item.prop))
    if (!match) continue
    grouped.set(column, match.prop)
    used.add(match.prop)
  }

  const mapped = new Map<number, string>()
  const occurrences = new Map<string, number>()
  const unknownHeaders = new Set<string>()
  for (const { column, label, group } of source) {
    const index = occurrences.get(label) ?? 0
    occurrences.set(label, index + 1)
    const explicit = grouped.get(column)
    const candidates = columnsByLabel.get(label)
    const usePosition = candidates?.length === 1 || !group || !groupLabels.has(group)
    const positional = usePosition ? candidates?.[candidates.length === 1 ? 0 : index]?.prop : undefined
    const prop = explicit ?? positional
    if (!prop || (!explicit && used.has(prop))) {
      unknownHeaders.add(label)
      continue
    }
    mapped.set(column, prop)
    used.add(prop)
  }
  return { mapped, unknownHeaders: [...unknownHeaders] }
}

/** Reads the first worksheet in IPMS monitoring-table format, keeping source row numbers. */
export async function readMesExcelImport(file: File): Promise<MesExcelImportResult> {
  if (!/\.xlsx$/i.test(file.name)) throw new Error('仅支持 .xlsx 文件')
  if (file.size === 0 || file.size > MAX_BYTES) throw new Error('文件大小必须为 1 字节至 10 MiB')

  const XLSX = await import('xlsx')
  let workbook: ReturnType<typeof XLSX.read>
  try {
    workbook = XLSX.read(await file.arrayBuffer(), {
      type: 'array', sheets: 0, dense: true, cellDates: true, cellNF: true,
      cellHTML: false, cellFormula: false, sheetRows: MAX_ROWS + MAX_HEADER_ROWS + 1,
    })
  } catch {
    throw new Error('Excel 文件损坏或格式不受支持')
  }
  if (workbook.bookType !== 'xlsx') throw new Error('仅支持 .xlsx 文件')

  const sheet = workbook.Sheets[workbook.SheetNames[0]]
  const ref = sheet?.['!fullref'] ?? sheet?.['!ref']
  if (!ref) throw new Error('找不到“工单号”栏')
  const { e } = XLSX.utils.decode_range(ref)
  const end = { ...e }
  for (const merge of sheet['!merges'] ?? []) {
    end.r = Math.max(end.r, merge.e.r)
    end.c = Math.max(end.c, merge.e.c)
  }
  if (end.c >= MAX_COLUMNS) throw new Error(`工作表最多支持 ${MAX_COLUMNS} 列`)
  if (end.r >= MAX_ROWS + MAX_HEADER_ROWS + 1) throw new Error(`工作表最多支持 ${MAX_ROWS} 条数据`)

  const data = sheet['!data'] ?? []
  expandMerges(sheet, data)
  const firstRows = Array.from({ length: Math.min(MAX_HEADER_ROWS, end.r + 1) }, (_, row) =>
    Array.from({ length: end.c + 1 }, (_, column) => textOf(data[row]?.[column])),
  )
  const headerEnd = firstRows.findLastIndex((cells) => cells.some((cell) => cell !== null && columnsByLabel.has(cell)))
  const { mapped, unknownHeaders } = mapHeaders(firstRows.slice(0, headerEnd + 1))
  const workOrderColumn = [...mapped].find(([, prop]) => prop === 'workOrder')?.[0]
  if (workOrderColumn === undefined) throw new Error('找不到“工单号”栏')

  const date1904 = workbook.Workbook?.WBProps?.date1904 === true
  const valueOf = (cell: CellObject | undefined): string | null => {
    if (cell?.t === 'd' && cell.v instanceof Date && Number.isFinite(cell.v.getTime())) {
      const milliseconds = cell.v.getTime()
      const iso = new Date(Math.round(milliseconds / 1000) * 1000).toISOString()
      return milliseconds % 86_400_000 === 0 ? iso.slice(0, 10) : iso.slice(0, 19).replace('T', ' ')
    }
    if (cell?.t === 'n' && typeof cell.v === 'number' && cell.z && XLSX.SSF.is_date(cell.z)) {
      const format = Number.isInteger(cell.v) ? 'yyyy-mm-dd' : 'yyyy-mm-dd hh:mm:ss'
      const dateText: string = XLSX.SSF.format(format, cell.v, { date1904 })
      if (dateText) return dateText
    }
    return textOf(cell)
  }
  // A numeric work-order cell keeps what Excel shows (e.g. 00123 from a zero-padded format); only General-formatted
  // whole numbers fall back to the plain digits, so large numbers never come through as 2.11E+07.
  const workOrderOf = (cell: CellObject | undefined): string | null => {
    if (cell?.t !== 'n' || typeof cell.v !== 'number') return valueOf(cell)
    const general = !cell.z || cell.z === 'General'
    if (general && Number.isSafeInteger(cell.v)) return String(cell.v)
    return cell.w?.trim() || textOf(cell)
  }

  const rows: MesExcelImportResult['rows'] = []
  const invalid: MesExcelImportResult['invalid'] = []
  let examples = 0
  for (let row = headerEnd + 1; row <= end.r; row += 1) {
    const cells = data[row]
    const workOrder = workOrderOf(cells?.[workOrderColumn])
    if (!workOrder) continue
    if (workOrder === EXAMPLE_WORK_ORDER) {
      examples++
      continue
    }
    const rowNumber = row + 1
    // An Excel error (#N/A, #REF!…) is not data: never import its display text as a work order or MES value.
    const errorColumns = [...mapped.keys()].filter((column) => cells?.[column]?.t === 'e')
    if (errorColumns.length > 0) {
      const where = errorColumns.map((column) => `${XLSX.utils.encode_col(column)} 列（${cells?.[column]?.w ?? '错误值'}）`).join('、')
      invalid.push({ rowNumber, workOrder, reason: `含 Excel 错误值：${where}` })
      continue
    }
    if (workOrder.length > 64) {
      invalid.push({ rowNumber, workOrder, reason: '工单号长度需为 1–64 个字符' })
      continue
    }
    const values: MesExcelImportRow['values'] = {}
    for (const [column, prop] of mapped) {
      const value = column === workOrderColumn ? workOrder : valueOf(cells?.[column])
      if (value !== null) values[prop] = codesByText.get(prop)?.get(value) ?? value
    }
    rows.push({ rowNumber, workOrder, values })
  }
  if (end.r - headerEnd - examples > MAX_ROWS) throw new Error(`工作表最多支持 ${MAX_ROWS} 条数据`)
  return { rows, invalid, unknownHeaders, exampleSkipped: examples > 0 }
}
