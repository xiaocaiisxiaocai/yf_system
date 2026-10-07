import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const hashMocks = vi.hoisted(() => ({ fileMd5: vi.fn() }))
vi.mock('../../api/file-hash', () => ({
  fileMd5: hashMocks.fileMd5,
  createChunkHasher: () => ({ sha256: async () => 'a'.repeat(64), dispose: () => undefined }),
}))

import { createAppQueryClient } from '../../api/queryClient'
import type { OemApi } from '../../oem/api/OemApi'
import type { Realm, TransferDetail } from '../../oem/api/types'
import { OemProvider } from '../../oem/OemContext'
import TransferListPage from '../../oem/pages/TransferListPage'

function detail(id: number, lifecycleStatus: TransferDetail['summary']['lifecycleStatus'] = 'DRAFT', version = 1): TransferDetail {
  return {
    summary: {
      id,
      direction: 'INTERNAL_TO_OEM',
      companyId: 9,
      companyName: '甲厂',
      title: '图纸.zip',
      sender: { realm: 'internal', id: 7, employeeNo: 'A0007', realName: '发送人' },
      lifecycleStatus,
      approvalStatus: lifecycleStatus === 'DRAFT' ? null : 'PENDING',
      approvalBlockedReason: null,
      validationSummary: 'PENDING',
      fileCount: lifecycleStatus === 'DRAFT' ? 2 : 2,
      totalBytes: 6,
      availableCount: 0,
      purgePendingCount: 0,
      purgedCount: 0,
      missingCount: 0,
      createdAt: '2026-10-05T00:00:00Z',
      sentAt: lifecycleStatus === 'DRAFT' ? null : '2026-10-05T00:01:00Z',
      releasedAt: null,
      version,
    },
    description: null,
    retention: { templateId: 1, templateName: '默认', mode: 'KEEP', releaseTtlMinutes: null, receiptGraceMinutes: null, summary: '永久保留' },
    manifestSha256: null,
    expiresAt: null,
    closedReason: null,
    closedAt: null,
    capabilities: { canEdit: lifecycleStatus === 'DRAFT', canSend: lifecycleStatus === 'DRAFT', canDelete: lifecycleStatus === 'DRAFT', canReadContent: true, contentPurpose: 'SENDER' },
    files: [],
    approval: null,
  }
}

function createApi(overrides: Partial<OemApi> = {}): OemApi {
  return {
    listTransfers: vi.fn().mockResolvedValue({ list: [], total: 0, page: 1, pageSize: 20 }),
    companyOptions: vi.fn().mockResolvedValue([{ id: 9, name: '甲厂', canReceive: true, unavailableReason: null }]),
    createTransfer: vi.fn().mockResolvedValue(detail(41)),
    initUpload: vi.fn().mockImplementation((_id: number, body: { fileName: string }) => Promise.resolve({
      sessionId: body.fileName,
      chunkSize: 16,
      totalChunks: 1,
      uploadedChunks: [],
    })),
    putChunk: vi.fn().mockResolvedValue(undefined),
    merge: vi.fn().mockResolvedValue({}),
    abortUpload: vi.fn().mockResolvedValue(undefined),
    transfer: vi.fn().mockResolvedValue(detail(41, 'DRAFT', 4)),
    send: vi.fn().mockResolvedValue(detail(41, 'SEALED', 5)),
    ...overrides,
  } as unknown as OemApi
}

function LocationProbe() {
  return <output aria-label="当前路径">{useLocation().pathname}</output>
}

