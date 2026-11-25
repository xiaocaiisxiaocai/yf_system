import { describe, expect, it } from 'vitest'
import {
  INITIAL_PASSWORD_MESSAGE,
  PASSWORD_VALIDATION_MESSAGE,
  validateInitialPassword,
  validatePassword,
} from '../../../utils/password'

describe('password policy', () => {
  it('accepts 5-7 char passwords whose characters only partially overlap across period 3 or 4', () => {
    for (const password of ['K9#mK', 'X!7@X', 'A2$bA', 'K9#mK9', 'K9#mK9#', 'ab#ab']) {
      expect(validatePassword(password)).toBeUndefined()
    }
  })

  it('still rejects true periodic repetitions of period 1 through 4', () => {
    for (const password of ['aaaaa', '11111', 'ababa', 'ababab', 'abcabc', 'a1!a1!', 'K9#mK9#m', 'abcdabcd']) {
      expect(validatePassword(password)).toBe(PASSWORD_VALIDATION_MESSAGE)
    }
  })

  it('rejects blocked stems, simple sequences, and out-of-range lengths', () => {
    for (const password of ['1234', 'a'.repeat(21), 'password1', 'P@ssw0rd!', 'qwerty', '123456']) {
      expect(validatePassword(password)).toBe(PASSWORD_VALIDATION_MESSAGE)
    }
    expect(validateInitialPassword('1234')).toBe(INITIAL_PASSWORD_MESSAGE)
    expect(validateInitialPassword('12345')).toBeUndefined()
  })
})
