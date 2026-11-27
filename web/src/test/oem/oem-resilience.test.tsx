import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const hashMocks = vi.hoisted(() => ({ fileMd5: vi.fn() }))
vi.mock('../../api/file-hash', () => ({
  fileMd5: hashMocks.fileMd5,
  createChunkHasher: () => ({ sha256: async () => 'a'.repeat(64), dispose: () => undefined }),
}))

import { Message } from '@arco-design/web-react'
import { createAppQueryClient } from '../../api/queryClient'
import { hasActiveUpload, retainActiveUpload } from '../../components/uploadActivity'
import type { OemApi } from '../../oem/api/OemApi'
import type { Company, Page, TransferDetail } from '../../oem/api/types'
import { portalHttp, usePortalAuth } from '../../oem/api/portalSession'
import OemUploader from '../../oem/components/OemUploader'
import { PortalLayout } from '../../oem/layouts/OemLayouts'
import { OemProvider } from '../../oem/OemContext'
import CompaniesPage from '../../oem/pages/admin/CompaniesPage'
import { PortalChangePasswordPage, PortalLoginPage } from '../../oem/pages/portal/PortalAuthPages'
import TransferDetailPage from '../../oem/pages/TransferDetailPage'

function context(api: Partial<OemApi>, permissions: string[] = []) {
  return { api: api as OemApi, realm: 'internal' as const, base: '/oem', permissions: new Set(permissions), userId: 7, queryScope: ['test'] }
}

function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((ok) => { resolve = ok })
  return { promise, resolve }
}

function LocationProbe() {
  return <output aria-label="当前路径">{useLocation().pathname}</output>
}

function uploadApi(overrides: Partial<OemApi> = {}): Partial<OemApi> {
  return {
    initUpload: vi.fn().mockResolvedValue({ sessionId: 'session-1', chunkSize: 16, totalChunks: 1, uploadedChunks: [] }),
    putChunk: vi.fn().mockResolvedValue(undefined),
    merge: vi.fn().mockResolvedValue({}),
    abortUpload: vi.fn().mockResolvedValue(undefined),
    ...overrides,
  } as Partial<OemApi>
}

function renderUploader(api: Partial<OemApi>) {
  return render(
    <OemProvider value={context(api)}>
      <OemUploader transferId={41} />
    </OemProvider>,
  )
}

