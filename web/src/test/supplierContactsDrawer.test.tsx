import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn(), put: vi.fn() }))

vi.mock('../api/client', () => ({
  default: { get: mocks.get, post: mocks.post, put: mocks.put },
}))

import { SupplierContactsDrawer } from '../pages/supplier/components/SupplierContactsDrawer'
import type { Supplier } from '../pages/supplier/components/supplierTypes'

const supplier = { id: 5, name: '甲厂' } as Supplier
const contact = { id: 11, name: '张三', email: 'zhang@example.com', sortNo: 1, status: 'ACTIVE' }
const unhandled: unknown[] = []
const onUnhandled = (reason: unknown) => { unhandled.push(reason) }

// 全局拦截器已弹 toast，这里的请求失败只需被抽屉接住，不能变成 unhandledrejection。
const failure = () => Object.assign(new Error('请求失败'), { response: { status: 400, data: { message: '请求失败' } } })
const settle = () => new Promise<void>((resolve) => setTimeout(resolve, 20))

describe('SupplierContactsDrawer failures', () => {
  beforeEach(() => {
    unhandled.length = 0
    process.on('unhandledRejection', onUnhandled)
    mocks.get.mockReset().mockResolvedValue({ data: [contact] })
    mocks.post.mockReset()
    mocks.put.mockReset()
  })

  afterEach(() => {
    process.off('unhandledRejection', onUnhandled)
  })

  it('a rejected save keeps the dialog and input open without an unhandled rejection', async () => {
    mocks.post.mockRejectedValue(failure())
    render(<SupplierContactsDrawer supplier={supplier} onClose={() => undefined} />)
    await screen.findByText('张三')

    fireEvent.click(screen.getByRole('button', { name: /新增联络人/ }))
    const dialog = await screen.findByRole('dialog', { name: '新增联络人' })
    fireEvent.change(within(dialog).getByPlaceholderText('签名时需与此姓名一致'), { target: { value: '李四' } })
    fireEvent.change(within(dialog).getByPlaceholderText('name@example.com'), { target: { value: 'li@example.com' } })
    // 输入框里按 Enter 走 Form onSubmit（返回的 promise 无人接收）。
    fireEvent.submit(dialog.querySelector('form')!)

    await waitFor(() => expect(mocks.post).toHaveBeenCalledTimes(1))
    await settle()
    expect(unhandled).toEqual([])
    expect(screen.getByRole('dialog', { name: '新增联络人' })).toBeInTheDocument()
    expect(within(dialog).getByPlaceholderText('签名时需与此姓名一致')).toHaveValue('李四')
    // 保存状态已复位，可再次提交。
    await waitFor(() => expect(within(dialog).getByRole('button', { name: '添加联络人' })).not.toBeDisabled())
    mocks.post.mockResolvedValue({ data: {} })
    fireEvent.submit(dialog.querySelector('form')!)
    await waitFor(() => expect(mocks.post).toHaveBeenCalledTimes(2))
  })

  it('a rejected enable/disable restores the button without an unhandled rejection', async () => {
    mocks.get.mockResolvedValue({ data: [{ ...contact, status: 'DISABLED' }] })
    mocks.put.mockRejectedValue(failure())
    render(<SupplierContactsDrawer supplier={supplier} onClose={() => undefined} />)
    await screen.findByText('张三')

    // “启用”按钮的 onClick 直接返回 toggle 的 promise。
    fireEvent.click(screen.getByRole('button', { name: '启用' }))

    await waitFor(() => expect(mocks.put).toHaveBeenCalledTimes(1))
    expect(mocks.put).toHaveBeenCalledWith('/admin/supplier-contacts/11/status', { status: 'ACTIVE' })
    await settle()
    expect(unhandled).toEqual([])
    await waitFor(() => expect(screen.getByRole('button', { name: '启用' })).not.toBeDisabled())
  })
})
