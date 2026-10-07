// 公司内部发给供应商（C2S）的资料要求；与后端 Yf.Api/Modules/Files/UploadMaterialRules.cs 保持一致。
// 供应商发给公司（S2C）不设限制，只展示资料示例。

const STEP_EXTENSIONS = ['step', 'stp']
const SPREADSHEET_EXTENSIONS = ['xls', 'xlsx', 'xlsm', 'xlsb']

/** CSLR-XXX XXX机 210XXX-X 动作流程.xlsx（.xls/.xlsx/.xlsm/.xlsb 同一规则）：每段 X 为 1~10 位，空白个数不限，机/機、动作/動作均可。 */
const MOTION_FLOW_WORKBOOK = /^CSLR\s*[-－]\s*[0-9A-Za-z]{1,10}(?![0-9A-Za-z])\s*\S.{0,9}?[机機]\s*210[0-9A-Za-z]{1,10}\s*[-－]\s*[0-9A-Za-z]{1,10}\s*[动動]作流程\s*\.xls[xmb]?$/i

export const MOTION_FLOW_NAMING_EXAMPLE = 'CSLR-XXX XXX机 210XXX-X 动作流程.xlsx'

function extensionOf(name: string): string {
  const dot = name.lastIndexOf('.')
  return dot < 0 ? '' : name.slice(dot + 1).toLowerCase()
}

export function isStepFile(name: string): boolean {
  return STEP_EXTENSIONS.includes(extensionOf(name))
}

export function isSpreadsheet(name: string): boolean {
  return SPREADSHEET_EXTENSIONS.includes(extensionOf(name))
}

export function isMotionFlowWorkbookName(name: string): boolean {
  return MOTION_FLOW_WORKBOOK.test(name)
}

/** 公司发给供应商的文件名不合规时返回提示，否则返回 null。 */
export function companyMaterialNameError(name: string): string | null {
  if (isSpreadsheet(name) && !isMotionFlowWorkbookName(name)) {
    return `Excel 需按「${MOTION_FLOW_NAMING_EXAMPLE}」命名（扩展名可为 .xls/.xlsx/.xlsm/.xlsb；每段 X 为 1~10 位，空格个数不限，机/機、动作/動作均可）`
  }
  return null
}
