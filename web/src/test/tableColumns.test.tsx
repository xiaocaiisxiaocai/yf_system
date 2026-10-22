import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import {
  columnLayoutStorageKey, normalizeColumnLayout, withFlexibleColumn, type ColumnLayoutSpec,
} from '../components/tableColumnLayout'

const mocks = vi.hoisted(() => ({
  get: vi.fn(), post: vi.fn(), put: vi.fn(), delete: vi.fn(),
  auth: {
    user: { id: 7, employeeNo: 'admin1', realName: '管理员', userType: 'INTERNAL' },
    permissions: ['project:create', 'project:update'] as string[],
  },
}))

vi.mock('../api/client', () => ({ default: mocks }))
vi.mock('../store/auth', () => ({
  useAuth: () => ({
    user: mocks.auth.user,
    hasPerm: (permission: string) => mocks.auth.permissions.includes(permission),
  }),
}))
vi.mock('../store/collaboration', () => ({
  useCollaboration: (selector: (state: { revision: string; status: string }) => unknown) => selector({ revision: '1', status: 'ready' }),
}))

import ProjectList from '../pages/project/ProjectList'

const storageKey = columnLayoutStorageKey('project-groups', 7)
const specs: ColumnLayoutSpec[] = [
  { key: 'name', title: '主项目名称', defaultWidth: 120, minWidth: 96, locked: true },
  { key: 'sectionName', title: '课别', defaultWidth: 72, minWidth: 64, maxWidth: 480 },
  { key: 'actions', title: '操作', defaultWidth: 140, minWidth: 108, maxWidth: 280, locked: true },
]

function savedLayout() {
  const raw = localStorage.getItem(storageKey)
  return raw ? JSON.parse(raw) as { hidden: string[]; widths: Record<string, number> } : null
}

function renderList() {
  mocks.get.mockImplementation((url: string) => {
    if (url === '/project-groups') return Promise.resolve({ data: { list: [], total: 0, page: 1, pageSize: 10 } })
    if (url === '/supplier-options') return Promise.resolve({ data: [] })
    if (url === '/project-dictionaries') return Promise.resolve({ data: [] })
    return Promise.reject(new Error(`unexpected GET ${url}`))
  })
  return render(<MemoryRouter><ProjectList /></MemoryRouter>)
}

describe('column layout storage', () => {
  it('drops unknown values, keeps locked columns, and clamps widths', () => {
    expect(normalizeColumnLayout({
      hidden: ['name', 'sectionName', 'missing'],
      widths: { name: 10, sectionName: 9999, actions: 140, missing: 20, robotTypeName: 'wide' },
    }, specs)).toEqual({
      hidden: ['sectionName'],
      widths: { name: 96, sectionName: 480 },
    })
    expect(normalizeColumnLayout('{', specs)).toEqual({ hidden: [], widths: {} })
  })

  it('keeps the fixed column width and shares leftover across the others', () => {
    const columns = [
      { key: 'name', width: 120 },
      { key: 'sectionName', width: 72 },
      { key: 'actions', width: 140 },
    ]
    expect(withFlexibleColumn(columns, 401, 'name')).toEqual([
      { key: 'name', width: 120 },
      { key: 'sectionName', width: 107 },
      { key: 'actions', width: 174 },
    ])
    expect(withFlexibleColumn(columns, 300, 'name')).toEqual(columns)
    expect(withFlexibleColumn(columns, Number.NaN, 'name')).toEqual(columns)
    expect(withFlexibleColumn([{ key: 'name', width: 120 }], 200, 'name')).toEqual([{ key: 'name', width: 120 }])
  })
})

describe('project list columns', () => {
  beforeEach(() => localStorage.clear())

  it('hides a column, remembers it, and restores the default set', async () => {
    const user = userEvent.setup()
    const view = renderList()
    expect(await screen.findByRole('columnheader', { name: /课别/ })).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: '显示列' }))
    const picker = await screen.findByRole('group', { name: '显示列' })
    expect(within(picker).getByRole('checkbox', { name: '主项目名称' })).toBeDisabled()
    expect(within(picker).getByRole('checkbox', { name: '操作' })).toBeDisabled()
    fireEvent.click(within(picker).getByRole('checkbox', { name: '课别' }))

    await waitFor(() => expect(screen.queryByRole('columnheader', { name: /课别/ })).not.toBeInTheDocument())
    expect(savedLayout()?.hidden).toEqual(['sectionName'])
    expect(screen.getByRole('columnheader', { name: /主项目名称/ })).toBeInTheDocument()

    view.unmount()
    renderList()
    await waitFor(() => expect(screen.queryByRole('columnheader', { name: /课别/ })).not.toBeInTheDocument())

    await user.click(screen.getByRole('button', { name: '显示列' }))
    const restored = await screen.findByRole('group', { name: '显示列' })
    fireEvent.click(within(restored).getByRole('button', { name: '恢复默认' }))
    expect(await screen.findByRole('columnheader', { name: /课别/ })).toBeInTheDocument()
    expect(savedLayout()).toBeNull()
  })

  it('drags a column wider and double-click restores its default width', async () => {
    renderList()
    const handle = await screen.findByRole('button', { name: '调整工令号列宽' })
    fireEvent.pointerDown(handle, { button: 0, clientX: 40 })
    fireEvent.pointerMove(window, { clientX: 120 })
    fireEvent.pointerUp(window, { clientX: 120 })

    await waitFor(() => {
      const widths = [...document.querySelectorAll('col')].map((col) => (col as HTMLElement).style.width)
      // 表头、表体各一份。只有工令号变成 176，原来同宽的 Robot 厂商仍是 96。
      expect(widths.filter((width) => width === '176px')).toHaveLength(2)
      expect(widths.filter((width) => width === '96px')).toHaveLength(2)
      expect(widths.filter((width) => width === '120px')).toHaveLength(2)
    })
    expect(savedLayout()?.widths).toEqual({ workOrderNos: 176 })

    fireEvent.doubleClick(handle)
    await waitFor(() => expect(savedLayout()).toBeNull())
    await waitFor(() => {
      const widths = [...document.querySelectorAll('col')].map((col) => (col as HTMLElement).style.width)
      expect(widths).toContain('96px')
      expect(widths).not.toContain('176px')
    })
  })

  it('changes width from the keyboard', async () => {
    renderList()
    const handle = await screen.findByRole('button', { name: '调整机型列宽' })
    handle.focus()
    fireEvent.keyDown(handle, { key: 'ArrowRight' })
    await waitFor(() => expect(savedLayout()?.widths.machineModel).toBe(96))
    fireEvent.keyDown(handle, { key: 'Home' })
    await waitFor(() => expect(savedLayout()).toBeNull())
  })
})
