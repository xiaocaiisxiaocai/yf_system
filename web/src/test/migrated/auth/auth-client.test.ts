import { afterEach, describe, expect, it, vi } from 'vitest'

type RequestConfig = {
  url?: string
  method?: string
  headers: Record<string, string>
  authGeneration?: number
  _retried?: boolean
}

function deferred<T>() {
  let resolve!: (value: T | PromiseLike<T>) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((ok, fail) => { resolve = ok; reject = fail })
  return { promise, resolve, reject }
}

function createAxiosHarness() {
  const requestHandlers: Array<(config: RequestConfig) => RequestConfig> = []
  const responseFailures: Array<(error: any) => Promise<unknown>> = []
  const replays: RequestConfig[] = []
  const errors: string[] = []
  const http = Object.assign(
    vi.fn(async (config: RequestConfig) => {
      const prepared = requestHandlers.reduce((value, handler) => handler(value), config)
      replays.push({ ...prepared, headers: { ...prepared.headers } })
      return { data: { ok: true }, config: prepared }
    }),
    {
      interceptors: {
        request: { use: (fulfilled: (config: RequestConfig) => RequestConfig) => { requestHandlers.push(fulfilled) } },
        response: {
          use: (_fulfilled: unknown, rejected: (error: any) => Promise<unknown>) => { responseFailures.push(rejected) },
        },
      },
    },
  )
  const axios = {
    create: vi.fn(() => http),
    post: vi.fn(),
    get: vi.fn(),
    isAxiosError: vi.fn((error: any) => error?.isAxiosError === true),
  }
  return { axios, http, requestHandlers, responseFailures, replays, errors }
}

function completeRefreshPayload(token = 'refreshed-token', id = 7) {
  return {
    accessToken: token,
    expiresAt: 123456,
    user: {
      id,
      employeeNo: `user-${id}`,
      realName: `User ${id}`,
      email: `${id}@example.invalid`,
      userType: 'INTERNAL' as const,
      supplierId: null,
      isSystemAdmin: false,
    },
    permissions: [`perm-${id}`],
    menus: [`menu-${id}`],
    mustChangePassword: false,
  }
}

function profileResponse(id: number) {
  const { accessToken: _token, expiresAt: _expires, ...profile } = completeRefreshPayload('unused', id)
  return { data: profile }
}

function jwt(exp: number) {
  const payload = btoa(JSON.stringify({ exp })).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
  return `header.${payload}.signature`
}

async function loadClient(harness = createAxiosHarness()) {
  vi.resetModules()
  vi.doMock('axios', () => ({ default: harness.axios }))
  vi.doMock('@arco-design/web-react', () => ({
    Message: { error: (message: string) => { harness.errors.push(message) } },
  }))
  const auth = await import('../../../store/auth')
  auth.useAuth.setState({
    token: null,
    user: null,
    permissions: [],
    menus: [],
    mustChangePassword: false,
    booted: false,
    generation: 0,
  })
  const client = await import('../../../api/client')
  return { harness, auth, client }
}

function seedBoot(auth: Awaited<ReturnType<typeof loadClient>>['auth']) {
  auth.useAuth.setState({
    token: null,
    user: { id: 7 } as any,
    permissions: [],
    menus: [],
    mustChangePassword: false,
    booted: false,
    generation: 4,
  })
}

function seedSession(auth: Awaited<ReturnType<typeof loadClient>>['auth'], token = 'token-A', id = 7, generation = 1) {
  auth.useAuth.setState({
    ...completeRefreshPayload(token, id),
    token,
    generation,
    booted: true,
  })
}

function rejected(error: unknown) {
  return Promise.resolve(error)
}

async function forbidden(loaded: Awaited<ReturnType<typeof loadClient>>, url = '/projects') {
  const config = loaded.harness.requestHandlers[0]({ url, headers: {} })
  return loaded.harness.responseFailures[0]({
    config,
    response: { status: 403, data: { message: 'forbidden' } },
    message: 'forbidden',
  })
}

