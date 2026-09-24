import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { deferred } from './testUtils'

const mocks = vi.hoisted(() => ({
  get: vi.fn(), post: vi.fn(), put: vi.fn(), delete: vi.fn(),
  auth: {
    user: { id: 1, employeeNo: 'admin1', realName: '管理员', userType: 'INTERNAL' },
    permissions: ['project:create', 'project:update'] as string[],
  },
}))

vi.mock('../../../api/client', () => ({ default: mocks }))
vi.mock('../../../store/auth', () => ({
  useAuth: () => ({
    user: mocks.auth.user,
    hasPerm: (permission: string) => mocks.auth.permissions.includes(permission),
  }),
}))
vi.mock('../../../store/collaboration', () => ({
  useCollaboration: (selector: (state: { revision: string; status: string }) => unknown) => selector({ revision: '1', status: 'ready' }),
}))

import ProjectList from '../../../pages/project/ProjectList'

const suppliers = [{ id: 8, name: '厂商甲' }, { id: 9, name: '厂商乙' }]
const priorities = [{ id: 14, type: 'PRIORITY', name: '普通', enabled: true, sortNo: 10, parentId: null, parentName: null, inUse: false }]

function page(list: unknown[] = []) {
  return { list, total: list.length, page: 1, pageSize: 10 }
}

function renderList() {
  return render(<MemoryRouter><ProjectList /></MemoryRouter>)
}

async function selectById(id: string, name: string) {
  const control = document.getElementById(id)!
  fireEvent.click(control)
  const options = await screen.findAllByRole('option', { name })
  fireEvent.click(options.at(-1)!)
}

async function enterCreatedOption(id: string, value: string) {
  const control = document.getElementById(id)!
  fireEvent.click(control)
  const input = control.querySelector('input')!
  fireEvent.change(input, { target: { value } })
  fireEvent.keyDown(input, { key: 'Enter', code: 'Enter' })
}

function successfulGet(url: string, config?: { params?: { supplierId?: number } }) {
  if (url === '/project-groups') return Promise.resolve({ data: page() })
  if (url === '/supplier-options') return Promise.resolve({ data: suppliers })
  if (url === '/project-dictionaries') return Promise.resolve({ data: priorities })
  if (url === '/robot-parts') {
    const supplierId = config?.params?.supplierId
    return Promise.resolve({ data: [{
      id: supplierId === 8 ? 101 : 202,
      supplierId,
      supplierName: supplierId === 8 ? '厂商甲' : '厂商乙',
      partNumber: supplierId === 8 ? 'A-101' : 'B-202',
      model: supplierId === 8 ? '甲厂商型号' : '乙厂商完整长型号',
      enabled: true,
      inUse: false,
      sortNo: 1,
    }] })
  }
  throw new Error(`unexpected GET ${url}`)
}

