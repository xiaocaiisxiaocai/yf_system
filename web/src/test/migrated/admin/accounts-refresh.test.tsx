import { act, render, screen, waitFor, within, fireEvent } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => {
  const authState = {
    hasPerm: (permission: string) => permission === 'supplier:account',
  }

  return {
    authState,
    get: vi.fn(),
    put: vi.fn(),
    post: vi.fn(),
    delete: vi.fn(),
  }
})

vi.mock('../../../api/client', () => ({
  default: {
    get: mocks.get,
    put: mocks.put,
    post: mocks.post,
    delete: mocks.delete,
  },
}))

vi.mock('../../../store/auth', () => ({
  useAuth: (selector?: (state: typeof mocks.authState) => unknown) => (
    selector ? selector(mocks.authState) : mocks.authState
  ),
}))

import SupplierList from '../../../pages/supplier/SupplierList'

type Deferred<T> = {
  promise: Promise<T>
  resolve: (value: T | PromiseLike<T>) => void
  reject: (reason?: unknown) => void
}

function deferred<T>(): Deferred<T> {
  let resolve!: Deferred<T>['resolve']
  let reject!: Deferred<T>['reject']
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise
    reject = rejectPromise
  })
  return { promise, resolve, reject }
}

const supplier = { id: 8, name: '并发供应商', status: 'ACTIVE' as const, createdAt: '' }
type AccountFixture = {
  id: number
  employeeNo: string
  realName: string
  email: string
  status: 'ACTIVE' | 'DISABLED'
  createdAt: string
}
const accountA: AccountFixture = { id: 11, employeeNo: 'a11', realName: '账号A', email: 'a@example.invalid', status: 'ACTIVE', createdAt: '' }
const accountB: AccountFixture = { id: 12, employeeNo: 'b12', realName: '账号B', email: 'b@example.invalid', status: 'ACTIVE', createdAt: '' }

function supplierPage() {
  return { data: { list: [supplier], total: 1, page: 1, pageSize: 10 } }
}

async function openAccounts(user: ReturnType<typeof userEvent.setup>, expectedCount: number) {
  await user.click(await screen.findByRole('button', { name: '账号管理' }))
  await screen.findByText(`共 ${expectedCount} 个账号`)
}

async function closeAccounts(user: ReturnType<typeof userEvent.setup>) {
  const closeIcon = document.querySelector<HTMLElement>('.arco-drawer-close-icon')
  expect(closeIcon).not.toBeNull()
  await user.click(closeIcon!)
  await waitFor(() => {
    expect(document.querySelector('.arco-drawer-wrapper')).toBeNull()
  })
}

function accountStatusButton(employeeNo: string) {
  const row = screen.getByText(employeeNo).closest('tr')
  expect(row).not.toBeNull()
  return within(row!).getByRole('button', { name: '禁用' })
}

describe('供应商账号刷新竞态', () => {
  beforeEach(() => {
    mocks.get.mockReset()
    mocks.put.mockReset()
    mocks.post.mockReset()
    mocks.delete.mockReset()
  })

  for (const connected of [true, false]) {
    it(`closing supplier accounts restores only a connected opener (connected=${connected})`, async () => {
      mocks.get.mockImplementation(async (url: string) => {
        if (url === '/admin/suppliers') return supplierPage()
        if (url === '/admin/suppliers/8/accounts') return { data: [accountA] }
        throw new Error(`unexpected GET ${url}`)
      })

      const user = userEvent.setup()
      render(<SupplierList />)
      const opener = await screen.findByRole('button', { name: '账号管理' })
      const focus = vi.spyOn(opener, 'focus')

      fireEvent.click(opener)
      await screen.findByText('共 1 个账号')
      expect(focus).not.toHaveBeenCalled()

      if (!connected) opener.remove()
      await closeAccounts(user)
      expect(focus).toHaveBeenCalledTimes(connected ? 1 : 0)
    })
  }

  for (const lateOutcome of ['success', 'failure'] as const) {
    it(`an older account refresh ${lateOutcome} cannot replace the latest successful snapshot`, async () => {
      const writes = new Map<number, Deferred<{ data: Record<string, never> }>>([
        [11, deferred<{ data: Record<string, never> }>()],
        [12, deferred<{ data: Record<string, never> }>()],
      ])
      const refreshes: Array<Deferred<{ data: AccountFixture[] }>> = []
      let accountGets = 0

      mocks.get.mockImplementation((url: string) => {
        if (url === '/admin/suppliers') return Promise.resolve(supplierPage())
        if (url === '/admin/suppliers/8/accounts') {
          accountGets += 1
          if (accountGets === 1) return Promise.resolve({ data: [accountA, accountB] })
          const request = deferred<{ data: AccountFixture[] }>()
          refreshes.push(request)
          return request.promise
        }
        throw new Error(`unexpected GET ${url}`)
      })
      mocks.put.mockImplementation((url: string) => {
        const id = Number(url.match(/^\/admin\/supplier-accounts\/(\d+)\/status$/)?.[1])
        return writes.get(id)!.promise
      })
      mocks.post.mockResolvedValue({ data: {} })
      mocks.delete.mockResolvedValue({ data: {} })

      const user = userEvent.setup()
      render(<SupplierList />)
      await openAccounts(user, 2)

      await act(async () => {
        await Promise.all([
          user.click(accountStatusButton('a11')),
          user.click(accountStatusButton('b12')),
        ])
      })

      await waitFor(() => {
        expect(mocks.put).toHaveBeenCalledTimes(2)
      })

      await act(async () => {
        writes.get(11)!.resolve({ data: {} })
        await Promise.resolve()
      })
      await waitFor(() => expect(refreshes).toHaveLength(1))

      await act(async () => {
        writes.get(12)!.resolve({ data: {} })
        await Promise.resolve()
      })
      await waitFor(() => expect(refreshes).toHaveLength(2))

      const latest = [
        { ...accountA, status: 'DISABLED' as const },
        { ...accountB, status: 'DISABLED' as const },
      ]
      await act(async () => {
        refreshes[1].resolve({ data: latest })
        await refreshes[1].promise
        await Promise.resolve()
      })

      await act(async () => {
        if (lateOutcome === 'success') {
          refreshes[0].resolve({ data: [{ ...accountA, status: 'DISABLED' as const }, accountB] })
          await refreshes[0].promise
        } else {
          refreshes[0].reject(new Error('older refresh failed'))
          await refreshes[0].promise.catch(() => undefined)
        }
        await Promise.resolve()
      })

      await waitFor(() => {
        const rowA = screen.getByText('a11').closest('tr')
        const rowB = screen.getByText('b12').closest('tr')
        expect(rowA).not.toBeNull()
        expect(rowB).not.toBeNull()
        expect(within(rowA!).getByText('禁用')).toBeVisible()
        expect(within(rowB!).getByText('禁用')).toBeVisible()
        expect(within(rowA!).getByRole('button', { name: '启用' })).toBeVisible()
        expect(within(rowB!).getByRole('button', { name: '启用' })).toBeVisible()
      })
      expect(screen.queryByText('加载失败')).not.toBeInTheDocument()
    })
  }
})