describe('ODM/OEMA 上传队列的中止与失效会话', () => {
  beforeEach(() => {
    hashMocks.fileMd5.mockResolvedValue('md5')
  })

  it('卸载时中止正在上传的分片', async () => {
    const user = userEvent.setup()
    let signal: AbortSignal | undefined
    const putChunk = vi.fn().mockImplementation((_id: string, _index: number, _blob: Blob, _sha256: string, abort: AbortSignal) => {
      signal = abort
      return new Promise(() => undefined)
    })
    const view = renderUploader(uploadApi({ putChunk } as Partial<OemApi>))
    await user.upload(screen.getByLabelText('选择文件'), new File(['abc'], '卸载.pdf'))
    await waitFor(() => expect(putChunk).toHaveBeenCalledTimes(1))
    expect(signal?.aborted).toBe(false)

    view.unmount()

    expect(signal?.aborted).toBe(true)
  })

  it('网络瞬断时自动重试分片', async () => {
    const user = userEvent.setup()
    const putChunk = vi.fn()
      .mockRejectedValueOnce(Object.assign(new Error('Network Error'), { code: 'ERR_NETWORK' }))
      .mockResolvedValue(undefined)
    const api = uploadApi({ putChunk } as Partial<OemApi>)
    renderUploader(api)
    await user.upload(screen.getByLabelText('选择文件'), new File(['abc'], '瞬断.pdf'))

    expect(await screen.findByText('上传完成', {}, { timeout: 3000 })).toBeInTheDocument()
    expect(putChunk).toHaveBeenCalledTimes(2)
    expect(api.merge).toHaveBeenCalledWith('session-1', expect.any(AbortSignal))
  })

  it('服务端已不存在的会话在移除时直接清掉本地项', async () => {
    const user = userEvent.setup()
    const api = uploadApi({
      putChunk: vi.fn().mockRejectedValue(new Error('chunk failed')),
      abortUpload: vi.fn().mockRejectedValue({ response: { status: 404, data: { message: '会话不存在' } } }),
    } as Partial<OemApi>)
    renderUploader(api)
    await user.upload(screen.getByLabelText('选择文件'), new File(['abc'], '过期.pdf'))
    await user.click(await screen.findByRole('button', { name: '移除「过期.pdf」' }))

    await waitFor(() => expect(screen.queryByText('过期.pdf')).not.toBeInTheDocument())
    expect(api.abortUpload).toHaveBeenCalledWith('session-1')
  })

  it('详情页失败项可单独重试，会话已失效时重新发起上传', async () => {
    const user = userEvent.setup()
    const initUpload = vi.fn()
      .mockResolvedValueOnce({ sessionId: 'old', chunkSize: 16, totalChunks: 1, uploadedChunks: [] })
      .mockResolvedValueOnce({ sessionId: 'new', chunkSize: 16, totalChunks: 1, uploadedChunks: [] })
    const putChunk = vi.fn()
      .mockRejectedValueOnce(new Error('chunk failed'))
      .mockRejectedValueOnce({ response: { status: 404 } })
      .mockResolvedValue(undefined)
    const api = uploadApi({ initUpload, putChunk } as Partial<OemApi>)
    renderUploader(api)
    await user.upload(screen.getByLabelText('选择文件'), new File(['abc'], '重试.pdf'))

    await user.click(await screen.findByRole('button', { name: '重试「重试.pdf」' }))

    expect(await screen.findByText('上传完成')).toBeInTheDocument()
    expect(initUpload).toHaveBeenCalledTimes(2)
    expect(putChunk.mock.calls.map((call) => call[0])).toEqual(['old', 'old', 'new'])
    expect(api.merge).toHaveBeenCalledWith('new', expect.any(AbortSignal))
  })

  it('上传期间登记活动上传，并在哈希进度更新及取消时释放且不弹错误提示', async () => {
    const toast = vi.spyOn(Message, 'error').mockImplementation(() => () => undefined)
    const user = userEvent.setup()
    let resolveMd5!: (value: string) => void
    let onProgress: ((fraction: number) => void) | undefined
    hashMocks.fileMd5.mockImplementation((_file: Blob, _cancelled: () => boolean, progress?: (fraction: number) => void) => {
      onProgress = progress
      return new Promise<string>((resolve) => { resolveMd5 = resolve })
    })
    let chunkSignal: AbortSignal | undefined
    const putChunk = vi.fn().mockImplementation((_id: string, _index: number, _blob: Blob, _sha256: string, signal: AbortSignal) => {
      chunkSignal = signal
      return new Promise<void>((_resolve, reject) => {
        signal.addEventListener('abort', () => reject(Object.assign(new Error('canceled'), { code: 'ERR_CANCELED' })), { once: true })
      })
    })
    const api = uploadApi({ putChunk } as Partial<OemApi>)
    expect(hasActiveUpload()).toBe(false)
    const view = renderUploader(api)
    await user.upload(screen.getByLabelText('选择文件'), new File(['abc'], '大文件.pdf'))

    await waitFor(() => expect(hasActiveUpload()).toBe(true))
    act(() => { onProgress?.(0.45) })
    expect(await screen.findByText('正在校验文件 45%')).toBeInTheDocument()

    act(() => { resolveMd5('md5-value') })
    await waitFor(() => expect(putChunk).toHaveBeenCalledTimes(1))
    expect(api.initUpload).toHaveBeenCalledWith(41, expect.objectContaining({ fileName: '大文件.pdf', fileMd5: 'md5-value' }), expect.any(AbortSignal))
    expect(chunkSignal?.aborted).toBe(false)

    await user.click(screen.getByRole('button', { name: '取消「大文件.pdf」' }))
    expect(chunkSignal?.aborted).toBe(true)
    await waitFor(() => expect(api.abortUpload).toHaveBeenCalledWith('session-1'))
    await waitFor(() => expect(screen.queryByText('大文件.pdf')).not.toBeInTheDocument())
    await waitFor(() => expect(hasActiveUpload()).toBe(false))
    expect(toast).not.toHaveBeenCalled()
    view.unmount()
    toast.mockRestore()
  })
})

