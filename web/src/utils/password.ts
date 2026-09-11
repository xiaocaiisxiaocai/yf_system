import type { RulesProps } from '@arco-design/web-react'
import policy from './password-policy.json'

export const PASSWORD_MIN_CHARS = policy.minChars
export const PASSWORD_MAX_CHARS = policy.maxChars
export const PASSWORD_MAX_BYTES = policy.maxBytes

export const PASSWORD_VALIDATION_MESSAGE = policy.message

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
  const characters = Array.from(value)
  const characterCount = characters.length
  if (
    characterCount < PASSWORD_MIN_CHARS
    || characterCount > PASSWORD_MAX_CHARS
    || utf8ByteLength(value) > PASSWORD_MAX_BYTES
  ) {
    return PASSWORD_VALIDATION_MESSAGE
  }
  const substitutions: Record<string, string> = { '@': 'a', '4': 'a', '$': 's', '5': 's', '0': 'o', '1': 'i', '!': 'i', '3': 'e', '7': 't' }
  const stem = value.replace(/[0-9!-/:-@[-`{-~]+$/, '').toLowerCase()
    .replace(/[@4$501!37]/g, character => substitutions[character]).replace(/[^a-z]/g, '')
  if (policy.blockedStems.some(word => stem.length > 0 && stem.length % word.length === 0 && word.repeat(stem.length / word.length) === stem)
    || '0123456789'.repeat(8).includes(value) || '9876543210'.repeat(8).includes(value)
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
