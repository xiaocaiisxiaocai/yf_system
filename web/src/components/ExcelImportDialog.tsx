import { useRef, useState } from 'react'
import { Alert, Button, Message, Modal, Space, Table, Typography } from '@arco-design/web-react'
import { downloadExcelWorkbook, readExcelTable, type ExcelImportRow, type ExcelWorkbookSheet } from '../utils/excelWorkbook'

export type ImportPreviewStatus = 'READY' | 'SKIPPED' | 'INVALID' | 'CREATED' | 'FAILED'

export interface ImportPreviewRow {
  rowNumber: number
  key: string
  detail: string
  status: ImportPreviewStatus
  message?: string
}

export interface ImportRowOutcome {
  rowNumber: number
  key: string
  status: 'CREATED' | 'SKIPPED' | 'FAILED'
  message?: string | null
}

export interface ImportBatchOutcome {
  created: number
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
  templateSheets?: readonly ExcelWorkbookSheet[]
  maxRows: number
  prepare: (rows: ExcelImportRow[]) => Promise<PreparedImport<T>> | PreparedImport<T>
  commit: (rows: T[]) => Promise<ImportBatchOutcome>
  onImported?: (result: ImportBatchOutcome) => void
  onClose: () => void
}

const STATUS_LABEL: Record<ImportPreviewStatus, string> = {
  READY: '待导入',
  SKIPPED: '跳过',
  INVALID: '有错误',
  CREATED: '已导入',
  FAILED: '失败',
}

export default function ExcelImportDialog<T>({
  visible, title, templateName, requiredHeaders, optionalHeaders = [], templateSheets = [], maxRows,
  prepare, commit, onImported, onClose,
}: ExcelImportDialogProps<T>) {
  const inputRef = useRef<HTMLInputElement>(null)
  const [fileName, setFileName] = useState('')
  const [prepared, setPrepared] = useState<PreparedImport<T> | null>(null)
  const [previewError, setPreviewError] = useState('')
  const [parsing, setParsing] = useState(false)
  const [saving, setSaving] = useState(false)

  const rows = prepared?.preview ?? []
  const counts = {
    ready: rows.filter(row => row.status === 'READY').length,
    skipped: rows.filter(row => row.status === 'SKIPPED').length,
    invalid: rows.filter(row => row.status === 'INVALID' || row.status === 'FAILED').length,
    created: rows.filter(row => row.status === 'CREATED').length,
  }

  const reset = () => {
    setFileName('')
    setPrepared(null)
    setPreviewError('')
    setParsing(false)
    setSaving(false)
    if (inputRef.current) inputRef.current.value = ''
  }

  const close = () => {
    if (parsing || saving) return
    reset()
    onClose()
  }

  const selectFile = async (file?: File) => {
    if (!file) return
    setFileName(file.name)
    setPrepared(null)
    setPreviewError('')
    setParsing(true)
    try {
      const source = await readExcelTable(file, { requiredHeaders, optionalHeaders, maxRows })
      const next = await prepare(source)
      if (next.preview.length !== source.length) throw new Error('预览结果与源文件行数不一致，请重新生成文件')
      setPrepared(next)
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

  const confirm = async () => {
    if (!prepared || prepared.payload.length === 0 || saving || parsing) return
    setSaving(true)
    try {
      const result = await commit(prepared.payload)
      const resultsByRow = new Map(result.rows.map(row => [row.rowNumber, row]))
      setPrepared(current => current && ({
        ...current,
        preview: current.preview.map(row => {
          const saved = resultsByRow.get(row.rowNumber)
          return saved ? { ...row, status: saved.status, message: saved.message ?? undefined } : row
        }),
      }))
      onImported?.(result)
      Message.success(`新增 ${result.created}，跳过 ${result.skipped}，失败 ${result.failed}`)
    } catch {
      // The request layer displays the server error. Keeping the parsed rows allows a safe retry.
    } finally {
      setSaving(false)
    }
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
      okButtonProps={{ disabled: !prepared || prepared.payload.length === 0 || saving || parsing }}
      closable={!saving && !parsing}
      maskClosable={!saving && !parsing}
      escToExit={!saving && !parsing}
      style={{ width: 820, maxWidth: 'calc(100vw - 32px)' }}
    >
      <Space wrap style={{ marginBottom: 12 }}>
        <Button size="small" onClick={() => void downloadTemplate()}>下载模板</Button>
        <Button size="small" loading={parsing} disabled={saving || parsing} onClick={() => inputRef.current?.click()}>
          {fileName || '选择 .xlsx 文件'}
        </Button>
        {prepared && !saving && <Button size="small" type="text" onClick={reset}>重新选择</Button>}
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
        仅新增记录；已存在或文件内重复的账号/厂商会跳过。账号初始密码仅用于本次导入，导出文件不含密码；新账号首次登录须修改密码。最多 {maxRows} 行。
      </Typography.Text>
      {previewError && <Alert type="error" content={previewError} style={{ marginBottom: 12 }} />}
      {prepared && (
        <>
          <Space wrap style={{ marginBottom: 8 }}>
            <Typography.Text>待导入 {counts.ready}</Typography.Text>
            <Typography.Text type="secondary">跳过 {counts.skipped}</Typography.Text>
            <Typography.Text type={counts.invalid ? 'error' : 'secondary'}>错误 {counts.invalid}</Typography.Text>
            {counts.created > 0 && <Typography.Text type="success">已新增 {counts.created}</Typography.Text>}
          </Space>
          <Table
            size="small"
            rowKey="rowNumber"
            data={rows}
            pagination={false}
            scroll={{ y: 360, x: 640 }}
            columns={[
              { title: 'Excel 行', dataIndex: 'rowNumber', width: 72 },
              { title: '账号 / 厂商', dataIndex: 'key', width: 180, ellipsis: true },
              { title: '内容', dataIndex: 'detail', width: 200, ellipsis: true },
              { title: '结果', dataIndex: 'status', width: 90, render: (value: ImportPreviewStatus) => STATUS_LABEL[value] ?? value },
              { title: '说明', dataIndex: 'message', ellipsis: true },
            ]}
          />
        </>
      )}
    </Modal>
  )
}
