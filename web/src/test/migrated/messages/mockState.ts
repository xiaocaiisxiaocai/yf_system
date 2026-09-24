import { vi } from 'vitest'

export const messageMocks = {
  get: vi.fn(),
  post: vi.fn(),
  delete: vi.fn(),
  permissions: new Set<string>(),
  generation: 0,
  user: {
    id: 9,
    employeeNo: 'E009',
    realName: '测试用户',
    userType: 'INTERNAL',
  } as Record<string, unknown> | null,
}

export function messageAuthState() {
  return {
    hasPerm: (permission: string) => messageMocks.permissions.has(permission),
    user: messageMocks.user,
    permissions: [...messageMocks.permissions],
    menus: [],
    mustChangePassword: false,
    generation: messageMocks.generation,
  }
}

export function selectMessageAuthState<T>(selector?: (state: ReturnType<typeof messageAuthState>) => T) {
  const state = messageAuthState()
  return selector ? selector(state) : state
}
