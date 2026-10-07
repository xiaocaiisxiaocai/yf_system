import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClientProvider } from '@tanstack/react-query'
import axios from 'axios'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import App from '../../App'
import http from '../../api/client'
import { createAppQueryClient } from '../../api/queryClient'
import { OemEntryButton, OemLanding } from '../../oem/layouts/OemLayouts'
import { portalHttp, usePortalAuth } from '../../oem/api/portalSession'
import { useAuth } from '../../store/auth'

const internalUser = {
  id: 7,
  employeeNo: 'A0007',
  realName: 'OEM 内部用户',
  email: 'internal@example.test',
  userType: 'INTERNAL' as const,
  supplierId: null,
  isSystemAdmin: false,
}

const blockedSummary = {
  id: 88,
  direction: 'INTERNAL_TO_OEM' as const,
  companyId: 9,
  companyName: '测试代工厂',
  title: '等待处置的传递单',
  sender: { realm: 'internal' as const, id: 7, employeeNo: 'A0007', realName: 'OEM 内部用户' },
  lifecycleStatus: 'SEALED' as const,
  approvalStatus: 'APPROVAL_BLOCKED' as const,
  approvalBlockedReason: '原审批人已停用',
  validationSummary: 'VALID' as const,
  fileCount: 1,
  totalBytes: 1024,
  availableCount: 1,
  purgePendingCount: 0,
  purgedCount: 0,
  missingCount: 0,
  createdAt: '2026-10-05T00:00:00Z',
  sentAt: '2026-10-05T00:05:00Z',
  releasedAt: null,
  version: 4,
}

const blockedDetail = {
  summary: blockedSummary,
  description: null,
  retention: { templateId: 1, templateName: '保留七天', mode: 'AFTER_RELEASE', releaseTtlMinutes: 10080, receiptGraceMinutes: null, summary: '发布后七天删除' },
  manifestSha256: null,
  expiresAt: null,
  closedReason: null,
  closedAt: null,
  capabilities: { canEdit: false, canSend: false, canDelete: false, canReadContent: false, contentPurpose: 'NONE' as const },
  files: [],
  approval: {
    instanceId: 5,
    status: 'APPROVAL_BLOCKED',
    blockedReason: '原审批人已停用',
    currentSortNo: 1,
    version: 6,
    templateName: '默认审批',
    nodes: [{
      sortNo: 1,
      name: '课别主管',
      approverSource: 'SECTION_LEADER',
      approvalMode: 'SINGLE',
      status: 'PENDING',
      skipReason: null,
      usedFallback: false,
      completedAt: null,
      tasks: [{
        id: 51,
        status: 'PENDING',
        approverUserId: 21,
        approverName: '已停用主管',
        approverEmployeeNo: 'A0021',
        reason: null,
        decidedAt: null,
        replacesTaskId: null,
        reassignReason: null,
        version: 3,
      }],
    }],
  },
}

function CurrentLocation() {
  const location = useLocation()
  return <output aria-label="当前地址">{location.pathname}</output>
}

function setInternalSession(permissions: string[]) {
  useAuth.setState({
    token: 'internal-token',
    user: internalUser,
    permissions,
    menus: [],
    mustChangePassword: false,
    booted: true,
    generation: 11,
  })
}