describe('auth client migration', () => {
  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
    vi.clearAllMocks()
    localStorage.clear()
    window.history.replaceState({}, '', '/')
  })

  it('boot refresh consumes the complete response without a profile request', async () => {
    const loaded = await loadClient()
    seedBoot(loaded.auth)
    const payload = completeRefreshPayload()
    loaded.harness.axios.post.mockResolvedValue({ data: payload })
    await loaded.client.bootAuth()
    expect(loaded.harness.axios.post).toHaveBeenCalledOnce()
    expect(loaded.harness.axios.get).not.toHaveBeenCalled()
    expect(loaded.auth.useAuth.getState()).toMatchObject({
      token: payload.accessToken,
      user: payload.user,
      booted: true,
      generation: 4,
    })
  })

  it('boot refresh rejects the obsolete token-only response without a profile request', async () => {
    const loaded = await loadClient()
    seedBoot(loaded.auth)
    loaded.harness.axios.post.mockResolvedValue({
      data: { accessToken: 'legacy-refreshed-token', expiresAt: 654321 },
    })
    await loaded.client.bootAuth()
    expect(loaded.harness.axios.post).toHaveBeenCalledOnce()
    expect(loaded.harness.axios.get).not.toHaveBeenCalled()
    expect(loaded.auth.useAuth.getState()).toMatchObject({
      token: null,
      user: null,
      booted: true,
      generation: 5,
    })
  })

  it('malformed refresh responses do not trigger the legacy profile fallback', async () => {
    const loaded = await loadClient()
    seedBoot(loaded.auth)
    loaded.harness.axios.post.mockResolvedValue({
      data: { accessToken: 'partial-token', expiresAt: 654321, permissions: [] },
    })
    await loaded.client.bootAuth()
    expect(loaded.harness.axios.get).not.toHaveBeenCalled()
    expect(loaded.auth.useAuth.getState()).toMatchObject({ token: null, user: null, generation: 5 })
  })

  it('a late refresh response cannot restore a session after logout', async () => {
    const loaded = await loadClient()
    seedBoot(loaded.auth)
    const response = deferred<{ data: ReturnType<typeof completeRefreshPayload> }>()
    loaded.harness.axios.post.mockReturnValue(response.promise)
    const boot = loaded.client.bootAuth()
    await Promise.resolve()
    loaded.auth.useAuth.getState().logout()
    response.resolve({ data: completeRefreshPayload('late-token') })
    await boot
    expect(loaded.harness.axios.get).not.toHaveBeenCalled()
    expect(loaded.auth.useAuth.getState()).toMatchObject({ token: null, user: null, generation: 5 })
  })

  it('403 profile refresh starts a new request for a new token and applies only that session', async () => {
    const loaded = await loadClient()
    seedSession(loaded.auth, 'token-A', 7, 1)
    const oldProfile = deferred<ReturnType<typeof profileResponse>>()
    const newProfile = deferred<ReturnType<typeof profileResponse>>()
    loaded.harness.axios.get.mockReturnValueOnce(oldProfile.promise).mockReturnValueOnce(newProfile.promise)
    const oldError = forbidden(loaded).catch(rejected)
    expect(loaded.harness.axios.get).toHaveBeenCalledWith('/api/v1/auth/profile', {
      headers: { Authorization: 'Bearer token-A' }, timeout: 60000,
    })
    seedSession(loaded.auth, 'token-B', 9, 2)
    const newError = forbidden(loaded).catch(rejected)
    expect(loaded.harness.axios.get).toHaveBeenCalledTimes(2)
    oldProfile.resolve(profileResponse(1))
    await oldError
    expect(loaded.auth.useAuth.getState().token).toBe('token-B')
    expect(loaded.auth.useAuth.getState().user?.id).toBe(9)
    newProfile.resolve(profileResponse(2))
    await newError
    expect(loaded.auth.useAuth.getState()).toMatchObject({ token: 'token-B', user: { id: 2 }, generation: 2 })
  })

  it('same-session concurrent 403 responses share one profile request', async () => {
    const loaded = await loadClient()
    seedSession(loaded.auth)
    const profile = deferred<ReturnType<typeof profileResponse>>()
    loaded.harness.axios.get.mockReturnValue(profile.promise)
    const first = forbidden(loaded).catch(rejected)
    const second = forbidden(loaded).catch(rejected)
    expect(loaded.harness.axios.get).toHaveBeenCalledOnce()
    profile.resolve(profileResponse(3))
    await Promise.all([first, second])
    expect(loaded.auth.useAuth.getState()).toMatchObject({ token: 'token-A', user: { id: 3 }, generation: 1 })
  })

  it('an old profile request cannot clear a newer session request', async () => {
    const loaded = await loadClient()
    seedSession(loaded.auth, 'token-A', 7, 1)
    const oldProfile = deferred<ReturnType<typeof profileResponse>>()
    const newProfile = deferred<ReturnType<typeof profileResponse>>()
    loaded.harness.axios.get.mockReturnValueOnce(oldProfile.promise).mockReturnValueOnce(newProfile.promise)
    const old = forbidden(loaded).catch(rejected)
    seedSession(loaded.auth, 'token-B', 9, 2)
    const current = forbidden(loaded).catch(rejected)
    oldProfile.resolve(profileResponse(1))
    await old
    await new Promise((resolve) => setTimeout(resolve, 0))
    const concurrent = forbidden(loaded).catch(rejected)
    expect(loaded.harness.axios.get).toHaveBeenCalledTimes(2)
    newProfile.resolve(profileResponse(2))
    await Promise.all([current, concurrent])
    expect(loaded.auth.useAuth.getState()).toMatchObject({ token: 'token-B', user: { id: 2 }, generation: 2 })
  })

  it('without Web Locks, concurrent refreshes in two tabs are serialized by the storage lease', async () => {
    const loaded = await loadClient()
    const descriptor = Object.getOwnPropertyDescriptor(navigator, 'locks')
    Object.defineProperty(navigator, 'locks', { configurable: true, value: undefined })
    localStorage.clear()
    let running = 0
    let overlapped = false
    const order: string[] = []
    const operation = (name: string) => async () => {
      running += 1
      if (running > 1) overlapped = true
      await new Promise((resolve) => setTimeout(resolve, 30))
      order.push(name)
      running -= 1
      return name
    }
    await expect(Promise.all([
      loaded.client.withAuthLock(operation('A')),
      loaded.client.withAuthLock(operation('B')),
    ])).resolves.toEqual(['A', 'B'])
    expect(overlapped).toBe(false)
    expect(order).toHaveLength(2)
    expect(localStorage.getItem('yf:auth-refresh-lock')).toBeNull()
    if (descriptor) Object.defineProperty(navigator, 'locks', descriptor)
    else Reflect.deleteProperty(navigator, 'locks')
  })

  it('the storage lease renews during a long operation and releases only after completion', async () => {
    const loaded = await loadClient()
    vi.useFakeTimers()
    const operation = deferred<string>()
    const pending = loaded.client.withStorageLease(() => operation.promise)
    await vi.advanceTimersByTimeAsync(50)
    const first = JSON.parse(localStorage.getItem('yf:auth-refresh-lock')!) as { owner: string; expires: number }
    await vi.advanceTimersByTimeAsync(20_000)
    const renewed = JSON.parse(localStorage.getItem('yf:auth-refresh-lock')!) as { owner: string; expires: number }
    expect(renewed.owner).toBe(first.owner)
    expect(renewed.expires).toBeGreaterThan(first.expires)
    operation.resolve('done')
    await expect(pending).resolves.toBe('done')
    expect(localStorage.getItem('yf:auth-refresh-lock')).toBeNull()
  })

  it('storage coordination failure rejects without running the protected operation', async () => {
    const loaded = await loadClient()
    const operation = vi.fn(async () => 'unsafe')
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('blocked') })
    await expect(loaded.client.withStorageLease(operation)).rejects.toThrow('无法安全协调登录状态')
    expect(operation).not.toHaveBeenCalled()
  })

  it('realtime token reads stay in memory until the JWT is close to expiry', async () => {
    const loaded = await loadClient()
    const descriptor = Object.getOwnPropertyDescriptor(navigator, 'locks')
    Object.defineProperty(navigator, 'locks', {
      configurable: true,
      value: { request: (_name: string, operation: () => Promise<unknown>) => operation() },
    })
    const valid = jwt(Math.floor(Date.now() / 1000) + 600)
    seedSession(loaded.auth, valid, 7, 3)
    await expect(loaded.client.getRealtimeAccessToken()).resolves.toBe(valid)
    expect(loaded.harness.axios.post).not.toHaveBeenCalled()

    seedSession(loaded.auth, jwt(Math.floor(Date.now() / 1000) + 30), 7, 4)
    loaded.harness.axios.post.mockResolvedValue({ data: completeRefreshPayload('renewed-token', 7) })
    await expect(loaded.client.getRealtimeAccessToken()).resolves.toBe('renewed-token')
    expect(loaded.harness.axios.post).toHaveBeenCalledOnce()
    expect(loaded.harness.axios.post.mock.calls[0][2]).toMatchObject({ timeout: loaded.client.REFRESH_TIMEOUT_MS })
    if (descriptor) Object.defineProperty(navigator, 'locks', descriptor)
    else Reflect.deleteProperty(navigator, 'locks')
  })

  it('two tabs can restore a shared rotating refresh cookie', async () => {
    const loaded = await loadClient()
    const descriptor = Object.getOwnPropertyDescriptor(navigator, 'locks')
    let queue = Promise.resolve()
    Object.defineProperty(navigator, 'locks', {
      configurable: true,
      value: {
        request: (_name: string, operation: () => Promise<unknown>) => {
          const result = queue.then(operation)
          queue = result.then(() => undefined, () => undefined)
          return result
        },
      },
    })
    let cookie = 0
    const consumed = new Set<number>()
    const tabs = [{ token: '' }, { token: '' }]
    await Promise.all(tabs.map((tab) => loaded.client.withAuthLock(async () => {
      const sent = cookie
      await Promise.resolve()
      if (consumed.has(sent)) throw new Error('refresh cookie replayed')
      consumed.add(sent)
      cookie += 1
      tab.token = `token-${cookie}`
    })))
    expect(tabs.every((tab) => tab.token.length > 0)).toBe(true)
    expect(consumed.size).toBe(2)
    if (descriptor) Object.defineProperty(navigator, 'locks', descriptor)
    else Reflect.deleteProperty(navigator, 'locks')
  })

  it('an in-flight refresh cannot restore a logged-out session', async () => {
    const loaded = await loadClient()
    seedBoot(loaded.auth)
    const refresh = deferred<{ data: ReturnType<typeof completeRefreshPayload> }>()
    loaded.harness.axios.post.mockReturnValue(refresh.promise)
    const boot = loaded.client.bootAuth()
    await Promise.resolve()
    loaded.auth.useAuth.getState().logout()
    refresh.resolve({ data: completeRefreshPayload('stale') })
    await boot
    expect(loaded.auth.useAuth.getState()).toMatchObject({ user: null, token: null })
  })

  it('queued stale writes cannot refresh or replay as a newly logged-in account', async () => {
    const loaded = await loadClient()
    seedSession(loaded.auth, 'account-a', 1, 0)
    const locked = deferred<void>()
    const descriptor = Object.getOwnPropertyDescriptor(navigator, 'locks')
    Object.defineProperty(navigator, 'locks', {
      configurable: true,
      value: { request: (_name: string, operation: () => Promise<unknown>) => locked.promise.then(operation) },
    })
    const config = loaded.harness.requestHandlers[0]({ url: '/projects/1/messages', method: 'POST', headers: {} })
    const failure = loaded.harness.responseFailures[0]({ config, response: { status: 401 }, message: 'expired' })
    seedSession(loaded.auth, 'account-b', 2, 1)
    locked.resolve()
    await expect(failure).rejects.toMatchObject({ response: { status: 401 } })
    expect(loaded.harness.axios.post).not.toHaveBeenCalled()
    expect(loaded.harness.replays).toEqual([])
    expect(loaded.auth.useAuth.getState().token).toBe('account-b')
    if (descriptor) Object.defineProperty(navigator, 'locks', descriptor)
    else Reflect.deleteProperty(navigator, 'locks')
  })

  it('simultaneous 401 responses share one refresh and replay each request once', async () => {
    const loaded = await loadClient()
    seedSession(loaded.auth, 'old-token', 1, 0)
    loaded.harness.axios.post.mockResolvedValue({ data: completeRefreshPayload('new-token', 1) })
    const configs = Array.from({ length: 5 }, (_, index) => loaded.harness.requestHandlers[0]({
      url: `/resource-${index}`, method: 'GET', headers: {},
    }))
    await Promise.all(configs.map((config) => loaded.harness.responseFailures[0]({
      config, response: { status: 401 }, message: 'expired',
    })))
    expect(loaded.harness.axios.post).toHaveBeenCalledOnce()
    expect(loaded.harness.axios.get).not.toHaveBeenCalled()
    expect(loaded.harness.replays).toHaveLength(5)
    expect(loaded.harness.replays.map((item) => item.url).sort()).toEqual(configs.map((item) => item.url).sort())
    expect(loaded.harness.replays.every((item) => item._retried && item.headers.Authorization === 'Bearer new-token')).toBe(true)
  })

  it('an internal-client 401 cannot refresh, clear or redirect the OEM portal session', async () => {
    const loaded = await loadClient()
    seedSession(loaded.auth, 'internal-token', 7, 4)
    window.history.replaceState({}, '', '/oem-portal/transfers/1')
    const config = loaded.harness.requestHandlers[0]({ url: '/projects', method: 'GET', headers: {} })
    const error = { config, response: { status: 401, data: { message: 'expired' } }, message: 'expired' }

    await expect(loaded.harness.responseFailures[0](error)).rejects.toBe(error)

    expect(loaded.harness.axios.post).not.toHaveBeenCalled()
    expect(loaded.harness.replays).toEqual([])
    expect(loaded.auth.useAuth.getState()).toMatchObject({ token: 'internal-token', user: { id: 7 }, generation: 4 })
    expect(window.location.pathname).toBe('/oem-portal/transfers/1')
  })

  it('an internal refresh failure cannot logout after navigation enters the OEM portal', async () => {
    const loaded = await loadClient()
    seedSession(loaded.auth, 'internal-token', 7, 4)
    window.history.replaceState({}, '', '/projects/1')
    const refresh = deferred<never>()
    loaded.harness.axios.post.mockReturnValue(refresh.promise)
    const config = loaded.harness.requestHandlers[0]({ url: '/projects/1', method: 'GET', headers: {} })
    const error = { config, response: { status: 401, data: { message: 'expired' } }, message: 'expired' }
    const failure = loaded.harness.responseFailures[0](error)
    await vi.waitFor(() => expect(loaded.harness.axios.post).toHaveBeenCalledOnce())

    window.history.replaceState({}, '', '/oem-portal/transfers/2')
    refresh.reject(new Error('refresh failed'))
    await expect(failure).rejects.toBe(error)

    expect(loaded.harness.replays).toEqual([])
    expect(loaded.auth.useAuth.getState()).toMatchObject({ token: 'internal-token', user: { id: 7 }, generation: 4 })
    expect(window.location.pathname).toBe('/oem-portal/transfers/2')
  })

  it('an internal forced-password response cannot redirect the OEM portal', async () => {
    const loaded = await loadClient()
    seedSession(loaded.auth, 'internal-token', 7, 4)
    window.history.replaceState({}, '', '/oem-portal/transfers/2')
    const config = loaded.harness.requestHandlers[0]({ url: '/projects/1', method: 'GET', headers: {} })
    const error = { config, response: { status: 403, data: { code: 40303, message: 'change password' } }, message: 'forbidden' }

    await expect(loaded.harness.responseFailures[0](error)).rejects.toBe(error)

    expect(loaded.auth.useAuth.getState()).toMatchObject({ token: 'internal-token', mustChangePassword: false })
    expect(window.location.pathname).toBe('/oem-portal/transfers/2')
  })

  async function verifyProfileAndTokenRefreshRace(profileFirst: boolean) {
    const loaded = await loadClient()
    seedSession(loaded.auth, 'old-token', 1, 6)
    const sessionGeneration = loaded.auth.useAuth.getState().generation
    const profile = deferred<ReturnType<typeof profileResponse>>()
    const refresh = deferred<{ data: ReturnType<typeof completeRefreshPayload> }>()
    loaded.harness.axios.get.mockReturnValue(profile.promise)
    loaded.harness.axios.post.mockReturnValue(refresh.promise)

    const denied = forbidden(loaded).catch(rejected)
    const expiredConfig = loaded.harness.requestHandlers[0]({
      url: '/projects/1/messages', method: 'POST', headers: {},
    })
    const expired = loaded.harness.responseFailures[0]({
      config: expiredConfig,
      response: { status: 401, data: { message: 'expired' } },
      message: 'expired',
    })

    if (profileFirst) {
      profile.resolve(profileResponse(8))
      await denied
      refresh.resolve({ data: completeRefreshPayload('new-token', 1) })
      await expired
    } else {
      refresh.resolve({ data: completeRefreshPayload('new-token', 1) })
      await expired
      profile.resolve(profileResponse(8))
      await denied
    }

    expect(loaded.auth.useAuth.getState()).toMatchObject({
      token: 'new-token',
      user: { id: 1 },
      permissions: ['perm-1'],
      menus: ['menu-1'],
      generation: sessionGeneration,
    })
    expect(loaded.harness.axios.get).toHaveBeenCalledOnce()
    expect(loaded.harness.axios.post).toHaveBeenCalledOnce()
    expect(loaded.harness.replays).toHaveLength(1)
    expect(loaded.harness.replays[0]).toMatchObject({
      url: '/projects/1/messages',
      _retried: true,
      headers: { Authorization: 'Bearer new-token' },
    })
  }

  it('permission profile and token refresh preserve one session (profile first: true)', async () => {
    await verifyProfileAndTokenRefreshRace(true)
  })

  it('permission profile and token refresh preserve one session (profile first: false)', async () => {
    await verifyProfileAndTokenRefreshRace(false)
  })

  it('the shared client no longer hides obsolete 428 responses', async () => {
    const loaded = await loadClient()
    const error = {
      config: { authGeneration: 0, url: '/auth/login', headers: {} },
      response: { status: 428, data: { message: '登录请求失败' } },
      message: 'failure',
    }
    await expect(loaded.harness.responseFailures[0](error)).rejects.toBe(error)
    expect(loaded.harness.errors).toEqual(['登录请求失败'])
  })

  it('preserves numeric API error codes and accepts only safe relative login return paths', async () => {
    const loaded = await loadClient()
    expect(loaded.client.getApiErrorCode({ isAxiosError: true, response: { data: { code: 40901 } } })).toBe(40901)
    expect(loaded.client.isSafeLoginReturnPath('/projects/7?tab=messages')).toBe(true)
    expect(loaded.client.isSafeLoginReturnPath('//external.invalid')).toBe(false)
    expect(loaded.client.isSafeLoginReturnPath('/projects\\7')).toBe(false)
    expect(loaded.client.buildLoginRedirectHref('/projects/7', '?tab=messages', '#latest'))
      .toBe('/login?from=%2Fprojects%2F7%3Ftab%3Dmessages%23latest')
    expect(loaded.client.isOemPortalPath('/oem-portal')).toBe(true)
    expect(loaded.client.isOemPortalPath('/oem-portal/transfers/1')).toBe(true)
    expect(loaded.client.isOemPortalPath('/oem')).toBe(false)
  })
})
