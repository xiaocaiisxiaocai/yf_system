import { afterEach, describe, expect, it, vi } from 'vitest'

describe('auth store migration', () => {
  afterEach(() => {
    localStorage.clear()
    vi.resetModules()
  })

  it('auth persistence restores the current schema and discards legacy records', async () => {
    localStorage.clear()
    vi.resetModules()
    let auth = await import('../../../store/auth')
    auth.useAuth.getState().setLogin({
      accessToken: 'fixture-token',
      expiresAt: 123456,
      user: {
        id: 7,
        employeeNo: 'fixture',
        realName: '个人姓名',
        email: 'fixture@example.invalid',
        userType: 'INTERNAL',
        supplierId: null,
        isSystemAdmin: false,
      },
      permissions: ['project:list'],
      menus: ['dashboard'],
      mustChangePassword: false,
    })
    const saved = JSON.parse(localStorage.getItem('yf-auth')!) as { state: Record<string, any> }
    expect(saved.state.user).toEqual({ id: 7 })
    expect(saved.state).not.toHaveProperty('token')
    expect(JSON.stringify(saved)).not.toContain('个人姓名')
    expect(JSON.stringify(saved)).not.toContain('fixture@example.invalid')

    vi.resetModules()
    auth = await import('../../../store/auth')
    expect(auth.useAuth.getState()).toMatchObject({
      user: { id: 7 },
      permissions: ['project:list'],
      menus: ['dashboard'],
      mustChangePassword: false,
      token: null,
      booted: false,
    })

    localStorage.setItem('yf-auth', JSON.stringify({
      version: 0,
      state: {
        user: { id: 9, employeeNo: 'legacy', realName: '旧姓名', email: 'legacy@example.invalid' },
        token: 'legacy-token',
        permissions: ['admin:all'],
        menus: ['system:admin'],
        mustChangePassword: true,
      },
    }))
    vi.resetModules()
    auth = await import('../../../store/auth')
    expect(auth.useAuth.getState()).toMatchObject({
      user: null,
      permissions: [],
      menus: [],
      mustChangePassword: false,
      token: null,
      booted: false,
    })
  })
})
