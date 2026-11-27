import { useRef, useState, type ReactNode } from 'react'
import axios from 'axios'
import { Alert, Button, Message, Modal, Space, Tag, Typography } from '@arco-design/web-react'
import ResizableTable from './ResizableTable'
import { downloadExcelWorkbook, readExcelTable, type ExcelImportRow, type ExcelWorkbookSheet } from '../utils/excelWorkbook'

export type ImportPreviewStatus = 'READY' | 'READY_UPDATE' | 'SKIPPED' | 'INVALID' | 'CREATED' | 'UPDATED' | 'FAILED'

export interface ImportPreviewRow {
  rowNumber: number
  key: string
  detail?: string
  fieldValues?: Readonly<Record<string, string | null | undefined>>
  changedLabels?: readonly string[]
  status: ImportPreviewStatus
  message?: string
}

export interface ImportRowOutcome {
  rowNumber: number
  key: string
  status: 'CREATED' | 'UPDATED' | 'SKIPPED' | 'FAILED'
  message?: string | null
}

export interface ImportBatchOutcome {
  created: number
  updated?: number
  skipped: number
  failed: number
  rows: ImportRowOutcome[]
}

export interface PreparedImport<T> {
  payload: T[]
  preview: ImportPreviewRow[]
}

interface ExcelImportDialogProps<T> {
  visible: boolean
  title: string
  templateName: string
  requiredHeaders: readonly string[]
  optionalHeaders?: readonly string[]
  matchHeadersByName?: boolean
  previewFields?: readonly { key: string; label: string }[]
  /** Preview supplies only actionable rows in payload; other rows retain their preview outcomes. */
  updateExisting?: boolean
  headerAliases?: Readonly<Record<string, string>>
  templateSheets?: readonly ExcelWorkbookSheet[]
  maxRows: number
  prepare: (rows: ExcelImportRow[]) => Promise<PreparedImport<T>> | PreparedImport<T>
  commit: (rows: T[]) => Promise<ImportBatchOutcome>
  onImported?: (result: ImportBatchOutcome) => void
  onClose: () => void
  /** Replaces the default account/vendor import notice. */
  notice?: ReactNode
  /** Title of the row key column. */
  keyTitle?: string
}

const STATUS_LABEL: Record<ImportPreviewStatus, string> = {
  READY: '待导入',
  READY_UPDATE: '待更新',
  SKIPPED: '跳过',
  INVALID: '有错误',
  CREATED: '已导入',
  UPDATED: '已更新',
  FAILED: '失败',
}

const STATUS_COLOR: Record<ImportPreviewStatus, string> = {
  READY: 'arcoblue',
  READY_UPDATE: 'orange',
  SKIPPED: 'gray',
  INVALID: 'red',
  CREATED: 'green',
  UPDATED: 'green',
  FAILED: 'red',
}

