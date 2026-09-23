import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Message } from '@arco-design/web-react'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({ get: vi.fn(), put: vi.fn() }))

vi.mock('../../../api/client', () => ({ default: { get: mocks.get, put: mocks.put } }))

import SysConfig from '../../../pages/system/SysConfig'

const notificationPolicy = {
  globalEnabled: true,
  internalEnabled: true,
  supplierEnabled: true,
  events: {
    messageCreated: true,
    fileUploaded: true,
    projectSubmitted: true,
    projectConfirmed: true,
    projectRejected: true,
    projectWithdrawn: true,
  },
}

const baseMail = {
  configured: true,
  notificationsEnabled: true,
  notificationPolicy,
  queue: { pending: 0, sending: 0, sent: 0, failed: 0, cancelled: 0 },
  missingEmailCount: 0,
  missingEmailAccounts: [],
  recent: [],
}

const baseSmtp = {
  host: 'smtp.example.invalid',
  port: 465,
  username: 'sender@example.invalid',
  from: 'notice@example.invalid',
  security: 'Auto',
  hasPassword: true,
  configured: true,
  passwordNeedsUpdate: false,
}

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((ok, fail) => { resolve = ok; reject = fail })
  return { promise, resolve, reject }
}

function renderConfig() {
  return render(<MemoryRouter><SysConfig /></MemoryRouter>)
}

async function openSmtp(user: ReturnType<typeof userEvent.setup>) {
  await screen.findByRole('tab', { name: 'SMTP 配置' })
  await user.click(screen.getByRole('tab', { name: 'SMTP 配置' }))
  return screen.findByText('邮件发送')
}

