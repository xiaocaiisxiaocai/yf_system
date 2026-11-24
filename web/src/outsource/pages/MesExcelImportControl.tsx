import { useId, useRef, useState } from 'react'
import { Alert, Button, Message, Modal, Space, Typography, type TableColumnProps } from '@arco-design/web-react'
import { IconUpload } from '@arco-design/web-react/icon'
import http, { type QuietRequestConfig } from '../../api/client'
import type { MesImportConfirmRequest, MesImportRequest } from '../../api/generated/api-types'
import { requestErrorMessage } from '../../api/requestError'
import type { ApiResponses } from '../../api/types'
import ResizableTable from '../../components/ResizableTable'
import { useAuth } from '../../store/auth'
import { saveBlob } from '../../utils/saveBlob'
import { createMesImportTemplate, readMesExcelImport } from '../mesExcelImport'

type Preview = ApiResponses['POST /outsource/mes-mirror/import/preview']
type Existing = Preview['existing'][number]
type DuplicateRow = Preview['duplicates'][number]['rows'][number] & { workOrder: string }
type InvalidRow = Preview['invalid'][number]
type ImportFile = Awaited<ReturnType<typeof readMesExcelImport>>

const EMPTY_PREVIEW: Preview = { added: 0, unchanged: 0, invalid: [], existing: [], duplicates: [] }
const SUMMARY_FIELDS = [
  { key: 'customerName', title: '客户名称', width: 160 },
  { key: 'deviceModels', title: '机型', width: 150 },
  { key: 'planEndDate', title: '计划交期', width: 160 },
] as const

const existingColumns: TableColumnProps<Existing>[] = [
  { title: '工单号', dataIndex: 'workOrder', width: 135 },
  ...SUMMARY_FIELDS.map(({ key, title, width }): TableColumnProps<Existing> => ({
    key,
    title,
    width,
    render: (_, row) => <div className="outsource-ipms-import-comparison">
      <span>现有 {row.current[key] ?? '—'}</span>
      <span>Excel {row.excel[key] ?? '—'}</span>
    </div>,
  })),
  { title: '差异字段数', dataIndex: 'changedFields', width: 105 },
]
const invalidColumns: TableColumnProps<InvalidRow>[] = [
  { title: '行号', dataIndex: 'rowNumber', width: 75 },
  { title: '工单号', dataIndex: 'workOrder', width: 150, ellipsis: true },
  { title: '原因', dataIndex: 'reason', width: 260 },
]

