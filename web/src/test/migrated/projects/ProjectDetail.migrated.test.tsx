import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Message } from '@arco-design/web-react'
import { MemoryRouter, Route, Routes, useLocation, useNavigate } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { deferred } from './testUtils'

const mocks = vi.hoisted(() => ({
  get: vi.fn(),
  collaboration: {
    revision: 'global-1',
    status: 'ready',
    messageRevisions: {} as Record<number, number>,
    receiptRevisions: {} as Record<number, number>,
    activityRevisions: {} as Record<number, number>,
    reconnectRevision: 0,
    realtimeStatus: 'connected',
  },
}))

vi.mock('../../../api/client', () => ({
  default: { get: mocks.get },
}))

vi.mock('../../../store/collaboration', () => ({
  useCollaboration: (selector: (state: typeof mocks.collaboration) => unknown) => selector(mocks.collaboration),
}))

vi.mock('../../../components/FileTable', () => ({
  default: ({ targetId, onTargetHandled }: { targetId?: number; onTargetHandled?: () => void }) => (
    <section aria-label="files-panel" data-target={targetId ?? ''}>
      文件面板
      {targetId && <button onClick={onTargetHandled}>文件定位完成</button>}
    </section>
  ),
}))

vi.mock('../../../components/MessagePanel', () => ({
  default: ({ active, revision, targetId, onRead }: { active?: boolean; revision?: string; targetId?: number; onRead?: () => void }) => (
    <section aria-label="messages-panel" data-active={String(active)} data-revision={revision} data-target={targetId ?? ''}>
      留言面板
      <button onClick={onRead}>标记已读</button>
    </section>
  ),
}))

vi.mock('../../../components/ProjectActivityPanel', () => ({
  default: ({ active, onNavigate }: { active?: boolean; onNavigate?: (type: string, id: number) => void }) => (
    <section aria-label="activity-panel" data-active={String(active)}>
      动态面板
      <button onClick={() => onNavigate?.('FILE', 7)}>定位文件</button>
      <button onClick={() => onNavigate?.('MESSAGE', 8)}>定位留言</button>
    </section>
  ),
}))

vi.mock('../../../components/ProjectWorkflowPanel', () => ({
  default: ({ onChanged }: { onChanged: () => void }) => <button onClick={onChanged}>完成操作后刷新</button>,
}))

import ProjectDetail from '../../../pages/project/ProjectDetail'

function project(id = 1, overrides = {}) {
  return {
    id,
    projectGroupId: 3,
    projectGroupName: '主项目',
    name: `项目 ${id}`,
    description: null,
    status: 'IN_PROGRESS',
    workOrderNos: ['WO-1'],
    machineModel: 'M1',
    supplierName: '供应商',
    robotPartNumber: 'R-1',
    robotModelName: 'Robot 型号',
    responsibleUserName: '负责人',
    responsibleUserEmployeeNo: 'E1',
    sectionName: '工程课',
    priorityName: '普通',
    expectedCompletionDate: '2026-09-30',
    createdByName: '创建人',
    updatedAt: '2026-09-09T00:00:00Z',
    hasCopyHistory: false,
    ...overrides,
  }
}

function defaultGet(url: string) {
  if (url === '/projects/1') return Promise.resolve({ data: project() })
  if (url === '/projects/1/summary') return Promise.resolve({ data: { unreadMessages: 0, activityRevision: 'a1' } })
  if (url === '/project-groups/3') return Promise.resolve({ data: { projects: [project()] } })
  throw new Error(`unexpected GET ${url}`)
}

function RouteControls() {
  const navigate = useNavigate()
  const location = useLocation()
  return <>
    <output aria-label="当前路径">{location.pathname}</output>
    <button onClick={() => navigate('/projects/2')}>转到项目2</button>
    <button onClick={() => navigate('/projects/bad')}>转到无效地址</button>
    <button onClick={() => navigate('/projects/1')}>返回项目1</button>
  </>
}

function renderDetail(initial = '/projects/1') {
  return render(
    <MemoryRouter initialEntries={[initial]}>
      <RouteControls />
      <Routes><Route path="/projects/:id" element={<ProjectDetail />} /></Routes>
    </MemoryRouter>,
  )
}

