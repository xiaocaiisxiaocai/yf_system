import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { ApiResponses } from '../../api/types'
import MesExcelImportControl from '../../outsource/pages/MesExcelImportControl'

const { post, readExcel, saveBlob } = vi.hoisted(() => ({ post: vi.fn(), readExcel: vi.fn(), saveBlob: vi.fn() }))
vi.mock('../../api/client', () => ({ default: { post } }))
vi.mock('../../outsource/mesExcelImport', async (importOriginal) => ({
  ...await importOriginal<typeof import('../../outsource/mesExcelImport')>(),
  readMesExcelImport: readExcel,
}))
vi.mock('../../utils/saveBlob', () => ({ saveBlob }))

type Preview = ApiResponses['POST /outsource/mes-mirror/import/preview']

const rows = [
  { rowNumber: 2, workOrder: 'KEEP', values: { customerName: 'Excel 保留' } },
  { rowNumber: 3, workOrder: 'UPDATE', values: { customerName: 'Excel 更新' } },
  { rowNumber: 4, workOrder: 'REPEAT', values: { customerName: '候选甲' } },
  { rowNumber: 5, workOrder: 'REPEAT', values: { customerName: '候选乙' } },
  { rowNumber: 6, workOrder: 'NEW', values: { customerName: '新增甲' } },
  { rowNumber: 7, workOrder: 'NEW', values: { customerName: '新增乙' } },
]
const parsed = {
  rows,
  invalid: [{ rowNumber: 12, workOrder: 'TOO-LONG', reason: '工单号长度无效' }],
  unknownHeaders: ['备注栏'],
  exampleSkipped: false,
}
const summary = (customerName: string) => ({ customerName, deviceModels: 'M-1', planEndDate: '2026-12-25' })
const conflict = (workOrder: string, customer: string) => ({
  workOrder, current: summary('原客户'), excel: summary(customer), changedFields: 1,
})
const preview: Preview = {
  added: 0,
  unchanged: 0,
  invalid: [{ rowNumber: 11, workOrder: 'INVALID', reason: '客户名称超过 1000 字符' }],
  existing: [conflict('KEEP', 'Excel 保留'), conflict('UPDATE', 'Excel 更新')],
  duplicates: [
    { workOrder: 'REPEAT', alreadyExists: true, rows: [
      { rowNumber: 4, ...summary('候选甲'), existing: conflict('REPEAT', '候选甲') },
      { rowNumber: 5, ...summary('候选乙'), existing: conflict('REPEAT', '候选乙') },
    ] },
    { workOrder: 'NEW', alreadyExists: false, rows: [
      { rowNumber: 6, ...summary('新增甲'), existing: null },
      { rowNumber: 7, ...summary('新增乙'), existing: null },
    ] },
  ],
}

function openDialog(onImported = vi.fn()) {
  render(<MesExcelImportControl onImported={onImported} />)
  expect(screen.queryByRole('button', { name: '下载模板' })).not.toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: '导入 Excel' }))
  return within(screen.getByRole('dialog', { name: '导入 IPMS 资料' }))
}

function selectFile(dialog: ReturnType<typeof within>, name = 'ipms.xlsx') {
  fireEvent.change(dialog.getByLabelText('选择 Excel 文件'), {
    target: { files: [new File(['xlsx'], name)] },
  })
}

function checkboxFor(order: string) {
  const table = within(screen.getByRole('region', { name: '已存在' }))
  return within(table.getByRole('cell', { name: order }).closest('tr')!).getByRole('checkbox')
}

