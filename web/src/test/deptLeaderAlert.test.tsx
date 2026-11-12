import { render, screen } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({
  get: vi.fn(),
  permissions: [] as string[],
  departments: [] as Array<Record<string, unknown>>,
}))

vi.mock('../api/client', () => ({
  default: { get: mocks.get, post: vi.fn(), put: vi.fn(), delete: vi.fn() },
  bootAuth: vi.fn(),
}))

vi.mock('../store/auth', () => ({
  useAuth: (selector?: (state: unknown) => unknown) => {
    const state = {
      token: 'test-token', permissions: mocks.permissions,
      user: { id: 1, employeeNo: 'admin', realName: '管理员', userType: 'INTERNAL', isSystemAdmin: false },
      hasPerm: (code: string) => mocks.permissions.includes(code),
      logout: vi.fn(),
    }
    return selector ? selector(state) : state
  },
}))

import DeptManage from '../pages/org/DeptManage'

const leader = { id: 9, employeeNo: 'E009', realName: '主管', active: true }

describe('组织树负责人缺失标记', () => {
  beforeEach(() => {
    mocks.permissions = []
    mocks.departments = [
      { id: 1, name: '事业一部', kind: 'DIVISION', sortNo: 1, status: 'ACTIVE', leader, children: [
        { id: 2, name: '无主管部', kind: 'DEPARTMENT', parentId: 1, sortNo: 1, status: 'ACTIVE', leader: null, children: [] },
        { id: 3, name: '停用部', kind: 'DEPARTMENT', parentId: 1, sortNo: 2, status: 'DISABLED', leader: null, children: [] },
      ] },
    ]
    mocks.get.mockImplementation(async (url: string) => {
      if (url === '/departments') return { data: mocks.departments }
      return { data: [] }
    })
  })

  it('负责人管理者只在启用且无负责人的节点上看到标记', async () => {
    mocks.permissions = ['dept:leader_manage']
    render(<DeptManage />)
    await screen.findByText('无主管部')
    expect(screen.getAllByText('未设负责人')).toHaveLength(1)
    expect(screen.getByText('无主管部').closest('.org-view-tree-node')).toHaveTextContent('未设负责人')
    expect(screen.getByText('停用部').closest('.org-view-tree-node')).not.toHaveTextContent('未设负责人')
    expect(screen.getAllByText('事业一部')[0].closest('.org-view-tree-node')).not.toHaveTextContent('未设负责人')
  })

  it('没有负责人管理权限时不显示标记', async () => {
    render(<DeptManage />)
    await screen.findByText('无主管部')
    expect(screen.queryByText('未设负责人')).not.toBeInTheDocument()
  })
})
