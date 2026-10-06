import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const hashMocks = vi.hoisted(() => ({ fileMd5: vi.fn() }))
vi.mock('../../api/file-hash', () => ({ fileMd5: hashMocks.fileMd5 }))

import { createAppQueryClient } from '../../api/queryClient'
import type { OemApi } from '../../oem/api/OemApi'
import type { Company, Page, TransferDetail } from '../../oem/api/types'
import { usePortalAuth } from '../../oem/api/portalSession'
import OemUploader from '../../oem/components/OemUploader'
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

describe('OEM 上传队列的中止与失效会话', () => {
  beforeEach(() => {
    hashMocks.fileMd5.mockResolvedValue('md5')
  })

  it('卸载时中止正在上传的分片', async () => {
    const user = userEvent.setup()
    let signal: AbortSignal | undefined
    const putChunk = vi.fn().mockImplementation((_id: string, _index: number, _blob: Blob, abort: AbortSignal) => {
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
    expect(api.merge).toHaveBeenCalledWith('session-1')
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
    expect(api.merge).toHaveBeenCalledWith('new')
  })
})

describe('OEM 传递单详情加载失败', () => {
  it('非 404 错误显示可重试的错误页而不是无限加载', async () => {
    const user = userEvent.setup()
    const loaded: TransferDetail = {
      summary: {
        id: 41, direction: 'INTERNAL_TO_OEM', companyId: 9, companyName: '甲厂', title: '重试后的传递单',
        sender: { realm: 'internal', id: 7, employeeNo: 'A0007', realName: '发送人' },
        lifecycleStatus: 'RELEASED', approvalStatus: 'APPROVED', approvalBlockedReason: null, validationSummary: 'VALID',
        fileCount: 0, totalBytes: 0, availableCount: 0, purgePendingCount: 0, purgedCount: 0, missingCount: 0,
        createdAt: '2026-10-05T00:00:00Z', sentAt: '2026-10-05T00:01:00Z', releasedAt: '2026-10-05T00:02:00Z', version: 3,
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

describe('OEM 厂商门户登录页跳转', () => {
  const account = { id: 31, employeeNo: 'OEM31', realName: '厂商用户', email: 'oem31@example.test', companyId: 9, companyName: '测试代工厂' }

  afterEach(() => {
    usePortalAuth.setState({ token: null, account: null, mustChangePassword: false, booted: true, generation: 0 })
  })

  function renderPortal(path: string) {
    return render(
      <MemoryRouter initialEntries={[path]}>
        <Routes>
          <Route path="/oem-portal/login" element={<><PortalLoginPage /><LocationProbe /></>} />
          <Route path="/oem-portal/change-password" element={<><PortalChangePasswordPage /><LocationProbe /></>} />
          <Route path="*" element={<LocationProbe />} />
        </Routes>
      </MemoryRouter>,
    )
  }

  it('恢复登录失败后修改密码页跳回登录页', async () => {
    usePortalAuth.setState({ token: null, account: null, mustChangePassword: false, booted: true, generation: 0 })
    renderPortal('/oem-portal/change-password')
    await waitFor(() => expect(screen.getByLabelText('当前路径')).toHaveTextContent('/oem-portal/login'))
    expect(screen.getByText('OEM 文件传递 · 厂商登录')).toBeInTheDocument()
  })

  it('已登录用户访问登录页时直接进入门户', async () => {
    usePortalAuth.setState({ token: 'portal-token', account, mustChangePassword: false, booted: true, generation: 1 })
    renderPortal('/oem-portal/login')
    await waitFor(() => expect(screen.getByLabelText('当前路径')).toHaveTextContent('/oem-portal/transfers'))
  })

  it('修改密码页按统一密码策略校验新密码', async () => {
    const user = userEvent.setup()
    usePortalAuth.setState({ token: 'portal-token', account, mustChangePassword: true, booted: true, generation: 1 })
    renderPortal('/oem-portal/change-password')
    await user.type(screen.getByLabelText('当前密码'), 'Old#Pass1')
    await user.type(screen.getByLabelText('新密码'), 'password1')
    await user.type(screen.getByLabelText('确认新密码'), 'password1')
    await user.click(screen.getByRole('button', { name: '保存' }))
    expect(await screen.findByText('密码需 6-20 个字符，不能使用常见弱密码或简单重复序列')).toBeInTheDocument()
  })
})

describe('OEM 管理列表的过期响应', () => {
  function company(id: number, name: string): Company {
    return {
      id, name, contactName: null, contactPhone: null, contactEmail: null, remark: null, status: 'ACTIVE',
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
      <OemProvider value={context({ companies } as Partial<OemApi>, ['oem:company_manage'])}>
        <CompaniesPage />
      </OemProvider>,
    )
    await waitFor(() => expect(companies).toHaveBeenCalledTimes(1))
    const search = screen.getByPlaceholderText('搜索厂商名称')
    await user.type(search, '新')
    fireEvent.keyDown(search, { key: 'Enter', keyCode: 13, which: 13 })
    await waitFor(() => expect(companies).toHaveBeenCalledTimes(2))
    expect(companies).toHaveBeenLastCalledWith(expect.objectContaining({ keyword: '新' }))
    expect(await screen.findByText('新结果厂')).toBeInTheDocument()

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
      <OemProvider value={context({ companies } as Partial<OemApi>, ['oem:company_manage'])}>
        <CompaniesPage />
      </OemProvider>,
    )
    expect(await screen.findByText('厂商列表加载失败：数据库忙')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: '重试' }))
    expect(await screen.findByText('重试厂')).toBeInTheDocument()
  })
})
