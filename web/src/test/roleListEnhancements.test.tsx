import { fireEvent, render, screen, within } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({
  get: vi.fn(),
  put: vi.fn(),
  post: vi.fn(),
  delete: vi.fn(),
  auth: {
    user: { id: 1, realName: '管理员', userType: 'INTERNAL' },
    hasPerm: () => true,
  },
}))

vi.mock('../api/client', () => ({
  default: {
    get: mocks.get,
    put: mocks.put,
    post: mocks.post,
    delete: mocks.delete,
  },
}))

vi.mock('../store/auth', () => ({
  useAuth: (selector?: (state: typeof mocks.auth) => unknown) =>
    selector ? selector(mocks.auth) : mocks.auth,
}))

import RoleList from '../pages/rbac/RoleList'

describe('RoleList permission tree enhancements and user impact indicator', () => {
  beforeEach(() => {
    mocks.get.mockImplementation(async (url: string) => {
      if (url === '/admin/roles') {
        return {
          data: {
            list: [
              {
                id: 10,
                name: '工程主管',
                description: '负责工程技术把关',
                isBuiltIn: false,
                status: 'ACTIVE',
                permissionIds: [1, 2],
                assignedUserCount: 8,
                canManage: true,
              },
            ],
            total: 1,
            page: 1,
            pageSize: 20,
          },
        }
      }
      if (url === '/permissions') {
        return {
          data: [
            { id: 1, name: '项目协作', type: 'MENU', parentId: null, grantable: true },
            { id: 2, name: '项目查看', type: 'ACTION', parentId: 1, grantable: true },
            { id: 3, name: '项目删除', type: 'ACTION', parentId: 1, grantable: true },
          ],
        }
      }
      return { data: [] }
    })
  })

  it('renders user impact count and alert notice in permission drawer', async () => {
    render(<RoleList />)

    const row = (await screen.findByText('工程主管')).closest('tr')!
    const assignBtn = within(row).getByRole('button', { name: /分配权限/ })
    fireEvent.click(assignBtn)

    expect(await screen.findByText(/分配权限 · 工程主管/)).toBeVisible()

    // Assert user impact tag is rendered
    expect(screen.getByText('8 位用户')).toBeVisible()
    expect(screen.getByText(/修改后将即时影响已绑定的用户权限/)).toBeVisible()
    expect(screen.getByText('项目查看')).toBeVisible()
  })
})