describe('ODM/OEMA 传递单详情加载失败', () => {
  it('非 404 错误显示可重试的错误页而不是无限加载', async () => {
    const user = userEvent.setup()
    const loaded: TransferDetail = {
      summary: {
        id: 41, direction: 'INTERNAL_TO_OEM', companyId: 9, companyName: '甲厂', title: '重试后的传递单',
        sender: { realm: 'internal', id: 7, employeeNo: 'A0007', realName: '发送人' }, signerName: null, recipientNames: [],
        lifecycleStatus: 'RELEASED', approvalStatus: 'APPROVED', approvalBlockedReason: null, validationSummary: 'VALID',
        fileCount: 0, totalBytes: 0, availableCount: 0, purgePendingCount: 0, purgedCount: 0, missingCount: 0, downloadableCount: 0, previewableCount: 0,
        createdAt: '2026-10-05T00:00:00Z', sentAt: '2026-10-05T00:01:00Z', releasedAt: '2026-10-05T00:02:00Z', purgeDueAt: null, version: 3,
      },
      description: null,
      retention: { templateId: 1, templateName: '默认', mode: 'KEEP', releaseTtlMinutes: null, receiptGraceMinutes: null, summary: '永久保留' },
      manifestSha256: null, expiresAt: null, closedReason: null, closedAt: null,
      capabilities: { canEdit: false, canSend: false, canDelete: false, canReadContent: true, contentPurpose: 'SENDER' },
      files: [], approval: null,
    }
    const transfer = vi.fn()
      .mockRejectedValueOnce({ response: { status: 500, data: { message: '服务错误' } } })
      .mockResolvedValue(loaded)
    render(
      <QueryClientProvider client={createAppQueryClient()}>
        <MemoryRouter initialEntries={['/oem/transfers/41']}>
          <OemProvider value={context({ transfer } as Partial<OemApi>, ['oem:transfer_view'])}>
            <Routes><Route path="/oem/transfers/:id" element={<TransferDetailPage />} /></Routes>
          </OemProvider>
        </MemoryRouter>
      </QueryClientProvider>,
    )

    expect(await screen.findByText('传递单加载失败')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: '重试' }))
    expect(await screen.findByText('重试后的传递单')).toBeInTheDocument()
  })
})

