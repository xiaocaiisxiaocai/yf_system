import { vi } from 'vitest'

export const messageMocks = {
  get: vi.fn(),
  post: vi.fn(),
  delete: vi.fn(),
  permissions: new Set<string>(),
  user: {
    id: 9,
    employeeNo: 'E009',
    realName: '测试用户',
    userType: 'INTERNAL',
  } as Record<string, unknown> | null,
}
