import type { FormInstance, RulesProps } from '@arco-design/web-react'
import policy from './password-policy.json'

export const PASSWORD_MIN_CHARS = policy.minChars
export const PASSWORD_MAX_CHARS = policy.maxChars
export const PASSWORD_MAX_BYTES = policy.maxBytes

export const PASSWORD_VALIDATION_MESSAGE = policy.message

const SIMPLE_SEQUENCE_WINDOWS = policy.simpleSequenceRows.flatMap(row => [
  row.repeat(3), Array.from(row).reverse().join('').repeat(3),
])

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

function withinLength(value: string): boolean {
  const characterCount = Array.from(value).length
  return characterCount >= PASSWORD_MIN_CHARS && characterCount <= PASSWORD_MAX_CHARS
    && utf8ByteLength(value) <= PASSWORD_MAX_BYTES
}

function isSimpleSequence(value: string): boolean {
  return value.length >= PASSWORD_MIN_CHARS
    && SIMPLE_SEQUENCE_WINDOWS.some(window => window.includes(value))
}

export const INITIAL_PASSWORD_MESSAGE = `初始密码需为 ${PASSWORD_MIN_CHARS}–${PASSWORD_MAX_CHARS} 个字符`

/**
 * 批量导入的初始密码只校验长度（如 123456 可用）：新账号首次登录必须改密，改密时仍按完整密码规则校验。
 * 与后端 PasswordService.ValidateInitial 一致。
 */
export function validateInitialPassword(value: string): string | undefined {
  return withinLength(value) ? undefined : INITIAL_PASSWORD_MESSAGE
}

export function validatePassword(value: string): string | undefined {
  const characters = Array.from(value)
  if (!withinLength(value)) {
    return PASSWORD_VALIDATION_MESSAGE
  }
  const substitutions: Record<string, string> = { '@': 'a', '4': 'a', '$': 's', '5': 's', '0': 'o', '1': 'i', '!': 'i', '3': 'e', '7': 't' }
  const stem = value.replace(/[0-9!-/:-@[-`{-~]+$/, '').toLowerCase()
    .replace(/[@4$501!37]/g, character => substitutions[character]).replace(/[^a-z]/g, '')
  const sequenceCandidate = value.replace(/[!-/:-@[-`{-~]+$/, '').toLowerCase()
  if (policy.blockedStems.some(word => stem.length > 0 && stem.length % word.length === 0 && word.repeat(stem.length / word.length) === stem)
    || isSimpleSequence(stem) || isSimpleSequence(sequenceCandidate)
    || [1, 2, 3, 4].some(period => characters.every((c, i) => c === characters[i % period]))) {
    return PASSWORD_VALIDATION_MESSAGE
  }
  return undefined
}

export const passwordRule: RulesProps<string> = {
  validator(value: string | undefined, callback: (error?: string) => void) {
    callback(value ? validatePassword(value) : undefined)
  },
}

export const PASSWORD_MISMATCH_MESSAGE = '两次输入的新密码不一致'

/** 确认密码与表单里的 newPassword 不一致时把错误标在确认框上；配合 Form.Item 的 dependencies={['newPassword']}。 */
export function confirmPasswordRule(form: FormInstance): RulesProps<string> {
  return {
    validator(value: string | undefined, callback: (error?: string) => void) {
      callback(value && value !== form.getFieldValue('newPassword') ? PASSWORD_MISMATCH_MESSAGE : undefined)
    },
  }
}
