import type { ApiResponses } from '../api/types'
import type { ExcelImportRow, ExcelWorkbookSheet } from '../utils/excelWorkbook'
import { normalizeImportDate } from './importDate'

type Field = ApiResponses['GET /outsource/fields']['fields'][number]
type Column = { key: string; label: string; rule: string; optionalInput?: boolean; date?: boolean }

const requiredFields = [
  { key: 'outsourceDate', label: '外发日期', example: '2026-11-23' },
  { key: 'workOrderNo', label: '工令', example: '21000001' },
  { key: 'type', label: '外发类型', example: 'OEMA' },
  { key: 'vendor', label: '外发厂商', example: '示例厂商' },
]
export const ITEM_IMPORT_HEADERS = requiredFields.map(field => field.label)

const dateKeys = new Set(['workOrderIssuedOn', 'bomOn', 'drawingReleasedOn', 'originalDueDate', 'latestDueDate'])
const referenceRule = '系统生成，仅供参考，填入的值不会导入。'

/** A single field map drives the downloaded headers, reader and preview. */
export function itemImportTemplate(layout: Field[], canImportDates: boolean) {
  const requiredByKey = new Map(requiredFields.map(field => [field.key, field]))
  const columns: Column[] = layout.map(field => {
    const required = requiredByKey.get(field.key)
    if (required) return { ...required, rule: '必填。' }
    if (field.key === 'supervisor' || field.category === 'FIXED') return { ...field, rule: referenceRule }
    const rule = field.category === 'IPMS' ? '选填。优先采用 IPMS，缺值时采用 Excel。'
      : field.category === 'IMPORTED' && !canImportDates ? '请留空。填写需要导入 BOM/发图权限。'
      : field.category !== 'IMPORTED' && !field.canEdit ? '请留空。当前账号没有此栏位的填写权限。'
      : field.options.length ? `选填：${field.options.join('、')}。`
      : field.dataType === 'BOOLEAN' ? '选填：是或否。'
      : dateKeys.has(field.key) || field.dataType === 'DATE' ? '选填，日期格式为 YYYY-MM-DD。'
      : '选填。'
    return { ...field, optionalInput: true, date: dateKeys.has(field.key) || field.dataType === 'DATE', rule }
  })
  for (const field of requiredFields) {
    if (!columns.some(column => column.key === field.key)) columns.push({ ...field, rule: '必填。' })
  }
  const labels = new Set(columns.map(column => column.label))
  const systemLabel = (title: string) => {
    let label = title
    while (labels.has(label)) label += '（系统）'
    labels.add(label)
    return label
  }
  columns.unshift({ key: 'serialNo', label: systemLabel('序号'), rule: referenceRule })
  const templateColumns = columns.filter(column => !['electricalHandoff', 'status', 'files'].includes(column.key))
  // Accept omitted columns in older workbooks, without adding their values to the import.
  const optionalHeaders = columns.filter(column => !requiredByKey.has(column.key)).map(column => column.label)
  optionalHeaders.push(...['发送状态', '导入人', '导入时间', '修改人', '修改时间'].map(systemLabel))
  const sheets: ExcelWorkbookSheet[] = [
    { name: '数据', headers: templateColumns.map(column => column.label), rows: [templateColumns.map(column => requiredByKey.get(column.key)?.example ?? '')] },
    { name: '说明', headers: ['栏位', '填写规则'], rows: templateColumns.map(column => [column.label, column.rule]) },
  ]
  return { columns: templateColumns, sheets, optionalHeaders }
}

export function itemImportRow(row: ExcelImportRow, columns: Column[]) {
  const fieldValues: Record<string, string> = {}
  for (const column of columns) {
    const value = row.values[column.label]?.trim()
    if (column.optionalInput && value) fieldValues[column.key] = column.date ? normalizeImportDate(value) : value
  }
  return {
    rowNumber: row.rowNumber,
    outsourceDate: normalizeImportDate(row.values['外发日期']), workOrderNo: row.values['工令'],
    vendorName: row.values['外发厂商'], typeName: row.values['外发类型'],
    ...(Object.keys(fieldValues).length ? { fieldValues } : {}),
  }
}