async function downloadTemplate() {
  try {
    const buffer = await createMesImportTemplate()
    saveBlob(new Blob([buffer], { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' }), 'IPMS资料导入模板.xlsx')
  } catch {
    Message.error('下载模板失败')
  }
}

export default function MesExcelImportControl({ onImported }: { onImported: () => void }) {
  const [visible, setVisible] = useState(false)

  return <>
    <Button icon={<IconUpload />} onClick={() => setVisible(true)}>导入 Excel</Button>
    {visible && <MesExcelImportDialog onClose={() => setVisible(false)} onImported={onImported} />}
  </>
}

function MesExcelImportDialog({ onClose, onImported }: { onClose: () => void; onImported: () => void }) {
  const inputRef = useRef<HTMLInputElement>(null)
  const radioPrefix = useId()
  const reading = useRef(false)
  const writing = useRef(false)
  const [fileName, setFileName] = useState('')
  const [previewError, setPreviewError] = useState('')
  const [parsing, setParsing] = useState(false)
  const [submitting, setSubmitting] = useState(false)
  const [draft, setDraft] = useState<{ file: ImportFile; preview: Preview; config: QuietRequestConfig }>()
  const [chosen, setChosen] = useState<Record<string, number>>({})
  const [overwrite, setOverwrite] = useState<string[]>([])

  const resetFile = (name = '') => {
    setFileName(name)
    setPreviewError('')
    setDraft(undefined)
    setChosen({})
    setOverwrite([])
  }

  const selectFile = async (selected?: File) => {
    if (!selected || reading.current || writing.current) return
    reading.current = true
    resetFile(selected.name)
    setParsing(true)
    const config: QuietRequestConfig = { authGeneration: useAuth.getState().generation }
    try {
      const file = await readMesExcelImport(selected)
      if (!file.rows.length && !file.invalid.length && !file.exampleSkipped) {
        setPreviewError('没有可导入的数据行')
        return
      }
      const request: MesImportRequest = { rows: file.rows }
      const previewConfig: QuietRequestConfig = { ...config, quietClientError: true, quietNetworkError: true }
      const preview = file.rows.length
        ? (await http.post<Preview>('/outsource/mes-mirror/import/preview', request, previewConfig)).data
        : EMPTY_PREVIEW
      setDraft({ file, preview, config })
    } catch (error) {
      setPreviewError(requestErrorMessage(error, 'Excel 解析或预览失败'))
    } finally {
      reading.current = false
      setParsing(false)
      if (inputRef.current) inputRef.current.value = ''
    }
  }

  const preview = draft?.preview ?? EMPTY_PREVIEW
  const existing = [...preview.existing]
  const invalid = [...(draft?.file.invalid ?? []), ...preview.invalid].sort((a, b) => a.rowNumber - b.rowNumber)
  let added = preview.added
  let unchanged = preview.unchanged
  let allChosen = true
  for (const group of preview.duplicates) {
    const choice = group.rows.find((row) => row.rowNumber === chosen[group.workOrder])
    if (!choice) allChosen = false
    else if (!group.alreadyExists) added++
    else if (choice.existing) existing.push(choice.existing)
    else unchanged++
  }
  const canConfirm = allChosen && (added > 0 || overwrite.length > 0)

  const choose = (row: DuplicateRow) => {
    const previous = preview.duplicates.find((group) => group.workOrder === row.workOrder)?.rows
      .find((option) => option.rowNumber === chosen[row.workOrder])
    const previousOrder = previous?.existing?.workOrder
    if (previousOrder) setOverwrite((current) => current.filter((key) => key !== previousOrder))
    setChosen((current) => ({ ...current, [row.workOrder]: row.rowNumber }))
  }
  const duplicateColumns: TableColumnProps<DuplicateRow>[] = [
    { title: '工单号', dataIndex: 'workOrder', width: 135 },
    { title: '行号', key: 'rowNumber', width: 95, render: (_, row) => <label className="outsource-ipms-import-radio">
      <input type="radio" name={`${radioPrefix}-${row.workOrder}`} aria-label={`${row.workOrder} 第 ${row.rowNumber} 行`}
        checked={chosen[row.workOrder] === row.rowNumber} disabled={submitting} onChange={() => choose(row)} />
      {row.rowNumber}
    </label> },
    ...SUMMARY_FIELDS.map(({ key, title, width }) => ({ dataIndex: key, title, width, ellipsis: true })),
  ]

  const confirm = async () => {
    if (!draft || !canConfirm || reading.current || writing.current) return
    writing.current = true
    setSubmitting(true)
    const { file, config } = draft
    try {
      const request: MesImportConfirmRequest = { rows: file.rows, overwrite, chosenRows: chosen }
      const { data } = await http.post<ApiResponses['POST /outsource/mes-mirror/import']>('/outsource/mes-mirror/import', request, config)
      Message.success(`新增 ${data.added}、覆盖 ${data.overwritten}、跳过 ${data.skipped + file.invalid.length}`)
      onClose()
      onImported()
    } catch {
      // The shared client displays the server's message, including a busy fetch lease.
    } finally {
      writing.current = false
      setSubmitting(false)
    }
  }

  const busy = parsing || submitting
  return <Modal title="导入 IPMS 资料" visible className="outsource-ipms-import-modal" style={{ width: 950, maxWidth: 'calc(100vw - 32px)' }}
    okText="确认导入" cancelText="关闭"
    closable={!busy} maskClosable={!busy} escToExit={!busy} confirmLoading={submitting}
    okButtonProps={{ disabled: busy || !canConfirm }} cancelButtonProps={{ disabled: busy }}
    onOk={() => void confirm()} onCancel={() => { if (!reading.current && !writing.current) onClose() }}>
    <Space wrap style={{ marginBottom: 12 }}>
      <Button size="small" onClick={() => void downloadTemplate()}>下载模板</Button>
      <Button size="small" loading={parsing} disabled={busy} onClick={() => inputRef.current?.click()}>
        {fileName || '选择 .xlsx 文件'}
      </Button>
      {draft && !submitting && <Button size="small" type="text" onClick={() => resetFile()}>重新选择</Button>}
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
      按工单号合并：新工单直接新增，已存在的工单勾选后才覆盖，空白格保留原值。每日自动抓取成功后以 IPMS 为准。最多 5000 行。
    </Typography.Text>
    {previewError && <Alert type="error" content={previewError} style={{ marginBottom: 12 }} />}
    {draft && <div className="outsource-ipms-import-body">
      <Space wrap>
        <span>新增 {added}</span>
        <span>无变化 {unchanged}</span>
        <span>无效 {invalid.length}</span>
        <span>已存在待选择 {existing.length}</span>
        <span>Excel 内重复 {preview.duplicates.length}</span>
        {draft.file.exampleSkipped && <Typography.Text type="secondary">已忽略范例行</Typography.Text>}
      </Space>
      {draft.file.unknownHeaders.length > 0 && <details>
        <summary>无法识别的表头（{draft.file.unknownHeaders.length}）</summary>
        <div className="outsource-ipms-import-unknown">{draft.file.unknownHeaders.join('、')}</div>
      </details>}
      {preview.duplicates.length > 0 && <section aria-label="Excel 内重复">
        <h3>Excel 内重复</h3>
        <ResizableTable<DuplicateRow> tableId="outsource-ipms-import-duplicates" size="small" rowKey="rowNumber"
          data={preview.duplicates.flatMap((group) => group.rows.map((row) => ({ ...row, workOrder: group.workOrder })))}
          columns={duplicateColumns} pagination={{ defaultPageSize: 10 }} />
      </section>}
      {existing.length > 0 && <section aria-label="已存在">
        <div className="outsource-ipms-import-heading">
          <h3>已存在</h3>
          <Space wrap>
            <Button size="mini" disabled={submitting} onClick={() => setOverwrite(existing.map((row) => row.workOrder))}>全选</Button>
            <Button size="mini" disabled={submitting} onClick={() => setOverwrite([])}>全不选</Button>
          </Space>
        </div>
        <ResizableTable<Existing> tableId="outsource-ipms-import-existing" size="small" rowKey="workOrder"
          data={existing} columns={existingColumns} pagination={{ defaultPageSize: 10 }}
          rowSelection={{ type: 'checkbox', checkCrossPage: true, selectedRowKeys: overwrite,
            checkboxProps: () => ({ disabled: submitting }), onChange: (keys) => setOverwrite(keys.map(String)) }} />
      </section>}
      {invalid.length > 0 && <section aria-label="无效行">
        <h3>无效行</h3>
        <ResizableTable<InvalidRow> tableId="outsource-ipms-import-invalid" size="small" rowKey="rowNumber"
          data={invalid} columns={invalidColumns} growKey="reason" pagination={{ defaultPageSize: 10 }} />
      </section>}
    </div>}
  </Modal>
}