function renderList(api: OemApi, realm: Realm = 'internal') {
  const base = realm === 'internal' ? '/oem' : '/oem-portal'
  return render(
    <QueryClientProvider client={createAppQueryClient()}>
      <MemoryRouter initialEntries={[`${base}/transfers`]}>
        <OemProvider value={{
          api,
          realm,
          base,
          permissions: realm === 'internal' ? new Set(['oem:transfer_create']) : new Set(),
          userId: realm === 'internal' ? 7 : 31,
          queryScope: [realm, 1],
        }}>
          <Routes>
            <Route path={`${base}/transfers`} element={<><TransferListPage /><LocationProbe /></>} />
            <Route path={`${base}/transfers/:id`} element={<LocationProbe />} />
          </Routes>
        </OemProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

async function openInternalDialog(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByRole('button', { name: '发送文件' }))
  const dialog = await screen.findByRole('dialog')
  await user.click(within(dialog).getByRole('combobox'))
  fireEvent.click((await screen.findAllByText('甲厂')).at(-1)!)
  return dialog
}

async function choose(dialog: HTMLElement, user: ReturnType<typeof userEvent.setup>, ...names: string[]) {
  const files = names.map((name) => new File(['abc'], name, { type: 'application/octet-stream' }))
  await user.upload(within(dialog).getByLabelText('选择文件'), files)
  await waitFor(() => expect(within(dialog).getByRole('button', { name: '提交审批' })).toBeEnabled())
}

describe('OEM 一键发送文件', () => {
  beforeEach(() => {
    hashMocks.fileMd5.mockResolvedValue('md5')
  })

  it('没有启用账号的厂商在下拉中禁用并显示原因', async () => {
    const user = userEvent.setup()
    const api = createApi({
      companyOptions: vi.fn().mockResolvedValue([{
        id: 9,
        name: '甲厂',
        canReceive: false,
        unavailableReason: '该厂商没有启用的登录账号，请先新增或启用账号后再发送',
      }]),
    } as Partial<OemApi>)
    renderList(api)

    await user.click(await screen.findByRole('button', { name: '发送文件' }))
    const dialog = await screen.findByRole('dialog')
    await user.click(within(dialog).getByRole('combobox'))
    const option = await screen.findByRole('option', { name: /甲厂.*该厂商没有启用的登录账号/ })
    expect(option).toHaveClass('arco-select-option-disabled')
    expect(await within(dialog).findByText('至少需要一个启用的 OEM 登录账号才能发送。')).toBeInTheDocument()
  })

  it('所有厂商都没有启用账号时阻止提交', async () => {
    const user = userEvent.setup()
    const api = createApi({
      companyOptions: vi.fn().mockResolvedValue([
        { id: 9, name: '甲厂', canReceive: false, unavailableReason: '该厂商没有启用的登录账号，请先新增或启用账号后再发送' },
        { id: 10, name: '乙厂', canReceive: false, unavailableReason: '该厂商没有启用的登录账号，请先新增或启用账号后再发送' },
      ]),
    } as Partial<OemApi>)
    renderList(api)

    await user.click(await screen.findByRole('button', { name: '发送文件' }))
    const dialog = await screen.findByRole('dialog')
    await user.upload(within(dialog).getByLabelText('选择文件'), new File(['abc'], '阻止.pdf'))
    const submit = within(dialog).getByRole('button', { name: '提交审批' })
    expect(await within(dialog).findByText('当前没有可接收的 OEM 厂商，请先新增或启用至少一个 OEM 登录账号后再发送。')).toBeInTheDocument()
    expect(submit).toBeDisabled()
    expect(api.createTransfer).not.toHaveBeenCalled()
  })

  it('只选厂商和多个文件即可依次建草稿、断点上传、读最新版本并提交审批', async () => {
    const user = userEvent.setup()
    const api = createApi()
    renderList(api)
    const dialog = await openInternalDialog(user)
    expect(within(dialog).queryByText('标题')).not.toBeInTheDocument()
    expect(within(dialog).queryByText('删除策略')).not.toBeInTheDocument()
    await choose(dialog, user, '图纸.pdf', '模型.step')

    await user.click(within(dialog).getByRole('button', { name: '提交审批' }))

    await waitFor(() => expect(screen.getByLabelText('当前路径')).toHaveTextContent('/oem/transfers/41'))
    expect(api.createTransfer).toHaveBeenCalledTimes(1)
    expect(api.createTransfer).toHaveBeenCalledWith({ oemCompanyId: 9 })
    expect(api.initUpload).toHaveBeenCalledTimes(2)
    expect(api.transfer).toHaveBeenCalledWith(41)
    expect(api.send).toHaveBeenCalledWith(41, 4)
  })

  it('部分上传失败时不提交，重试只上传失败文件且不重复创建草稿', async () => {
    const user = userEvent.setup()
    const merge = vi.fn()
      .mockResolvedValueOnce({})
      .mockRejectedValueOnce({ response: { data: { message: '网络中断' } } })
      .mockResolvedValueOnce({})
    const api = createApi({ merge } as Partial<OemApi>)
    renderList(api)
    const dialog = await openInternalDialog(user)
    await choose(dialog, user, '成功.pdf', '失败.step')

    await user.click(within(dialog).getByRole('button', { name: '提交审批' }))
    expect(await within(dialog).findByText('部分文件上传失败。已上传成功的文件不会重复，请修复失败项后再次提交。')).toBeInTheDocument()
    expect(api.send).not.toHaveBeenCalled()
    expect(api.createTransfer).toHaveBeenCalledTimes(1)

    await user.click(within(dialog).getByRole('button', { name: '提交审批' }))
    await waitFor(() => expect(screen.getByLabelText('当前路径')).toHaveTextContent('/oem/transfers/41'))
    expect(api.createTransfer).toHaveBeenCalledTimes(1)
    expect(api.initUpload).toHaveBeenCalledTimes(2)
    expect((api.initUpload as ReturnType<typeof vi.fn>).mock.calls.map((call) => call[1].fileName)).toEqual(['成功.pdf', '失败.step'])
    expect(merge).toHaveBeenNthCalledWith(2, '失败.step')
    expect(merge).toHaveBeenNthCalledWith(3, '失败.step')
    expect(api.send).toHaveBeenCalledTimes(1)
  })

  it('取消本地文件选择不创建草稿，快速双击也只执行一次提交', async () => {
    const user = userEvent.setup()
    let resolveCreate!: (value: TransferDetail) => void
    const createTransfer = vi.fn().mockImplementation(() => new Promise<TransferDetail>((resolve) => { resolveCreate = resolve }))
    const api = createApi({ createTransfer } as Partial<OemApi>)
    renderList(api)
    let dialog = await openInternalDialog(user)
    await choose(dialog, user, '取消.pdf')
    await user.click(within(dialog).getByRole('button', { name: '取消' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(createTransfer).not.toHaveBeenCalled()

    dialog = await openInternalDialog(user)
    await choose(dialog, user, '提交.pdf')
    const submit = within(dialog).getByRole('button', { name: '提交审批' })
    await user.dblClick(submit)
    expect(createTransfer).toHaveBeenCalledTimes(1)
    resolveCreate(detail(41))
    await waitFor(() => expect(screen.getByLabelText('当前路径')).toHaveTextContent('/oem/transfers/41'))
    expect(api.send).toHaveBeenCalledTimes(1)
  })

  it('OEM 回传无需选择厂商，发送结果未知时先查状态并避免重复发送', async () => {
    const user = userEvent.setup()
    const inboundDraft = detail(52, 'DRAFT', 3)
    inboundDraft.summary.direction = 'OEM_TO_INTERNAL'
    const inboundSealed = detail(52, 'SEALED', 4)
    inboundSealed.summary.direction = 'OEM_TO_INTERNAL'
    const transfer = vi.fn()
      .mockResolvedValueOnce(inboundDraft)
      .mockResolvedValueOnce(inboundSealed)
    const send = vi.fn().mockRejectedValue(new Error('response lost'))
    const api = createApi({
      createTransfer: vi.fn().mockResolvedValue(inboundDraft),
      transfer,
      send,
    } as Partial<OemApi>)
    renderList(api, 'oem')

    await user.click(await screen.findByRole('button', { name: '发送文件' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).queryByText('目标 OEM 厂商')).not.toBeInTheDocument()
    await user.upload(within(dialog).getByLabelText('选择文件'), new File(['abc'], '回传.pdf'))
    await user.click(within(dialog).getByRole('button', { name: '发送文件' }))

    await waitFor(() => expect(screen.getByLabelText('当前路径')).toHaveTextContent('/oem-portal/transfers/52'))
    expect(api.createTransfer).toHaveBeenCalledWith({})
    expect(send).toHaveBeenCalledTimes(1)
    expect(transfer).toHaveBeenCalledTimes(2)
  })

  it('草稿创建结果未知时阻止盲目重试，并提示回列表确认', async () => {
    const user = userEvent.setup()
    const createTransfer = vi.fn().mockRejectedValue(new Error('connection reset'))
    const api = createApi({ createTransfer } as Partial<OemApi>)
    renderList(api)
    const dialog = await openInternalDialog(user)
    await choose(dialog, user, '未知.pdf')

    await user.click(within(dialog).getByRole('button', { name: '提交审批' }))
    expect(await within(dialog).findByText('草稿创建结果未知。请关闭窗口并从列表确认，避免重复创建传递单。')).toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: '提交审批' })).toBeDisabled()
    expect(createTransfer).toHaveBeenCalledTimes(1)
    expect(api.initUpload).not.toHaveBeenCalled()
  })

  it('关闭已创建的失败草稿时明确保留，并可直接打开继续', async () => {
    const user = userEvent.setup()
    const api = createApi({ merge: vi.fn().mockRejectedValue(new Error('merge failed')) } as Partial<OemApi>)
    renderList(api)
    const dialog = await openInternalDialog(user)
    await choose(dialog, user, '继续.pdf')
    await user.click(within(dialog).getByRole('button', { name: '提交审批' }))
    await within(dialog).findByText('部分文件上传失败。已上传成功的文件不会重复，请修复失败项后再次提交。')

    await user.click(within(dialog).getByRole('button', { name: '取消' }))
    const keepDialog = await screen.findByText('草稿已保留')
    expect(screen.getByText('已创建的草稿和成功上传的文件会保留，不会自动删除。你可以现在打开草稿继续，或稍后从列表进入。')).toBeInTheDocument()
    await user.click(within(keepDialog.closest('[role="dialog"]')!).getByRole('button', { name: '打开草稿' }))
    await waitFor(() => expect(screen.getByLabelText('当前路径')).toHaveTextContent('/oem/transfers/41'))
  })

  it('移除分片上传失败项时先取消服务端会话，避免草稿被活动上传阻塞', async () => {
    const user = userEvent.setup()
    const api = createApi({ putChunk: vi.fn().mockRejectedValue(new Error('chunk failed')) } as Partial<OemApi>)
    renderList(api)
    const dialog = await openInternalDialog(user)
    await choose(dialog, user, '分片失败.pdf')
    await user.click(within(dialog).getByRole('button', { name: '提交审批' }))
    const remove = await within(dialog).findByRole('button', { name: '移除「分片失败.pdf」' })

    await user.click(remove)

    await waitFor(() => expect(api.abortUpload).toHaveBeenCalledWith('分片失败.pdf'))
    await waitFor(() => expect(within(dialog).queryByText('分片失败.pdf')).not.toBeInTheDocument())
    expect(api.send).not.toHaveBeenCalled()
  })

  it('重试已创建草稿前先核对状态，已提交时不再上传或发送', async () => {
    const user = userEvent.setup()
    const merge = vi.fn().mockRejectedValue(new Error('merge response lost'))
    const alreadySubmitted = detail(41, 'SEALED', 5)
    const transfer = vi.fn().mockResolvedValue(alreadySubmitted)
    const api = createApi({ merge, transfer } as Partial<OemApi>)
    renderList(api)
    const dialog = await openInternalDialog(user)
    await choose(dialog, user, '状态确认.pdf')
    await user.click(within(dialog).getByRole('button', { name: '提交审批' }))
    await within(dialog).findByText('部分文件上传失败。已上传成功的文件不会重复，请修复失败项后再次提交。')

    await user.click(within(dialog).getByRole('button', { name: '提交审批' }))

    await waitFor(() => expect(screen.getByLabelText('当前路径')).toHaveTextContent('/oem/transfers/41'))
    expect(transfer).toHaveBeenCalledTimes(1)
    expect(merge).toHaveBeenCalledTimes(1)
    expect(api.send).not.toHaveBeenCalled()
  })

  it('已有草稿的厂商不再可接收时给出打开或删除草稿的出路，删除后可重新选择厂商', async () => {
    const user = userEvent.setup()
    const companyOptions = vi.fn()
      .mockResolvedValueOnce([{ id: 9, name: '甲厂', canReceive: true, unavailableReason: null }])
      .mockResolvedValue([
        { id: 9, name: '甲厂', canReceive: false, unavailableReason: '甲厂账号已停用' },
        { id: 10, name: '乙厂', canReceive: true, unavailableReason: null },
      ])
    const merge = vi.fn().mockRejectedValueOnce(new Error('merge failed')).mockResolvedValue({})
    const deleteDraft = vi.fn().mockResolvedValue(undefined)
    const api = createApi({ companyOptions, merge, deleteDraft } as Partial<OemApi>)
    renderList(api)
    const dialog = await openInternalDialog(user)
    await choose(dialog, user, '换厂商.pdf')
    await user.click(within(dialog).getByRole('button', { name: '提交审批' }))
    await within(dialog).findByText('部分文件上传失败。已上传成功的文件不会重复，请修复失败项后再次提交。')

    await user.click(within(dialog).getByRole('button', { name: '提交审批' }))

    expect(await within(dialog).findByText('草稿的目标厂商当前无法接收文件：甲厂账号已停用')).toBeInTheDocument()
    expect(companyOptions).toHaveBeenCalledTimes(2)
    expect(merge).toHaveBeenCalledTimes(1)
    expect(api.send).not.toHaveBeenCalled()
    expect(within(dialog).getByRole('button', { name: '提交审批' })).toBeDisabled()
    expect(within(dialog).getByRole('button', { name: '打开草稿' })).toBeEnabled()

    await user.click(within(dialog).getByRole('button', { name: '删除草稿并重新选择' }))

    await waitFor(() => expect(deleteDraft).toHaveBeenCalledWith(41, 4))
    await waitFor(() => expect(within(dialog).queryByText(/草稿的目标厂商当前无法接收文件/)).not.toBeInTheDocument())
    expect(within(dialog).getByRole('combobox')).not.toHaveClass('arco-select-disabled')
    expect(within(dialog).getByText('等待提交')).toBeInTheDocument()
  })

  it('已有草稿重新核对厂商失败时保留原选项，只在厂商区域提示一次且不继续上传', async () => {
    const user = userEvent.setup()
    const companyOptions = vi.fn()
      .mockResolvedValueOnce([{ id: 9, name: '甲厂', canReceive: true, unavailableReason: null }])
      .mockRejectedValueOnce({ response: { data: { message: '厂商服务暂不可用' } } })
    const merge = vi.fn().mockRejectedValueOnce(new Error('merge failed')).mockResolvedValue({})
    const api = createApi({ companyOptions, merge } as Partial<OemApi>)
    renderList(api)
    const dialog = await openInternalDialog(user)
    await choose(dialog, user, '核对失败.pdf')
    await user.click(within(dialog).getByRole('button', { name: '提交审批' }))
    await within(dialog).findByText('部分文件上传失败。已上传成功的文件不会重复，请修复失败项后再次提交。')

    await user.click(within(dialog).getByRole('button', { name: '提交审批' }))

    expect(await within(dialog).findByText('目标 OEM 厂商加载失败：厂商服务暂不可用')).toBeInTheDocument()
    expect(within(dialog).getAllByText(/厂商服务暂不可用/)).toHaveLength(1)
    // The chosen vendor keeps its name instead of collapsing to a bare id.
    // Scope to the select's rendered value: the option list may still be closing and contain the same text.
    expect(dialog.querySelector('.arco-select-view-value')).toHaveTextContent('甲厂')
    expect(merge).toHaveBeenCalledTimes(1)
    expect(api.send).not.toHaveBeenCalled()
  })

  it('标题单元格是指向详情的链接，键盘也能进入', async () => {
    const api = createApi({
      listTransfers: vi.fn().mockResolvedValue({ list: [detail(41, 'SEALED').summary], total: 1, page: 1, pageSize: 20 }),
    } as Partial<OemApi>)
    renderList(api)
    expect(await screen.findByRole('link', { name: '图纸.zip' })).toHaveAttribute('href', '/oem/transfers/41')
  })
})
