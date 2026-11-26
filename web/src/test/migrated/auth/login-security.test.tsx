import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({
  post: vi.fn(),
  httpPost: vi.fn(),
  httpPut: vi.fn(),
  isAxiosError: vi.fn((error: any) => error?.isAxiosError === true),
  withAuthLock: vi.fn((operation: () => Promise<unknown>) => operation()),
  retryBootAuth: vi.fn(),
  error: vi.fn(),
  success: vi.fn(),
}))

vi.mock('axios', () => ({
  default: { post: mocks.post, isAxiosError: mocks.isAxiosError, create: vi.fn(() => ({ interceptors: { request: { use: vi.fn() }, response: { use: vi.fn() } } })) },
}))

vi.mock('../../../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../api/client')>()
  return {
    ...actual,
    default: { post: mocks.httpPost, put: mocks.httpPut },
    withAuthLock: mocks.withAuthLock,
    retryBootAuth: mocks.retryBootAuth,
  }
})

vi.mock('@arco-design/web-react', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@arco-design/web-react')>()
  return { ...actual, Message: { error: mocks.error, success: mocks.success } }
})

vi.mock('../../../components/AuthShell', () => ({
  default: ({ children }: { children: React.ReactNode }) => <main>{children}</main>,
}))

import { Guard } from '../../../App'
import ChangePassword from '../../../pages/ChangePassword'
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
    mocks.httpPost.mockReset()
    mocks.httpPut.mockReset()
    mocks.isAxiosError.mockClear()
    mocks.withAuthLock.mockReset().mockImplementation((operation) => operation())
    mocks.retryBootAuth.mockReset().mockResolvedValue(undefined)
    mocks.error.mockReset()
    mocks.success.mockReset()
    useAuth.setState({
      token: null,
      user: null,
      permissions: [],
      menus: [],
      mustChangePassword: false,
      booted: true,
      bootUnavailable: false,
      generation: 0,
    })
  })

  it('login needs only employee number and password, recovers after failure, and rejects external return paths', async () => {
    const first = deferred<{ data: ReturnType<typeof loginPayload> }>()
    mocks.post.mockReturnValueOnce(first.promise).mockResolvedValueOnce({ data: loginPayload() })
    const user = userEvent.setup()
    renderLogin('//external.invalid')

    expect(screen.queryByLabelText('验证码')).not.toBeInTheDocument()
    await user.type(screen.getByLabelText('账号'), ' tester ')
    await user.type(screen.getByLabelText('密码'), 'old')
    await user.click(screen.getByRole('button', { name: '登录' }))
    await waitFor(() => expect(mocks.post).toHaveBeenCalledOnce())
    expect(screen.getByLabelText('账号')).toBeDisabled()
    first.reject({ isAxiosError: true, response: { status: 401, data: { message: '账号或密码错误' } } })
    await waitFor(() => expect(mocks.error).toHaveBeenCalledWith('账号或密码错误'))
    expect(screen.getByLabelText('账号')).toBeEnabled()

    await user.click(screen.getByRole('button', { name: '登录' }))
    await waitFor(() => expect(mocks.post).toHaveBeenCalledTimes(2))
    expect(mocks.post).toHaveBeenLastCalledWith('/api/v1/auth/login', {
      employeeNo: 'tester', password: 'old',
    }, { withCredentials: true, timeout: 30_000 })
    expect(mocks.withAuthLock).toHaveBeenCalledTimes(2)
    await waitFor(() => expect(screen.getByRole('status', { name: '当前地址' })).toHaveTextContent('/'))
    expect(useAuth.getState().token).toBe('fixture-token')
  })

  it('login rejects malformed response payloads with a friendly error instead of persisting corrupt session', async () => {
    mocks.post.mockResolvedValueOnce({ data: { accessToken: 'partial-only' } })
    const user = userEvent.setup()
    renderLogin('/projects')

    await user.type(screen.getByLabelText('账号'), 'tester')
    await user.type(screen.getByLabelText('密码'), 'secret')
    await user.click(screen.getByRole('button', { name: '登录' }))

    await waitFor(() => expect(mocks.error).toHaveBeenCalledWith('登录响应格式无效，请稍后重试'))
    expect(useAuth.getState().token).toBeNull()
    expect(useAuth.getState().user).toBeNull()
  })

  it('shows the server message verbatim when the network scope rejects login', async () => {
    const message = '该账号仅限内网访问'
    mocks.post.mockRejectedValueOnce({ isAxiosError: true, response: { status: 403, data: { code: 40305, message } } })
    const user = userEvent.setup()
    renderLogin('/projects')

    await user.type(screen.getByLabelText('账号'), 'tester')
    await user.type(screen.getByLabelText('密码'), 'secret')
    await user.click(screen.getByRole('button', { name: '登录' }))

    await waitFor(() => expect(mocks.error).toHaveBeenCalledExactlyOnceWith(message))
    expect(useAuth.getState().token).toBeNull()
    expect(screen.getByRole('button', { name: '登录' })).toBeEnabled()
  })

  it('ChangePassword exitLogin logs out locally even if auth lock fails and passes quietNetworkError', async () => {
    const user = userEvent.setup()
    useAuth.setState({ ...loginPayload('cp-token'), token: 'cp-token', mustChangePassword: true, booted: true })
    mocks.withAuthLock.mockRejectedValueOnce(new Error('登录状态协调超时，请稍后重试'))

    const firstView = render(
      <MemoryRouter initialEntries={['/change-password']}>
        <Routes>
          <Route path="/change-password" element={<ChangePassword />} />
          <Route path="/login" element={<LocationProbe />} />
        </Routes>
      </MemoryRouter>,
    )

    await user.click(screen.getByRole('button', { name: '退出登录' }))
    await waitFor(() => expect(screen.getByRole('status', { name: '当前地址' })).toHaveTextContent('/login'))
    expect(useAuth.getState().token).toBeNull()
    firstView.unmount()

    // Normal exit sends quietNetworkError: true on logout request.
    useAuth.setState({ ...loginPayload('cp-token-2'), token: 'cp-token-2', mustChangePassword: true, booted: true })
    mocks.withAuthLock.mockImplementation((operation) => operation())
    mocks.httpPost.mockResolvedValueOnce({ data: {} })
    render(
      <MemoryRouter initialEntries={['/change-password']}>
        <Routes>
          <Route path="/change-password" element={<ChangePassword />} />
          <Route path="/login" element={<LocationProbe />} />
        </Routes>
      </MemoryRouter>,
    )
    await user.click(screen.getByRole('button', { name: '退出登录' }))
    await waitFor(() => expect(mocks.httpPost).toHaveBeenCalledWith('/auth/logout', null, { quietNetworkError: true }))
  })

  it('ChangePassword renders large buttons matching large password inputs', () => {
    useAuth.setState({ ...loginPayload('cp-token-size'), token: 'cp-token-size', mustChangePassword: false, booted: true })
    render(
      <MemoryRouter initialEntries={['/change-password']}>
        <ChangePassword />
      </MemoryRouter>,
    )
    const backBtn = screen.getByRole('button', { name: '返回' })
    const submitBtn = screen.getByRole('button', { name: '确认修改' })
    expect(backBtn).toHaveClass('arco-btn-size-large')
    expect(submitBtn).toHaveClass('arco-btn-size-large')
  })

  it('Guard renders session recovery screen when bootUnavailable is true and lets user retry or re-login', async () => {
    const user = userEvent.setup()
    useAuth.setState({
      token: null,
      user: { id: 7 } as any,
      permissions: [],
      menus: [],
      mustChangePassword: false,
      booted: true,
      bootUnavailable: true,
      generation: 4,
    })

    render(
      <MemoryRouter initialEntries={['/projects?page=2']}>
        <Routes>
          <Route path="/projects" element={<Guard><div>受保护内容</div></Guard>} />
          <Route path="/login" element={<LocationProbe />} />
        </Routes>
      </MemoryRouter>,
    )

    expect(screen.getByText('暂时无法恢复登录状态')).toBeInTheDocument()
    expect(screen.queryByText('受保护内容')).not.toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: '重试恢复会话' }))
    expect(mocks.retryBootAuth).toHaveBeenCalledOnce()

    await user.click(screen.getByRole('button', { name: '重新登录' }))
    await waitFor(() => expect(screen.getByRole('status', { name: '当前地址' })).toHaveTextContent('/login'))
    expect(useAuth.getState().bootUnavailable).toBe(false)
    expect(useAuth.getState().user).toBeNull()
  })

  it('login rejects duplicate submissions even before the browser lock runs and allows retry after failure', async () => {
    const lock = deferred<void>()
    const first = deferred<{ data: ReturnType<typeof loginPayload> }>()
    const second = deferred<{ data: ReturnType<typeof loginPayload> }>()
    mocks.withAuthLock.mockImplementation((operation) => lock.promise.then(operation))
    mocks.post.mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise)
    const user = userEvent.setup()
    const view = renderLogin('/projects')
    await user.type(screen.getByLabelText('账号'), ' admin ')
    await user.type(screen.getByLabelText('密码'), 'synthetic-login-password')
    const form = view.container.querySelector('form')!
    fireEvent.submit(form)
    fireEvent.submit(form)
    expect(mocks.post).not.toHaveBeenCalled()
    await waitFor(() => expect(screen.getByLabelText('账号')).toBeDisabled())
    expect(screen.getByLabelText('密码')).toBeDisabled()

    lock.resolve()
    await waitFor(() => expect(mocks.post).toHaveBeenCalledOnce())
    first.reject(new Error('simulated network failure'))
    await waitFor(() => expect(screen.getByLabelText('账号')).toBeEnabled())
    expect(useAuth.getState().token).toBeNull()

    await user.click(screen.getByRole('button', { name: '登录' }))
    await waitFor(() => expect(mocks.post).toHaveBeenCalledTimes(2))
    second.resolve({ data: loginPayload('retry-token') })
    await waitFor(() => expect(screen.getByRole('status', { name: '当前地址' })).toHaveTextContent('/projects'))
    expect(useAuth.getState().token).toBe('retry-token')
  })
})
