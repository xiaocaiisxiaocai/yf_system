import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { OemProvider } from '../../oem/OemContext'
import type { OemApi } from '../../oem/api/OemApi'
import type { RetentionTemplate } from '../../oem/api/types'
import { RetentionPage } from '../../oem/pages/admin/PolicyPages'

const policy: RetentionTemplate = {
  id: 1, name: '原有统一策略', mode: 'AFTER_RELEASE', releaseTtlMinutes: 60,
  receiptGraceMinutes: null, status: 'ACTIVE', version: 3, summary: '发布 1 小时后删除',
}

function setup(api: Partial<OemApi>) {
  render(<OemProvider value={{ api: api as OemApi, realm: 'internal', base: '/oem',
    permissions: new Set(['oem:retention_template_manage']), userId: 1, queryScope: ['policy-test'] }}>
    <RetentionPage />
  </OemProvider>)
  return userEvent.setup()
}

describe('统一删除策略', () => {
  it('edits the effective policy directly without exposing template selection or activation', async () => {
    const updateRetention = vi.fn().mockResolvedValue({ ...policy, releaseTtlMinutes: 120, version: 4, summary: '发布 2 小时后删除' })
    const createRetention = vi.fn()
    const user = setup({ retentionTemplates: vi.fn().mockResolvedValue([
      { ...policy, id: 5, name: '历史策略' }, policy,
    ]), updateRetention, createRetention })
    await screen.findByText('当前规则：发布 1 小时后删除')
    expect(screen.queryByRole('button', { name: '新建策略' })).not.toBeInTheDocument()
    expect(screen.queryByRole('switch')).not.toBeInTheDocument()
    expect(screen.queryByText('历史策略')).not.toBeInTheDocument()
    const duration = screen.getByRole('spinbutton')
    await user.clear(duration)
    await user.type(duration, '120')
    await user.click(screen.getByRole('button', { name: '保存统一策略' }))
    await waitFor(() => expect(updateRetention).toHaveBeenCalledWith(1, {
      name: policy.name, mode: 'AFTER_RELEASE', releaseTtlMinutes: 120, receiptGraceMinutes: null, status: 'ACTIVE', version: 3,
    }))
    expect(createRetention).not.toHaveBeenCalled()
    await screen.findByText('当前规则：发布 2 小时后删除')
  })

  it('keeps the edited rule and displays a rejected save without claiming success', async () => {
    const updateRetention = vi.fn().mockRejectedValue({ response: { data: { message: '策略已被其他管理员修改，请重新加载' } } })
    const user = setup({ retentionTemplates: vi.fn().mockResolvedValue([policy]), updateRetention })
    await screen.findByText('当前规则：发布 1 小时后删除')
    await user.clear(screen.getByRole('spinbutton'))
    await user.type(screen.getByRole('spinbutton'), '90')
    await user.click(screen.getByRole('button', { name: '保存统一策略' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('策略已被其他管理员修改')
    expect(screen.getByRole('spinbutton')).toHaveValue('90')
    expect(screen.getByText('当前规则：发布 1 小时后删除')).toBeInTheDocument()
  })
})
