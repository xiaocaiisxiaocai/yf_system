import type { RulesProps } from '@arco-design/web-react'

export const PASSWORD_MIN_CHARS = 6
export const PASSWORD_MAX_CHARS = 20
export const PASSWORD_MAX_BYTES = 128

export const PASSWORD_VALIDATION_MESSAGE = '密码需 6-20 个字符'

function utf8ByteLength(value: string): number {
  let length = 0
  for (const scalar of value) {
    const codePoint = scalar.codePointAt(0)!
    if (codePoint <= 0x7f) length += 1
    else if (codePoint <= 0x7ff) length += 2
    else if (codePoint <= 0xffff) length += 3
    else length += 4
  }
  return length
}

export function validatePassword(value: string): string | undefined {
  const characterCount = Array.from(value).length
  if (
    characterCount < PASSWORD_MIN_CHARS
    || characterCount > PASSWORD_MAX_CHARS
    || utf8ByteLength(value) > PASSWORD_MAX_BYTES
  ) {
    return PASSWORD_VALIDATION_MESSAGE
  }
  return undefined
}

export const passwordRule: RulesProps<string> = {
  validator(value: string | undefined, callback: (error?: string) => void) {
    callback(value ? validatePassword(value) : undefined)
  },
}
