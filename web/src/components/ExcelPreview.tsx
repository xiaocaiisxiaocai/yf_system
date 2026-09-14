import { useEffect, useMemo, useState } from 'react'
import { Empty, Result, Select, Spin, Typography } from '@arco-design/web-react'
import * as XLSX from 'xlsx'
import http from '../api/client'

interface Props {
  fileId: number
}

interface PreviewCell {
  column: number
  text: string
  rowSpan: number
  colSpan: number
}

interface SheetPreview {
  cells: Array<Array<PreviewCell | null>>
  columns: number[]
  rowStart: number
  totalRows: number
  totalColumns: number
  truncated: boolean
}

interface PreviewResult {
  fileId: number
  state: 'loading' | 'ok' | 'error'
  error: string
  workbook: XLSX.WorkBook | null
  activeSheet: string
}

const MAX_PREVIEW_ROWS = 500
const MAX_PREVIEW_COLUMNS = 100

function buildSheetPreview(sheet: XLSX.WorkSheet): SheetPreview | null {
  if (!sheet['!ref']) return null

  const range = XLSX.utils.decode_range(sheet['!ref'])
  const rowEnd = Math.min(range.e.r, range.s.r + MAX_PREVIEW_ROWS - 1)
  const columnEnd = Math.min(range.e.c, range.s.c + MAX_PREVIEW_COLUMNS - 1)
  const columns = Array.from({ length: columnEnd - range.s.c + 1 }, (_, index) => range.s.c + index)
  const mergeStarts = new Map<string, { rowSpan: number; colSpan: number }>()
  const mergedCells = new Set<string>()

  for (const merge of sheet['!merges'] ?? []) {
    if (merge.e.r < range.s.r || merge.s.r > rowEnd || merge.e.c < range.s.c || merge.s.c > columnEnd) continue
    const startRow = Math.max(merge.s.r, range.s.r)
    const startColumn = Math.max(merge.s.c, range.s.c)
    const endRow = Math.min(merge.e.r, rowEnd)
    const endColumn = Math.min(merge.e.c, columnEnd)
    mergeStarts.set(`${startRow}:${startColumn}`, {
      rowSpan: endRow - startRow + 1,
      colSpan: endColumn - startColumn + 1,
    })
    for (let row = startRow; row <= endRow; row += 1) {
      for (let column = startColumn; column <= endColumn; column += 1) {
        if (row !== startRow || column !== startColumn) mergedCells.add(`${row}:${column}`)
      }
    }
  }

  const cells = Array.from({ length: rowEnd - range.s.r + 1 }, (_, rowOffset) => {
    const row = range.s.r + rowOffset
    return columns.map((column) => {
      const key = `${row}:${column}`
      if (mergedCells.has(key)) return null
      const cell = sheet[XLSX.utils.encode_cell({ r: row, c: column })] as XLSX.CellObject | undefined
      const merge = mergeStarts.get(key)
      return {
        column,
        text: cell?.w ?? (cell?.v !== undefined && cell?.v !== null ? String(cell.v) : cell?.f ? `=${cell.f}` : ''),
        rowSpan: merge?.rowSpan ?? 1,
        colSpan: merge?.colSpan ?? 1,
      }
    })
  })

  return {
    cells,
    columns,
    rowStart: range.s.r,
    totalRows: range.e.r - range.s.r + 1,
    totalColumns: range.e.c - range.s.c + 1,
    truncated: range.e.r > rowEnd || range.e.c > columnEnd,
  }
}

