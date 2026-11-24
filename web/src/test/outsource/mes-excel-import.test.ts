import * as XLSX from 'xlsx'
import { describe, expect, it } from 'vitest'
import { isGroup, MES_FIXED_COLUMNS, MES_LAYOUT } from '../../outsource/mesColumns'
import { createMesImportTemplate, readMesExcelImport } from '../../outsource/mesExcelImport'

function fileOf(sheet: XLSX.WorkSheet, date1904 = false) {
  const book = XLSX.utils.book_new()
  book.Workbook = { WBProps: { date1904 } }
  XLSX.utils.book_append_sheet(book, sheet, '生产监控')
  XLSX.utils.book_append_sheet(book, XLSX.utils.aoa_to_sheet([['工单号'], ['其他页']]), '不读取')
  const bytes = XLSX.write(book, { type: 'array', bookType: 'xlsx', cellDates: true }) as ArrayBuffer
  return new File([bytes], 'IPMS.XLSX')
}

describe('IPMS Excel 导入解析', () => {
  it('生成两行合并表头和一行范例，跳过示例工单并解析改过工单号的范例与其他数据', async () => {
    const columns = [...MES_FIXED_COLUMNS, ...MES_LAYOUT.flatMap((item) => isGroup(item) ? item.columns : [item])]
    const template = await createMesImportTemplate()
    const workbook = XLSX.read(template, { type: 'array', cellStyles: true })
    expect(workbook.SheetNames).toEqual(['IPMS资料'])
    const sheet = workbook.Sheets['IPMS资料']
    expect(sheet['!ref']).toBe(`A1:${XLSX.utils.encode_col(columns.length - 1)}3`)
    expect(sheet['!cols']).toHaveLength(columns.length)
    expect(sheet['!merges']?.map((range) => XLSX.utils.encode_range(range))).toEqual(expect.arrayContaining(['A1:A2', 'G1:I1']))
    expect(sheet.A1.v).toBe('客户名称')
    expect(sheet.G1.v).toBe('影响天数')
    expect(sheet.H2.v).toBe('厂内异常')
    const example: Record<string, string> = {
      customerName: '示例客户', workOrder: '示例工单', deviceModels: 'MX-100', planEndDate: '2026-12-15',
      customerEndDate: '2026-12-18', applyEndTime: '2026-11-23', bomChargeUser: '示例工程师',
      deviceNum: '2', deliveryDate: '2026-12-12',
    }
    const exampleCells = XLSX.utils.sheet_to_json<(string | null)[]>(sheet, { header: 1, defval: null })[2]
    expect(exampleCells).toEqual(columns.map(({ prop }) => example[prop] ?? null))
    await expect(readMesExcelImport(new File([template], 'IPMS资料导入模板.xlsx'))).resolves.toEqual({
      rows: [], invalid: [], unknownHeaders: [], exampleSkipped: true,
    })

    const sample: Record<string, string> = {
      workOrder: 'WO-001', zzzzPlanDate: '2026-11-25', ortzkWorkorderbgbl: '75%', yjChargeUser: '品保',
    }
    XLSX.utils.sheet_add_aoa(sheet, [
      columns.map(({ prop }) => prop === 'customerName' ? '改过客户的示例工单' : example[prop] ?? null),
      columns.map(({ prop }) => prop === 'workOrder' ? '21000001' : example[prop] ?? null),
      columns.map(({ prop }) => sample[prop] ?? null),
    ], { origin: -1 })
    const bytes = XLSX.write(workbook, { type: 'array', bookType: 'xlsx' }) as ArrayBuffer
    const result = await readMesExcelImport(new File([bytes], 'IPMS资料导入模板.xlsx'))

    expect(result.unknownHeaders).toEqual([])
    expect(result.invalid).toEqual([])
    expect(result.exampleSkipped).toBe(true)
    expect(result.rows).toEqual([
      { rowNumber: 5, workOrder: '21000001', values: { ...example, workOrder: '21000001' } },
      { rowNumber: 6, workOrder: 'WO-001', values: sample },
    ])
  })

  it('展开合并表头，优先匹配分组及叶子，并且只读取首张工作表', async () => {
    const sheet = XLSX.utils.aoa_to_sheet([
      ['序号', '工单号', '采购', null, '研发电控', null, '研发机构', null, '未知列', '采购'],
      [null, null, '计划完成', '实际完成', '计划完成', '实际完成', '责任人', '责任人', null, '计划完成'],
      [1, ' 00123 ', '采购计划', '采购完成', '电控计划', '电控完成', 'BOM 人员', '2D 人员', '忽略', '额外采购计划'],
    ])
    sheet['!merges'] = ['A1:A2', 'B1:B2', 'C1:D1', 'E1:F1', 'G1:H1'].map(XLSX.utils.decode_range)
    await expect(readMesExcelImport(fileOf(sheet))).resolves.toEqual({
      rows: [{
        rowNumber: 3, workOrder: '00123', values: {
          workOrder: '00123', cgPlanDate: '采购计划', cgActualDate: '采购完成',
          yfdkPlanDate: '电控计划', yfdkActualDate: '电控完成', bomChargeUser: 'BOM 人员', twodChargeUser: '2D 人员',
        },
      }],
      unknownHeaders: ['未知列', '计划完成'],
      invalid: [],
      exampleSkipped: false,
    })
  })

  it('单行表头的重复标题按 MES 布局次序对应', async () => {
    const sheet = XLSX.utils.aoa_to_sheet([
      ['工单号', '计划完成', '实际完成', '计划完成', '实际完成', '计划完成'],
      ['WO-1', '第一组计划', '第一组完成', '第二组计划', '第二组完成', '第三组计划'],
    ])
    const result = await readMesExcelImport(fileOf(sheet))
    expect(result.rows[0].values).toEqual({
      workOrder: 'WO-1', yfdkPlanDate: '第一组计划', yfdkActualDate: '第一组完成',
      cgPlanDate: '第二组计划', cgActualDate: '第二组完成', zzzzPlanDate: '第三组计划',
    })
  })

  it('格式化日期、日期时间和原始数字，并省略空白栏位', async () => {
    const sheet = XLSX.utils.aoa_to_sheet([
      ['工单号', '签收日期', '原交期', '数量', '机型', '计划交期'],
      ['WO-2', null, null, 12345.5, '  '],
    ])
    sheet.B2 = { t: 'd', v: new Date('2026-11-23T00:00:00Z'), z: 'General' }
    sheet.C2 = { t: 'n', v: 46349 + (15 * 3600 + 6 * 60 + 7) / 86400, z: 'm/d/yy h:mm:ss' }
    sheet.D2.z = '#,##0.0'
    const result = await readMesExcelImport(fileOf(sheet))
    expect(result.rows[0].values).toEqual({
      workOrder: 'WO-2', applyEndTime: '2026-11-23', deliveryDate: '2026-11-23 15:06:07',
      deviceNum: '12345.5',
    })
  })

  it('日期遵照工作簿的 1904 日期系统', async () => {
    const sheet = XLSX.utils.aoa_to_sheet([['工单号', '计划交期'], ['WO-4', 1]])
    sheet.B2.z = 'm/d/yy'
    const result = await readMesExcelImport(fileOf(sheet, true))
    expect(result.rows[0].values.planEndDate).toBe('1904-01-02')
  })

  it('反查已知代码文字、保留未知值、跳过空工单号并报告过长的行号', async () => {
    const tooLong = 'A'.repeat(65)
    const sheet = XLSX.utils.aoa_to_sheet([
      ['工单号', '工单状态', '是否超期', '出机状态'],
      [' ', '任务完成', '是', '已出机'],
      [tooLong, '任务完成', '是', '已出机'],
      [' WO-3 ', ' 任务中 ', '是', '其他状态 '],
    ])
    const result = await readMesExcelImport(fileOf(sheet))
    expect(result.rows).toEqual([{
      rowNumber: 4, workOrder: 'WO-3',
      values: { workOrder: 'WO-3', productionStatus: '2', delayType: '1', outMachineStatus: '其他状态' },
    }])
    expect(result.invalid).toEqual([{ rowNumber: 3, workOrder: tooLong, reason: '工单号长度需为 1–64 个字符' }])
  })

  it('含 Excel 错误值的行列为无效，不把 #N/A 等文字当成工单号或字段值导入', async () => {
    const sheet = XLSX.utils.aoa_to_sheet([['工单号', '机型'], ['WO-1', 'M-1'], ['WO-2', 'M-2'], ['WO-3', 'M-3']])
    sheet.B3 = { t: 'e', v: 0x2a, w: '#N/A' }
    sheet.A4 = { t: 'e', v: 0x17, w: '#REF!' }
    const result = await readMesExcelImport(fileOf(sheet))
    expect(result.rows).toEqual([{ rowNumber: 2, workOrder: 'WO-1', values: { workOrder: 'WO-1', deviceModels: 'M-1' } }])
    expect(result.invalid).toEqual([
      { rowNumber: 3, workOrder: 'WO-2', reason: '含 Excel 错误值：B 列（#N/A）' },
      { rowNumber: 4, workOrder: '#REF!', reason: '含 Excel 错误值：A 列（#REF!）' },
    ])
  })

  it('数字工单号保留补零格式显示的文字，常规格式的大整数不变成科学计数法', async () => {
    const sheet = XLSX.utils.aoa_to_sheet([['工单号', '机型'], [123, 'M-1'], [21074672, 'M-2'], [123456789012, 'M-3']])
    sheet.A2.z = '00000'
    const result = await readMesExcelImport(fileOf(sheet))
    expect(result.rows.map((row) => [row.workOrder, row.values.workOrder])).toEqual([
      ['00123', '00123'], ['21074672', '21074672'], ['123456789012', '123456789012'],
    ])
  })

  it('缺少工单号栏时使用指定错误文案', async () => {
    await expect(readMesExcelImport(fileOf(XLSX.utils.aoa_to_sheet([['机型'], ['M-1']]))))
      .rejects.toThrow('找不到“工单号”栏')
  })

  it('限制格式、文件大小、数据行数和列数', async () => {
    await expect(readMesExcelImport(new File(['x'], 'IPMS.xls'))).rejects.toThrow('仅支持 .xlsx 文件')
    await expect(readMesExcelImport(new File(['工单号\nWO-1'], '改名.xlsx'))).rejects.toThrow('仅支持 .xlsx 文件')
    await expect(readMesExcelImport(new File([new ArrayBuffer(10 * 1024 * 1024 + 1)], 'IPMS.xlsx')))
      .rejects.toThrow('10 MiB')

    const tooWide = XLSX.utils.aoa_to_sheet([['工单号', ...Array<string>(256).fill('其他')]])
    await expect(readMesExcelImport(fileOf(tooWide))).rejects.toThrow('256 列')

    const boundary = XLSX.utils.aoa_to_sheet([
      ['工单号'], ['示例工单'], ...Array.from({ length: 5000 }, (_, index) => [`WO-${index + 1}`]),
    ])
    expect((await readMesExcelImport(fileOf(boundary))).rows).toHaveLength(5000)
    XLSX.utils.sheet_add_aoa(boundary, [['WO-5001']], { origin: -1 })
    await expect(readMesExcelImport(fileOf(boundary))).rejects.toThrow('5000 条数据')
  })
})
