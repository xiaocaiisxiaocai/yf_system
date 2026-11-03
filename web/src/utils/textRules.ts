import type { RulesProps } from '@arco-design/web-react'

// The API trims names/emails and counts Unicode scalars, not UTF-16 units.
export function textLengthRule(label: string, maximum: number): RulesProps<string> {
  return {
    validator(value: string | undefined, callback: (error?: string) => void) {
      const length = Array.from((value ?? '').trim()).length
      callback(length < 1 || length > maximum ? `${label}需为 1-${maximum} 个字符` : undefined)
    },
  }
}

/** For optional fields: empty is allowed, otherwise the same scalar-count maximum applies. */
export function optionalTextLengthRule(label: string, maximum: number): RulesProps<string> {
  return {
    validator(value: string | undefined, callback: (error?: string) => void) {
      const length = Array.from((value ?? '').trim()).length
      callback(length > maximum ? `${label}不能超过 ${maximum} 个字符` : undefined)
    },
  }
}
