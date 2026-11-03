import { describe, expect, it } from 'vitest'
import { isValidAccountName } from '../utils/accountName'

describe('account name rule', () => {
  it.each(['zhuhui.cai', 'Robert.Lee', 'a.b.c', 'employee_1', 'FANUC001'])('accepts %s', (name) => {
    expect(isValidAccountName(name)).toBe(true)
  })

  it.each(['.lee', 'lee.', 'robert..lee', 'ab', 'robert-lee', 'robert lee', '王小明', 'a'.repeat(16) + '.' + 'b'.repeat(16)])('rejects %s', (name) => {
    expect(isValidAccountName(name)).toBe(false)
  })
})
