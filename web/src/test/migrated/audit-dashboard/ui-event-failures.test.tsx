import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({
  get: vi.fn(),
  put: vi.fn(),
  post: vi.fn(),
  delete: vi.fn(),
  permissions: [] as string[],
}))

vi.mock('../../../api/client', () => ({
  default: { get: mocks.get, put: mocks.put, post: mocks.post, delete: mocks.delete },
}))
vi.mock('../../../store/auth', () => ({
  useAuth: Object.assign(
    (selector?: (state: { user: { id: number; userType: string }; hasPerm: (permission: string) => boolean }) => unknown) => {
      const state = { user: { id: 999, userType: 'INTERNAL' }, hasPerm: (permission: string) => mocks.permissions.includes(permission) }
      return selector ? selector(state) : state
    },
    { getState: () => ({ generation: 1 }) },
  ),
}))
vi.mock('dockview-react', () => import('../../dockviewMock'))
vi.mock('../../../components/FileTable', () => ({ default: () => <div>file table</div> }))
vi.mock('../../../components/MessagePanel', () => ({ default: () => <div>messages</div> }))
vi.mock('../../../components/ProjectActivityPanel', () => ({ default: () => <div>activity</div> }))
vi.mock('../../../store/collaboration', () => ({
  useCollaboration: (selector: (state: { revision: string; status: string }) => unknown) => selector({ revision: '', status: 'connected' }),
}))

import RoleList from '../../../pages/rbac/RoleList'
import ProjectGroupDetail from '../../../pages/project/ProjectGroupDetail'
import SupplierList from '../../../pages/supplier/SupplierList'

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((ok, fail) => { resolve = ok; reject = fail })
  return { promise, resolve, reject }
}

function projectView() {
  return (
    <MemoryRouter initialEntries={['/project-groups/3']}>
      <Routes><Route path="/project-groups/:id" element={<ProjectGroupDetail />} /></Routes>
    </MemoryRouter>
  )
}

