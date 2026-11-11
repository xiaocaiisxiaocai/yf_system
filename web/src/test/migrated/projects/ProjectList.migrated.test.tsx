import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useEffect } from 'react'
import { MemoryRouter, useLocation } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { deferred } from './testUtils'

const mocks = vi.hoisted(() => ({
  get: vi.fn(), post: vi.fn(), put: vi.fn(), delete: vi.fn(),
  auth: {
    user: { id: 1, employeeNo: 'admin1', realName: '管理员', userType: 'INTERNAL' },
    permissions: ['project:create', 'project:update'] as string[],
  },
  collaboration: { revision: '1', status: 'ready' },
}))

vi.mock('../../../api/client', () => ({ default: mocks }))
vi.mock('../../../store/auth', () => ({
  useAuth: () => ({
    user: mocks.auth.user,
    hasPerm: (permission: string) => mocks.auth.permissions.includes(permission),
  }),
}))
vi.mock('../../../store/collaboration', () => ({
  useCollaboration: (selector: (state: { revision: string; status: string }) => unknown) => selector(mocks.collaboration),
}))

import ProjectList from '../../../pages/project/ProjectList'

const suppliers = [{ id: 8, name: '厂商甲' }, { id: 9, name: '厂商乙' }]
const priorities = [{ id: 14, type: 'PRIORITY', name: '普通', enabled: true, sortNo: 10, parentId: null, parentName: null, inUse: false }]
const robotTypes = [{ id: 21, type: 'ROBOT_TYPE', name: '六轴', enabled: true, sortNo: 10, parentId: null, parentName: null, inUse: false }]
type GetConfig = { params?: { supplierId?: number; type?: string } }
const contactsBySupplier: Record<number, unknown[]> = {
  8: [{ id: 31, name: '张三', enabled: true }, { id: 32, name: '李四', enabled: true }],
  9: [{ id: 41, name: '张三', enabled: true }, { id: 42, name: '王五', enabled: false }],
}

function dictionaryOptions(config?: GetConfig, ready = true) {
  if (!ready) return []
  return config?.params?.type === 'ROBOT_TYPE' ? robotTypes : priorities
}

function page(list: unknown[] = []) {
  return { list, total: list.length, page: 1, pageSize: 10 }
}

function renderList() {
  return render(<MemoryRouter><ProjectList /></MemoryRouter>)
}

const locationProbe = { search: '' }
const pathProbe = { value: '' }
function PathProbe() {
  const { pathname, search } = useLocation()
  useEffect(() => { pathProbe.value = `${pathname}${search}` }, [pathname, search])
  return null
}
function LocationProbe() {
  const { search } = useLocation()
  useEffect(() => { locationProbe.search = search }, [search])
  return null
}

async function selectById(id: string, name: string) {
  const control = document.getElementById(id)!
  fireEvent.click(control)
  const options = await screen.findAllByRole('option', { name })
  fireEvent.click(options.at(-1)!)
}

/** 多选的 Robot 负责人：多选模式下 role=option 是外层包装，点击内部的选项元素才会选中。 */
async function selectOwner(name: string) {
  fireEvent.click(document.getElementById('robotOwnerContactIds_input')!)
  const option = (await screen.findAllByRole('option', { name })).at(-1)!
  fireEvent.click(option.querySelector('.arco-select-option') ?? option)
  await waitFor(() => expect(document.querySelector('#robotOwnerContactIds_input .arco-input-tag-tag')).toHaveTextContent(name))
}

async function enterCreatedOption(id: string, value: string) {
  const control = document.getElementById(id)!
  fireEvent.click(control)
  const input = control.querySelector('input')!
  fireEvent.change(input, { target: { value } })
  fireEvent.keyDown(input, { key: 'Enter', code: 'Enter' })
}

