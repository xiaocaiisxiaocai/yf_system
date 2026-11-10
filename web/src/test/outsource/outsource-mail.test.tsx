import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import http from '../../api/client'
import OutsourceMailsPage from '../../outsource/pages/OutsourceMailsPage'
import SendMailDialog from '../../outsource/pages/SendMailDialog'
import { useAuth } from '../../store/auth'

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
  afterEach(() => {
    vi.restoreAllMocks()
    useAuth.setState({ token: null, user: null, permissions: [], menus: [] })
  })

  it('二次确认逐封列出收件人与默认抄送，另加的抄送只加在对应那封', async () => {
    const user = userEvent.setup()
    setSession(['outsource:view', 'outsource:manage'])
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
    await user.clear(within(notice).getByLabelText('鼎亚 另加外部抄送'))
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
    setSession(['outsource:view', 'outsource:manage'])
    vi.spyOn(http, 'post').mockRejectedValue({ response: { status: 400, data: { message: '工令 A1 的外发类型与厂商不符' } } })
    render(<SendMailDialog itemIds={[1]} onClose={() => undefined} onSent={() => undefined} />)
    expect(await screen.findByText('工令 A1 的外发类型与厂商不符')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '确认发送' })).toBeDisabled()
  })

  it('发送记录显示状态与被拒地址，失败的邮件可重新发送', async () => {
    const user = userEvent.setup()
    setSession(['outsource:view', 'outsource:manage'])
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
    await user.click(within(row).getByRole('button', { name: '重新发送' }))
    await waitFor(() => expect(post).toHaveBeenCalledWith('/outsource/mails/5/retry'))
  })
})
