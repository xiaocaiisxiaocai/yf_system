// 公司内部发给供应商（C2S）的资料要求；与后端 Yf.Api/Modules/Files/UploadMaterialRules.cs 保持一致。
// 上传窗口的每一批必须同时有动作流程 Excel 与 STEP 3D 图或压缩文件（STEP 很多时会打包压缩）。
// 供应商发给公司（S2C）不设限制，只展示资料示例。动作流程 Excel 不再校验文件名。

const STEP_EXTENSIONS = ['step', 'stp']
const ARCHIVE_EXTENSIONS = ['zip', 'rar', '7z']
const SPREADSHEET_EXTENSIONS = ['xls', 'xlsx', 'xlsm', 'xlsb']

function extensionOf(name: string): string {
  const dot = name.lastIndexOf('.')
  return dot < 0 ? '' : name.slice(dot + 1).toLowerCase()
}

export function isStepFile(name: string): boolean {
  return STEP_EXTENSIONS.includes(extensionOf(name))
}

export function isArchive(name: string): boolean {
  return ARCHIVE_EXTENSIONS.includes(extensionOf(name))
}

/** 3D 资料：STEP 或压缩文件。 */
export function isModelMaterial(name: string): boolean {
  return isStepFile(name) || isArchive(name)
}

export function isSpreadsheet(name: string): boolean {
  return SPREADSHEET_EXTENSIONS.includes(extensionOf(name))
}

/** Excel、STEP、压缩文件本身就是必需资料，同批其它文件要等它们建立上传会话。 */
export function isRequiredMaterial(name: string): boolean {
  return isModelMaterial(name) || isSpreadsheet(name)
}

/** 一批文件是否同时有动作流程 Excel 与 STEP 或压缩文件。 */
export function hasRequiredMaterial(names: readonly string[]): boolean {
  return names.some(isModelMaterial) && names.some(isSpreadsheet)
}