describe('ProjectList migrated behavior', () => {
  beforeEach(() => {
    mocks.auth.permissions = ['project:create', 'project:update']
    mocks.get.mockImplementation(successfulGet)
    mocks.post.mockResolvedValue({ data: {} })
    mocks.put.mockResolvedValue({ data: {} })
    mocks.delete.mockResolvedValue({ data: {} })
  })

  it('robot part selection ignores late vendors, rejects stale parts, and submits only the new contract', async () => {
    const pending = new Map<number, ReturnType<typeof deferred<{ data: unknown[] }>>>()
    mocks.get.mockImplementation((url: string, config?: { params?: { supplierId?: number } }) => {
      if (url !== '/robot-parts') return successfulGet(url, config)
      const request = deferred<{ data: unknown[] }>()
      pending.set(config!.params!.supplierId!, request)
      return request.promise
    })
    const user = userEvent.setup()
    renderList()
    await user.click(await screen.findByRole('button', { name: '新建主项目' }))
    const dialog = await screen.findByRole('dialog')

    await user.type(within(dialog).getByPlaceholderText('主项目名称'), '新项目')
    await enterCreatedOption('subprojectNames_input', '子项目-A')
    await enterCreatedOption('workOrderNos_input', 'WO-1')
    await user.type(within(dialog).getByPlaceholderText('请输入机型'), 'M1')

    await selectById('supplierId_input', '厂商甲')
    await selectById('supplierId_input', '厂商乙')
    pending.get(9)!.resolve({ data: [{ id: 202, supplierId: 9, supplierName: '厂商乙', partNumber: 'B-202', model: '乙厂商完整长型号', enabled: true, inUse: false, sortNo: 1 }] })
    await waitFor(() => expect(document.getElementById('robotPartId_input')).not.toHaveAttribute('aria-disabled', 'true'))
    await selectById('robotPartId_input', 'B-202')
    expect(within(dialog).getByPlaceholderText('所选料号未设置型号')).toHaveValue('乙厂商完整长型号')

    pending.get(8)!.resolve({ data: [{ id: 101, supplierId: 8, supplierName: '厂商甲', partNumber: 'A-101', model: '旧响应型号', enabled: true, inUse: false, sortNo: 1 }] })
    fireEvent.click(document.getElementById('robotPartId_input')!)
    // Wait until the dropdown is really open: before that, "A-101 absent" is trivially true, and an
    // Escape that reaches the modal instead of the dropdown closes the whole create dialog under load.
    expect(await screen.findByRole('option', { name: 'B-202' })).toBeVisible()
    expect(screen.queryByRole('option', { name: 'A-101' })).not.toBeInTheDocument()
    fireEvent.keyDown(document.getElementById('robotPartId_input')!, { key: 'Escape', code: 'Escape' })
    await waitFor(() => expect(screen.queryByRole('option', { name: 'B-202' })).not.toBeInTheDocument())
    expect(screen.getByRole('dialog')).toBeInTheDocument()

    await selectById('priorityId_input', '普通')
    await user.click(within(dialog).getByPlaceholderText('选择需求完成时间'))
    fireEvent.click(await screen.findByText('30', { selector: '.arco-picker-cell-in-view .arco-picker-date-value' }))
    await user.click(within(dialog).getByRole('button', { name: '创建主项目' }))

    await waitFor(() => expect(mocks.post).toHaveBeenCalledTimes(1))
    expect(mocks.post).toHaveBeenCalledWith('/project-groups', {
      name: '新项目',
      description: undefined,
      supplierId: 9,
      workOrderNos: ['WO-1'],
      machineModel: 'M1',
      robotPartId: 202,
      priorityId: 14,
      expectedCompletionDate: '2026-09-30',
      subprojectNames: ['子项目-A'],
    })
    expect(mocks.post.mock.calls[0][1]).not.toHaveProperty('robotVendorId')
    expect(mocks.post.mock.calls[0][1]).not.toHaveProperty('responsibleUserId')
  })

  it('historical main projects preserve their owner and legacy model without selecting a catalog part', async () => {
    const historical = {
      id: 3, name: '历史项目', description: null, supplierId: 8, supplierName: '历史厂商', status: 'DRAFT',
      workOrderNos: ['WO-OLD'], machineModel: 'M1', robotPartId: null, robotPartNumber: null, robotModelName: '历史型号',
      responsibleUserId: 5, responsibleUserEmployeeNo: '0004', responsibleUserName: '旧负责人', sectionId: null, sectionName: null,
      priorityId: 14, priorityName: '普通', expectedCompletionDate: '2026-09-30', subprojectCount: 0, completedCount: 0,
      pendingCount: 0, terminatedCount: 0, unreadMessages: 0,
    }
    mocks.get.mockImplementation((url: string) => {
      if (url === '/project-groups') return Promise.resolve({ data: page([historical]) })
      if (url === '/supplier-options') return Promise.resolve({ data: [{ id: 8, name: '历史厂商' }] })
      if (url === '/project-dictionaries') return Promise.resolve({ data: priorities })
      if (url === '/robot-parts') return Promise.reject(new Error('catalog temporarily unavailable'))
      throw new Error(`unexpected GET ${url}`)
    })
    const user = userEvent.setup()
    renderList()
    const row = await screen.findByRole('row', { name: /历史项目/ })
    await user.click(within(row).getByRole('button', { name: '编辑' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByDisplayValue('旧负责人（0004）')).toHaveAttribute('readonly')
    expect(within(dialog).getByDisplayValue('历史型号')).toHaveAttribute('readonly')
    await waitFor(() => expect(within(dialog).getByRole('button', { name: '保存并同步' })).toBeEnabled())
    await user.click(within(dialog).getByRole('button', { name: '保存并同步' }))
    await waitFor(() => expect(mocks.put).toHaveBeenCalledTimes(1))
    expect(mocks.put.mock.calls[0][1]).toEqual(expect.objectContaining({ robotPartId: null, supplierId: 8 }))
    expect(mocks.put.mock.calls[0][1]).not.toHaveProperty('responsibleUserId')
  })

  it('main projects with pending child acceptance expose a disabled edit action and reason', async () => {
    const pendingGroup = {
      id: 3, name: '待验收主项目', supplierId: 8, status: 'IN_PROGRESS', workOrderNos: [], unreadMessages: 0,
      subprojectCount: 2, completedCount: 0, pendingCount: 1, terminatedCount: 0,
    }
    mocks.get.mockImplementation((url: string) => {
      if (url === '/project-groups') return Promise.resolve({ data: page([pendingGroup]) })
      if (url === '/supplier-options') return Promise.resolve({ data: suppliers })
      return Promise.resolve({ data: [] })
    })
    renderList()
    const row = await screen.findByRole('row', { name: /待验收主项目/ })
    expect(within(row).getByRole('button', { name: '编辑' })).toBeDisabled()
    fireEvent.mouseEnter(within(row).getByRole('button', { name: '编辑' }).parentElement!)
    expect(await screen.findByText(/有 1 个子项目待公司验收/)).toBeInTheDocument()
  })

  it('project creation explains missing prerequisites and refreshes without discarding entered data', async () => {
    let ready = false
    mocks.get.mockImplementation((url: string, config?: { params?: { supplierId?: number } }) => {
      if (url === '/project-groups') return Promise.resolve({ data: page() })
      if (url === '/supplier-options') return Promise.resolve({ data: ready ? suppliers : [] })
      if (url === '/project-dictionaries') return Promise.resolve({ data: ready ? priorities : [] })
      if (url === '/robot-parts') return Promise.resolve({ data: [] })
      return successfulGet(url, config)
    })
    const user = userEvent.setup()
    renderList()
    await user.click(await screen.findByRole('button', { name: '新建主项目' }))
    const dialog = await screen.findByRole('dialog')
    const name = within(dialog).getByPlaceholderText('主项目名称')
    await user.type(name, '已填写项目')
    expect(await within(dialog).findByText(/暂无启用的供应商，请先新增或启用供应商。/)).toBeInTheDocument()
    expect(within(dialog).getByText(/暂无启用的优先级，请在数据字典中维护。/)).toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: '创建主项目' })).toBeDisabled()

    ready = true
    await user.click(within(dialog).getByRole('button', { name: '刷新基础数据' }))
    await waitFor(() => expect(within(dialog).queryByText(/暂无启用的供应商，请先新增或启用供应商。/)).not.toBeInTheDocument())
    expect(name).toHaveValue('已填写项目')
  })

  it('required option sources expose loading and retry states and block submits until ready', async () => {
    const firstSupplier = deferred<{ data: unknown[] }>()
    let supplierAttempts = 0
    mocks.get.mockImplementation((url: string) => {
      if (url === '/project-groups') return Promise.resolve({ data: page() })
      if (url === '/supplier-options') {
        supplierAttempts += 1
        return supplierAttempts === 1 ? firstSupplier.promise : Promise.resolve({ data: suppliers })
      }
      if (url === '/project-dictionaries') return Promise.resolve({ data: priorities })
      if (url === '/robot-parts') return Promise.resolve({ data: [] })
      throw new Error(`unexpected GET ${url}`)
    })
    const user = userEvent.setup()
    renderList()
    const create = await screen.findByRole('button', { name: '新建主项目' })
    expect(create).toBeEnabled()
    await user.click(create)
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByText('正在加载创建所需的基础数据…')).toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: '创建主项目' })).toBeDisabled()
    firstSupplier.reject(new Error('offline'))
    expect(await within(dialog).findByText('Robot 厂商选项加载失败，请刷新基础数据后重试。')).toBeInTheDocument()
    await user.click(within(dialog).getByRole('button', { name: '刷新基础数据' }))
    await waitFor(() => expect(within(dialog).queryByText('Robot 厂商选项加载失败，请刷新基础数据后重试。')).not.toBeInTheDocument())
    expect(supplierAttempts).toBe(2)
  })

  it('project pages contain no round entry points or project round selectors', async () => {
    const user = userEvent.setup()
    renderList()
    await user.click(await screen.findByRole('button', { name: '新建主项目' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).queryByText(/轮次/)).not.toBeInTheDocument()
    expect(within(dialog).queryByRole('combobox', { name: /轮次/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /轮次/ })).not.toBeInTheDocument()
  })
})