describe('OEM 前端入口与路由', () => {
  beforeEach(() => {
    setInternalSession([])
    usePortalAuth.setState({
      token: null,
      account: null,
      mustChangePassword: false,
      booted: true,
      generation: 0,
    })
  })

  afterEach(() => {
    vi.restoreAllMocks()
    window.history.replaceState({}, '', '/')
  })

  it('只向有 OEM 权限的内部员工显示入口，并导航到其首个可用页面', async () => {
    const user = userEvent.setup()
    setInternalSession(['oem:audit_view'])
    render(
      <MemoryRouter initialEntries={['/']}>
        <Routes>
          <Route path="*" element={<><OemEntryButton /><CurrentLocation /></>} />
        </Routes>
      </MemoryRouter>,
    )

    await user.click(screen.getByRole('button', { name: 'OEM 文件传递' }))
    expect(screen.getByRole('status', { name: '当前地址' })).toHaveTextContent('/oem/admin/audit')
  })

  it('没有任何 OEM 权限时不显示内部入口', () => {
    render(<MemoryRouter><OemEntryButton /></MemoryRouter>)
    expect(screen.queryByRole('button', { name: 'OEM 文件传递' })).not.toBeInTheDocument()
  })

  it('仅维护组织主管不再显示 OEM 入口', () => {
    setInternalSession(['dept:leader_manage'])
    render(<MemoryRouter><OemEntryButton /></MemoryRouter>)
    expect(screen.queryByRole('button', { name: 'OEM 文件传递' })).not.toBeInTheDocument()
  })

  it.each(['/org/depts', '/oem/admin/leaders'])('主管维护权限可经 %s 进入统一组织架构', async (path) => {
    setInternalSession(['dept:leader_manage'])
    const get = vi.spyOn(http, 'get').mockResolvedValue({ data: [] } as never)
    const client = createAppQueryClient()
    render(
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[path]}><App /><CurrentLocation /></MemoryRouter>
      </QueryClientProvider>,
    )

    expect(await screen.findByRole('heading', { name: '组织架构' })).toBeInTheDocument()
    expect(screen.getByRole('status', { name: '当前地址' })).toHaveTextContent('/org/depts')
    expect(screen.queryByRole('button', { name: '新增事业部' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'OEM 文件传递' })).not.toBeInTheDocument()
    expect(get).toHaveBeenCalledWith('/departments')
  })

  it('OEM 根路径按权限落到后台页面，而不是暴露无权的传递单页', async () => {
    setInternalSession(['oem:flow_approve'])
    render(
      <MemoryRouter initialEntries={['/oem']}>
        <Routes>
          <Route path="/oem" element={<OemLanding />} />
          <Route path="/oem/approvals" element={<CurrentLocation />} />
        </Routes>
      </MemoryRouter>,
    )

    expect(await screen.findByRole('status', { name: '当前地址' })).toHaveTextContent('/oem/approvals')
  })

  it('仅有审批权限的合法审批人可从待办进入传递单详情', async () => {
    setInternalSession(['oem:flow_approve'])
    vi.spyOn(http, 'get').mockImplementation(async (url) => {
      if (url === '/oem/transfers/88') return { data: blockedDetail } as never
      throw new Error(`unexpected GET ${String(url)}`)
    })
    const client = createAppQueryClient()
    render(
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/oem/transfers/88']}><App /></MemoryRouter>
      </QueryClientProvider>,
    )

    expect(await screen.findByText('等待处置的传递单')).toBeInTheDocument()
    expect(screen.queryByText('无操作权限')).not.toBeInTheDocument()
  })

  it('仅有异常处置权限时进入受限阻断列表并取得改派和终止操作', async () => {
    const user = userEvent.setup()
    setInternalSession(['oem:approval_recover'])
    const get = vi.spyOn(http, 'get').mockImplementation(async (url, config) => {
      if (url === '/oem/transfers') {
        expect(config?.params).toEqual(expect.objectContaining({ approvalStatus: 'APPROVAL_BLOCKED' }))
        return { data: { list: [blockedSummary], total: 1, page: 1, pageSize: 20 } } as never
      }
      if (url === '/oem/transfers/88') return { data: blockedDetail } as never
      throw new Error(`unexpected GET ${String(url)}`)
    })
    const client = createAppQueryClient()
    render(
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/oem']}><App /></MemoryRouter>
      </QueryClientProvider>,
    )

    expect(await screen.findByText('这里只显示尚未发布且审批已阻断的传递单。处置管理员可以改派未完成任务或终止传递，不能代替审批人通过，也不能读取附件内容。')).toBeInTheDocument()
    await waitFor(() => expect(get).toHaveBeenCalledWith('/oem/transfers', expect.objectContaining({ params: expect.objectContaining({ approvalStatus: 'APPROVAL_BLOCKED' }) })))
    await user.click(screen.getByRole('button', { name: '处置' }))
    expect(await screen.findByRole('button', { name: '终止传递' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '改派' })).toBeInTheDocument()
    expect(screen.getByText('异常处置权限不包含附件内容；此页仅显示完成改派或终止所需的审批任务信息。')).toBeInTheDocument()
  })

  it('供应商账号即使收到异常权限字符串也不能进入 OEM 内部恢复区', async () => {
    useAuth.setState({
      user: { ...internalUser, userType: 'SUPPLIER', supplierId: 3 },
      permissions: ['oem:approval_recover'],
    })
    const client = createAppQueryClient()
    render(
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/oem/recovery']}><App /></MemoryRouter>
      </QueryClientProvider>,
    )

    expect(await screen.findByText('OEM 内部入口仅供公司员工使用')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'OEM 文件传递' })).not.toBeInTheDocument()
  })

  it('OEM 厂商门户会话不能用于内部异常恢复路由', async () => {
    useAuth.setState({ token: null, user: null, permissions: [], menus: [], booted: true, generation: 12 })
    usePortalAuth.setState({
      token: 'portal-token',
      account: {
        id: 31,
        employeeNo: 'OEM31',
        realName: '厂商用户',
        email: 'oem31@example.test',
        companyId: 9,
        companyName: '测试代工厂',
      },
      mustChangePassword: false,
      booted: true,
      generation: 3,
    })
    const client = createAppQueryClient()
    render(
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/oem/recovery']}><App /><CurrentLocation /></MemoryRouter>
      </QueryClientProvider>,
    )

    await waitFor(() => expect(screen.getByRole('status', { name: '当前地址' })).toHaveTextContent('/login'))
    expect(screen.queryByText('审批阻断处置')).not.toBeInTheDocument()
  })

  it.each([
    ['仅恢复权限', ['oem:approval_recover']],
    ['创建加恢复但无全局查看', ['oem:transfer_create', 'oem:approval_recover']],
  ])('%s终止成功后提示并返回受限恢复列表', async (_label, permissions) => {
    const user = userEvent.setup()
    setInternalSession(permissions)
    vi.spyOn(http, 'get').mockImplementation(async (url) => {
      if (url === '/oem/transfers/88') return { data: blockedDetail } as never
      if (url === '/oem/transfers') return { data: { list: [], total: 0, page: 1, pageSize: 20 } } as never
      throw new Error(`unexpected GET ${String(url)}`)
    })
    const post = vi.spyOn(http, 'post').mockResolvedValue({
      data: { ...blockedDetail, summary: { ...blockedSummary, lifecycleStatus: 'CANCELLED' } },
    } as never)
    const client = createAppQueryClient()
    render(
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/oem/transfers/88']}><App /><CurrentLocation /></MemoryRouter>
      </QueryClientProvider>,
    )

    await user.click(await screen.findByRole('button', { name: '终止传递' }))
    await user.type(await screen.findByPlaceholderText('请输入终止原因（发送人将收到通知）'), '审批人离职，终止旧传递')
    await user.click(await screen.findByRole('button', { name: '确定' }))

    await waitFor(() => expect(post).toHaveBeenCalledWith('/oem/transfers/88/cancel', {
      reason: '审批人离职，终止旧传递', version: 4,
    }))
    expect(await screen.findByText('传递已终止')).toBeInTheDocument()
    await waitFor(() => expect(screen.getByRole('status', { name: '当前地址' })).toHaveTextContent('/oem/recovery'))
  })

  it('同时有传递查看和恢复权限的管理员终止后仍留在详情', async () => {
    const user = userEvent.setup()
    setInternalSession(['oem:transfer_view', 'oem:approval_recover'])
    vi.spyOn(http, 'get').mockResolvedValue({ data: blockedDetail } as never)
    const post = vi.spyOn(http, 'post').mockResolvedValue({
      data: { ...blockedDetail, summary: { ...blockedSummary, lifecycleStatus: 'CANCELLED' } },
    } as never)
    const client = createAppQueryClient()
    render(
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/oem/transfers/88']}><App /><CurrentLocation /></MemoryRouter>
      </QueryClientProvider>,
    )

    await user.click(await screen.findByRole('button', { name: '终止传递' }))
    await user.type(await screen.findByPlaceholderText('请输入终止原因（发送人将收到通知）'), '管理员终止')
    await user.click(await screen.findByRole('button', { name: '确定' }))

    await waitFor(() => expect(post).toHaveBeenCalled())
    expect(screen.getByRole('status', { name: '当前地址' })).toHaveTextContent('/oem/transfers/88')
    expect(screen.getByText('等待处置的传递单')).toBeInTheDocument()
  })

  it('完整 App 保留内部双向传递路由并使用当前内部 HTTP 会话', async () => {
    setInternalSession(['oem:transfer_create', 'oem:transfer_view'])
    const get = vi.spyOn(http, 'get').mockImplementation(async (url) => {
      if (url === '/oem/transfers') return { data: { list: [], total: 0, page: 1, pageSize: 20 } } as never
      throw new Error(`unexpected GET ${String(url)}`)
    })
    const client = createAppQueryClient()

    render(
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/oem/transfers']}>
          <App />
        </MemoryRouter>
      </QueryClientProvider>,
    )

    expect(await screen.findByText('公司发出')).toBeInTheDocument()
    expect(screen.getByText('OEM 发来')).toBeInTheDocument()
    await waitFor(() => expect(get).toHaveBeenCalledWith('/oem/transfers', expect.objectContaining({ params: expect.any(Object) })))
  })

  it('厂商门户登录不依赖内部登录态', async () => {
    useAuth.setState({ token: null, user: null, permissions: [], menus: [], booted: true, generation: 12 })
    const client = createAppQueryClient()
    render(
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/oem-portal/login']}>
          <App />
        </MemoryRouter>
      </QueryClientProvider>,
    )

    expect(await screen.findByText('OEM 文件传递 · 厂商登录')).toBeInTheDocument()
    expect(screen.getByLabelText('登录账号')).toBeInTheDocument()
  })

  it('厂商门户不启动或同步内部认证域', async () => {
    useAuth.setState({
      token: null,
      user: internalUser,
      permissions: ['oem:transfer_create'],
      menus: [],
      mustChangePassword: false,
      booted: false,
      generation: 12,
    })
    usePortalAuth.setState({
      token: 'portal-token',
      account: {
        id: 31,
        employeeNo: 'OEM31',
        realName: '厂商用户',
        email: 'oem31@example.test',
        companyId: 9,
        companyName: '测试代工厂',
      },
      mustChangePassword: false,
      booted: true,
      generation: 3,
    })
    const internalRefresh = vi.spyOn(axios, 'post').mockRejectedValue(new Error('内部刷新不应执行'))
    vi.spyOn(portalHttp, 'get').mockImplementation(async (url) => {
      if (url === '/oem/transfers') return { data: { list: [], total: 0, page: 1, pageSize: 20 } } as never
      throw new Error(`unexpected GET ${String(url)}`)
    })
    const client = createAppQueryClient()

    render(
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/oem-portal/transfers']}>
          <App />
          <CurrentLocation />
        </MemoryRouter>
      </QueryClientProvider>,
    )

    expect(await screen.findByPlaceholderText('搜索传递记录')).toBeInTheDocument()
    expect(internalRefresh).not.toHaveBeenCalled()
    expect(useAuth.getState()).toMatchObject({ user: internalUser, token: null, booted: false })

    act(() => {
      window.dispatchEvent(new StorageEvent('storage', {
        key: 'yf-auth',
        newValue: JSON.stringify({ state: { user: null }, version: 1 }),
      }))
    })

    expect(screen.getByLabelText('当前地址')).toHaveTextContent('/oem-portal/transfers')
    expect(useAuth.getState().user).toEqual(internalUser)
    expect(usePortalAuth.getState()).toMatchObject({ token: 'portal-token', account: { id: 31 } })
  })

  it('内部页面仍同步其他标签的内部退出', async () => {
    setInternalSession(['oem:transfer_create'])
    vi.spyOn(http, 'get').mockImplementation(async (url) => {
      if (url === '/oem/transfers') return { data: { list: [], total: 0, page: 1, pageSize: 20 } } as never
      throw new Error(`unexpected GET ${String(url)}`)
    })
    const client = createAppQueryClient()
    render(
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/oem/transfers']}><App /></MemoryRouter>
      </QueryClientProvider>,
    )
    expect(await screen.findByText('公司发出')).toBeInTheDocument()

    window.history.replaceState({}, '', '/login')
    act(() => {
      window.dispatchEvent(new StorageEvent('storage', {
        key: 'yf-auth',
        newValue: JSON.stringify({ state: { user: null }, version: 1 }),
      }))
    })

    expect(useAuth.getState()).toMatchObject({ token: null, user: null })
  })

  it('已注册的内部监听器在当前地址进入门户后忽略迟到的退出事件', async () => {
    setInternalSession(['oem:transfer_create'])
    vi.spyOn(http, 'get').mockImplementation(async (url) => {
      if (url === '/oem/transfers') return { data: { list: [], total: 0, page: 1, pageSize: 20 } } as never
      throw new Error(`unexpected GET ${String(url)}`)
    })
    const client = createAppQueryClient()
    render(
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/oem/transfers']}><App /></MemoryRouter>
      </QueryClientProvider>,
    )
    expect(await screen.findByText('公司发出')).toBeInTheDocument()

    window.history.replaceState({}, '', '/oem-portal/transfers/2')
    act(() => {
      window.dispatchEvent(new StorageEvent('storage', {
        key: 'yf-auth',
        newValue: JSON.stringify({ state: { user: null }, version: 1 }),
      }))
    })

    expect(useAuth.getState()).toMatchObject({ token: 'internal-token', user: internalUser })
  })

  it('厂商门户允许 OEM 账号创建回传单且不要求选择目标厂商', async () => {
    const user = userEvent.setup()
    usePortalAuth.setState({
      token: 'portal-token',
      account: {
        id: 31,
        employeeNo: 'OEM31',
        realName: '厂商用户',
        email: 'oem31@example.test',
        companyId: 9,
        companyName: '测试代工厂',
      },
      mustChangePassword: false,
      booted: true,
      generation: 3,
    })
    vi.spyOn(portalHttp, 'get').mockImplementation(async (url) => {
      if (url === '/oem/transfers') return { data: { list: [], total: 0, page: 1, pageSize: 20 } } as never
      if (url === '/oem/retention-template-options') return { data: [{ id: 1, name: '保留七天', summary: '发布后七天删除' }] } as never
      throw new Error(`unexpected GET ${String(url)}`)
    })
    const client = createAppQueryClient()
    render(
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/oem-portal/transfers']}>
          <App />
        </MemoryRouter>
      </QueryClientProvider>,
    )

    await user.click(await screen.findByRole('button', { name: '发送文件' }))
    expect(await screen.findByText('拖放文件到这里，或点击选择多个文件')).toBeInTheDocument()
    expect(screen.queryByText('目标 OEM 厂商')).not.toBeInTheDocument()
    expect(screen.queryByText('标题')).not.toBeInTheDocument()
    expect(screen.queryByText('删除策略')).not.toBeInTheDocument()
  })

  it('移动导航按 Escape 关闭后把焦点还给触发按钮', async () => {
    const user = userEvent.setup()
    setInternalSession(['oem:transfer_create'])
    vi.spyOn(http, 'get').mockImplementation(async (url) => {
      if (url === '/oem/transfers') return { data: { list: [], total: 0, page: 1, pageSize: 20 } } as never
      throw new Error(`unexpected GET ${String(url)}`)
    })
    Object.defineProperty(window, 'innerWidth', { configurable: true, value: 390 })
    Object.defineProperty(window, 'innerHeight', { configurable: true, value: 600 })
    const client = createAppQueryClient()
    render(
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/oem/transfers']}><App /></MemoryRouter>
      </QueryClientProvider>,
    )
    const trigger = await screen.findByRole('button', { name: '打开导航菜单' })

    await user.click(trigger)
    const drawer = await waitFor(() => {
      const element = document.querySelector<HTMLElement>('.mobile-nav-drawer')
      expect(element).not.toBeNull()
      return element!
    })
    expect(trigger).toHaveAttribute('aria-expanded', 'true')
    expect(document.activeElement).not.toBe(document.body)

    fireEvent.keyDown(drawer, { key: 'Escape', code: 'Escape', keyCode: 27, which: 27 })

    await waitFor(() => expect(document.querySelector('.mobile-nav-drawer')).toBeNull())
    await waitFor(() => expect(trigger).toHaveFocus())
    expect(trigger).toHaveAttribute('aria-expanded', 'false')
  })
})