describe('ODM/OEMA 厂商门户登录页跳转', () => {
  const account = { id: 31, employeeNo: 'OEM31', companyId: 9, companyName: '测试代工厂' }

  afterEach(() => {
    vi.restoreAllMocks()
    usePortalAuth.setState({ token: null, account: null, mustChangePassword: false, booted: true, bootUnavailable: false, generation: 0 })
  })

  function renderPortal(entry: string | { pathname: string; search?: string; state?: unknown }) {
    return render(
      <MemoryRouter initialEntries={[entry]}>
        <Routes>
          <Route path="/oem-portal/login" element={<><PortalLoginPage /><LocationProbe /></>} />
          <Route path="/oem-portal/change-password" element={<><PortalChangePasswordPage /><LocationProbe /></>} />
          <Route path="/oem-portal" element={<PortalLayout />}>
            <Route path="transfers" element={<><div>门户列表</div><LocationProbe /></>} />
          </Route>
          <Route path="*" element={<LocationProbe />} />
        </Routes>
      </MemoryRouter>,
    )
  }

  it('恢复登录失败后修改密码页跳回登录页', async () => {
    usePortalAuth.setState({ token: null, account: null, mustChangePassword: false, booted: true, bootUnavailable: false, generation: 0 })
    renderPortal('/oem-portal/change-password')
    await waitFor(() => expect(screen.getByLabelText('当前路径')).toHaveTextContent('/oem-portal/login'))
    expect(screen.getByLabelText('SAA ODM/OEM工令协作')).toBeInTheDocument()
  })

  it('已登录用户访问登录页时直接进入门户，并校验安全且属于门户的回跳地址', async () => {
    usePortalAuth.setState({ token: 'portal-token', account, mustChangePassword: false, booted: true, generation: 1 })
    const unsafe = renderPortal({ pathname: '/oem-portal/login', state: { from: '//evil.invalid' } })
    await waitFor(() => expect(screen.getByLabelText('当前路径')).toHaveTextContent('/oem-portal/transfers'))
    unsafe.unmount()

    const internal = renderPortal({ pathname: '/oem-portal/login', state: { from: '/projects/1' } })
    await waitFor(() => expect(screen.getByLabelText('当前路径')).toHaveTextContent('/oem-portal/transfers'))
    internal.unmount()

    function FullLocationProbe() {
      const loc = useLocation()
      return <output aria-label="完整地址">{`${loc.pathname}${loc.search}`}</output>
    }
    render(
      <MemoryRouter initialEntries={[{ pathname: '/oem-portal/login', state: { from: '/oem-portal/transfers?page=2' } }]}>
        <Routes>
          <Route path="/oem-portal/login" element={<PortalLoginPage />} />
          <Route path="*" element={<FullLocationProbe />} />
        </Routes>
      </MemoryRouter>,
    )
    await waitFor(() => expect(screen.getByLabelText('完整地址')).toHaveTextContent('/oem-portal/transfers?page=2'))
  })

  it('PortalLoginPage 与 PortalChangePasswordPage 捕获非 Axios 异常并提示错误', async () => {
    const user = userEvent.setup()
    const toast = vi.spyOn(Message, 'error').mockImplementation(() => () => undefined)
    vi.spyOn(portalHttp, 'post').mockResolvedValueOnce({ data: { invalid: true } } as never)

    usePortalAuth.setState({ token: null, account: null, mustChangePassword: false, booted: true, generation: 0 })
    const loginView = renderPortal('/oem-portal/login')
    await user.type(screen.getByLabelText('登录账号'), 'OEM31')
    await user.type(screen.getByLabelText('密码'), 'Old#Pass1')
    await user.click(screen.getByRole('button', { name: '登录' }))
    await waitFor(() => expect(toast).toHaveBeenCalledWith('ODM/OEMA 登录响应格式无效'))
    loginView.unmount()

    toast.mockClear()
    vi.spyOn(portalHttp, 'put').mockRejectedValueOnce(new Error('登录状态协调超时，请稍后重试'))
    usePortalAuth.setState({ token: 'portal-token', account, mustChangePassword: true, booted: true, generation: 1 })
    renderPortal('/oem-portal/change-password')
    await user.type(screen.getByLabelText('当前密码'), 'Old#Pass1')
    await user.type(screen.getByLabelText('新密码'), 'New#Pass9')
    await user.type(screen.getByLabelText('确认新密码'), 'New#Pass9')
    await user.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(toast).toHaveBeenCalledWith('登录状态协调超时，请稍后重试'))
  })

  it('PortalLayout 在 bootUnavailable 时显示恢复界面，未登录跳转保留查询参数，且退出前确认进行中的上传', async () => {
    const user = userEvent.setup()
    usePortalAuth.setState({ token: null, account, mustChangePassword: false, booted: true, bootUnavailable: true, generation: 2 })
    const recovery = renderPortal('/oem-portal/transfers?page=3')
    expect(screen.getByText('暂时无法恢复登录状态')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: '重新登录' }))
    await waitFor(() => expect(screen.getByLabelText('当前路径')).toHaveTextContent('/oem-portal/login'))
    recovery.unmount()

    // Preserves location.search in navigation state when redirecting to portal login.
    function StateProbe() {
      const loc = useLocation()
      return <output aria-label="来源地址">{String((loc.state as { from?: string } | null)?.from ?? '')}</output>
    }
    usePortalAuth.setState({ token: null, account: null, mustChangePassword: false, booted: true, bootUnavailable: false, generation: 2 })
    const preserved = render(
      <MemoryRouter initialEntries={['/oem-portal/transfers?page=3&tab=sent']}>
        <Routes>
          <Route path="/oem-portal/login" element={<StateProbe />} />
          <Route path="/oem-portal" element={<PortalLayout />}>
            <Route path="transfers" element={<div>门户列表</div>} />
          </Route>
        </Routes>
      </MemoryRouter>,
    )
    await waitFor(() => expect(screen.getByLabelText('来源地址')).toHaveTextContent('/oem-portal/transfers?page=3&tab=sent'))
    preserved.unmount()

    // Active upload prompts before portal logout.
    usePortalAuth.setState({ token: 'portal-token', account, mustChangePassword: false, booted: true, bootUnavailable: false, generation: 3 })
    const endUpload = retainActiveUpload()
    try {
      renderPortal('/oem-portal/transfers')
      await user.click(screen.getByRole('button', { name: 'OEM31' }))
      fireEvent.click((await screen.findAllByText('退出登录')).at(-1)!.closest('.arco-dropdown-menu-item')!)
      expect(await screen.findByText('文件仍在上传')).toBeInTheDocument()
      await user.click(screen.getByRole('button', { name: '继续上传' }))
      expect(usePortalAuth.getState().token).toBe('portal-token')
    } finally {
      endUpload()
    }
  })

  it('PortalLayout 在 mustChangePassword 为 true 时自动导航至门户改密页', async () => {
    usePortalAuth.setState({ token: 'portal-token', account, mustChangePassword: true, booted: true, bootUnavailable: false, generation: 4 })
    renderPortal('/oem-portal/transfers')
    await waitFor(() => expect(screen.getByLabelText('当前路径')).toHaveTextContent('/oem-portal/change-password'))
  })

  it('修改密码页按统一密码策略校验新密码', async () => {
    const user = userEvent.setup()
    usePortalAuth.setState({ token: 'portal-token', account, mustChangePassword: true, booted: true, generation: 1 })
    renderPortal('/oem-portal/change-password')
    await user.type(screen.getByLabelText('当前密码'), 'Old#Pass1')
    await user.type(screen.getByLabelText('新密码'), 'password1')
    await user.type(screen.getByLabelText('确认新密码'), 'password1')
    await user.click(screen.getByRole('button', { name: '保存' }))
    expect(await screen.findByText('密码需 5-20 个字符，不能使用常见弱密码或简单重复序列')).toBeInTheDocument()
  })

  it('PortalChangePasswordPage 按钮采用 large 尺寸，当前密码自动聚焦，无历史记录时返回门户列表', async () => {
    const user = userEvent.setup()
    window.history.replaceState({ idx: 0 }, '', '/oem-portal/change-password')
    usePortalAuth.setState({ token: 'portal-token', account, mustChangePassword: false, booted: true, generation: 1 })
    renderPortal('/oem-portal/change-password')

    expect(screen.getByLabelText('当前密码')).toHaveFocus()
    const backBtn = screen.getByRole('button', { name: '返回' })
    const saveBtn = screen.getByRole('button', { name: '保存' })
    expect(backBtn).toHaveClass('arco-btn-size-large')
    expect(saveBtn).toHaveClass('arco-btn-size-large')

    await user.click(backBtn)
    await waitFor(() => expect(screen.getByLabelText('当前路径')).toHaveTextContent('/oem-portal/transfers'))
  })
})

