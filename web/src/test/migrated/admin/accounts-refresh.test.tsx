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
const secondSupplier = { id: 9, name: '切换供应商', status: 'ACTIVE' as const, createdAt: '' }
type AccountFixture = {
  id: number
  employeeNo: string
  status: 'ACTIVE' | 'DISABLED'
  createdAt: string
}
const accountA: AccountFixture = { id: 11, employeeNo: 'a11', status: 'ACTIVE', createdAt: '' }
const accountB: AccountFixture = { id: 12, employeeNo: 'b12', status: 'ACTIVE', createdAt: '' }

function supplierPage(list = [supplier]) {
  return { data: { list, total: list.length, page: 1, pageSize: 10 } }
}

async function waitForAccountsPanel(expectedCount: number) {
  const panel = await screen.findByRole('region', { name: '账号管理' })
  await within(panel).findByText(`共 ${expectedCount} 个账号`)
  return panel
}

function accountStatusButton(panel: HTMLElement, employeeNo: string) {
  const row = within(panel).getByText(employeeNo).closest('tr')
  expect(row).not.toBeNull()
  return within(row!).getByRole('button', { name: '停用' })
}

async function confirmAccountDisable(user: ReturnType<typeof userEvent.setup>, panel: HTMLElement, employeeNo: string) {
  await user.click(accountStatusButton(panel, employeeNo))
  fireEvent.click((await screen.findAllByRole('button', { name: '确定' })).at(-1)!)
}

describe('供应商账号刷新竞态', () => {
  beforeEach(() => {
    mocks.get.mockReset()
    mocks.put.mockReset()
    mocks.post.mockReset()
    mocks.delete.mockReset()
  })

  it('switching suppliers ignores a late account response from the previous supplier', async () => {
    const firstAccounts = deferred<{ data: AccountFixture[] }>()
    mocks.get.mockImplementation((url: string) => {
      if (url === '/admin/suppliers') return Promise.resolve(supplierPage([supplier, secondSupplier]))
      if (url === '/admin/suppliers/8/accounts') return firstAccounts.promise
      if (url === '/admin/suppliers/9/accounts') return Promise.resolve({ data: [accountB] })
      throw new Error(`unexpected GET ${url}`)
    })

    const user = userEvent.setup()
    render(<SupplierList />)
    const listPanel = await screen.findByRole('complementary', { name: '供应商列表' })
    await within(listPanel).findByText('并发供应商')

    await user.click(within(listPanel).getByRole('button', { name: /切换供应商/ }))
    const accountsPanel = await waitForAccountsPanel(1)
    expect(within(accountsPanel).getByText('b12')).toBeVisible()

    await act(async () => {
      firstAccounts.resolve({ data: [accountA] })
      await firstAccounts.promise
      await Promise.resolve()
    })

    expect(within(accountsPanel).getByText('b12')).toBeVisible()
    expect(within(accountsPanel).queryByText('a11')).not.toBeInTheDocument()
  })

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
      const panel = await waitForAccountsPanel(2)

      await confirmAccountDisable(user, panel, 'a11')
      await confirmAccountDisable(user, panel, 'b12')

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
        const rowA = within(panel).getByText('a11').closest('tr')
        const rowB = within(panel).getByText('b12').closest('tr')
        expect(rowA).not.toBeNull()
        expect(rowB).not.toBeNull()
        expect(within(rowA!).getByText('停用')).toBeVisible()
        expect(within(rowB!).getByText('停用')).toBeVisible()
        expect(within(rowA!).getByRole('button', { name: '启用' })).toBeVisible()
        expect(within(rowB!).getByRole('button', { name: '启用' })).toBeVisible()
      })
      expect(screen.queryByText('加载失败')).not.toBeInTheDocument()
    })
  }
})
