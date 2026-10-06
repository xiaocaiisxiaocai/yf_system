import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { ProjectSummary } from '../api/types'
import { deferred } from './migrated/projects/testUtils'

const mocks = vi.hoisted(() => ({
  get: vi.fn(),
  collaboration: {
    realtimeStatus: 'connected',
    activityRevisions: {} as Record<number, number>,
    messageRevisions: {} as Record<number, number>,
    receiptRevisions: {} as Record<number, number>,
    reconnectRevision: 0,
  },
}))

vi.mock('../api/client', () => ({ default: { get: mocks.get } }))
vi.mock('../store/collaboration', () => ({
  useCollaboration: (selector: (state: typeof mocks.collaboration) => unknown) => selector(mocks.collaboration),
}))
vi.mock('../components/FileTable', () => ({ default: () => <div>文件列表</div> }))
vi.mock('../components/MessagePanel', () => ({ default: () => <div>留言列表</div> }))
vi.mock('../components/ProjectActivityPanel', () => ({ default: () => <div>动态列表</div> }))
vi.mock('../components/ProjectWorkflowPanel', () => ({
  default: ({ onChanged }: { onChanged: () => void }) => <button onClick={onChanged}>流程操作</button>,
}))

import SubprojectPane from '../pages/project/SubprojectPane'
type PaneProps = Parameters<typeof SubprojectPane>[0]
import { SubprojectDockContext } from '../pages/project/subprojectDockContext'

const T1 = '2026-09-09T00:00:00Z'
const T2 = '2026-09-09T00:05:00Z'
const T3 = '2026-09-09T00:10:00Z'
const panelApi = { isVisible: true, onDidVisibilityChange: () => ({ dispose: () => undefined }) }

function row(updatedAt: string): ProjectSummary {
  return { id: 9, name: '子项目', status: 'IN_PROGRESS', description: null, unreadMessages: 0, updatedAt } as unknown as ProjectSummary
}

function ui(updatedAt: string, onGroupChanged = () => undefined) {
  const context = { projects: new Map([[9, row(updatedAt)]]), renderManageActions: () => null, onGroupChanged }
  return (
    <MemoryRouter>
      <SubprojectDockContext.Provider value={context}>
        <SubprojectPane {...({ params: { projectId: 9 }, api: panelApi } as unknown as PaneProps)} />
      </SubprojectDockContext.Provider>
    </MemoryRouter>
  )
}

describe('SubprojectPane detail refresh', () => {
  let projectCalls = 0
  beforeEach(() => {
    projectCalls = 0
    mocks.get.mockReset()
    mocks.get.mockImplementation((url: string) => {
      if (url === '/projects/9') { projectCalls += 1; return Promise.resolve({ data: { ...row(T1), updatedAt: T1 } }) }
      if (url === '/projects/9/summary') return Promise.resolve({ data: { unreadMessages: 0, activityRevision: 'a1' } })
      throw new Error(`unexpected GET ${url}`)
    })
  })

  it('an own operation refreshes the detail once even when the refreshed group row arrives', async () => {
    const pending = deferred<{ data: ProjectSummary }>()
    const user = userEvent.setup()
    const view = render(ui(T1))
    await screen.findByRole('button', { name: '流程操作' })
    expect(projectCalls).toBe(1)

    mocks.get.mockImplementation((url: string) => {
      if (url === '/projects/9') { projectCalls += 1; return pending.promise }
      if (url === '/projects/9/summary') return Promise.resolve({ data: { unreadMessages: 0, activityRevision: 'a2' } })
      throw new Error(`unexpected GET ${url}`)
    })
    await user.click(screen.getByRole('button', { name: '流程操作' }))
    expect(projectCalls).toBe(2)
    // 主项目刷新先返回：行的 updatedAt 已变新，但详情请求仍在进行中，不重复拉取。
    view.rerender(ui(T2))
    pending.resolve({ data: row(T2) })
    await waitFor(() => expect(screen.getByRole('button', { name: '流程操作' })).toBeInTheDocument())
    await new Promise((resolve) => setTimeout(resolve, 0))
    view.rerender(ui(T2))
    await new Promise((resolve) => setTimeout(resolve, 0))
    expect(projectCalls).toBe(2)
  })

  it('refetches the detail once when the group row is newer than the loaded detail', async () => {
    const view = render(ui(T1))
    await screen.findByRole('button', { name: '流程操作' })
    mocks.get.mockImplementation((url: string) => {
      if (url === '/projects/9') { projectCalls += 1; return Promise.resolve({ data: row(T3) }) }
      if (url === '/projects/9/summary') return Promise.resolve({ data: { unreadMessages: 0, activityRevision: 'a1' } })
      throw new Error(`unexpected GET ${url}`)
    })
    view.rerender(ui(T3))
    await waitFor(() => expect(projectCalls).toBe(2))
    view.rerender(ui(T3))
    await new Promise((resolve) => setTimeout(resolve, 0))
    expect(projectCalls).toBe(2)
  })
})

