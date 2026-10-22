// 公司内部发给供应商（C2S）的资料要求；与后端 Yf.Api/Modules/Files/UploadMaterialRules.cs 保持一致。
// 供应商发给公司（S2C）不设限制，只展示资料示例。动作流程 Excel 不再校验文件名。

const STEP_EXTENSIONS = ['step', 'stp']
const SPREADSHEET_EXTENSIONS = ['xls', 'xlsx', 'xlsm', 'xlsb']

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
