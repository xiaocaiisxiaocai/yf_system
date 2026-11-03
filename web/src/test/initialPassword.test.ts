import { describe, expect, it } from 'vitest'
import { INITIAL_PASSWORD_MESSAGE, validateInitialPassword, validatePassword } from '../utils/password'

describe('imported initial password', () => {
  it('accepts simple passwords that the full policy rejects', () => {
    for (const password of ['123456', '11111', 'abcdef']) {
      expect(validateInitialPassword(password)).toBeUndefined()
      expect(validatePassword(password)).toBeDefined()
    }
  })

  it('still enforces the length bounds', () => {
    expect(validateInitialPassword('1234')).toBe(INITIAL_PASSWORD_MESSAGE)
    expect(validateInitialPassword('1'.repeat(21))).toBe(INITIAL_PASSWORD_MESSAGE)
  })
})