describe('ODM/OEMA 管理列表的过期响应', () => {
  function company(id: number, name: string): Company {
    return {
      id, name, remark: null, status: 'ACTIVE',
      accountCount: 0, activeAccountCount: 0, createdAt: '2026-10-05T00:00:00Z', updatedAt: '2026-10-05T00:00:00Z',
    }
  }

  it('先发出的慢请求不会覆盖后一次搜索结果', async () => {
    const user = userEvent.setup()
    const first = deferred<Page<Company>>()
    const companies = vi.fn()
      .mockImplementationOnce(() => first.promise)
      .mockResolvedValue({ list: [company(2, '新结果厂')], total: 1, page: 1, pageSize: 20 })
    render(
      <OemProvider value={context({ companies, contacts: vi.fn().mockResolvedValue([]) } as Partial<OemApi>, ['oem:company_manage'])}>
        <MemoryRouter><CompaniesPage /></MemoryRouter>
      </OemProvider>,
    )
    await waitFor(() => expect(companies).toHaveBeenCalledTimes(1))
    const search = screen.getByPlaceholderText('厂商名称')
    await user.type(search, '新')
    fireEvent.keyDown(search, { key: 'Enter', keyCode: 13, which: 13 })
    await waitFor(() => expect(companies).toHaveBeenCalledTimes(2))
    expect(companies).toHaveBeenLastCalledWith(expect.objectContaining({ keyword: '新' }))
    expect(await within(document.querySelector('.oem-directory-table')!).findByText('新结果厂')).toBeInTheDocument()

    first.resolve({ list: [company(1, '旧结果厂')], total: 1, page: 1, pageSize: 20 })
    await new Promise((resolve) => setTimeout(resolve, 20))

    expect(screen.queryByText('旧结果厂')).not.toBeInTheDocument()
    expect(within(document.querySelector('.oem-directory-table')!).getByText('新结果厂')).toBeInTheDocument()
  })

  it('列表加载失败时显示错误并可重试', async () => {
    const user = userEvent.setup()
    const companies = vi.fn()
      .mockRejectedValueOnce({ response: { data: { message: '数据库忙' } } })
      .mockResolvedValue({ list: [company(3, '重试厂')], total: 1, page: 1, pageSize: 20 })
    render(
      <OemProvider value={context({ companies, contacts: vi.fn().mockResolvedValue([]) } as Partial<OemApi>, ['oem:company_manage'])}>
        <MemoryRouter><CompaniesPage /></MemoryRouter>
      </OemProvider>,
    )
    expect(await screen.findByText('厂商列表加载失败：数据库忙')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: '重试' }))
    expect(await within(document.querySelector('.oem-directory-table')!).findByText('重试厂')).toBeInTheDocument()
  })
})
