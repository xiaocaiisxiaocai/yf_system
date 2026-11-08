import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, expect, it, vi } from 'vitest'
import SysConfig from '../pages/system/SysConfig'
import BusinessMailLogPanel from '../pages/system/components/BusinessMailLogPanel'
import { EMPTY_MAIL_STATUS } from '../pages/system/components/systemConfigModel'

const mocks = vi.hoisted(() => ({ get: vi.fn(), put: vi.fn(), canOem: true }))
vi.mock('../api/client', () => ({ default: { get: mocks.get, put: mocks.put } }))
vi.mock('../store/auth', () => ({ useAuth: () => ({ hasPerm: (code: string) => mocks.canOem && ['oem:notify_manage', 'oem:audit_view'].includes(code) }) }))

const keys = ['oem.notify.enabled', 'oem.notify.event.approval_pending', 'oem.notify.event.transfer_released', 'oem.notify.event.sender_result', 'oem.notify.event.receipt']
const settings = keys.map(key => ({ key, label: key, kind: 'boolean', value: key.endsWith('receipt') ? 'false' : 'true', min: null, max: null, readOnly: false, unsupportedReason: null, hint: null }))
beforeEach(() => {
  mocks.canOem = true
  mocks.get.mockReset()
  mocks.put.mockReset()
  mocks.get.mockImplementation(async (url: string) => ({ data: url === '/oem/notify-policies' ? settings : url.endsWith('/mail-status') ? EMPTY_MAIL_STATUS : url.endsWith('/mail-settings') ? { configured: false } : [] }))
})

it('switches Robot and OEM rules while preserving drafts and saves only OEM changes to the OEM endpoint', async () => {
  const user = userEvent.setup()
  mocks.put.mockImplementation(async (_url: string, body: { items: Array<{ key: string; value: string }> }) => ({ data: settings.map(item => ({ ...item, value: body.items.find(change => change.key === item.key)?.value ?? item.value })) }))
  render(<MemoryRouter><SysConfig /></MemoryRouter>)
  await user.click(await screen.findByRole('tab', { name: '邮件提醒规则' }))
  await user.click(screen.getByRole('switch', { name: '内部员工邮件通知' }))
  await user.click(screen.getByRole('radio', { name: 'ODM/OEMA' }))
  expect(await screen.findByRole('switch', { name: 'ODM/OEMA 邮件通知总开关' })).toBeChecked()
  await user.click(screen.getByRole('switch', { name: '接收与清理进度' }))
  await user.click(screen.getByRole('radio', { name: 'Robot 仿真协作' }))
  expect(screen.getByRole('switch', { name: '内部员工邮件通知' })).not.toBeChecked()
  await user.click(screen.getByRole('radio', { name: 'ODM/OEMA' }))
  expect(screen.getByRole('switch', { name: '接收与清理进度' })).toBeChecked()
  await user.click(screen.getByRole('button', { name: '保存 ODM/OEMA 邮件提醒' }))
  await waitFor(() => expect(mocks.put).toHaveBeenCalledTimes(1))
  expect(mocks.put).toHaveBeenCalledWith('/oem/notify-policies', { items: [{ key: 'oem.notify.event.receipt', value: 'true' }] })
  await waitFor(() => expect(screen.getByRole('button', { name: '保存 ODM/OEMA 邮件提醒' })).toBeDisabled())
})

it('keeps the all-business queue visible when an earlier slow OEM query finishes later', async () => {
  const user = userEvent.setup()
  let resolveOem!: (value: unknown) => void
  mocks.get.mockImplementation((url: string, config: { params: { scope: string } }) => url.endsWith('/mail-logs')
    ? Promise.resolve({ data: { list: config.params.scope === 'all' ? [{ id: 7, action: 'OEM_EMAIL_SENT', detail: { recipient: 'v***@example.invalid' }, createdAt: '2026-11-08T00:00:00Z' }] : [], total: 1, page: 1, pageSize: 20 } })
    : config.params.scope === 'oem'
      ? new Promise(resolve => { resolveOem = resolve })
      : Promise.resolve({ data: { ...EMPTY_MAIL_STATUS, queue: { ...EMPTY_MAIL_STATUS.queue, pending: 3 } } }))
  render(<MemoryRouter><BusinessMailLogPanel mail={EMPTY_MAIL_STATUS} formatTime={() => '-'} onViewLogs={vi.fn()} /></MemoryRouter>)
  await user.click(screen.getByRole('combobox', { name: '邮件日志业务' }))
  fireEvent.click(await screen.findByRole('option', { name: 'ODM/OEMA' }))
  await waitFor(() => expect(mocks.get).toHaveBeenCalledWith('/admin/system/mail-status', { params: { scope: 'oem' } }))
  await user.click(screen.getByRole('combobox', { name: '邮件日志业务' }))
  fireEvent.click(await screen.findByRole('option', { name: '全部业务' }))
  expect(await screen.findByText('ODM/OEMA 发送成功')).toBeVisible()
  await act(async () => resolveOem({ data: { ...EMPTY_MAIL_STATUS, queue: { ...EMPTY_MAIL_STATUS.queue, pending: 1 } } }))
  expect(screen.getByText('全部业务通知队列')).toBeVisible()
  expect(screen.getByText(/待发送/)).toHaveTextContent('3')
})

it('does not expose OEM queue and rule controls without OEM notification permission', async () => {
  mocks.canOem = false
  const user = userEvent.setup()
  render(<MemoryRouter><SysConfig /></MemoryRouter>)
  await user.click(await screen.findByRole('tab', { name: '邮件提醒规则' }))
  expect(screen.queryByRole('radio', { name: 'ODM/OEMA' })).not.toBeInTheDocument()
  await user.click(screen.getByRole('tab', { name: 'SMTP 配置' }))
  expect(screen.queryByRole('combobox', { name: '邮件日志业务' })).not.toBeInTheDocument()
  expect(mocks.get.mock.calls.some(([url]) => url === '/oem/notify-policies')).toBe(false)
})