export default function ExcelImportDialog<T>({
  visible, title, templateName, requiredHeaders, optionalHeaders = [], matchHeadersByName, previewFields, headerAliases, templateSheets = [], maxRows,
  prepare, commit, onImported, onClose, notice, keyTitle = '账号 / 厂商', updateExisting = false,
}: ExcelImportDialogProps<T>) {
  const inputRef = useRef<HTMLInputElement>(null)
  const [fileName, setFileName] = useState('')
  const [prepared, setPrepared] = useState<PreparedImport<T> | null>(null)
  const [source, setSource] = useState<ExcelImportRow[]>([])
  const [previewError, setPreviewError] = useState('')
  const [exampleSkipped, setExampleSkipped] = useState(false)
  const [parsing, setParsing] = useState(false)
  const [saving, setSaving] = useState(false)
  const saveLock = useRef(false)
  const [needsPreview, setNeedsPreview] = useState(false)

  const rows = prepared?.preview ?? []
  const statusLabels = updateExisting ? { ...STATUS_LABEL, READY: '待新增', CREATED: '已新增' } : STATUS_LABEL
  const shownFields = previewFields?.filter(field => rows.some(row => row.fieldValues?.[field.key]))
  const counts = {
    ready: rows.filter(row => row.status === 'READY').length,
    readyUpdate: rows.filter(row => row.status === 'READY_UPDATE').length,
    skipped: rows.filter(row => row.status === 'SKIPPED').length,
    invalid: rows.filter(row => row.status === 'INVALID' || row.status === 'FAILED').length,
    created: rows.filter(row => row.status === 'CREATED').length,
    updated: rows.filter(row => row.status === 'UPDATED').length,
  }
  const canConfirm = Boolean(prepared?.payload.length && (counts.ready || counts.readyUpdate)) && !parsing && !saving && !needsPreview

  const reset = () => {
    setFileName('')
    setPrepared(null)
    setSource([])
    setPreviewError('')
    setExampleSkipped(false)
    setParsing(false)
    setSaving(false)
    setNeedsPreview(false)
    if (inputRef.current) inputRef.current.value = ''
  }

  const close = () => {
    if (parsing || saving) return
    reset()
    onClose()
  }

  const previewRows = async (table: ExcelImportRow[]) => {
    const next = table.length ? await prepare(table) : { payload: [], preview: [] }
    if (next.preview.length !== table.length) throw new Error('预览结果与源文件行数不一致，请重新生成文件')
    setSource(table)
    setPrepared(next)
    setNeedsPreview(false)
  }

  const repreview = async () => {
    setParsing(true)
    setPreviewError('')
    try { await previewRows(source) }
    catch (error) { setPreviewError(error instanceof Error ? error.message : '预览失败，请重试') }
    finally { setParsing(false) }
  }

  const selectFile = async (file?: File) => {
    if (!file) return
    setFileName(file.name)
    setPrepared(null)
    setNeedsPreview(false)
    setPreviewError('')
    setExampleSkipped(false)
    setParsing(true)
    try {
      const dataSheet = templateSheets.find(sheet => sheet.name === '数据')
      const example = dataSheet?.rows[0]
      const exampleValues = dataSheet && example
        ? dataSheet.headers.map((header, index) => [header, example[index]?.trim() ?? ''] as const)
        : undefined
      let ignored = false
      const ignoreExampleRow = exampleValues && (({ values }: ExcelImportRow) => {
        const matches = exampleValues.every(([header, value]) => (values[header] ?? '') === value)
        ignored ||= matches
        return matches
      })
      const table = await readExcelTable(file, { requiredHeaders, optionalHeaders, matchHeadersByName, headerAliases, maxRows, ignoreExampleRow })
      setExampleSkipped(ignored)
      await previewRows(table)
    } catch (error) {
      setPreviewError(error instanceof Error ? error.message : 'Excel 文件无法读取')
    } finally {
      setParsing(false)
      if (inputRef.current) inputRef.current.value = ''
    }
  }

  const downloadTemplate = async () => {
    try {
      const sheets = templateSheets.length > 0 ? templateSheets : [{ name: '数据', headers: requiredHeaders, rows: [] }]
      await downloadExcelWorkbook(templateName, sheets)
    } catch (error) {
      Message.error(error instanceof Error ? error.message : '模板下载失败')
    }
  }

  /** Exports the rows that failed as a workbook the user can fix and import again; passwords are never written out. */
  const exportErrorRows = async () => {
    const failed = rows.filter(row => row.status === 'INVALID' || row.status === 'FAILED')
    const byRow = new Map(source.map(row => [row.rowNumber, row]))
    const headers = (matchHeadersByName && templateSheets.find(sheet => sheet.name === '数据')?.headers) || [...requiredHeaders, ...optionalHeaders]
    try {
      await downloadExcelWorkbook(`${templateName.replace(/\.xlsx$/i, '')}_错误数据.xlsx`, [{
        name: '错误数据',
        headers: [...headers, '错误原因'],
        rows: failed.map(row => [
          ...headers.map(header => header.includes('密码') ? '' : byRow.get(row.rowNumber)?.values[header] ?? ''),
          row.message ?? '',
        ]),
      }])
    } catch (error) {
      Message.error(error instanceof Error ? error.message : '错误数据导出失败')
    }
  }

  const confirm = async () => {
    if (!prepared || !canConfirm || saveLock.current) return
    saveLock.current = true
    setSaving(true)
    let finished = false
    try {
      const savedBatch = await commit(prepared.payload)
      const result = updateExisting ? {
        ...savedBatch,
        skipped: savedBatch.skipped + counts.skipped,
        failed: savedBatch.failed + counts.invalid,
        rows: [...savedBatch.rows, ...rows.filter(row => row.status === 'INVALID' || row.status === 'SKIPPED')
          .map((row): ImportRowOutcome => ({
            rowNumber: row.rowNumber, key: row.key, message: row.message, status: row.status === 'INVALID' ? 'FAILED' : 'SKIPPED',
          }))],
      } : savedBatch
      const resultsByRow = new Map(result.rows.map(row => [row.rowNumber, row]))
      setPrepared(current => current && ({
        ...current,
        payload: updateExisting ? [] : current.payload,
        preview: current.preview.map(row => {
          const saved = resultsByRow.get(row.rowNumber)
          return saved ? { ...row, status: saved.status, message: saved.message ?? undefined } : row
        }),
      }))
      onImported?.(result)
      const updateCount = updateExisting ? `，更新 ${result.updated ?? 0}` : ''
      const summary = `新增 ${result.created}${updateCount}，跳过 ${result.skipped}，失败 ${result.failed}`
      if (updateExisting && result.failed) Message.warning(summary)
      else Message.success(summary)
      // 没有失败行就直接关闭；有失败行时留着结果表，方便查看原因和导出错误数据。
      finished = result.failed === 0
    } catch (error) {
      if (updateExisting && axios.isAxiosError(error) && error.response?.status === 409) {
        setPreviewError('工令资料已变更，请重新预览后确认。')
        setNeedsPreview(true)
      }
    } finally {
      saveLock.current = false
      setSaving(false)
    }
    if (finished) { reset(); onClose() }
  }

  return (
    <Modal
      title={title}
      visible={visible}
      onCancel={close}
      onOk={() => void confirm()}
      confirmLoading={saving}
      okText="确认导入"
      cancelText="关闭"
      okButtonProps={{ disabled: !canConfirm }}
      closable={!saving && !parsing}
      maskClosable={!saving && !parsing}
      escToExit={!saving && !parsing}
      style={{ width: 820, maxWidth: 'calc(100vw - 32px)' }}
    >
      <Space wrap style={{ marginBottom: 12 }}>
        <Button size="small" onClick={() => void downloadTemplate()}>下载模板</Button>
        <Button
          size="small"
          loading={parsing}
          disabled={saving || parsing}
          title={fileName || undefined}
          style={{ display: 'inline-flex', alignItems: 'center', maxWidth: 'min(360px, 100%)' }}
          onClick={() => inputRef.current?.click()}
        >
          <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0 }}>
            {fileName || '选择 .xlsx 文件'}
          </span>
        </Button>
        {needsPreview && <Button size="small" loading={parsing} onClick={() => void repreview()}>重新预览</Button>}
        {prepared && !saving && !parsing && <Button size="small" type="text" onClick={reset}>重新选择</Button>}
      </Space>
      <input
        ref={inputRef}
        aria-label="选择 Excel 文件"
        type="file"
        accept=".xlsx,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
        style={{ display: 'none' }}
        onChange={(event) => { void selectFile(event.target.files?.[0]) }}
      />
      <Typography.Text type="secondary" style={{ display: 'block', marginBottom: 12 }}>
        {notice ?? <>仅新增记录；已存在或文件内重复的账号/厂商会跳过。账号初始密码仅用于本次导入，导出文件不含密码；新账号首次登录须修改密码。最多 {maxRows} 行。</>}
      </Typography.Text>
      {previewError && <Alert type="error" content={previewError} style={{ marginBottom: 12 }} />}
      {prepared && (
        <>
          <Space wrap style={{ marginBottom: 8 }}>
            <Typography.Text>{updateExisting ? '待新增' : '待导入'} {counts.ready}</Typography.Text>
            {updateExisting && <Typography.Text>待更新 {counts.readyUpdate}</Typography.Text>}
            <Typography.Text type="secondary">跳过 {counts.skipped}</Typography.Text>
            <Typography.Text type={counts.invalid ? 'error' : 'secondary'}>错误 {counts.invalid}</Typography.Text>
            {exampleSkipped && <Typography.Text type="secondary">已忽略范例行</Typography.Text>}
            {counts.created > 0 && <Typography.Text type="success">已新增 {counts.created}</Typography.Text>}
            {counts.updated > 0 && <Typography.Text type="success">已更新 {counts.updated}</Typography.Text>}
            {counts.invalid > 0 && <Button size="mini" status="danger" onClick={() => void exportErrorRows()}>导出错误数据</Button>}
          </Space>
          <ResizableTable<ImportPreviewRow> tableId={previewFields ? 'excel-import-field-preview' : 'excel-import-preview'}
            size="small"
            rowKey="rowNumber"
            data={rows}
            pagination={false}
            scroll={{ y: 360, x: 640 + (shownFields?.length ?? 0) * 140 }}
            columns={[
              { title: 'Excel 行', dataIndex: 'rowNumber', width: 72 },
              { title: keyTitle, dataIndex: 'key', width: 180, ellipsis: true },
              ...(!shownFields ? [{ title: '内容', dataIndex: 'detail', width: 200, ellipsis: true }] : []),
              { title: '结果', dataIndex: 'status', width: 90, render: (value: ImportPreviewStatus) => <Tag color={STATUS_COLOR[value]}>{statusLabels[value] ?? value}</Tag> },
              ...(rows.some(row => row.changedLabels?.length) ? [{
                title: '变更栏位', key: 'changes', width: 160, render: (_: unknown, row: ImportPreviewRow) => row.changedLabels?.join('、'),
              }] : []),
              { title: '说明', dataIndex: 'message', width: shownFields ? 220 : undefined, ellipsis: !shownFields },
              ...(shownFields ?? []).map(field => ({
                title: field.label, key: `value-${field.key}`, width: 140,
                render: (_: unknown, row: ImportPreviewRow) => <span style={{ whiteSpace: 'normal', overflowWrap: 'anywhere' }}>{row.fieldValues?.[field.key]}</span>,
              })),
            ]}
          />
        </>
      )}
    </Modal>
  )
}