function successfulGet(url: string, config?: GetConfig) {
  if (url === '/projects') return Promise.resolve({ data: page() })
  if (url === '/supplier-options') return Promise.resolve({ data: suppliers })
  if (url === '/project-dictionaries') return Promise.resolve({ data: dictionaryOptions(config) })
  if (url === '/supplier-contacts') return Promise.resolve({ data: contactsBySupplier[config?.params?.supplierId ?? 0] ?? [] })
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
    mocks.collaboration.revision = '1'
    mocks.collaboration.status = 'ready'
    mocks.auth.permissions = ['project:create', 'project:update']
    mocks.auth.user = { id: 1, employeeNo: 'admin1', realName: '管理员', userType: 'INTERNAL' }
    mocks.get.mockImplementation(successfulGet)
    mocks.post.mockResolvedValue({ data: {} })
    mocks.put.mockResolvedValue({ data: {} })
    mocks.delete.mockResolvedValue({ data: {} })
  })

  it('offers deletion of terminated projects the API marks deletable, without a global delete grant', async () => {
    mocks.get.mockImplementation((url: string, config?: GetConfig) => {
      if (url === '/projects') return Promise.resolve({ data: page([{
        id: 77, name: '主管辖区已终止项目', supplierId: 8, supplierName: '厂商甲', status: 'TERMINATED',
        workOrderNos: [], unreadMessages: 0, canManage: false, canDelete: true,
      }, {
        id: 78, name: '他人进行中项目', supplierId: 8, supplierName: '厂商甲', status: 'IN_PROGRESS',
        workOrderNos: [], unreadMessages: 0, canManage: false, canDelete: false,
      }]) })
      return successfulGet(url, config)
    })
    const user = userEvent.setup()
    renderList()

    expect(await screen.findByText('主管辖区已终止项目')).toBeInTheDocument()
    const terminatedRow = screen.getByText('主管辖区已终止项目').closest('tr')!
    const otherRow = screen.getByText('他人进行中项目').closest('tr')!
    // Someone else's project is read-only: no edit, no delete.
    expect(within(otherRow).queryByRole('button', { name: '删除' })).not.toBeInTheDocument()
    expect(within(otherRow).queryByRole('button', { name: '编辑' })).not.toBeInTheDocument()
    await user.click(within(terminatedRow).getByRole('button', { name: '删除' }))
    expect(await screen.findByText(/确认删除项目“主管辖区已终止项目”/)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: '确定' }))
    await waitFor(() => expect(mocks.delete).toHaveBeenCalledWith('/projects/77'))
    expect(mocks.auth.permissions).not.toContain('project:delete')
  })

  it('robot part selection ignores late vendors, rejects stale parts, creates and opens the started project', async () => {
    // The date picker opens the current month; keep the selected September date deterministic.
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(new Date(2026, 8, 15, 12))
    const pending = new Map<number, ReturnType<typeof deferred<{ data: unknown[] }>>>()
    mocks.get.mockImplementation((url: string, config?: { params?: { supplierId?: number } }) => {
      if (url !== '/robot-parts') return successfulGet(url, config)
      const request = deferred<{ data: unknown[] }>()
      pending.set(config!.params!.supplierId!, request)
      return request.promise
    })
    mocks.post.mockResolvedValue({ data: { id: 99, status: 'IN_PROGRESS' } })
    const user = userEvent.setup()
    render(<MemoryRouter><ProjectList /><PathProbe /></MemoryRouter>)
    await user.click(await screen.findByRole('button', { name: '新建项目' }))
    const dialog = await screen.findByRole('dialog')

    await user.type(within(dialog).getByPlaceholderText('项目名称'), '新项目')
    await enterCreatedOption('workOrderNos_input', 'WO-1')
    await user.type(within(dialog).getByPlaceholderText('请输入机型'), 'M1')

    await selectById('supplierId_input', '厂商甲')
    await selectById('supplierId_input', '厂商乙')
    pending.get(9)!.resolve({ data: [{ id: 202, supplierId: 9, supplierName: '厂商乙', partNumber: 'B-202', model: '乙厂商完整长型号', enabled: true, inUse: false, sortNo: 1 }] })
    await waitFor(() => expect(document.getElementById('robotPartId_input')).not.toHaveAttribute('aria-disabled', 'true'))
    await selectById('robotPartId_input', 'B-202')
    expect(within(dialog).getByPlaceholderText('所选料号未设置型号')).toHaveValue('乙厂商完整长型号')
    // Robot 负责人只能从所选厂商的启用联络人中选择。
    await waitFor(() => expect(document.getElementById('robotOwnerContactIds_input')).not.toHaveAttribute('aria-disabled', 'true'))
    fireEvent.click(document.getElementById('robotOwnerContactIds_input')!)
    expect(await screen.findByRole('option', { name: '张三' })).toBeVisible()
    expect(screen.queryByRole('option', { name: /王五/ })).not.toBeInTheDocument()
    await selectOwner('张三')

    pending.get(8)!.resolve({ data: [{ id: 101, supplierId: 8, supplierName: '厂商甲', partNumber: 'A-101', model: '旧响应型号', enabled: true, inUse: false, sortNo: 1 }] })
    fireEvent.click(document.getElementById('robotPartId_input')!)
    const selectedPartOption = await screen.findByRole('option', { name: 'B-202' })
    expect(selectedPartOption).toBeVisible()
    expect(screen.queryByRole('option', { name: 'A-101' })).not.toBeInTheDocument()
    fireEvent.click(selectedPartOption)
    await waitFor(() => expect(screen.queryByRole('option', { name: 'B-202' })).not.toBeInTheDocument())
    expect(dialog.isConnected).toBe(true)
    expect(dialog).toBeVisible()

    await selectById('robotTypeId_input', '六轴')
    await selectById('priorityId_input', '普通')
    const completionDate = within(dialog).getByPlaceholderText('选择需求完成时间')
    await user.click(completionDate)
    fireEvent.click(await screen.findByText('30', { selector: '.arco-picker-cell-in-view .arco-picker-date-value' }))
    await waitFor(() => expect(completionDate).toHaveValue('2026-09-30'))
    await waitFor(() => expect(document.querySelector('.arco-picker-container')).not.toBeInTheDocument())
    const createButton = within(dialog).getByRole('button', { name: '创建并开始' })
    await waitFor(() => expect(createButton).toBeEnabled())
    await user.click(createButton)

    await waitFor(() => expect(mocks.post).toHaveBeenCalledTimes(1))
    expect(mocks.post).toHaveBeenCalledWith('/projects', {
      name: '新项目',
      description: undefined,
      supplierId: 9,
      workOrderNos: ['WO-1'],
      machineModel: 'M1',
      robotPartId: 202,
      priorityId: 14,
      robotTypeId: 21,
      robotOwnerContactIds: [41],
      expectedCompletionDate: '2026-09-30',
    })
    expect(mocks.post.mock.calls[0][1]).not.toHaveProperty('robotVendorId')
    expect(mocks.post.mock.calls[0][1]).not.toHaveProperty('responsibleUserId')
    expect(mocks.post.mock.calls[0][1]).not.toHaveProperty('subprojectNames')
    // Creating starts the project, so the user lands on its files to upload material.
    await waitFor(() => expect(pathProbe.value).toBe('/projects/99?tab=files'))
  })

  it('historical main projects preserve their owner and legacy model without selecting a catalog part', async () => {
    const historical = {
      id: 3, name: '历史项目', description: null, supplierId: 8, supplierName: '历史厂商', status: 'IN_PROGRESS', canManage: true,
      workOrderNos: ['WO-OLD'], machineModel: 'M1', robotPartId: null, robotPartNumber: null, robotModelName: '历史型号',
      responsibleUserId: 5, responsibleUserEmployeeNo: '0004', responsibleUserName: '旧负责人', sectionId: null, sectionName: null,
      priorityId: 14, priorityName: '普通', robotTypeId: null, robotTypeName: null, expectedCompletionDate: '2026-09-30',
      updatedAt: '2026-09-01T00:00:00.000000Z', unreadMessages: 0,
    }
    mocks.get.mockImplementation((url: string, config?: GetConfig) => {
      if (url === '/projects') return Promise.resolve({ data: page([historical]) })
      if (url === '/supplier-options') return Promise.resolve({ data: [{ id: 8, name: '历史厂商' }] })
      if (url === '/project-dictionaries') return Promise.resolve({ data: dictionaryOptions(config) })
      if (url === '/robot-parts') return Promise.reject(new Error('catalog temporarily unavailable'))
      if (url === '/supplier-contacts') return Promise.resolve({ data: contactsBySupplier[8] })
      throw new Error(`unexpected GET ${url}`)
    })
    const user = userEvent.setup()
    renderList()
    const row = await screen.findByRole('row', { name: /历史项目/ })
    await user.click(within(row).getByRole('button', { name: '编辑' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByDisplayValue('旧负责人（0004）')).toHaveAttribute('readonly')
    expect(within(dialog).getByDisplayValue('历史型号')).toHaveAttribute('readonly')
    await waitFor(() => expect(within(dialog).getByRole('button', { name: '保存' })).toBeEnabled())
    // Projects created before Robot 类型 existed have none; it is required, so saving asks for it first.
    await user.click(within(dialog).getByRole('button', { name: '保存' }))
    expect(await within(dialog).findByText('请选择 Robot 类型')).toBeInTheDocument()
    expect(within(dialog).getByText('请选择 Robot 负责人')).toBeInTheDocument()
    expect(mocks.put).not.toHaveBeenCalled()
    await selectById('robotTypeId_input', '六轴')
    await selectOwner('李四')
    await user.click(within(dialog).getByRole('button', { name: '保存' }))
    await waitFor(() => expect(mocks.put).toHaveBeenCalledTimes(1))
    expect(mocks.put.mock.calls[0][1]).toEqual(expect.objectContaining({
      robotPartId: null, supplierId: 8, robotTypeId: 21, robotOwnerContactIds: [32], expectedUpdatedAt: '2026-09-01T00:00:00.000000Z',
    }))
    expect(mocks.put.mock.calls[0][1]).not.toHaveProperty('responsibleUserId')
    expect(await screen.findByText('项目资料已保存')).toBeInTheDocument()
  })

  function editableGroup(updatedAt: string, name = '并发项目') {
    return {
      id: 3, name, description: null, supplierId: 8, supplierName: '厂商甲', status: 'IN_PROGRESS', canManage: true,
      workOrderNos: ['WO-1'], machineModel: 'M1', robotPartId: 101, robotPartNumber: 'A-101', robotModelName: '甲厂商型号',
      responsibleUserId: 5, responsibleUserEmployeeNo: '0004', responsibleUserName: '旧负责人', sectionId: null, sectionName: null,
      priorityId: 14, priorityName: '普通', robotTypeId: 21, robotTypeName: '六轴', robotOwnerName: '张三',
      robotOwnerContactIds: [31], expectedCompletionDate: '2026-09-30', updatedAt, unreadMessages: 0,
    }
  }

  async function editGroupName(rows: () => unknown[]) {
    mocks.get.mockImplementation((url: string, config?: GetConfig) => {
      if (url === '/projects') return Promise.resolve({ data: page(rows()) })
      if (url === '/projects/3') {
        const project = rows()[0]
        return project ? Promise.resolve({ data: project }) : Promise.reject({ isAxiosError: true, response: { status: 404, data: { code: 40401 } } })
      }
      return successfulGet(url, config)
    })
    const user = userEvent.setup()
    renderList()
    const row = await screen.findByRole('row', { name: /并发项目/ })
    await user.click(within(row).getByRole('button', { name: '编辑' }))
    const dialog = await screen.findByRole('dialog')
    const name = within(dialog).getByPlaceholderText('项目名称')
    await user.clear(name)
    await user.type(name, '我的名称')
    const save = within(dialog).getByRole('button', { name: '保存' })
    await waitFor(() => expect(save).toBeEnabled())
    await user.click(save).catch(() => undefined)
    return { user, dialog }
  }

  it('a 40903 keeps the project dialog and adopts the refreshed updatedAt', async () => {
    const t1 = '2026-10-01T00:00:00.000000Z'
    const t2 = '2026-10-01T00:01:00.000000Z'
    let current = editableGroup(t1)
    mocks.put.mockImplementationOnce(() => {
      current = editableGroup(t2, '他人改名')
      return Promise.reject({ isAxiosError: true, response: { status: 409, data: { code: 40903 } } })
    })
    const { user, dialog } = await editGroupName(() => [current])
    await waitFor(() => expect(mocks.put).toHaveBeenCalledTimes(1))
    expect(mocks.put.mock.calls[0][1]).toEqual(expect.objectContaining({ name: '我的名称', expectedUpdatedAt: t1 }))
    expect(await within(dialog).findByText(/项目已被他人修改，已刷新为最新数据（当前名称：他人改名）/)).toBeInTheDocument()
    expect(within(dialog).getByPlaceholderText('项目名称')).toHaveValue('我的名称')
    mocks.put.mockResolvedValueOnce({ data: {} })
    await user.click(within(dialog).getByRole('button', { name: '保存' }))
    await waitFor(() => expect(mocks.put).toHaveBeenCalledTimes(2))
    expect(mocks.put.mock.calls[1][1]).toEqual(expect.objectContaining({ name: '我的名称', expectedUpdatedAt: t2 }))
  })

  it('a 40901 does not treat a project save as a version conflict', async () => {
    const t1 = '2026-10-01T00:00:00.000000Z'
    const current = editableGroup(t1)
    mocks.put.mockRejectedValue({ isAxiosError: true, response: { status: 409, data: { code: 40901, message: '名称已存在' } } })
    const { user, dialog } = await editGroupName(() => [current])
    await waitFor(() => expect(mocks.put).toHaveBeenCalledTimes(1))
    expect(within(dialog).queryByText(/已被他人修改/)).not.toBeInTheDocument()
    expect(within(dialog).getByPlaceholderText('项目名称')).toHaveValue('我的名称')
    await user.click(within(dialog).getByRole('button', { name: '保存' }))
    await waitFor(() => expect(mocks.put).toHaveBeenCalledTimes(2))
    expect(mocks.put.mock.calls[1][1]).toEqual(expect.objectContaining({ expectedUpdatedAt: t1 }))
  })

  it('closes the project dialog when the 40903 refresh finds the project gone', async () => {
    const t1 = '2026-10-01T00:00:00.000000Z'
    let rows = [editableGroup(t1)]
    mocks.put.mockImplementationOnce(() => {
      rows = []
      return Promise.reject({ isAxiosError: true, response: { status: 409, data: { code: 40903 } } })
    })
    await editGroupName(() => rows)
    expect(await screen.findByText('项目已不可编辑或已被删除，请刷新后查看')).toBeInTheDocument()
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  })

  it('own projects with material awaiting review stay editable and show the material state', async () => {
    const pendingGroup = {
      id: 3, name: '资料待确认项目', supplierId: 8, status: 'IN_PROGRESS', workOrderNos: [], unreadMessages: 0,
      canManage: true, materialReviewStatus: 'PENDING',
    }
    mocks.get.mockImplementation((url: string) => {
      if (url === '/projects') return Promise.resolve({ data: page([pendingGroup]) })
      if (url === '/supplier-options') return Promise.resolve({ data: suppliers })
      return Promise.resolve({ data: [] })
    })
    renderList()
    const row = await screen.findByRole('row', { name: /资料待确认项目/ })
    expect(within(row).getByRole('button', { name: '编辑' })).toBeEnabled()
    expect(within(row).getByText('资料待审核')).toBeInTheDocument()
  })

  it('filters by material state and overdue through the address bar and marks overdue main projects', async () => {
    const seen: Array<Record<string, unknown>> = []
    const overdueGroup = {
      id: 4, name: '逾期主项目', supplierId: 8, status: 'IN_PROGRESS', workOrderNos: [], unreadMessages: 0,
      expectedCompletionDate: '2000-01-01',
    }
    const doneGroup = { ...overdueGroup, id: 5, name: '已完成主项目', status: 'COMPLETED' }
    mocks.get.mockImplementation((url: string, config?: { params?: Record<string, unknown> }) => {
      if (url === '/projects') {
        seen.push(config?.params ?? {})
        return Promise.resolve({ data: page([overdueGroup, doneGroup]) })
      }
      if (url === '/supplier-options') return Promise.resolve({ data: suppliers })
      return Promise.resolve({ data: [] })
    })
    render(<MemoryRouter initialEntries={['/projects?material=RETURNED']}><ProjectList /><LocationProbe /></MemoryRouter>)
    const row = await screen.findByRole('row', { name: /逾期主项目/ })
    expect(within(row).getByText(/^逾期 \d+ 天$/)).toBeInTheDocument()
    expect(within(screen.getByRole('row', { name: /已完成主项目/ })).queryByText(/逾期/)).not.toBeInTheDocument()
    expect(seen.at(-1)).toMatchObject({ materialState: 'RETURNED' })
    expect(seen.at(-1)?.overdue).toBeUndefined()

    fireEvent.click(screen.getByRole('checkbox', { name: '仅看逾期' }))
    await waitFor(() => expect(seen.at(-1)).toMatchObject({ materialState: 'RETURNED', overdue: true }))
    expect(locationProbe.search).toContain('overdue=1')

    // Internal accounts see every project; "只看我负责的" narrows the list through the address bar too.
    fireEvent.click(screen.getByRole('checkbox', { name: '只看我负责的' }))
    await waitFor(() => expect(seen.at(-1)).toMatchObject({ materialState: 'RETURNED', overdue: true, mine: true }))
    expect(locationProbe.search).toContain('mine=1')
  })

  it('project creation explains missing prerequisites and refreshes without discarding entered data', async () => {
    let ready = false
    mocks.get.mockImplementation((url: string, config?: GetConfig) => {
      if (url === '/projects') return Promise.resolve({ data: page() })
      if (url === '/supplier-options') return Promise.resolve({ data: ready ? suppliers : [] })
      if (url === '/project-dictionaries') return Promise.resolve({ data: dictionaryOptions(config, ready) })
      if (url === '/robot-parts') return Promise.resolve({ data: [] })
      return successfulGet(url, config)
    })
    const user = userEvent.setup()
    renderList()
    await user.click(await screen.findByRole('button', { name: '新建项目' }))
    const dialog = await screen.findByRole('dialog')
    const name = within(dialog).getByPlaceholderText('项目名称')
    await user.type(name, '已填写项目')
    expect(await within(dialog).findByText(/暂无启用的供应商，请先新增或启用供应商。/)).toBeInTheDocument()
    expect(within(dialog).getByText(/暂无启用的优先级，请在数据字典中维护。/)).toBeInTheDocument()
    expect(within(dialog).getByText(/暂无启用的 Robot 类型，请在数据字典中维护。/)).toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: '创建并开始' })).toBeDisabled()

    ready = true
    await user.click(within(dialog).getByRole('button', { name: '刷新基础数据' }))
    await waitFor(() => expect(within(dialog).queryByText(/暂无启用的供应商，请先新增或启用供应商。/)).not.toBeInTheDocument())
    expect(name).toHaveValue('已填写项目')
  })

  it('required option sources expose loading and retry states and block submits until ready', async () => {
    const firstSupplier = deferred<{ data: unknown[] }>()
    let supplierAttempts = 0
    mocks.get.mockImplementation((url: string, config?: GetConfig) => {
      if (url === '/projects') return Promise.resolve({ data: page() })
      if (url === '/supplier-options') {
        supplierAttempts += 1
        // The list loads its own filter options first; the dialog's first request is the second one.
        return supplierAttempts === 2 ? firstSupplier.promise : Promise.resolve({ data: suppliers })
      }
      if (url === '/project-dictionaries') return Promise.resolve({ data: dictionaryOptions(config) })
      if (url === '/robot-parts') return Promise.resolve({ data: [] })
      throw new Error(`unexpected GET ${url}`)
    })
    const user = userEvent.setup()
    renderList()
    const create = await screen.findByRole('button', { name: '新建项目' })
    expect(create).toBeEnabled()
    await user.click(create)
    const dialog = await screen.findByRole('dialog')
    expect(await within(dialog).findByText('正在加载创建所需的基础数据…')).toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: '创建并开始' })).toBeDisabled()
    firstSupplier.reject(new Error('offline'))
    expect(await within(dialog).findByText('Robot 厂商选项加载失败，请刷新基础数据后重试。')).toBeInTheDocument()
    await user.click(within(dialog).getByRole('button', { name: '刷新基础数据' }))
    await waitFor(() => expect(within(dialog).queryByText('Robot 厂商选项加载失败，请刷新基础数据后重试。')).not.toBeInTheDocument())
    expect(supplierAttempts).toBe(3)
  })

  it('project pages contain no round entry points or project round selectors', async () => {
    const user = userEvent.setup()
    renderList()
    await user.click(await screen.findByRole('button', { name: '新建项目' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).queryByText(/轮次/)).not.toBeInTheDocument()
    expect(within(dialog).queryByRole('combobox', { name: /轮次/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /轮次/ })).not.toBeInTheDocument()
  })

  it('shows a full error before any list has loaded and keeps the last rows after a later failure', async () => {
    mocks.get.mockImplementation((url: string, config?: GetConfig) => {
      if (url === '/projects') return Promise.reject(new Error('down'))
      return successfulGet(url, config)
    })
    const failed = renderList()
    expect(await screen.findByText('加载失败')).toBeInTheDocument()
    expect(screen.queryByText(/当前显示上次获取的数据/)).not.toBeInTheDocument()
    failed.unmount()

    const historical = {
      id: 3, name: '历史项目', supplierId: 8, supplierName: '历史厂商', status: 'IN_PROGRESS',
      workOrderNos: ['WO-OLD'], unreadMessages: 0,
    }
    let failList = false
    mocks.get.mockImplementation((url: string, config?: GetConfig) => {
      if (url === '/projects') {
        if (failList) return Promise.reject(new Error('down'))
        return Promise.resolve({ data: page([historical]) })
      }
      return successfulGet(url, config)
    })
    const view = renderList()
    expect(await screen.findByText('历史项目')).toBeInTheDocument()
    const callsBeforeStatus = mocks.get.mock.calls.filter(([url]) => url === '/projects').length
    mocks.collaboration.status = 'error'
    view.rerender(<MemoryRouter><ProjectList /></MemoryRouter>)
    expect(mocks.get.mock.calls.filter(([url]) => url === '/projects')).toHaveLength(callsBeforeStatus)

    failList = true
    mocks.collaboration.revision = '2'
    view.rerender(<MemoryRouter><ProjectList /></MemoryRouter>)
    expect(await screen.findByText(/当前显示上次获取的数据/)).toBeInTheDocument()
    expect(screen.getByText('历史项目')).toBeInTheDocument()
    expect(screen.queryByText('加载失败')).not.toBeInTheDocument()

    failList = false
    fireEvent.click(screen.getByRole('button', { name: '重新获取' }))
    await waitFor(() => expect(screen.queryByText(/当前显示上次获取的数据/)).not.toBeInTheDocument())
    expect(screen.getByText('历史项目')).toBeInTheDocument()
  })

  it('restores filters and paging from the address and writes searches back to it', async () => {
    mocks.get.mockImplementation((url: string) => url === '/projects'
      ? Promise.resolve({ data: { list: [], total: 0, page: 2, pageSize: 20 } })
      : Promise.resolve({ data: url === '/supplier-options' ? suppliers : [] }))
    render(
      <MemoryRouter initialEntries={['/projects?q=旧关键字&status=IN_PROGRESS&supplier=8&page=2&pageSize=20']}>
        <ProjectList />
        <LocationProbe />
      </MemoryRouter>,
    )
    await waitFor(() => expect(mocks.get).toHaveBeenCalledWith('/projects', expect.objectContaining({
      params: { page: 2, pageSize: 20, keyword: '旧关键字', status: 'IN_PROGRESS', supplierId: 8 },
    })))
    const search = screen.getByPlaceholderText('项目名称 / 工令号 / 机型 / 料号')
    expect(search).toHaveValue('旧关键字')
    fireEvent.change(search, { target: { value: '新关键字' } })
    fireEvent.keyDown(search, { key: 'Enter', code: 'Enter', keyCode: 13 })
    await waitFor(() => expect(new URLSearchParams(locationProbe.search).get('q')).toBe('新关键字'))
    const params = new URLSearchParams(locationProbe.search)
    expect(params.get('page')).toBeNull()
    expect(params.get('pageSize')).toBe('20')
    expect(params.get('status')).toBe('IN_PROGRESS')
  })

  it('ignores invalid address parameters and the supplier filter for supplier accounts', async () => {
    mocks.auth.user = { ...mocks.auth.user, userType: 'SUPPLIER' }
    mocks.get.mockImplementation((url: string) => Promise.resolve({ data: url === '/projects' ? page() : [] }))
    render(
      <MemoryRouter initialEntries={['/projects?status=BOGUS&supplier=8&page=-1&pageSize=5000']}>
        <ProjectList />
      </MemoryRouter>,
    )
    await waitFor(() => expect(mocks.get).toHaveBeenCalledWith('/projects', expect.objectContaining({
      params: { page: 1, pageSize: 10, keyword: undefined, status: undefined, supplierId: undefined },
    })))
  })
})