export default function ExcelPreview({ fileId }: Props) {
  const [result, setResult] = useState<PreviewResult>({
    fileId,
    state: 'loading',
    error: '',
    workbook: null,
    activeSheet: '',
  })

  useEffect(() => {
    let active = true
    const controller = new AbortController()
    http
      .get(`/files/${fileId}/content`, { responseType: 'arraybuffer', signal: controller.signal })
      .then((response) => {
        if (!active) return
        const buffer = new Uint8Array(response.data as ArrayBuffer)
        const isXlsx = buffer[0] === 0x50 && buffer[1] === 0x4b
        const isXls = buffer[0] === 0xd0 && buffer[1] === 0xcf
        if (!isXlsx && !isXls) {
          throw new Error('文件扩展名为 Excel，但实际内容不是工作簿（可能是测试占位数据或文件已损坏）')
        }
        const workbook = XLSX.read(buffer, { type: 'array', cellStyles: true })
        if (active) {
          setResult({
            fileId,
            state: 'ok',
            error: '',
            workbook,
            activeSheet: workbook.SheetNames[0] ?? '',
          })
        }
      })
      .catch((error) => {
        if (active) {
          setResult({
            fileId,
            state: 'error',
            error: error instanceof Error ? error.message : '解析失败',
            workbook: null,
            activeSheet: '',
          })
        }
      })
    return () => {
      active = false
      controller.abort()
    }
  }, [fileId])

  const current = result.fileId === fileId
    ? result
    : { fileId, state: 'loading' as const, error: '', workbook: null, activeSheet: '' }
  const sheet = current.workbook && current.activeSheet ? current.workbook.Sheets[current.activeSheet] : null
  const preview = useMemo(() => (sheet ? buildSheetPreview(sheet) : null), [sheet])

  if (current.state === 'error') {
    return <Result status="warning" title="Excel 预览失败" subTitle={current.error} />
  }
  if (current.state === 'loading') {
    return (
      <div style={{ textAlign: 'center', padding: 60 }}>
        <Spin size={32} />
        <div style={{ marginTop: 12 }}>加载并解析 Excel…</div>
      </div>
    )
  }
  if (!current.workbook || current.workbook.SheetNames.length === 0) {
    return <Empty description="工作簿没有可预览的数据" />
  }

  const columnWidths = current.workbook.Sheets[current.activeSheet]?.['!cols'] ?? []

  return (
    <div className="excel-preview">
      <div className="excel-preview-toolbar">
        <Typography.Text type="secondary">工作表</Typography.Text>
        <Select
          value={current.activeSheet}
          style={{ width: 220, maxWidth: '100%' }}
          onChange={(value) => setResult((previous) => (
            previous.fileId === fileId ? { ...previous, activeSheet: value as string } : previous
          ))}
        >
          {current.workbook.SheetNames.map((name) => (
            <Select.Option key={name} value={name}>{name}</Select.Option>
          ))}
        </Select>
        <Typography.Text type="secondary">
          {preview ? `${preview.totalRows} 行 × ${preview.totalColumns} 列` : '空白工作表'}
        </Typography.Text>
      </div>
      {!preview ? <Empty description="当前工作表没有可预览的数据，可切换其他工作表" /> : <div className="excel-preview-wrap">
        <table className="excel-preview-table" aria-label={`Excel 工作表：${current.activeSheet}`}>
          <colgroup>
            <col style={{ width: 52 }} />
            {preview.columns.map((column) => (
              <col key={column} style={{ width: columnWidths[column]?.wpx ?? 96 }} />
            ))}
          </colgroup>
          <thead>
            <tr>
              <th className="excel-preview-corner" scope="col" />
              {preview.columns.map((column) => (
                <th key={column} scope="col">{XLSX.utils.encode_col(column)}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {preview.cells.map((row, rowOffset) => (
              <tr key={preview.rowStart + rowOffset}>
                <th scope="row">{preview.rowStart + rowOffset + 1}</th>
                {row.map((cell) => cell && (
                  <td
                    key={cell.column}
                    rowSpan={cell.rowSpan}
                    colSpan={cell.colSpan}
                    title={cell.text}
                  >
                    {cell.text}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>}
      {preview?.truncated && (
        <Typography.Text type="secondary" className="excel-preview-note">
          为保证浏览器流畅，仅预览前 {MAX_PREVIEW_ROWS} 行、{MAX_PREVIEW_COLUMNS} 列。
        </Typography.Text>
      )}
    </div>
  )
}
