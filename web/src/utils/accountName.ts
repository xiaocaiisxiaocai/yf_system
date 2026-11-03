// 登录账号规则，与后端 AccountNameRule 一致：3~32 位字母、数字、下划线或点；点只能用来分隔，
// 不能在开头、结尾或连续出现（例如 zhuhui.cai）。
export const ACCOUNT_NAME_PATTERN = /^(?=.{3,32}$)[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)*$/
export const ACCOUNT_NAME_MESSAGE = '账号需为 3~32 位字母、数字、下划线或点，点不能在开头、结尾或连续出现'
export const ACCOUNT_NAME_PLACEHOLDER = '3-32 位字母、数字、下划线或点'

export function isValidAccountName(value: string): boolean {
  return ACCOUNT_NAME_PATTERN.test(value)
}