describe('ProjectDetail migrated behavior', () => {
  beforeEach(() => {
    mocks.get.mockImplementation(defaultGet)
    mocks.collaboration = {
      revision: 'global-1', status: 'ready', messageRevisions: {}, receiptRevisions: {}, activityRevisions: {}, reconnectRevision: 0, realtimeStatus: 'connected',
    }
  })
  afterEach(() => { vi.restoreAllMocks() })

  it('invalid project route identifiers render a recoverable error without API calls', async () => {
    renderDetail('/projects/not-a-number')
    expect(await screen.findByText('项目地址无效')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '返回项目列表' })).toBeInTheDocument()
    expect(mocks.get).not.toHaveBeenCalled()
  })

  it('project navigation ignores late responses across valid and invalid routes', async () => {
    const first = deferred<{ data: ReturnType<typeof project> }>()
    mocks.get.mockImplementation((url: string) => {
      if (url === '/projects/1') return first.promise
      if (url === '/projects/2') return Promise.resolve({ data: project(2) })
      if (url === '/projects/2/summary') return Promise.resolve({ data: { unreadMessages: 0 } })
      if (url === '/project-groups/3') return Promise.resolve({ data: { projects: [project(2)] } })
      throw new Error(`unexpected GET ${url}`)
    })
    const user = userEvent.setup()
    renderDetail()
    await user.click(screen.getByRole('button', { name: '转到无效地址' }))
    expect(await screen.findByText('项目地址无效')).toBeInTheDocument()
    first.resolve({ data: project(1) })
    expect(screen.queryByRole('heading', { name: '项目 1' })).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: '转到项目2' }))
    expect(await screen.findByRole('heading', { name: '项目 2' })).toBeInTheDocument()
  })

  it('an operation refresh for the same project supersedes an older background snapshot', async () => {
    const initial = deferred<{ data: ReturnType<typeof project> }>()
    let projectCalls = 0
    mocks.get.mockImplementation((url: string) => {
      if (url === '/projects/1') {
        projectCalls += 1
        if (projectCalls === 1) return Promise.resolve({ data: project(1, { name: '初始项目' }) })
        if (projectCalls === 2) return initial.promise
        return Promise.resolve({ data: project(1, { name: '操作后项目' }) })
      }
      if (url === '/projects/1/summary') return Promise.resolve({ data: { unreadMessages: 0 } })
      if (url === '/project-groups/3') return Promise.resolve({ data: { projects: [project()] } })
      throw new Error(`unexpected GET ${url}`)
    })
    const user = userEvent.setup()
    const view = renderDetail()
    expect(await screen.findByRole('heading', { name: '初始项目' })).toBeInTheDocument()
    mocks.collaboration.revision = 'global-2'
    view.rerender(
      <MemoryRouter initialEntries={['/projects/1']}><RouteControls /><Routes><Route path="/projects/:id" element={<ProjectDetail />} /></Routes></MemoryRouter>,
    )
    await new Promise((resolve) => setTimeout(resolve, 0))
    expect(projectCalls).toBe(1)
    mocks.collaboration.activityRevisions = { 1: 1 }
    view.rerender(
      <MemoryRouter initialEntries={['/projects/1']}><RouteControls /><Routes><Route path="/projects/:id" element={<ProjectDetail />} /></Routes></MemoryRouter>,
    )
    await waitFor(() => expect(projectCalls).toBe(2))
    await user.click(screen.getByRole('button', { name: '完成操作后刷新' }))
    expect(await screen.findByRole('heading', { name: '操作后项目' })).toBeInTheDocument()
    initial.resolve({ data: project(1, { name: '过期后台项目' }) })
    await waitFor(() => expect(screen.queryByRole('heading', { name: '过期后台项目' })).not.toBeInTheDocument())
  })

  it('an operation refresh after authorization loss warns once and returns to the project list', async () => {
    const warning = vi.spyOn(Message, 'warning').mockImplementation(() => () => {})
    let calls = 0
    mocks.get.mockImplementation((url: string) => {
      if (url === '/projects/1') {
        calls += 1
        if (calls === 1) return Promise.resolve({ data: project(1, { name: '敏感项目' }) })
        return Promise.reject({ isAxiosError: true, response: { status: 403 } })
      }
      if (url === '/projects/1/summary') return Promise.resolve({ data: { unreadMessages: 0 } })
      if (url === '/project-groups/3') return Promise.resolve({ data: { projects: [project()] } })
      throw new Error(`unexpected GET ${url}`)
    })
    const user = userEvent.setup()
    renderDetail()
    expect(await screen.findByRole('heading', { name: '敏感项目' })).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: '完成操作后刷新' }))
    await waitFor(() => expect(screen.getByLabelText('当前路径')).toHaveTextContent(/^\/projects$/))
    expect(warning).toHaveBeenCalledTimes(1)
    expect(warning).toHaveBeenCalledWith('该子项目负责人已变更或权限已调整，您已无权访问')
    expect(screen.queryByRole('heading', { name: '敏感项目' })).not.toBeInTheDocument()
    expect(screen.queryByText('项目加载失败或没有访问权限')).not.toBeInTheDocument()
  })

  it('an initial authorization failure keeps the no-access state without redirecting', async () => {
    const warning = vi.spyOn(Message, 'warning').mockImplementation(() => () => {})
    mocks.get.mockImplementation((url: string) => {
      if (url === '/projects/1') return Promise.reject({ isAxiosError: true, response: { status: 403 } })
      throw new Error(`unexpected GET ${url}`)
    })
    renderDetail()
    expect(await screen.findByText('项目加载失败或没有访问权限')).toBeInTheDocument()
    expect(screen.getByLabelText('当前路径')).toHaveTextContent('/projects/1')
    expect(warning).not.toHaveBeenCalled()
  })

  it('project deletion refreshes only the affected detail while message events refresh only its summary', async () => {
    const warning = vi.spyOn(Message, 'warning').mockImplementation(() => () => {})
    let projectCalls = 0
    let summaryCalls = 0
    let deleted = false
    mocks.get.mockImplementation((url: string) => {
      if (url === '/projects/1') {
        projectCalls += 1
        return deleted
          ? Promise.reject({ isAxiosError: true, response: { status: 404 } })
          : Promise.resolve({ data: project(1, { name: '待删除项目' }) })
      }
      if (url === '/projects/1/summary') {
        summaryCalls += 1
        return Promise.resolve({ data: { unreadMessages: summaryCalls, activityRevision: `a${summaryCalls}` } })
      }
      if (url === '/project-groups/3') return Promise.resolve({ data: { projects: [project()] } })
      throw new Error(`unexpected GET ${url}`)
    })
    const view = renderDetail('/projects/1?tab=messages')
    expect(await screen.findByRole('heading', { name: '待删除项目' })).toBeInTheDocument()
    await waitFor(() => expect(summaryCalls).toBe(1))

    mocks.collaboration.activityRevisions = { 2: 1 }
    view.rerender(<MemoryRouter initialEntries={['/projects/1?tab=messages']}><RouteControls /><Routes><Route path="/projects/:id" element={<ProjectDetail />} /></Routes></MemoryRouter>)
    await new Promise((resolve) => setTimeout(resolve, 0))
    expect(projectCalls).toBe(1)

    mocks.collaboration.messageRevisions = { 1: 1 }
    view.rerender(<MemoryRouter initialEntries={['/projects/1?tab=messages']}><RouteControls /><Routes><Route path="/projects/:id" element={<ProjectDetail />} /></Routes></MemoryRouter>)
    await waitFor(() => expect(summaryCalls).toBe(2))
    expect(projectCalls).toBe(1)

    deleted = true
    mocks.collaboration.activityRevisions = { 1: 1, 2: 1 }
    view.rerender(<MemoryRouter initialEntries={['/projects/1?tab=messages']}><RouteControls /><Routes><Route path="/projects/:id" element={<ProjectDetail />} /></Routes></MemoryRouter>)
    // 已加载的项目被删除时提示一次并回到项目列表，而不是停留在“不存在”页面。
    await waitFor(() => expect(screen.getByLabelText('当前路径')).toHaveTextContent(/^\/projects$/))
    expect(projectCalls).toBe(2)
    expect(warning).toHaveBeenCalledTimes(1)
    expect(warning).toHaveBeenCalledWith('该子项目已被删除')
    expect(screen.queryByRole('heading', { name: '待删除项目' })).not.toBeInTheDocument()
  })

  it('unknown project tab query falls back to files and keeps description expansion keyboard accessible', async () => {
    mocks.get.mockImplementation((url: string) => {
      if (url === '/projects/1') return Promise.resolve({ data: project(1, { description: '这是一个需要展开的长项目说明。'.repeat(10) }) })
      return defaultGet(url)
    })
    const user = userEvent.setup()
    renderDetail('/projects/1?tab=unknown')
    expect(await screen.findByLabelText('files-panel')).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: '文件' })).toHaveAttribute('aria-selected', 'true')
    const description = document.querySelector<HTMLElement>('.project-description')!
    expect(description).toHaveTextContent('这是一个需要展开的长项目说明。')
    // jsdom没有排版尺寸，Arco只会在真实溢出时生成展开按钮；若生成，必须可由键盘触发。
    const expand = screen.queryByRole('button', { name: '展开项目说明' })
    if (expand) {
      expand.focus()
      await user.keyboard('{Enter}')
      expect(await screen.findByRole('button', { name: '收起项目说明' })).toHaveAttribute('aria-expanded', 'true')
    } else {
      expect(description.querySelector('.arco-ellipsis-content')).toBeInTheDocument()
    }
  })

  it('project detail isolates live message revision from summary churn while preserving fallback and reconnect invalidation', async () => {
    const view = renderDetail('/projects/1?tab=messages')
    const panel = await screen.findByLabelText('messages-panel')
    expect(panel).toHaveAttribute('data-revision', 'live:0:0')

    mocks.collaboration.revision = 'global-2'
    view.rerender(<MemoryRouter initialEntries={['/projects/1?tab=messages']}><Routes><Route path="/projects/:id" element={<ProjectDetail />} /></Routes></MemoryRouter>)
    expect(await screen.findByLabelText('messages-panel')).toHaveAttribute('data-revision', 'live:0:0')

    mocks.collaboration.messageRevisions = { 1: 4 }
    view.rerender(<MemoryRouter initialEntries={['/projects/1?tab=messages']}><Routes><Route path="/projects/:id" element={<ProjectDetail />} /></Routes></MemoryRouter>)
    expect(await screen.findByLabelText('messages-panel')).toHaveAttribute('data-revision', 'live:4:0')

    mocks.collaboration.realtimeStatus = 'disconnected'
    view.rerender(<MemoryRouter initialEntries={['/projects/1?tab=messages']}><Routes><Route path="/projects/:id" element={<ProjectDetail />} /></Routes></MemoryRouter>)
    await waitFor(() => expect(screen.getByLabelText('messages-panel').getAttribute('data-revision')).toMatch(/^poll:/))

    mocks.collaboration.realtimeStatus = 'connected'
    mocks.collaboration.reconnectRevision = 1
    view.rerender(<MemoryRouter initialEntries={['/projects/1?tab=messages']}><Routes><Route path="/projects/:id" element={<ProjectDetail />} /></Routes></MemoryRouter>)
    expect(await screen.findByLabelText('messages-panel')).toHaveAttribute('data-revision', 'live:4:1')
  })

  it('new message during disconnect appears', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true, toFake: ['setTimeout', 'clearTimeout'] })
    try {
      let activityRevision = 'a1'
      let summaryCalls = 0
      let projectCalls = 0
      mocks.collaboration.realtimeStatus = 'disconnected'
      mocks.get.mockImplementation((url: string) => {
        if (url === '/projects/1') { projectCalls += 1; return Promise.resolve({ data: project() }) }
        if (url === '/projects/1/summary') {
          summaryCalls += 1
          return Promise.resolve({ data: { unreadMessages: activityRevision === 'a1' ? 0 : 1, activityRevision } })
        }
        return defaultGet(url)
      })
      const view = renderDetail('/projects/1?tab=messages')
      await waitFor(() => expect(screen.getByLabelText('messages-panel')).toHaveAttribute('data-revision', 'poll:a1:0:0'))
      const initialSummaryCalls = summaryCalls

      // 另一端在实时断开期间发了留言：概览动态指纹变化，留言面板的修订号随之变化并重新拉取。
      activityRevision = 'a2'
      await act(async () => { await vi.advanceTimersByTimeAsync(5_000) })
      await waitFor(() => expect(screen.getByLabelText('messages-panel')).toHaveAttribute('data-revision', 'poll:a2:0:0'))
      expect(summaryCalls).toBe(initialSummaryCalls + 1)
      await waitFor(() => expect(screen.getByRole('tab', { name: /留言/ }).querySelector('.arco-badge')).toHaveTextContent('1'))
      await waitFor(() => expect(projectCalls).toBe(2))

      // 实时恢复后停止兜底轮询。
      mocks.collaboration.realtimeStatus = 'connected'
      view.rerender(<MemoryRouter initialEntries={['/projects/1?tab=messages']}><RouteControls /><Routes><Route path="/projects/:id" element={<ProjectDetail />} /></Routes></MemoryRouter>)
      const connectedCalls = summaryCalls
      await act(async () => { await vi.advanceTimersByTimeAsync(120_000) })
      expect(summaryCalls).toBe(connectedCalls)
    } finally {
      vi.useRealTimers()
    }
  })

  it('project detail activity URL tab and target navigation preserve valid tab state', async () => {
    const user = userEvent.setup()
    renderDetail('/projects/1?tab=activity')
    expect(await screen.findByRole('tab', { name: '项目动态' })).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByLabelText('activity-panel')).toHaveAttribute('data-active', 'true')
    expect(screen.queryByLabelText('messages-panel')).not.toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: '定位文件' }))
    expect(await screen.findByLabelText('files-panel')).toHaveAttribute('data-target', '7')
    expect(screen.getByText('已定位到目标内容')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: '显示全部' }))
    expect(screen.getByLabelText('files-panel')).toHaveAttribute('data-target', '')
  })

  it('project detail ignores an older unread summary after the read refresh completes', async () => {
    const summaries: Array<ReturnType<typeof deferred<{ data: { unreadMessages: number } }>>> = []
    mocks.get.mockImplementation((url: string) => {
      if (url === '/projects/1') return Promise.resolve({ data: project() })
      if (url === '/projects/1/summary') {
        const request = deferred<{ data: { unreadMessages: number } }>()
        summaries.push(request)
        return request.promise
      }
      if (url === '/project-groups/3') return Promise.resolve({ data: { projects: [project()] } })
      throw new Error(`unexpected GET ${url}`)
    })
    const user = userEvent.setup()
    renderDetail('/projects/1?tab=messages')
    await waitFor(() => expect(summaries).toHaveLength(1))
    await user.click(screen.getByRole('button', { name: '标记已读' }))
    await waitFor(() => expect(summaries).toHaveLength(2))
    summaries[1].resolve({ data: { unreadMessages: 0 } })
    summaries[0].resolve({ data: { unreadMessages: 1 } })
    await waitFor(() => expect(screen.queryByText('1', { selector: '.arco-badge-number' })).not.toBeInTheDocument())
  })

  it('project copy history hides restricted relations and pages file mappings on demand', async () => {
    const calls: Array<{ url: string; config?: { params?: Record<string, unknown> } }> = []
    mocks.get.mockImplementation((url: string, config?: { params?: Record<string, unknown> }) => {
      calls.push({ url, config })
      if (url === '/projects/1') return Promise.resolve({ data: project(1, { hasCopyHistory: true, copySource: { projectId: 2, name: '原项目' } }) })
      if (url === '/projects/1/summary') return Promise.resolve({ data: { unreadMessages: 0 } })
      if (url === '/project-groups/3') return Promise.resolve({ data: { projects: [project()] } })
      if (url === '/projects/1/copy-history') return Promise.resolve({ data: {
        source: { copyId: 16, projectId: 2, name: '原项目', fileCount: 21, totalBytes: 2048, copiedByName: '管理员', createdAt: '2026-09-15T01:00:00Z' },
        copies: [],
        hasRestrictedRelations: true,
      } })
      if (url === '/project-copies/16/files') return Promise.resolve({ data: {
        list: [{ sourceFileId: 1, sourceFileName: '规格.xlsx', sourceDeleted: false, targetFileId: 7, targetFileName: '规格.xlsx', targetDeleted: false }],
        total: 21, page: config?.params?.page, pageSize: 20,
      } })
      throw new Error(`unexpected GET ${url}`)
    })
    const user = userEvent.setup()
    renderDetail()
    await user.click(await screen.findByRole('button', { name: '查看复制履历' }))
    await waitFor(() => expect(document.querySelector('.project-copy-history-drawer')).toBeInTheDocument())
    const drawer = document.querySelector<HTMLElement>('.project-copy-history-drawer')!
    expect(await within(drawer).findByText(/部分复制关联项目当前无权查看/)).toBeInTheDocument()
    await user.click(within(drawer).getByRole('button', { name: '查看文件映射' }))
    expect((await within(drawer).findAllByText('规格.xlsx', { selector: '.copy-file-name' })).length).toBe(2)
    expect(calls.find((call) => call.url === '/project-copies/16/files')?.config?.params).toEqual({ page: 1, pageSize: 20 })
  })
})
