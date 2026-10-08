import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { deferred } from './testUtils'

const mocks = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  delete: vi.fn(),
}))

vi.mock('../../../api/client', () => ({
  default: mocks,
}))

import Dictionaries from '../../../pages/system/Dictionaries'

const suppliers = [{ id: 8, name: '珞石', status: 'ACTIVE' }]
const part = {
  id: 21,
  supplierId: 8,
  supplierName: '珞石',
  partNumber: 'NB80-9/2.2',
  model: '长规格型号示例',
  sortNo: 10,
  enabled: true,
  inUse: true,
}

function successfulGet(url: string) {
  if (url === '/robot-part-supplier-options') return Promise.resolve({ data: suppliers })
  if (url === '/robot-parts') return Promise.resolve({ data: [] })
  if (url === '/project-dictionaries') return Promise.resolve({ data: [] })
  throw new Error(`unexpected GET ${url}`)
}

describe('Dictionaries migrated behavior', () => {
  beforeEach(() => {
    mocks.get.mockImplementation(successfulGet)
    mocks.post.mockResolvedValue({ data: {} })
    mocks.put.mockResolvedValue({ data: {} })
    mocks.delete.mockResolvedValue({ data: {} })
  })

  it('priority save blocks duplicates and retains failed form before retry', async () => {
    const first = deferred<{ data: unknown }>()
    mocks.post.mockImplementationOnce(() => first.promise).mockResolvedValueOnce({ data: {} })
    const user = userEvent.setup()
    render(<Dictionaries />)

    await waitFor(() => expect(document.querySelector<HTMLButtonElement>('.dictionary-add')).toBeEnabled())
    await user.click(screen.getByText('优先级', { selector: '.arco-tabs-header-title-text' }))
    await user.click(document.querySelector<HTMLButtonElement>('.dictionary-add')!)
    const dialog = await screen.findByRole('dialog')
    const name = within(dialog).getByPlaceholderText('优先级')
    await user.type(name, ' 紧急 ')
    const save = within(dialog).getByRole('button', { name: '保存' })

    fireEvent.click(save)
    fireEvent.click(save)
    await waitFor(() => expect(mocks.post).toHaveBeenCalledTimes(1))
    expect(mocks.post).toHaveBeenCalledWith('/project-dictionaries', expect.objectContaining({
      type: 'PRIORITY',
      name: '紧急',
    }))

    first.reject(new Error('conflict'))
    await waitFor(() => expect(save).not.toBeDisabled())
    expect(name).toHaveValue(' 紧急 ')
    expect(screen.getByRole('dialog')).toBeInTheDocument()

    await user.click(save)
    await waitFor(() => expect(mocks.post).toHaveBeenCalledTimes(2))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  })

  it('robot type is the first, default tab and saves as a ROBOT_TYPE dictionary entry', async () => {
    const user = userEvent.setup()
    render(<Dictionaries />)

    const tabs = Array.from(document.querySelectorAll('.arco-tabs-header-title-text')).map((tab) => tab.textContent)
    expect(tabs).toEqual(['Robot 类型', 'Robot 料号', '优先级'])
    await waitFor(() => expect(document.querySelector<HTMLButtonElement>('.dictionary-add')).toBeEnabled())
    expect(document.querySelector('.dictionary-add')).toHaveAccessibleName('新增 Robot 类型')
    expect(mocks.get).toHaveBeenCalledWith('/project-dictionaries', { params: { type: 'ROBOT_TYPE' } })

    await user.click(document.querySelector<HTMLButtonElement>('.dictionary-add')!)
    const dialog = await screen.findByRole('dialog')
    await user.type(within(dialog).getByPlaceholderText('Robot 类型'), ' 六轴 ')
    await user.click(within(dialog).getByRole('button', { name: '保存' }))
    await waitFor(() => expect(mocks.post).toHaveBeenCalledWith('/project-dictionaries', expect.objectContaining({
      type: 'ROBOT_TYPE',
      name: '六轴',
    })))
  })

  it('dictionary loading uses config-scoped supplier options and recovers without admin supplier access', async () => {
    let failed = true
    mocks.get.mockImplementation((url: string) => {
      if (failed) return Promise.reject(new Error('offline'))
      return successfulGet(url)
    })
    const user = userEvent.setup()
    render(<Dictionaries />)

    expect(await screen.findByText('基础数据加载失败')).toBeInTheDocument()
    expect(document.querySelector<HTMLButtonElement>('.dictionary-add')).toBeDisabled()
    failed = false
    await user.click(screen.getByRole('button', { name: '重试' }))

    await waitFor(() => expect(screen.queryByText('基础数据加载失败')).not.toBeInTheDocument())
    expect(document.querySelector<HTMLButtonElement>('.dictionary-add')).toBeEnabled()
    expect(mocks.get).toHaveBeenCalledWith('/robot-part-supplier-options')
    expect(mocks.get.mock.calls.some(([url]) => url === '/admin/suppliers')).toBe(false)
  })

  it('referenced robot parts lock catalog identity fields and cannot be deleted', async () => {
    mocks.get.mockImplementation((url: string) => {
      if (url === '/robot-parts') return Promise.resolve({ data: [part] })
      return successfulGet(url)
    })
    const user = userEvent.setup()
    render(<Dictionaries />)

    await user.click(screen.getByText('Robot 料号', { selector: '.arco-tabs-header-title-text' }))
    const row = await screen.findByRole('row', { name: /NB80-9\/2\.2/ })
    expect(within(row).getByRole('button', { name: '删除' })).toBeDisabled()
    await user.click(within(row).getByRole('button', { name: '编辑' }))

    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByText('已被项目引用，厂商、料号和型号不可修改。')).toBeInTheDocument()
    expect(within(dialog).getByRole('combobox')).toHaveAttribute('aria-disabled', 'true')
    expect(within(dialog).getByPlaceholderText('输入 Robot 料号')).toBeDisabled()
    const model = within(dialog).getByPlaceholderText('输入该料号对应的完整型号')
    expect(model).toBeDisabled()
    expect(model).toHaveAttribute('maxlength', '512')
  })
})