describe('UI 事件失败保护', () => {
  beforeEach(() => {
    mocks.permissions = []
    mocks.put.mockResolvedValue({ data: {} })
    mocks.post.mockResolvedValue({ data: {} })
    mocks.delete.mockResolvedValue({ data: {} })
  })

  it('role permission save failure resolves the click handler, keeps selections, and unlocks retry', async () => {
    const role = {
      id: 7, name: '质量角色', description: '', isBuiltIn: false, status: 'ACTIVE',
      permissionIds: [1], assignedUserCount: 0, canManage: true, supplierRestricted: false,
    }
    const permissions = [
      { id: 1, code: 'project', name: '项目协作', type: 'MENU', parentId: null, grantable: true, supplierAssignable: true },
      { id: 2, code: 'project:list', name: '查看项目', type: 'ACTION', parentId: 1, grantable: true, supplierAssignable: true },
    ]
    const firstSave = deferred<{ data: unknown }>()
    let attempts = 0
    mocks.get.mockImplementation(async (url: string) => ({ data: url === '/permissions' ? permissions : { list: [role], total: 1, page: 1, pageSize: 20 } }))
    mocks.put.mockImplementation(async (url: string) => {
      expect(url).toBe('/admin/roles/7/permissions')
      attempts += 1
      return attempts === 1 ? firstSave.promise : { data: {} }
    })
    const user = userEvent.setup()
    render(<RoleList />)
    await user.click(await screen.findByRole('button', { name: '分配权限' }))

    const childNode = screen.getByText('查看项目').closest('.arco-tree-node') as HTMLElement
    const childCheckbox = childNode.querySelector<HTMLInputElement>('input[type="checkbox"]')
    expect(childCheckbox).not.toBeNull()
    await user.click(childCheckbox as HTMLInputElement)
    expect(childCheckbox).toBeChecked()

    const save = screen.getByRole('button', { name: '保存权限' })
    fireEvent.click(save)
    fireEvent.click(save)
    await waitFor(() => expect(attempts).toBe(1))
    expect(save).toHaveClass('arco-btn-loading')
    expect(childCheckbox).toBeDisabled()

    await act(async () => firstSave.reject(new Error('save failed')))
    await waitFor(() => expect(save).toBeEnabled())
    expect(screen.getByText('分配权限 · 质量角色')).toBeVisible()
    expect((screen.getByText('查看项目').closest('.arco-tree-node') as HTMLElement).querySelector('input')).toBeChecked()

    await user.click(save)
    await waitFor(() => expect(attempts).toBe(2))
    await waitFor(() => expect(screen.queryByText('分配权限 · 质量角色')).not.toBeInTheDocument())
  })

  it('empty permission catalog disables saving but an intentionally cleared role remains saveable', async () => {
    const role = { id: 7, name: '测试角色', isBuiltIn: false, status: 'ACTIVE', permissionIds: [1], assignedUserCount: 0, canManage: true }
    for (const catalogAvailable of [false, true]) {
      const writes: Array<{ permissionIds: number[] }> = []
      mocks.get.mockImplementation(async (url: string) => ({
        data: url === '/permissions'
          ? catalogAvailable ? [{ id: 1, code: 'project', name: '项目协作', type: 'MENU', parentId: null, grantable: true }] : []
          : { list: [role], total: 1, page: 1, pageSize: 20 },
      }))
      mocks.put.mockImplementation(async (_url: string, body: { permissionIds: number[] }) => { writes.push(body); return { data: {} } })
      const user = userEvent.setup()
      const view = render(<RoleList />)
      await user.click(await screen.findByRole('button', { name: '分配权限' }))
      const save = screen.getByRole('button', { name: '保存权限' })
      if (!catalogAvailable) {
        expect(await screen.findByText('暂无权限点')).toBeVisible()
        expect(save).toBeDisabled()
        fireEvent.click(save)
      } else {
        const rootNode = screen.getByText('项目协作').closest('.arco-tree-node') as HTMLElement
        await user.click(rootNode.querySelector<HTMLInputElement>('input[type="checkbox"]') as HTMLInputElement)
        await user.click(save)
        await waitFor(() => expect(writes).toEqual([{ permissionIds: [] }]))
      }
      expect(writes).toHaveLength(catalogAvailable ? 1 : 0)
      view.unmount()
      mocks.get.mockClear()
    }
  })

  it('subproject status failure resolves the action handler and re-enables the same row for retry', async () => {
    const project = {
      id: 101, projectGroupId: 3, projectGroupName: '主项目', name: '失败重试子项目', supplierId: 8, supplierName: '供应商', status: 'DRAFT',
      createdByName: '创建人', updatedAt: '',
    }
    const group = { id: 3, name: '主项目', status: 'IN_PROGRESS', subprojectCount: 1, completedCount: 0, pendingCount: 0, terminatedCount: 0 }
    const firstWrite = deferred<{ data: unknown }>()
    let projectGets = 0
    let attempts = 0
    mocks.permissions = ['project:list', 'project:status']
    mocks.get.mockImplementation(async (url: string) => {
      if (url === '/project-groups/3') { projectGets += 1; return { data: { group, projects: [project] } } }
      if (url === '/projects/101') return { data: { ...project, workOrderNos: [] } }
      if (url === '/projects/101/summary') return { data: { unreadMessages: 0 } }
      throw new Error(`unexpected GET ${url}`)
    })
    mocks.put.mockImplementation(async (url: string) => {
      expect(url).toBe('/projects/101/status')
      attempts += 1
      return attempts === 1 ? firstWrite.promise : { data: {} }
    })
    const user = userEvent.setup()
    render(projectView())

    const start = await screen.findByRole('button', { name: '开始' })
    await user.click(start)
    await waitFor(() => expect(start).toHaveClass('arco-btn-loading'))
    await user.click(start)
    expect(attempts).toBe(1)
    await act(async () => firstWrite.reject(new Error('status failed')))
    await waitFor(() => expect(start).not.toHaveClass('arco-btn-loading'))
    expect(projectGets).toBe(1)
    expect(screen.getByRole('region', { name: '失败重试子项目' })).toBeVisible()

    await user.click(start)
    await waitFor(() => expect(attempts).toBe(2))
    await waitFor(() => expect(projectGets).toBeGreaterThan(1))
  })

  it('supplier account status failure resolves the button handler and preserves the account for retry', async () => {
    const supplier = { id: 8, name: '供应商', status: 'ACTIVE', createdAt: '' }
    const account = { id: 12, employeeNo: 's12', realName: '供应商账号', email: 's12@example.invalid', roleName: '供应商角色', status: 'ACTIVE', createdAt: '' }
    const firstWrite = deferred<{ data: unknown }>()
    let accountGets = 0
    let attempts = 0
    mocks.permissions = ['supplier:account']
    mocks.get.mockImplementation(async (url: string) => {
      if (url === '/admin/suppliers') return { data: { list: [supplier], total: 1, page: 1, pageSize: 10 } }
      if (url === '/admin/suppliers/8/accounts') { accountGets += 1; return { data: [account] } }
      throw new Error(`unexpected GET ${url}`)
    })
    mocks.put.mockImplementation(async (url: string) => {
      expect(url).toBe('/admin/supplier-accounts/12/status')
      attempts += 1
      return attempts === 1 ? firstWrite.promise : { data: {} }
    })
    const user = userEvent.setup()
    render(<SupplierList />)
    await user.click(await screen.findByRole('button', { name: '账号管理' }))
    expect(await screen.findByText('供应商账号')).toBeVisible()

    const disable = screen.getByRole('button', { name: '禁用' })
    await user.click(disable)
    expect(disable).toBeDisabled()
    await act(async () => firstWrite.reject(new Error('account status failed')))
    await waitFor(() => expect(disable).toBeEnabled())
    expect(accountGets).toBe(1)
    expect(screen.getByText('供应商账号')).toBeVisible()

    await user.click(disable)
    await waitFor(() => expect(attempts).toBe(2))
    await waitFor(() => expect(accountGets).toBe(2))
  })
})
