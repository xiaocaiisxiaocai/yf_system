import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Modal } from '@arco-design/web-react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import http from '../../api/client'
import OutsourceMailsPage from '../../outsource/pages/OutsourceMailsPage'
import SendMailDialog from '../../outsource/pages/SendMailDialog'
import { useAuth } from '../../store/auth'

function deferred<T>() {
  let resolve!: (value: T | PromiseLike<T>) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((ok, fail) => { resolve = ok; reject = fail })
  return { promise, resolve, reject }
}

function setSession(permissions: string[]) {
  useAuth.setState({
    token: 'token',
    user: { id: 7, employeeNo: 'buyer', realName: '采购', email: 'buyer@example.test', userType: 'INTERNAL', supplierId: null, isSystemAdmin: false },
    permissions, menus: [], mustChangePassword: false, booted: true, generation: 1,
  })
}

const preview = {
  fingerprint: 'abc',
  mails: [
    {
      key: 'NOTICE:1', kind: 'NOTICE', vendorId: 1, vendorName: '鼎亚', subject: '【外发通知】鼎亚 2026-11-10（共 2 笔）',
      to: [{ email: 'd1@vendor.test', name: null }], defaultCc: [{ email: 'pm@example.test', name: '生管' }],
      itemIds: [1, 2], workOrderNos: ['A1', 'B1'], customerCount: 2, htmlBody: '<table></table>',
    },
    {
      key: 'WITHDRAW:2', kind: 'WITHDRAW', vendorId: 2, vendorName: '多普', subject: '【外发撤回】多普 2026-11-10（共 1 笔）',
      to: [{ email: 'p1@vendor.test', name: null }], defaultCc: [{ email: 'pm@example.test', name: '生管' }],
      itemIds: [2], workOrderNos: ['B1'], customerCount: 1, htmlBody: '<table></table>',
    },
  ],
}

