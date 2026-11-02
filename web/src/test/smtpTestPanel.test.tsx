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
  mocks.post.mockImplementation(() => new Promise(resolve => { complete = resolve }))
  render(<SmtpTestPanel configured dirty={false} saving={false} />)
  await user.type(screen.getByLabelText('测试收件邮箱'), 'test@example.invalid')
  await user.dblClick(screen.getByRole('button', { name: '发送测试邮件' }))
  expect(mocks.post).toHaveBeenCalledTimes(1)
  expect(mocks.post).toHaveBeenCalledWith('/admin/system/mail-settings/test', { recipient: 'test@example.invalid' }, { timeout: 45000 })
  complete({ data: { success: false, message: 'SMTP 认证失败' } })
  await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('SMTP 认证失败'))
  expect(screen.getByLabelText('测试收件邮箱')).toBeEnabled()
})
