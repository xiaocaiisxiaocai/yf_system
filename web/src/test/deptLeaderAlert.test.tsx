import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  permissions: [] as string[],
  departments: [] as Array<Record<string, unknown>>,
}))

vi.mock('../api/client', () => ({
  default: { get: mocks.get, post: mocks.post, put: vi.fn(), delete: vi.fn() },
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
    mocks.post.mockReset().mockResolvedValue({ data: {} })
  })

  it('负责人管理者只在启用且无负责人的节点上看到标记', async () => {
    mocks.permissions = ['dept:leader_manage']
    render(<DeptManage />)
    await screen.findByText('无主管部')
    const badges = screen.getAllByText('待设主管')
    expect(badges).toHaveLength(1)
    expect(badges[0].closest('.arco-tag')).toHaveClass('org-view-missing-leader')
    expect(screen.getByText('无主管部').closest('.org-view-tree-node')).toHaveTextContent('待设主管')
    expect(screen.getByText('停用部').closest('.org-view-tree-node')).not.toHaveTextContent('待设主管')
    expect(screen.getAllByText('事业一部')[0].closest('.org-view-tree-node')).not.toHaveTextContent('待设主管')
    const childCard = document.querySelector('.org-view-child')!
    expect(childCard.querySelector('.arco-tag')).toHaveTextContent('启用')
  })

  it('没有负责人管理权限时不显示标记', async () => {
    render(<DeptManage />)
    await screen.findByText('无主管部')
    expect(screen.queryByText('待设主管')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '仅看待设主管' })).not.toBeInTheDocument()
  })

  it('新增组织拒绝纯空格名称', async () => {
    mocks.permissions = ['dept:manage']
    const user = userEvent.setup()
    render(<DeptManage />)
    await screen.findByText('无主管部')

    await user.click(screen.getByRole('button', { name: '新增事业部' }))
    const dialog = await screen.findByRole('dialog', { name: '新增事业部' })
    await user.type(within(dialog).getByPlaceholderText('请输入事业部名称'), '   ')
    await user.click(within(dialog).getByRole('button', { name: '创建事业部' }))

    expect(await within(dialog).findByText('事业部名称需为 1-64 个字符')).toBeInTheDocument()
    await waitFor(() => expect(mocks.post).not.toHaveBeenCalled())
  })

  it('负责人管理者可查看待设主管统计并按仅看待设主管筛选组织树', async () => {
    mocks.permissions = ['dept:leader_manage']
    render(<DeptManage />)
    await screen.findByText('无主管部')
    expect(screen.getByLabelText('组织统计')).toHaveTextContent('1 个待设主管')

    const filterBtn = screen.getByRole('button', { name: '仅看待设主管' })
    expect(filterBtn).toHaveAttribute('aria-pressed', 'false')
    fireEvent.click(filterBtn)
    expect(filterBtn).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByText('无主管部')).toBeInTheDocument()
    expect(screen.queryByText('停用部')).not.toBeInTheDocument()
  })
})
