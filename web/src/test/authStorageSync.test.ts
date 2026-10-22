import { describe, expect, it } from 'vitest'
import { decideAuthStorageSync } from '../store/authStorageSync'

const current = { user: { id: 7 }, permissions: ['a', 'b'], menus: ['m1'], mustChangePassword: false }
const stored = (state: unknown) => JSON.stringify({ state, version: 1 })

describe('decideAuthStorageSync', () => {
  it('logs out when another tab signed out or cleared the record', () => {
    expect(decideAuthStorageSync(stored({ user: null, permissions: [], menus: [] }), current)).toEqual({ type: 'logout' })
    expect(decideAuthStorageSync(null, current)).toEqual({ type: 'logout' })
  })

  it('reloads only when another tab switched to a different account', () => {
    expect(decideAuthStorageSync(stored({ user: { id: 8 }, permissions: [], menus: [] }), current)).toEqual({ type: 'reload' })
  })

  it('ignores a pure reordering of the same permissions and menus', () => {
    expect(decideAuthStorageSync(stored({ user: { id: 7 }, permissions: ['b', 'a'], menus: ['m1'], mustChangePassword: false }), current))
      .toEqual({ type: 'ignore' })
  })

  it('applies changed permissions, menus or the forced password change in place', () => {
    expect(decideAuthStorageSync(stored({ user: { id: 7 }, permissions: ['a'], menus: ['m1', 'm2'], mustChangePassword: true }), current))
      .toEqual({ type: 'apply', permissions: ['a'], menus: ['m1', 'm2'], mustChangePassword: true })
  })

  it('ignores malformed records and tabs that are not signed in', () => {
    expect(decideAuthStorageSync('{not json', current)).toEqual({ type: 'ignore' })
    expect(decideAuthStorageSync(stored({ user: { id: 7 } }), { ...current, user: null })).toEqual({ type: 'ignore' })
  })
})
