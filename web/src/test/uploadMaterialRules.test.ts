import { describe, expect, it } from 'vitest'
import { isSpreadsheet, isStepFile } from '../utils/uploadMaterialRules'

describe('company-to-supplier upload material rules', () => {
  it('classifies STEP files and spreadsheets by extension only', () => {
    expect(isStepFile('装配.STEP')).toBe(true)
    expect(isStepFile('装配.stp')).toBe(true)
    expect(isStepFile('装配.step.zip')).toBe(false)
    expect(isSpreadsheet('a.xlsm')).toBe(true)
    expect(isSpreadsheet('动作流程.xlsx')).toBe(true)
    expect(isSpreadsheet('a.csv')).toBe(false)
  })
})
