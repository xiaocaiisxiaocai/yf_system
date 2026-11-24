import { useState } from 'react'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import ExcelImportDialog, { type ImportBatchOutcome } from '../components/ExcelImportDialog'
import { createExcelWorkbook, type ExcelImportRow } from '../utils/excelWorkbook'
import * as workbook from '../utils/excelWorkbook'

interface Row { rowNumber: number; employeeNo: string; initialPassword: string }

const importResult: ImportBatchOutcome = {
  created: 1,
  skipped: 0,
  failed: 0,
  rows: [{ rowNumber: 2, key: 'U001', status: 'CREATED', message: null }],
}

function Harness({ commit }: { commit: (rows: Row[]) => Promise<ImportBatchOutcome> }) {
  const [visible, setVisible] = useState(true)
  return (
    <ExcelImportDialog<Row>
      visible={visible}
      title="账号导入"
      templateName="账号模板.xlsx"
      requiredHeaders={['账号', '初始密码']}
      maxRows={10}
      prepare={(rows: ExcelImportRow[]) => ({
        payload: rows.map(row => ({ rowNumber: row.rowNumber, employeeNo: row.values['账号'], initialPassword: row.values['初始密码'] })),
        preview: rows.map(row => ({ rowNumber: row.rowNumber, key: row.values['账号'], detail: 'Robot角色', status: 'READY' as const })),
      })}
      commit={commit}
      onClose={() => setVisible(false)}
    />
  )
}