describe('外协邮件发送', () => {
  const originalHidden = Object.getOwnPropertyDescriptor(document, 'hidden')
  const originalVisibility = Object.getOwnPropertyDescriptor(document, 'visibilityState')

  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
    if (originalHidden) Object.defineProperty(document, 'hidden', originalHidden)
    else delete (document as unknown as Record<string, unknown>).hidden
    if (originalVisibility) Object.defineProperty(document, 'visibilityState', originalVisibility)
    else delete (document as unknown as Record<string, unknown>).visibilityState
    useAuth.setState({ token: null, user: null, permissions: [], menus: [] })
  })

  it('二次确认逐封列出收件人与默认抄送，另加的抄送只加在对应那封', async () => {
    const user = userEvent.setup()
    setSession(['outsource:manage', 'outsource:send'])
    const post = vi.spyOn(http, 'post').mockImplementation((url: string) => Promise.resolve({
      data: url === '/outsource/mails/preview' ? preview : { mails: [] },
    } as never))
    const onSent = vi.fn()
    render(<SendMailDialog itemIds={[1, 2]} onClose={() => undefined} onSent={onSent} />)

    const notice = await screen.findByRole('region', { name: '鼎亚 外发通知' })
    expect(within(notice).getByText('d1@vendor.test')).toBeInTheDocument()
    expect(within(notice).getByText('生管')).toBeInTheDocument()
    expect(screen.getByRole('region', { name: '多普 撤回通知' })).toBeInTheDocument()
    expect(screen.getByText('外发通知 1 封，撤回通知 1 封')).toBeInTheDocument()

    await user.type(within(notice).getByLabelText('鼎亚 另加外部抄送'), 'not-an-email')
    await user.click(within(notice).getByRole('button', { name: '加入' }))
    expect(within(notice).queryByText('not-an-email')).not.toBeInTheDocument()
    expect(within(notice).getByRole('alert')).toHaveTextContent('请输入有效的邮箱地址')
    await user.clear(within(notice).getByLabelText('鼎亚 另加外部抄送'))
    expect(within(notice).queryByRole('alert')).not.toBeInTheDocument()
    await user.type(within(notice).getByLabelText('鼎亚 另加外部抄送'), 'boss@client.test')
    await user.click(within(notice).getByRole('button', { name: '加入' }))
    expect(within(notice).getByText('boss@client.test')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: '确认发送 2 封' }))
    // The mail has two customers and an external copy: one more confirmation.
    await user.click(await screen.findByRole('button', { name: '仍要发送' }))
    await waitFor(() => expect(post).toHaveBeenCalledWith('/outsource/mails/send', expect.objectContaining({
      itemIds: [1, 2], fingerprint: 'abc',
      extraCc: [{ key: 'NOTICE:1', userIds: [], emails: ['boss@client.test'] }],
    })))
    expect(onSent).toHaveBeenCalled()
  })

  it('预览被拒时显示原因，不能确认', async () => {
    setSession(['outsource:manage', 'outsource:send'])
    vi.spyOn(http, 'post').mockRejectedValue({ response: { status: 400, data: { message: '工令 A1 的外发类型与厂商不符' } } })
    render(<SendMailDialog itemIds={[1]} onClose={() => undefined} onSent={() => undefined} />)
    expect(await screen.findByText('工令 A1 的外发类型与厂商不符')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '确认发送' })).toBeDisabled()
  })

  it('发送记录显示状态与被拒地址，失败的邮件可重新发送', async () => {
    const user = userEvent.setup()
    setSession(['outsource:manage', 'outsource:send'])
    const mail = {
      id: 5, batchKey: 'k', kind: 'NOTICE', vendorId: 1, vendorName: '鼎亚', subject: '【外发通知】鼎亚', status: 'FAILED',
      recipients: [
        { kind: 'TO', email: 'd1@vendor.test', name: null, source: 'VENDOR', rejected: true },
        { kind: 'CC', email: 'pm@example.test', name: '生管', source: 'DEFAULT_CC', rejected: false },
      ],
      workOrderNos: ['A1'], attachmentName: '外发清单_鼎亚_20261110.xlsx', attempts: 1, lastError: '厂商收件地址全部被拒收',
      createdByName: '采购', createdAt: '2026-11-10T01:00:00Z', sentAt: null,
    }
    vi.spyOn(http, 'get').mockImplementation((url: string) => Promise.resolve({
      data: url === '/outsource/mails' ? { list: [mail], total: 1, page: 1, pageSize: 20 } : [],
    } as never))
    const post = vi.spyOn(http, 'post').mockResolvedValue({ data: { ...mail, status: 'QUEUED' } } as never)
    render(<MemoryRouter><OutsourceMailsPage /></MemoryRouter>)

    const row = (await screen.findByText('厂商收件地址全部被拒收')).closest('tr')!
    expect(within(row).getByText('失败')).toBeInTheDocument()
    expect(within(row).getByText('（拒收 1）')).toBeInTheDocument()
    await user.click(within(row).getByRole('button', { name: '详情' }))
    const detailDialog = await screen.findByRole('dialog', { name: '邮件发送详情' })
    expect(within(detailDialog).getByText('d1@vendor.test（被拒收）')).toBeInTheDocument()
    expect(within(detailDialog).getByText('外发清单_鼎亚_20261110.xlsx')).toBeInTheDocument()
    await user.click(within(row).getByRole('button', { name: '重新发送' }))
    await waitFor(() => expect(post).toHaveBeenCalledWith('/outsource/mails/5/retry'))
  })

  it('显示厂商发起的两种转迅得电控邮件，失败可重发', async () => {
    const user = userEvent.setup()
    setSession(['outsource:send'])
    const handoff = {
      id: 6, kind: 'ELECTRICAL_HANDOFF', vendorId: 1, vendorName: '鼎亚', subject: '【转迅得电控】鼎亚 工令 A1', status: 'SENT',
      recipients: [], workOrderNos: ['A1'], attachmentName: null, attempts: 1, lastError: null,
      createdByName: '鼎亚（签名：王小明）', createdAt: '2026-11-24T01:00:00Z', sentAt: '2026-11-24T01:00:01Z',
    }
    const withdrawn = {
      ...handoff, id: 7, kind: 'ELECTRICAL_HANDOFF_WITHDRAW', subject: '【撤回转迅得电控】鼎亚 工令 A1', status: 'FAILED', sentAt: null,
      createdByName: '鼎亚（签名：李小美）',
    }
    vi.spyOn(http, 'get').mockImplementation((url: string) => Promise.resolve({
      data: url === '/outsource/mails' ? { list: [handoff, withdrawn], total: 2, page: 1, pageSize: 20 } : [],
    } as never))
    const post = vi.spyOn(http, 'post').mockResolvedValue({ data: { ...withdrawn, status: 'QUEUED' } } as never)
    render(<MemoryRouter><OutsourceMailsPage /></MemoryRouter>)

    const sent = within((await screen.findByText(handoff.subject)).closest('tr')!)
    const failed = within(screen.getByText(withdrawn.subject).closest('tr')!)
    expect(sent.getByText('转迅得电控')).toBeInTheDocument()
    expect(sent.getByText(handoff.createdByName)).toBeInTheDocument()
    expect(failed.getByText('撤回转迅得电控')).toBeInTheDocument()
    expect(failed.getByRole('button', { name: '附件' })).toBeDisabled()
    await user.click(failed.getByRole('button', { name: '详情' }))
    const dialog = within(await screen.findByRole('dialog', { name: '邮件发送详情' }))
    expect(dialog.getByText(withdrawn.createdByName)).toBeInTheDocument()
    expect(dialog.getByText('无附件')).toBeInTheDocument()
    await user.click(failed.getByRole('button', { name: '重新发送' }))
    await waitFor(() => expect(post).toHaveBeenCalledWith('/outsource/mails/7/retry'))
  })

  it('另加内部抄送禁用未填写邮箱的人员并标注提示，有邮箱人员可选', async () => {
    const user = userEvent.setup()
    setSession(['outsource:manage', 'outsource:send'])
    vi.spyOn(http, 'post').mockResolvedValue({ data: preview } as never)
    vi.spyOn(http, 'get').mockImplementation((url: string) => Promise.resolve({
      data: url === '/outsource/settings/cc-user-options'
        ? [
          { userId: 101, realName: '无邮箱员工', employeeNo: 'E101', email: '' },
          { userId: 102, realName: '有邮箱员工', employeeNo: 'E102', email: 'staff@example.test' },
        ]
        : [],
    } as never))

    render(<SendMailDialog itemIds={[1, 2]} onClose={() => undefined} onSent={() => undefined} />)
    const notice = await screen.findByRole('region', { name: '鼎亚 外发通知' })
    const select = within(notice).getByLabelText('鼎亚 另加内部抄送')
    await user.click(select)

    const disabledOption = await screen.findByRole('option', { name: '无邮箱员工（未填写邮箱）' })
    expect(disabledOption).toHaveClass('arco-select-option-disabled')
    const enabledOption = screen.getByRole('option', { name: '有邮箱员工（staff@example.test）' })
    expect(enabledOption).not.toHaveClass('arco-select-option-disabled')

    fireEvent.click(disabledOption)
    expect(within(notice).queryByText('无邮箱员工')).not.toBeInTheDocument()

    fireEvent.click(enabledOption)
    expect(await within(notice).findByText('有邮箱员工')).toBeInTheDocument()
  })

  it('快速连点确认发送不会堆叠多个外部抄送确认框，取消后仍可再次发送', async () => {
    const user = userEvent.setup()
    setSession(['outsource:manage', 'outsource:send'])
    const post = vi.spyOn(http, 'post').mockImplementation((url: string) => Promise.resolve({
      data: url === '/outsource/mails/preview' ? preview : { mails: [] },
    } as never))
    const confirmCalls: Array<{ onOk?: () => void; onCancel?: () => void }> = []
    const confirmSpy = vi.spyOn(Modal, 'confirm').mockImplementation((config) => {
      confirmCalls.push(config as { onOk?: () => void; onCancel?: () => void })
      return { close: vi.fn(), update: vi.fn() } as never
    })
    const onSent = vi.fn()

    render(<SendMailDialog itemIds={[1, 2]} onClose={() => undefined} onSent={onSent} />)
    const notice = await screen.findByRole('region', { name: '鼎亚 外发通知' })
    await user.type(within(notice).getByLabelText('鼎亚 另加外部抄送'), 'boss@client.test')
    await user.click(within(notice).getByRole('button', { name: '加入' }))

    const sendButton = screen.getByRole('button', { name: '确认发送 2 封' })
    fireEvent.click(sendButton)
    fireEvent.click(sendButton)
    fireEvent.click(sendButton)

    expect(confirmSpy).toHaveBeenCalledTimes(1)

    // Cancelling releases the in-flight guard so the user can retry sending.
    await act(async () => { confirmCalls[0].onCancel?.() })
    expect(post).toHaveBeenCalledTimes(1)

    fireEvent.click(sendButton)
    expect(confirmSpy).toHaveBeenCalledTimes(2)
    await act(async () => { confirmCalls[1].onOk?.() })
    await waitFor(() => expect(onSent).toHaveBeenCalledTimes(1))
  })

  it('排队中邮件采用串行轮询（慢响应不丢弃、切后台暂停）且键盘聚焦可展开收件人气泡', async () => {
    vi.useFakeTimers()
    Object.defineProperty(document, 'hidden', { configurable: true, value: false })
    Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'visible' })
    setSession(['outsource:manage', 'outsource:send'])

    const queuedMail = {
      id: 9, batchKey: 'k9', kind: 'NOTICE', vendorId: 1, vendorName: '鼎亚', subject: '【外发通知】排队中', status: 'QUEUED',
      recipients: [{ kind: 'TO', email: 'd1@vendor.test', name: '窗口甲', source: 'VENDOR', rejected: false }],
      workOrderNos: ['A1'], attachmentName: null, attempts: 0, lastError: null,
      createdByName: '采购', createdAt: '2026-11-10T01:00:00Z', sentAt: null,
    }
    const slowPoll = deferred<{ data: { list: Array<typeof queuedMail>; total: number; page: number; pageSize: number } }>()
    let mailGets = 0
    vi.spyOn(http, 'get').mockImplementation((url: string) => {
      if (url === '/outsource/settings/vendors') return Promise.resolve({ data: [] } as never)
      if (url === '/outsource/mails') {
        mailGets += 1
        if (mailGets === 1) return Promise.resolve({ data: { list: [queuedMail], total: 1, page: 1, pageSize: 20 } } as never)
        if (mailGets === 2) return slowPoll.promise as never
        return Promise.resolve({ data: { list: [{ ...queuedMail, status: 'SENT' }], total: 1, page: 1, pageSize: 20 } } as never)
      }
      throw new Error(`unexpected GET ${url}`)
    })

    render(<MemoryRouter><OutsourceMailsPage /></MemoryRouter>)
    await act(async () => { await vi.advanceTimersByTimeAsync(10) })
    expect(screen.getByText('排队中')).toBeInTheDocument()
    expect(mailGets).toBe(1)

    // Keyboard focus on the recipient count opens the Popover (FE-23).
    const trigger = screen.getByText('1 人')
    fireEvent.focus(trigger)
    await act(async () => { await vi.advanceTimersByTimeAsync(300) })
    expect(screen.getByText('窗口甲 <d1@vendor.test>')).toBeInTheDocument()

    // First background poll starts at 5s and takes >10s: must NOT overlap or discard the slow response.
    await act(async () => { await vi.advanceTimersByTimeAsync(5000) })
    expect(mailGets).toBe(2)
    await act(async () => { await vi.advanceTimersByTimeAsync(12000) })
    expect(mailGets).toBe(2)

    // Resolving the slow response updates state and chains the next poll 5s later.
    await act(async () => {
      slowPoll.resolve({ data: { list: [{ ...queuedMail, status: 'SENDING' }], total: 1, page: 1, pageSize: 20 } })
      await Promise.resolve()
    })
    expect(screen.getByText('发送中')).toBeInTheDocument()

    // Hiding the page pauses polling; becoming visible resumes immediately.
    Object.defineProperty(document, 'hidden', { configurable: true, value: true })
    Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'hidden' })
    fireEvent(document, new Event('visibilitychange'))
    await act(async () => { await vi.advanceTimersByTimeAsync(15000) })
    expect(mailGets).toBe(2)

    Object.defineProperty(document, 'hidden', { configurable: true, value: false })
    Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'visible' })
    fireEvent(document, new Event('visibilitychange'))
    await act(async () => { await vi.advanceTimersByTimeAsync(10) })
    expect(mailGets).toBe(3)
    expect(screen.getByText('已寄出')).toBeInTheDocument()
  })

  it('点击重新发送仅旋转重新发送按钮，不会误让附件按钮进入加载态', async () => {
    const user = userEvent.setup()
    setSession(['outsource:manage', 'outsource:send'])
    const mail = {
      id: 11, batchKey: 'k11', kind: 'NOTICE', vendorId: 1, vendorName: '鼎亚', subject: '【外发通知】鼎亚重发测试', status: 'FAILED',
      recipients: [{ kind: 'TO', email: 'd1@vendor.test', name: null, source: 'VENDOR', rejected: true }],
      workOrderNos: ['A1'], attachmentName: '外发清单_鼎亚_20261110.xlsx', attempts: 1, lastError: '发送失败',
      createdByName: '采购', createdAt: '2026-11-10T01:00:00Z', sentAt: null,
    }
    const pendingDownload = deferred<{ data: Blob }>()
    vi.spyOn(http, 'get').mockImplementation((url: string) => {
      if (url === '/outsource/mails') return Promise.resolve({ data: { list: [mail], total: 1, page: 1, pageSize: 20 } } as never)
      if (url === '/outsource/mails/11/attachment') return pendingDownload.promise as never
      return Promise.resolve({ data: [] } as never)
    })
    const pendingRetry = deferred<{ data: typeof mail }>()
    vi.spyOn(http, 'post').mockReturnValue(pendingRetry.promise as never)
    render(<MemoryRouter><OutsourceMailsPage /></MemoryRouter>)

    const row = (await screen.findByText('【外发通知】鼎亚重发测试')).closest('tr')!
    const attachmentBtn = within(row).getByRole('button', { name: '附件' })
    const retryBtn = within(row).getByRole('button', { name: '重新发送' })

    await user.click(retryBtn)
    expect(retryBtn).toHaveClass('arco-btn-loading')
    expect(attachmentBtn).not.toHaveClass('arco-btn-loading')
    expect(attachmentBtn).toBeDisabled()

    await act(async () => {
      pendingRetry.resolve({ data: mail })
      await Promise.resolve()
    })
    await waitFor(() => expect(retryBtn).not.toHaveClass('arco-btn-loading'))
    expect(attachmentBtn).toBeEnabled()

    await user.click(attachmentBtn)
    expect(attachmentBtn).toHaveClass('arco-btn-loading')
    expect(retryBtn).not.toHaveClass('arco-btn-loading')
    expect(retryBtn).toBeDisabled()

    await act(async () => {
      pendingDownload.reject(new Error('abort'))
      await Promise.resolve()
    })
    await waitFor(() => expect(attachmentBtn).not.toHaveClass('arco-btn-loading'))
    expect(retryBtn).toBeEnabled()
  })
})