describe('SubprojectPane stale in-flight detail', () => {
  it('catches up once after an in-flight request returns a detail older than the newer group row', async () => {
    let projectCalls = 0
    mocks.get.mockReset()
    mocks.get.mockImplementation((url: string) => {
      if (url === '/projects/9') { projectCalls += 1; return Promise.resolve({ data: row(T1) }) }
      if (url === '/projects/9/summary') return Promise.resolve({ data: { unreadMessages: 0, activityRevision: 'a1' } })
      throw new Error(`unexpected GET ${url}`)
    })
    const user = userEvent.setup()
    const view = render(ui(T1))
    await screen.findByRole('button', { name: '流程操作' })
    const stale = deferred<{ data: ProjectSummary }>()
    mocks.get.mockImplementation((url: string) => {
      if (url === '/projects/9') {
        projectCalls += 1
        return projectCalls === 2 ? stale.promise : Promise.resolve({ data: row(T3) })
      }
      if (url === '/projects/9/summary') return Promise.resolve({ data: { unreadMessages: 0, activityRevision: 'a1' } })
      throw new Error(`unexpected GET ${url}`)
    })
    await user.click(screen.getByRole('button', { name: '流程操作' }))
    expect(projectCalls).toBe(2)
    // 请求进行中到达较新的行；该请求随后返回旧详情（T1 < T3），结束后应补拉一次。
    view.rerender(ui(T3))
    stale.resolve({ data: row(T1) })
    await waitFor(() => expect(projectCalls).toBe(3))
    await new Promise((resolve) => setTimeout(resolve, 0))
    view.rerender(ui(T3))
    await new Promise((resolve) => setTimeout(resolve, 0))
    expect(projectCalls).toBe(3)
  })
})

describe('SubprojectPane access loss', () => {
  it('reports a lost subproject to the group once instead of navigating itself', async () => {
    let status: number | null = null
    mocks.get.mockReset()
    mocks.get.mockImplementation((url: string) => {
      if (url === '/projects/9') {
        return status
          ? Promise.reject({ isAxiosError: true, response: { status } })
          : Promise.resolve({ data: { ...row(T1), updatedAt: T1 } })
      }
      if (url === '/projects/9/summary') return Promise.resolve({ data: { unreadMessages: 0 } })
      throw new Error(`unexpected GET ${url}`)
    })
    const onGroupChanged = vi.fn()
    const view = render(ui(T1, onGroupChanged))
    expect(await screen.findByRole('button', { name: '流程操作' })).toBeInTheDocument()
    expect(onGroupChanged).not.toHaveBeenCalled()

    status = 404
    mocks.collaboration = { ...mocks.collaboration, activityRevisions: { 9: 1 } }
    view.rerender(ui(T1, onGroupChanged))
    expect(await screen.findByText('子项目不存在或没有访问权限')).toBeInTheDocument()
    expect(onGroupChanged).toHaveBeenCalledTimes(1)

    mocks.collaboration = { ...mocks.collaboration, activityRevisions: { 9: 2 } }
    view.rerender(ui(T1, onGroupChanged))
    await waitFor(() => expect(mocks.get.mock.calls.filter(([url]) => url === '/projects/9')).toHaveLength(3))
    expect(onGroupChanged).toHaveBeenCalledTimes(1)
    mocks.collaboration = { ...mocks.collaboration, activityRevisions: {} }
  })
})
