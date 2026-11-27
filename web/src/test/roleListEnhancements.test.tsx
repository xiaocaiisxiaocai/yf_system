import { fireEvent, render, screen, within } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({
  get: vi.fn(),
  put: vi.fn(),
  post: vi.fn(),
  delete: vi.fn(),
  auth: {
    user: { id: 1, realName: '管理员', userType: 'INTERNAL' },
    permissions: ['role:manage', 'role:delete'],
    hasPerm: (code: string): boolean => mocks.auth.permissions.includes(code),
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
    mocks.auth.permissions = ['role:manage', 'role:delete']
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
            { id: 4, name: '项目导出', type: 'ACTION', parentId: 1, grantable: true },
            { id: 200, code: 'oem', name: 'ODM/OEMA 文件传递', type: 'MENU', parentId: null, grantable: true },
            { id: 217, code: 'outsource', name: '外协清单', type: 'MENU', parentId: null, grantable: true },
            { id: 235, code: 'outsource:view', name: '查看外发工令', type: 'ACTION', parentId: 217, grantable: true },
            { id: 236, code: 'outsource:export', name: '导出外发工令', type: 'ACTION', parentId: 217, grantable: true },
            { id: 237, code: 'outsource:import', name: '导入外发工令', type: 'ACTION', parentId: 217, grantable: true },
            { id: 238, code: 'outsource:delete', name: '删除外发工令', type: 'ACTION', parentId: 217, grantable: true },
            { id: 233, code: 'outsource:date_import', name: '导入BOM时间与发图', type: 'ACTION', parentId: 217, grantable: true },
            { id: 221, code: 'outsource:settings', name: '外发工令设定', type: 'ACTION', parentId: 217, grantable: true },
            { id: 232, code: 'outsource:send', name: '发送外发工令邮件', type: 'ACTION', parentId: 217, grantable: true },
            { id: 220, code: 'outsource:manage', name: '管理外协清单', type: 'ACTION', parentId: 217, grantable: true },
          ],
        }
      }
      return { data: [] }
    })
  })

  it('查看与删除授权不能代替管理权，即使目标角色在委派范围内', async () => {
    mocks.auth.permissions = ['role:view', 'role:delete']
    render(<RoleList />)
    const row = within((await screen.findByText('工程主管')).closest('tr')!)
    expect(row.queryByRole('button', { name: '编辑' })).not.toBeInTheDocument()
    expect(row.queryByRole('button', { name: '停用' })).not.toBeInTheDocument()
    expect(row.queryByRole('button', { name: '删除' })).not.toBeInTheDocument()
    fireEvent.click(row.getByRole('button', { name: '查看权限' }))
    expect(await screen.findByText('查看权限 · 工程主管')).toBeVisible()
    const checked = screen.getByText('项目查看').closest('.arco-tree-node')!.querySelector('input')!
    expect(checked).toBeChecked()
    expect(checked).toBeDisabled()
    for (const name of ['项目范围', 'ODM/OEMA 传递单范围']) {
      expect(screen.getByRole('combobox', { name })).toHaveClass('arco-select-disabled')
    }
    expect(screen.queryByRole('button', { name: '保存权限' })).not.toBeInTheDocument()
  })

  it('已打开的授权抽屉在撤销管理权后丢弃未保存的勾选', async () => {
    const view = render(<RoleList />)
    fireEvent.click(await screen.findByRole('button', { name: '分配权限' }))
    const checkbox = (await screen.findByText('项目删除')).closest('.arco-tree-node')!.querySelector('input')!
    fireEvent.click(checkbox)
    expect(checkbox).toBeChecked()
    mocks.auth.permissions = ['role:view']
    view.rerender(<RoleList />)
    expect(screen.queryByRole('button', { name: '保存权限' })).not.toBeInTheDocument()
    fireEvent.click(await screen.findByRole('button', { name: '查看权限' }))
    const saved = (await screen.findByText('项目删除')).closest('.arco-tree-node')!.querySelector('input')!
    expect(saved).not.toBeChecked()
    expect(saved).toBeDisabled()
    expect(mocks.put).not.toHaveBeenCalled()
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

  it('shows and filters work-order permissions by their current UI names', async () => {
    render(<RoleList />)
    fireEvent.click(await screen.findByRole('button', { name: '分配权限' }))
    expect(await screen.findByText('ODM/OEM工令协作')).toBeVisible()
    const filter = screen.getByPlaceholderText('筛选权限名称或标识')
    fireEvent.change(filter, { target: { value: '管理外发工令' } })
    expect(screen.getByText('外发工令')).toBeVisible()
    expect(screen.getByText('管理外发工令')).toBeVisible()
    expect(screen.queryByText('项目协作')).not.toBeInTheDocument()
  })

  it('勾选清单操作时说明查看依赖，独立邮件和设置不触发，也不自动授权', async () => {
    render(<RoleList />)
    fireEvent.click(await screen.findByRole('button', { name: '分配权限' }))
    await screen.findByText('查看外发工令')
    const checkbox = (name: string) => screen.getByText(name).closest('.arco-tree-node')!.querySelector('input')!
    const view = checkbox('查看外发工令')
    const hint = '办理这些操作还需要查看外发工令。'
    fireEvent.click(checkbox('外发工令设定'))
    fireEvent.click(checkbox('发送外发工令邮件'))
    expect(screen.queryByText(hint)).not.toBeInTheDocument()
    for (const label of ['导入外发工令', '删除外发工令', '导入BOM时间与发图', '导出外发工令']) {
      fireEvent.click(checkbox(label))
      expect(screen.getByText(hint)).toBeVisible()
      expect(view).not.toBeChecked()
      fireEvent.click(checkbox(label))
      expect(screen.queryByText(hint)).not.toBeInTheDocument()
    }
    fireEvent.click(checkbox('导出外发工令'))
    fireEvent.click(view)
    expect(screen.queryByText(hint)).not.toBeInTheDocument()
    fireEvent.click(view)
    fireEvent.click(screen.getByRole('button', { name: '保存权限' }))
    await vi.waitFor(() => expect(mocks.put).toHaveBeenCalled())
    const [, body] = mocks.put.mock.calls[0] as [string, { permissionIds: number[] }]
    expect(body.permissionIds).toContain(236)
    expect(body.permissionIds).not.toContain(235)
  })

  it('limits a parent toggle to the children left visible by the permission filter', async () => {
    render(<RoleList />)

    const row = (await screen.findByText('工程主管')).closest('tr')!
    fireEvent.click(within(row).getByRole('button', { name: /分配权限/ }))
    expect(await screen.findByText(/分配权限 · 工程主管/)).toBeVisible()

    fireEvent.change(screen.getByPlaceholderText('筛选权限名称或标识'), { target: { value: '项目删除' } })
    expect(screen.queryByText('项目查看')).not.toBeInTheDocument()
    const parentNode = screen.getByText('项目协作').closest('.arco-tree-node')!
    fireEvent.click(parentNode.querySelector('.arco-checkbox')!)
    fireEvent.click(screen.getByRole('button', { name: /保存权限/ }))

    await vi.waitFor(() => expect(mocks.put).toHaveBeenCalled())
    const [, body] = mocks.put.mock.calls[0] as [string, { permissionIds: number[] | null }]
    // 项目删除 (id 3) is the only visible child that is added; the hidden 项目导出 (id 4) must stay ungranted.
    expect(body.permissionIds).toEqual(expect.arrayContaining([1, 2, 3]))
    expect(body.permissionIds).not.toContain(4)
  })
})
