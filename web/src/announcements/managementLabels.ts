export function internalAudience(scope: string, departmentName: string | null, names: string[]) {
  if (scope === 'ALL') return '全体内部人员'
  if (scope === 'SELECTED') return names.length ? names.join('、') + '（含下属课别）' : '未选部门'
  return departmentName ? departmentName + '（含下属课别）' : '未确定部门'
}
