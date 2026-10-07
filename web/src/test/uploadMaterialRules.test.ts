import { describe, expect, it } from 'vitest'
import { companyMaterialNameError, isMotionFlowWorkbookName, isSpreadsheet, isStepFile } from '../utils/uploadMaterialRules'
import sharedCases from '../utils/upload-material-rules.cases.json'

// 与后端 UploadMaterialRules.cs 同一正则；样例覆盖简繁、全角、空白个数与每段 1~10 位上限。
describe('company-to-supplier upload material rules', () => {
  it.each([
    'CSLR-605 JH机 210231-1 动作流程.xlsx',
    'CSLR-605 JH機 210231-1 動作流程.xlsx',
    'CSLR-605　JH机　210231-1　动作流程.xlsx',
    'CSLR-605放板機2105931-1動作流程.xlsx',
    'CSLR-605   JH机   210231-12   动作流程.xlsx',
    'cslr-a1b2 贴标机 210ABC-x 动作流程.XLSX',
    'CSLR－605 JH机 210231－1 动作流程.xlsx',
    'CSLR-0123456789 一二三四五六七八九機 2100123456789-0123456789 動作流程.xlsx',
    'CSLR-605 一二三四五六七八九十機 210231-1 動作流程.xlsx',
    'CSLR-605 JH机 210231-1 动作流程.xls',
    'CSLR-605 JH機 210231-1 動作流程.xlsm',
    'CSLR-605 JH机 210231-1 动作流程.XLSB',
  ])('accepts %s', (name) => {
    expect(isMotionFlowWorkbookName(name)).toBe(true)
    expect(companyMaterialNameError(name)).toBeNull()
  })

  it.each([
    'CSLR-01234567890 JH机 210231-1 动作流程.xlsx',
    'CSLR-605 JH机 21001234567890-1 动作流程.xlsx',
    'CSLR-605 JH机 210231-01234567890 动作流程.xlsx',
    'CSLR-605 一二三四五六七八九十一機 210231-1 動作流程.xlsx',
    'CSLR-605 机 210231-1 动作流程.xlsx',
    'CSLR-605JH机 210231-1 动作流程.xlsx',
    '动作流程.xls',
    '动作流程.xlsm',
    'CSLR-605 JH机 210231-1 流程.xlsb',
    'CSLX-605 JH机 210231-1 动作流程.xlsx',
    'CSLR-605 JH机 220231-1 动作流程.xlsx',
    'CSLR-605 JH机 210231-1 流程.xlsx',
    '动作流程.xlsx',
  ])('rejects %s', (name) => {
    expect(isMotionFlowWorkbookName(name)).toBe(false)
    expect(companyMaterialNameError(name)).toMatch(/CSLR-XXX XXX机 210XXX-X 动作流程\.xlsx/)
  })

  // 与后端共享的契约用例：同一份 JSON 由 UploadMaterialRules.cs 的测试逐条校验，保证前后端判定一致。
  describe('shared contract cases (upload-material-rules.cases.json)', () => {
    it('has both accept and reject cases', () => {
      expect(sharedCases.accept.length).toBeGreaterThan(0)
      expect(sharedCases.reject.length).toBeGreaterThan(0)
    })

    it.each(sharedCases.accept.map((name) => [JSON.stringify(name), name]))('accepts %s', (_label, name) => {
      expect(isMotionFlowWorkbookName(name)).toBe(true)
      expect(companyMaterialNameError(name)).toBeNull()
    })

    it.each(sharedCases.reject.map((name) => [JSON.stringify(name), name]))('rejects %s', (_label, name) => {
      expect(isMotionFlowWorkbookName(name)).toBe(false)
    })
  })

  it('classifies STEP files and spreadsheets by extension only', () => {
    expect(isStepFile('装配.STEP')).toBe(true)
    expect(isStepFile('装配.stp')).toBe(true)
    expect(isStepFile('装配.step.zip')).toBe(false)
    expect(isSpreadsheet('a.xlsm')).toBe(true)
    expect(isSpreadsheet('a.csv')).toBe(false)
    expect(companyMaterialNameError('说明.pdf')).toBeNull()
  })
})
