import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { expect, it, vi } from 'vitest'
import SmtpTestPanel from '../pages/system/components/SmtpTestPanel'

const mocks = vi.hoisted(() => ({ post: vi.fn() }))
vi.mock('../api/client', () => ({ default: { post: mocks.post } }))

it('requires saved configuration and a valid recipient before sending', async () => {
  const user = userEvent.setup()
  const view = render(<SmtpTestPanel configured dirty saving={false} />)
  expect(screen.getByRole('button', { name: '发送测试邮件' })).toBeDisabled()
  view.rerender(<SmtpTestPanel configured dirty={false} saving={false} />)
  await user.click(screen.getByRole('button', { name: '发送测试邮件' }))
  expect(screen.getByRole('status')).toHaveTextContent('请输入有效的测试收件邮箱')
  expect(mocks.post).not.toHaveBeenCalled()
})

it('sends once and displays the server result without changing SMTP settings', async () => {
  const user = userEvent.setup()
  let complete!: (value: unknown) => void
  const onTested = vi.fn().mockResolvedValue(undefined)
  mocks.post.mockImplementation(() => new Promise(resolve => { complete = resolve }))
  render(<SmtpTestPanel configured dirty={false} saving={false} onTested={onTested} />)
  await user.type(screen.getByLabelText('测试收件邮箱'), 'test@example.invalid')
  await user.dblClick(screen.getByRole('button', { name: '发送测试邮件' }))
  expect(mocks.post).toHaveBeenCalledTimes(1)
  expect(mocks.post).toHaveBeenCalledWith('/admin/system/mail-settings/test', { recipient: 'test@example.invalid' }, { timeout: 45000 })
  complete({ data: { success: false, message: 'SMTP 认证失败' } })
  await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('SMTP 认证失败'))
  await waitFor(() => expect(onTested).toHaveBeenCalledTimes(1))
  expect(screen.getByLabelText('测试收件邮箱')).toBeEnabled()
})

it('checks the connection step by step without sending mail', async () => {
  const user = userEvent.setup()
  const onTested = vi.fn().mockResolvedValue(undefined)
  let complete!: (value: unknown) => void
  mocks.post.mockReset()
  mocks.post.mockImplementation(() => new Promise(resolve => { complete = resolve }))
  const response = { data: { success: false, steps: [
    { name: '连接', status: 'OK', elapsedMs: 12, detail: '已连接 smtp.office365.com:587' },
    { name: '加密', status: 'FAILED', elapsedMs: 30, detail: 'TLS 握手失败：请检查端口、加密方式和服务器证书' },
    { name: '登录', status: 'SKIPPED', elapsedMs: 0, detail: null },
  ] } }
  render(<SmtpTestPanel configured dirty={false} saving={false} onTested={onTested} />)
  await user.dblClick(screen.getByRole('button', { name: '检测连接' }))
  expect(mocks.post).toHaveBeenCalledTimes(1)
  expect(mocks.post).toHaveBeenCalledWith('/admin/system/mail-settings/diagnose', {}, { timeout: 75000 })
  expect(screen.getByRole('button', { name: '发送测试邮件' })).toBeDisabled()
  complete(response)
  const steps = await screen.findByRole('status', { name: '连接检测结果' })
  expect(steps).toHaveTextContent('已连接 smtp.office365.com:587')
  expect(steps).toHaveTextContent('TLS 握手失败')
  expect(steps).toHaveTextContent('未检查（前一步失败）')
  await waitFor(() => expect(onTested).toHaveBeenCalledTimes(1))
})