describe('系统参数与 SMTP 生产界面', () => {
  beforeEach(() => {
    mocks.get.mockImplementation(async (url: string) => ({
      data: url.endsWith('/mail-settings') ? baseSmtp : url.endsWith('/mail-status') ? baseMail : [],
    }))
    mocks.put.mockResolvedValue({ data: baseSmtp })
  })

  it('system config exposes mail configuration, queue outcomes and missing mailbox hints', async () => {
    mocks.get.mockImplementation(async (url: string) => {
      if (url === '/admin/system/configs') return { data: [{ key: 'storage.warn_percent', value: '85' }] }
      if (url === '/admin/system/mail-settings') return { data: { ...baseSmtp, configured: false, hasPassword: false } }
      return { data: {
        ...baseMail,
        configured: false,
        queue: { pending: 2, sending: 1, sent: 8, failed: 3, cancelled: 4 },
        latestSentAt: '2026-09-09T01:00:00Z',
        latestFailedAt: '2026-09-09T02:00:00Z',
        missingEmailCount: 1,
        missingEmailAccounts: [{ userId: 7, employeeNo: 'E7', realName: '未填邮箱', userType: 'INTERNAL', status: 'ACTIVE' }],
        recent: [
          { id: 11, action: 'EMAIL_FAILED', targetType: 'email_outbox', targetId: '11', detail: { error: 'SMTP 连接失败' }, createdAt: '2026-09-09T02:00:00Z' },
          { id: 12, action: 'EMAIL_CANCELLED_STALE', targetType: 'email_outbox', targetId: '12', detail: { reason: '项目状态已变化' }, createdAt: '2026-09-09T03:00:00Z' },
          { id: 13, action: 'EMAIL_FUTURE_ACTION', targetType: 'email_outbox', targetId: '13', detail: {}, createdAt: '2026-09-09T04:00:00Z' },
        ],
      } }
    })
    const user = userEvent.setup()
    renderConfig()

    expect(await screen.findByRole('tab', { name: '系统参数' })).toHaveAttribute('aria-selected', 'true')
    expect(screen.queryByText('邮件发送')).not.toBeInTheDocument()
    await openSmtp(user)
    expect(screen.getAllByText('SMTP 未配置')).toHaveLength(2)
    expect(screen.getByText(/待发送/)).toHaveTextContent('2')
    expect(screen.getAllByText((_text, element) => element?.textContent === '失败 3')).toHaveLength(2)
    expect(screen.getByText(/1 个启用账号未填写邮箱/)).toHaveTextContent('未填邮箱（E7）')
    expect(screen.getByText('发送失败')).toBeVisible()
    expect(screen.getByText('已取消：事件失效')).toBeVisible()
    expect(screen.getByText('未知动作：EMAIL_FUTURE_ACTION')).toBeVisible()
  })

  it('SMTP editor preserves authorization codes, retries failed saves, and keeps unrelated edits', async () => {
    let stored = { ...baseSmtp }
    let configValue = 'pdf'
    let fail = true
    const writes: Array<{ url: string; body: Record<string, unknown> }> = []
    mocks.get.mockImplementation(async (url: string) => ({
      data: url.endsWith('/mail-settings') ? stored
        : url.endsWith('/configs') ? [{ key: 'upload.allowed_exts', value: configValue }]
          : baseMail,
    }))
    mocks.put.mockImplementation(async (url: string, body: Record<string, unknown>) => {
      writes.push({ url, body })
      if (fail) throw new Error('simulated save failure')
      if (url.endsWith('/configs')) {
        configValue = (body.items as Array<{ key: string; value: string }>).find((item) => item.key === 'upload.allowed_exts')?.value ?? configValue
        return { data: {} }
      }
      const { password: _password, ...fields } = body
      stored = { ...stored, ...fields, hasPassword: true } as typeof stored
      return { data: stored }
    })
    const user = userEvent.setup()
    renderConfig()
    await openSmtp(user)

    await user.click(screen.getByRole('combobox', { name: 'SMTP 连接加密' }))
    fireEvent.click(await screen.findByText('STARTTLS（通常为 587）'))
    const port = screen.getByRole('spinbutton', { name: 'SMTP 端口' })
    await user.clear(port)
    await user.type(port, '587')
    await user.click(screen.getByRole('switch', { name: '内部员工邮件通知' }))
    await user.click(screen.getByRole('button', { name: '保存邮箱设置' }))
    await waitFor(() => expect(screen.getByRole('button', { name: '保存邮箱设置' })).not.toHaveClass('arco-btn-loading'))
    expect(port).toHaveValue('587')

    fail = false
    await user.click(screen.getByRole('button', { name: '保存邮箱设置' }))
    await waitFor(() => expect(writes.at(-1)).toMatchObject({
      url: '/admin/system/mail-settings',
      body: { password: null, security: 'StartTls', port: 587 },
    }))
    expect(screen.getByRole('switch', { name: '内部员工邮件通知' })).not.toBeChecked()

    const password = screen.getByLabelText('邮箱密码或授权码')
    fireEvent.change(password, { target: { value: 'new-fixture-code' } })
    await user.click(screen.getByRole('button', { name: '保存邮箱设置' }))
    await waitFor(() => expect(writes.at(-1)?.body.password).toBe('new-fixture-code'))
    expect(password).toHaveValue('')

    const beforeIdentityChange = writes.length
    const host = screen.getByLabelText('SMTP 服务器')
    fireEvent.change(host, { target: { value: 'other.example.invalid' } })
    await user.click(screen.getByRole('button', { name: '保存邮箱设置' }))
    expect(writes).toHaveLength(beforeIdentityChange)
    fireEvent.change(password, { target: { value: 'replacement-fixture-code' } })
    await user.click(screen.getByRole('button', { name: '保存邮箱设置' }))
    await waitFor(() => expect(writes.at(-1)).toMatchObject({ body: { host: 'other.example.invalid', password: 'replacement-fixture-code' } }))

    fireEvent.change(password, { target: { value: 'unsaved-mail-code' } })
    await user.click(screen.getByRole('tab', { name: '系统参数' }))
    const allowed = screen.getByLabelText('允许上传类型')
    fireEvent.change(allowed, { target: { value: 'pdf,cfgtest' } })
    const systemCard = screen.getByText('允许上传类型').closest('.arco-card')
    expect(systemCard).not.toBeNull()
    await user.click(within(systemCard as HTMLElement).getByRole('button', { name: '保存' }))
    await waitFor(() => expect(configValue).toBe('pdf,cfgtest'))
    await openSmtp(user)
    expect(screen.getByLabelText('邮箱密码或授权码')).toHaveValue('unsaved-mail-code')
  }, 15_000)

  it('unreadable SMTP authorization code cannot be retained by an empty save', async () => {
    const settings = { ...baseSmtp, from: 'sender@example.invalid', configured: false, passwordNeedsUpdate: true }
    const writes: Array<{ url: string; body: Record<string, unknown> }> = []
    let initialStatusRead = false
    mocks.get.mockImplementation(async (url: string) => {
      if (url.endsWith('/mail-settings')) return { data: settings }
      if (url.endsWith('/configs')) return { data: [] }
      if (!initialStatusRead) {
        initialStatusRead = true
        return { data: baseMail }
      }
      throw new Error('status refresh failed')
    })
    mocks.put.mockImplementation(async (url: string, body: Record<string, unknown>) => {
      writes.push({ url, body })
      return { data: { ...settings, ...body, passwordNeedsUpdate: false, configured: true } }
    })
    const warning = vi.spyOn(Message, 'warning')
    const user = userEvent.setup()
    renderConfig()
    await openSmtp(user)

    expect(screen.getByLabelText('邮箱密码或授权码')).toHaveAttribute('placeholder', '请输入邮箱密码或授权码')
    const port = screen.getByRole('spinbutton', { name: 'SMTP 端口' })
    await user.clear(port)
    await user.type(port, '587')
    await user.click(screen.getByRole('button', { name: '保存邮箱设置' }))
    expect(writes).toHaveLength(0)
    expect(warning).toHaveBeenCalledWith('已保存的授权码无法读取，请重新填写并保存')

    fireEvent.change(screen.getByLabelText('邮箱密码或授权码'), { target: { value: 'replacement-fixture-code' } })
    await user.click(screen.getByRole('button', { name: '保存邮箱设置' }))
    await waitFor(() => expect(writes).toHaveLength(1))
    expect(writes[0].body.password).toBe('replacement-fixture-code')
    expect(warning).toHaveBeenCalledWith('邮箱设置已保存，但邮件状态刷新失败，请稍后刷新页面')
    expect(screen.getByLabelText('邮箱密码或授权码')).toHaveValue('')
    expect(screen.getByLabelText('邮箱密码或授权码')).toHaveAttribute('placeholder', '已设置，留空保留原授权码')
  })

  it('system config freezes edits during save and keeps the committed value when refresh fails', async () => {
    const write = deferred<{ data: unknown }>()
    const refresh = deferred<{ data: unknown }>()
    let statusGets = 0
    mocks.get.mockImplementation(async (url: string) => {
      if (url.endsWith('/configs')) return { data: [] }
      if (url.endsWith('/mail-settings')) return { data: { ...baseSmtp, host: '', username: '', from: '', hasPassword: false, configured: false } }
      statusGets += 1
      if (statusGets === 1) return { data: baseMail }
      return refresh.promise
    })
    mocks.put.mockImplementation(async (url: string, body: { items: Array<{ key: string; value: string }> }) => {
      expect(url).toBe('/admin/system/configs')
      expect(body.items).toContainEqual({ key: 'notify.internal.enabled', value: 'false' })
      return write.promise
    })
    const warning = vi.spyOn(Message, 'warning')
    const user = userEvent.setup()
    renderConfig()
    await openSmtp(user)

    const notification = screen.getByRole('switch', { name: '内部员工邮件通知' })
    await user.click(notification)
    await user.click(screen.getByRole('button', { name: '保存邮件提醒' }))
    expect(notification).toBeDisabled()
    expect(screen.getByRole('button', { name: '保存邮件提醒' })).toBeDisabled()
    fireEvent.click(notification)
    expect(notification).not.toBeChecked()

    await act(async () => write.resolve({ data: {} }))
    await waitFor(() => expect(statusGets).toBe(2))
    await act(async () => refresh.reject(new Error('simulated refresh failure after commit')))
    await waitFor(() => expect(notification).toBeEnabled())
    expect(notification).not.toBeChecked()
    expect(screen.getByRole('button', { name: '保存邮件提醒' })).toBeDisabled()
    expect(warning).toHaveBeenCalledWith('邮件提醒设置已保存，但状态刷新失败，请稍后刷新页面')
  })
})