describe('IPMS Excel 导入控件', () => {
  beforeEach(() => {
    saveBlob.mockReset()
    post.mockReset().mockResolvedValueOnce({ data: preview })
    readExcel.mockReset().mockResolvedValue(parsed)
  })

  it('模板下载失败时显示错误', async () => {
    saveBlob.mockImplementationOnce(() => { throw new Error('save failed') })
    const dialog = openDialog()
    fireEvent.click(dialog.getByRole('button', { name: '下载模板' }))
    expect(await screen.findByText('下载模板失败')).toBeVisible()
  })

  it('仅有范例时提示已忽略，确认不可用且不请求预览', async () => {
    readExcel.mockResolvedValueOnce({ rows: [], invalid: [], unknownHeaders: [], exampleSkipped: true })
    const dialog = openDialog()
    selectFile(dialog, 'IPMS资料导入模板.xlsx')
    expect(await dialog.findByText('已忽略范例行')).toBeVisible()
    expect(dialog.getByText('无效 0')).toBeVisible()
    expect(dialog.getByRole('button', { name: '确认导入' })).toBeDisabled()
    expect(post).not.toHaveBeenCalled()
  })

  it('requires one row per duplicate and updates the visible counts and conflicts', async () => {
    const dialog = openDialog()
    selectFile(dialog)
    await dialog.findByText('Excel 内重复 2')
    const confirm = dialog.getByRole('button', { name: '确认导入' })
    expect(confirm).toBeDisabled()
    expect(dialog.getByText('无效 2')).toBeVisible()
    expect(dialog.getByText('Excel 内重复 2')).toBeVisible()
    expect(dialog.getByText('已存在待选择 2')).toBeVisible()
    expect(dialog.getByText('备注栏')).not.toBeVisible()
    fireEvent.click(dialog.getByText('无法识别的表头（1）'))
    expect(dialog.getByText('备注栏')).toBeVisible()
    fireEvent.click(dialog.getByRole('radio', { name: 'REPEAT 第 4 行' }))
    expect(confirm).toBeDisabled()
    expect(dialog.getByText('已存在待选择 3')).toBeVisible()
    expect(checkboxFor('UPDATE')).not.toBeChecked()
    expect(post).toHaveBeenCalledTimes(1)
    fireEvent.click(dialog.getByRole('radio', { name: 'NEW 第 6 行' }))
    expect(confirm).toBeEnabled()
    expect(dialog.getByText('新增 1')).toBeVisible()

    fireEvent.click(dialog.getByRole('button', { name: '重新选择' }))
    expect(dialog.getByRole('button', { name: '选择 .xlsx 文件' })).toBeEnabled()
    expect(dialog.queryByRole('radio')).not.toBeInTheDocument()
    expect(confirm).toBeDisabled()
  })

  it('从对话框下载模板、选择文件，并只提交选定的重复行和覆盖项', async () => {
    const onImported = vi.fn()
    readExcel.mockResolvedValueOnce({ ...parsed, exampleSkipped: true })
    post.mockResolvedValueOnce({ data: { added: 1, overwritten: 2, skipped: 4, unchanged: 0 } })
    const dialog = openDialog(onImported)
    const confirm = dialog.getByRole('button', { name: '确认导入' })
    expect(confirm).toBeDisabled()
    fireEvent.click(dialog.getByRole('button', { name: '下载模板' }))
    await waitFor(() => expect(saveBlob).toHaveBeenCalledExactlyOnceWith(expect.any(Blob), 'IPMS资料导入模板.xlsx'))
    const clickInput = vi.spyOn(dialog.getByLabelText('选择 Excel 文件'), 'click')
    fireEvent.click(dialog.getByRole('button', { name: '选择 .xlsx 文件' }))
    expect(clickInput).toHaveBeenCalledOnce()
    selectFile(dialog)
    expect(dialog.getByRole('button', { name: 'ipms.xlsx' })).toBeDisabled()
    await dialog.findByRole('radio', { name: 'REPEAT 第 4 行' })
    expect(dialog.getByText('已忽略范例行')).toBeVisible()
    fireEvent.click(dialog.getByRole('radio', { name: 'REPEAT 第 4 行' }))
    fireEvent.click(dialog.getByRole('radio', { name: 'NEW 第 7 行' }))
    fireEvent.click(dialog.getByRole('button', { name: '全选' }))
    expect(checkboxFor('KEEP')).toBeChecked()
    expect(checkboxFor('UPDATE')).toBeChecked()
    fireEvent.click(dialog.getByRole('button', { name: '全不选' }))
    expect(checkboxFor('KEEP')).not.toBeChecked()
    expect(checkboxFor('UPDATE')).not.toBeChecked()

    fireEvent.click(checkboxFor('UPDATE'))
    fireEvent.click(checkboxFor('REPEAT'))
    fireEvent.click(dialog.getByRole('radio', { name: 'REPEAT 第 5 行' }))
    expect(checkboxFor('REPEAT')).not.toBeChecked()
    expect(checkboxFor('UPDATE')).toBeChecked()
    fireEvent.click(checkboxFor('REPEAT'))
    fireEvent.click(confirm)
    expect(confirm).toBeDisabled()
    expect(dialog.getByRole('button', { name: '关闭' })).toBeDisabled()
    expect(dialog.queryByRole('button', { name: 'Close' })).not.toBeInTheDocument()
    expect(dialog.queryByRole('button', { name: '重新选择' })).not.toBeInTheDocument()

    expect(post.mock.calls[0]?.slice(0, 2)).toEqual(['/outsource/mes-mirror/import/preview', { rows }])
    expect(post.mock.calls[1]?.slice(0, 2)).toEqual(['/outsource/mes-mirror/import', {
      rows, overwrite: ['UPDATE', 'REPEAT'], chosenRows: { REPEAT: 5, NEW: 7 },
    }])
    await waitFor(() => expect(onImported).toHaveBeenCalledOnce())
    expect(await screen.findByText('新增 1、覆盖 2、跳过 5')).toBeVisible()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('在对话框内显示解析与预览错误，并允许重新选档', async () => {
    readExcel.mockRejectedValueOnce(new Error('Excel 文件损坏或格式不受支持'))
    post.mockReset()
      .mockRejectedValueOnce({ response: { data: { message: '预览服务暂时不可用' } } })
      .mockResolvedValueOnce({ data: preview })
    const dialog = openDialog()
    selectFile(dialog, 'broken.xlsx')
    expect(await dialog.findByRole('alert')).toHaveTextContent('Excel 文件损坏或格式不受支持')
    expect(post).not.toHaveBeenCalled()

    selectFile(dialog)
    expect(await dialog.findByRole('alert')).toHaveTextContent('预览服务暂时不可用')
    expect(dialog.getByRole('button', { name: '确认导入' })).toBeDisabled()
    expect(dialog.getByRole('button', { name: 'ipms.xlsx' })).toBeEnabled()

    selectFile(dialog)
    expect(await dialog.findByText('Excel 内重复 2')).toBeVisible()
    expect(dialog.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('没有选中可写入的工单时禁用确认，关闭后清空预览和选择', async () => {
    post.mockReset().mockResolvedValue({ data: { ...preview, duplicates: [] } })
    const dialog = openDialog()
    selectFile(dialog)
    await dialog.findByRole('region', { name: '已存在' })
    const confirm = dialog.getByRole('button', { name: '确认导入' })
    expect(confirm).toBeDisabled()
    fireEvent.click(checkboxFor('UPDATE'))
    expect(confirm).toBeEnabled()

    fireEvent.click(dialog.getByRole('button', { name: '关闭' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: '导入 Excel' }))
    const reopened = within(screen.getByRole('dialog', { name: '导入 IPMS 资料' }))
    expect(reopened.getByRole('button', { name: '选择 .xlsx 文件' })).toBeEnabled()
    expect(reopened.queryByRole('region', { name: '已存在' })).not.toBeInTheDocument()
    selectFile(reopened)
    await reopened.findByRole('region', { name: '已存在' })
    expect(checkboxFor('UPDATE')).not.toBeChecked()
    expect(reopened.getByRole('button', { name: '确认导入' })).toBeDisabled()
  })
})