describe('ExcelImportDialog', () => {
  it('ignores the exact example and imports a row with one edited cell', async () => {
    const user = userEvent.setup()
    const commit = vi.fn(async () => importResult)
    const prepare = vi.fn((rows: ExcelImportRow[]) => ({
      payload: rows.map(row => ({ rowNumber: row.rowNumber, employeeNo: row.values['账号'], initialPassword: '' })),
      preview: rows.map(row => ({ rowNumber: row.rowNumber, key: row.values['账号'], detail: '', status: 'READY' as const })),
    }))
    const headers = ['账号', '备注']
    const example = ['demo001', '']
    const template = { name: '数据', headers, rows: [example] }
    render(<ExcelImportDialog<Row>
      visible title="账号导入" templateName="账号模板.xlsx" requiredHeaders={headers}
      optionalHeaders={['状态']} templateSheets={[template]} maxRows={1} prepare={prepare} commit={commit} onClose={() => undefined}
    />)

    const input = screen.getByLabelText('选择 Excel 文件')
    const sampleOnly = await createExcelWorkbook([template])
    await user.upload(input, new File([sampleOnly], 'template.xlsx'))
    expect(await screen.findByText('已忽略范例行')).toBeVisible()
    expect(prepare).not.toHaveBeenCalled()
    const confirm = screen.getByRole('button', { name: '确认导入' })
    expect(confirm).toBeDisabled()

    const edited = await createExcelWorkbook([{
      ...template, headers: [...headers, '状态'], rows: [['demo001', '  ', '启用'], ['demo001', '已修改', '']],
    }])
    await user.upload(input, new File([edited], 'edited.xlsx'))
    expect(await screen.findByText('待导入 1')).toBeVisible()
    expect(screen.getByText('已忽略范例行')).toBeVisible()
    expect(prepare).toHaveBeenCalledExactlyOnceWith([
      expect.objectContaining({ rowNumber: 3, values: { 账号: 'demo001', 备注: '已修改', 状态: '' } }),
    ])
    await user.click(confirm)
    expect(commit).toHaveBeenCalledExactlyOnceWith([{ rowNumber: 3, employeeNo: 'demo001', initialPassword: '' }])
  })

  it('shows a preview before committing and reports persisted row results', async () => {
    const user = userEvent.setup()
    const commit = vi.fn(async () => importResult)
    const buffer = await createExcelWorkbook([{ name: '数据', headers: ['账号', '初始密码'], rows: [['U001', 'Temporary#2026']] }])
    render(<Harness commit={commit} />)

    const input = screen.getByLabelText('选择 Excel 文件') as HTMLInputElement
    await user.upload(input, new File([buffer], 'accounts.xlsx', { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' }))
    expect(await screen.findByText('待导入 1')).toBeInTheDocument()
    expect(screen.queryByText('Temporary#2026')).not.toBeInTheDocument()
    expect(commit).not.toHaveBeenCalled()

    await user.click(screen.getByRole('button', { name: '确认导入' }))
    await waitFor(() => expect(commit).toHaveBeenCalledWith([
      { rowNumber: 2, employeeNo: 'U001', initialPassword: 'Temporary#2026' },
    ]))
    // 没有失败行时导入完成后自动关闭。
    await waitFor(() => expect(screen.getByRole('dialog', { hidden: true })).not.toBeVisible())
  })

  it('keeps 确认导入 disabled when no row is ready to import', async () => {
    const user = userEvent.setup()
    const commit = vi.fn(async () => importResult)
    const buffer = await createExcelWorkbook([{ name: '数据', headers: ['账号', '初始密码'], rows: [['U001', 'Temporary#2026']] }])
    render(
      <ExcelImportDialog<Row>
        visible
        title="账号导入"
        templateName="账号模板.xlsx"
        requiredHeaders={['账号', '初始密码']}
        maxRows={10}
        prepare={(rows: ExcelImportRow[]) => ({
          payload: rows.map(row => ({ rowNumber: row.rowNumber, employeeNo: row.values['账号'], initialPassword: row.values['初始密码'] })),
          preview: rows.map(row => ({ rowNumber: row.rowNumber, key: row.values['账号'], detail: '', status: 'SKIPPED' as const, message: '已存在' })),
        })}
        commit={commit}
        onClose={() => undefined}
      />,
    )
    await user.upload(screen.getByLabelText('选择 Excel 文件') as HTMLInputElement,
      new File([buffer], 'accounts.xlsx', { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' }))
    expect(await screen.findByText('跳过 1')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '确认导入' })).toBeDisabled()
    expect(commit).not.toHaveBeenCalled()
  })

  it('does not commit when the preview is cancelled', async () => {
    const user = userEvent.setup()
    const commit = vi.fn(async () => importResult)
    render(<Harness commit={commit} />)
    await user.click(screen.getByRole('button', { name: '关闭' }))
    expect(commit).not.toHaveBeenCalled()
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  })

  it('shows the export button only when rows fail, and exports them without passwords plus the reason', async () => {
    const user = userEvent.setup()
    const download = vi.spyOn(workbook, 'downloadExcelWorkbook').mockResolvedValue(undefined)
    const buffer = await createExcelWorkbook([{ name: '数据', headers: ['账号', '初始密码'], rows: [['U001', 'Temporary#2026'], ['', 'Temporary#2027']] }])
    render(
      <ExcelImportDialog<Row>
        visible
        title="账号导入"
        templateName="账号模板.xlsx"
        requiredHeaders={['账号', '初始密码']}
        maxRows={10}
        prepare={(rows: ExcelImportRow[]) => ({
          payload: [],
          preview: rows.map(row => row.values['账号']
            ? { rowNumber: row.rowNumber, key: row.values['账号'], detail: '', status: 'READY' as const }
            : { rowNumber: row.rowNumber, key: '', detail: '', status: 'INVALID' as const, message: '账号不能为空' }),
        })}
        commit={async () => importResult}
        onClose={() => undefined}
      />,
    )
    expect(screen.queryByRole('button', { name: '导出错误数据' })).not.toBeInTheDocument()
    await user.upload(screen.getByLabelText('选择 Excel 文件'), new File([buffer], 'accounts.xlsx'))
    expect(await screen.findByText('错误 1')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: '导出错误数据' }))
    expect(download).toHaveBeenCalledWith('账号模板_错误数据.xlsx', [{
      name: '错误数据',
      headers: ['账号', '初始密码', '错误原因'],
      rows: [['', '', '账号不能为空']],
    }])
    download.mockRestore()
  })
})
