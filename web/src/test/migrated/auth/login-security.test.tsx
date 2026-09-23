import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({
  post: vi.fn(),
  isAxiosError: vi.fn((error: any) => error?.isAxiosError === true),
  withAuthLock: vi.fn((operation: () => Promise<unknown>) => operation()),
  error: vi.fn(),
  success: vi.fn(),
}))

vi.mock('axios', () => ({
  default: { post: mocks.post, isAxiosError: mocks.isAxiosError },
}))

vi.mock('../../../api/client', () => ({ withAuthLock: mocks.withAuthLock }))

vi.mock('@arco-design/web-react', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@arco-design/web-react')>()
  return { ...actual, Message: { error: mocks.error, success: mocks.success } }
})

vi.mock('../../../components/AuthShell', () => ({
  default: ({ children }: { children: React.ReactNode }) => <main>{children}</main>,
}))

import Login from '../../../pages/Login'
import { useAuth } from '../../../store/auth'

function deferred<T>() {
  let resolve!: (value: T | PromiseLike<T>) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((ok, fail) => { resolve = ok; reject = fail })
  return { promise, resolve, reject }
}

function LocationProbe() {
  const location = useLocation()
  return <output aria-label="当前地址">{location.pathname}</output>
}

function renderLogin(from: string) {
  return render(
    <MemoryRouter initialEntries={[{ pathname: '/login', state: { from } }]}>
      <Routes>
        <Route path="/login" element={<Login />} />
        <Route path="*" element={<LocationProbe />} />
      </Routes>
    </MemoryRouter>,
  )
}

function loginPayload(token = 'fixture-token') {
  return {
    accessToken: token,
    expiresAt: 123456,
    user: {
      id: 7,
      employeeNo: 'tester',
      realName: 'Tester',
      email: '',
      userType: 'INTERNAL' as const,
      supplierId: null,
      isSystemAdmin: false,
    },
    permissions: [],
    menus: [],
    mustChangePassword: false,
  }
}

describe('login security migration', () => {
  beforeEach(() => {
    mocks.post.mockReset()
    mocks.isAxiosError.mockClear()
    mocks.withAuthLock.mockReset().mockImplementation((operation) => operation())
    mocks.error.mockReset()
    mocks.success.mockReset()
    useAuth.setState({
      token: null,
      user: null,
      permissions: [],
      menus: [],
      mustChangePassword: false,
      booted: true,
      generation: 0,
    })
  })

  it('login needs only employee number and password, recovers after failure, and rejects external return paths', async () => {
    const first = deferred<{ data: ReturnType<typeof loginPayload> }>()
    mocks.post.mockReturnValueOnce(first.promise).mockResolvedValueOnce({ data: loginPayload() })
    const user = userEvent.setup()
    renderLogin('//external.invalid')

    expect(screen.queryByLabelText('验证码')).not.toBeInTheDocument()
    await user.type(screen.getByLabelText('工号'), ' tester ')
    await user.type(screen.getByLabelText('密码'), 'old')
    await user.click(screen.getByRole('button', { name: '登录' }))
    await waitFor(() => expect(mocks.post).toHaveBeenCalledOnce())
    expect(screen.getByLabelText('工号')).toBeDisabled()
    first.reject({ isAxiosError: true, response: { status: 401, data: { message: '工号或密码错误' } } })
    await waitFor(() => expect(mocks.error).toHaveBeenCalledWith('工号或密码错误'))
    expect(screen.getByLabelText('工号')).toBeEnabled()

    await user.click(screen.getByRole('button', { name: '登录' }))
    await waitFor(() => expect(mocks.post).toHaveBeenCalledTimes(2))
    expect(mocks.post).toHaveBeenLastCalledWith('/api/v1/auth/login', {
      employeeNo: 'tester', password: 'old',
    }, { withCredentials: true })
    expect(mocks.withAuthLock).toHaveBeenCalledTimes(2)
    await waitFor(() => expect(screen.getByRole('status', { name: '当前地址' })).toHaveTextContent('/'))
    expect(useAuth.getState().token).toBe('fixture-token')
  })

  it('login rejects duplicate submissions even before the browser lock runs and allows retry after failure', async () => {
    const lock = deferred<void>()
    const first = deferred<{ data: ReturnType<typeof loginPayload> }>()
    const second = deferred<{ data: ReturnType<typeof loginPayload> }>()
    mocks.withAuthLock.mockImplementation((operation) => lock.promise.then(operation))
    mocks.post.mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise)
    const user = userEvent.setup()
    const view = renderLogin('/projects')
    await user.type(screen.getByLabelText('工号'), ' admin ')
    await user.type(screen.getByLabelText('密码'), 'synthetic-login-password')
    const form = view.container.querySelector('form')!
    fireEvent.submit(form)
    fireEvent.submit(form)
    expect(mocks.post).not.toHaveBeenCalled()
    await waitFor(() => expect(screen.getByLabelText('工号')).toBeDisabled())
    expect(screen.getByLabelText('密码')).toBeDisabled()

    lock.resolve()
    await waitFor(() => expect(mocks.post).toHaveBeenCalledOnce())
    first.reject(new Error('simulated network failure'))
    await waitFor(() => expect(screen.getByLabelText('工号')).toBeEnabled())
    expect(useAuth.getState().token).toBeNull()

    await user.click(screen.getByRole('button', { name: '登录' }))
    await waitFor(() => expect(mocks.post).toHaveBeenCalledTimes(2))
    second.resolve({ data: loginPayload('retry-token') })
    await waitFor(() => expect(screen.getByRole('status', { name: '当前地址' })).toHaveTextContent('/projects'))
    expect(useAuth.getState().token).toBe('retry-token')
  })
})
