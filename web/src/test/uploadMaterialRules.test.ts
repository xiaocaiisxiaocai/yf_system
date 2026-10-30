import { describe, expect, it } from 'vitest'
import { hasRequiredMaterial, isModelMaterial, isRequiredMaterial, isSpreadsheet, isStepFile } from '../utils/uploadMaterialRules'

describe('company-to-supplier upload material rules', () => {
  it('classifies STEP files, archives and spreadsheets by extension only', () => {
    expect(isStepFile('装配.STEP')).toBe(true)
    expect(isStepFile('装配.stp')).toBe(true)
    expect(isStepFile('装配.step.zip')).toBe(false)
    expect(isModelMaterial('装配.step.zip')).toBe(true)
    expect(isModelMaterial('模型.RAR')).toBe(true)
    expect(isModelMaterial('模型.7z')).toBe(true)
    expect(isModelMaterial('7z')).toBe(false)
    expect(isSpreadsheet('a.xlsm')).toBe(true)
    expect(isSpreadsheet('动作流程.xlsx')).toBe(true)
    expect(isSpreadsheet('a.csv')).toBe(false)
    expect(isRequiredMaterial('说明.pdf')).toBe(false)
  })

  it('requires an Excel file together with a STEP or archive file in one batch', () => {
    expect(hasRequiredMaterial(['模型.7z', '动作流程.xlsx'])).toBe(true)
    expect(hasRequiredMaterial(['assembly.stp', '动作流程.xls', '说明.pdf'])).toBe(true)
    expect(hasRequiredMaterial(['模型.zip', 'assembly.step'])).toBe(false)
    expect(hasRequiredMaterial(['动作流程.xlsx', '说明.pdf'])).toBe(false)
    expect(hasRequiredMaterial([])).toBe(false)
  })
})
