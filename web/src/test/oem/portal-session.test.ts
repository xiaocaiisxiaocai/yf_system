import { createRequire } from 'node:module'
import axios, { AxiosError, type AxiosAdapter, type InternalAxiosRequestConfig } from 'axios'
import { Message } from '@arco-design/web-react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, renderHook } from '@testing-library/react'
import {
  bootPortalSession,
  createPortalQueryScope,
  portalHttp,
  portalLogout,
  portalQueryClient,
  retryBootPortalSession,
  usePortalAuth,
  usePortalSessionSync,
  type PortalAccount,
} from '../../oem/api/portalSession'
import { SessionChangedError } from '../../api/client'

const account: PortalAccount = {
  id: 31,
  employeeNo: 'OEM31',
  companyId: 9,
  companyName: '测试代工厂',
}

afterEach(() => {
  usePortalAuth.setState({
    token: null,
    account: null,
    mustChangePassword: false,
    booted: false,
    generation: 0,
  })
  portalQueryClient.clear()
})

describe('ODM/OEMA 厂商会话缓存隔离', () => {
  it('账号切换和退出会清空门户缓存，刷新同一会话则保留缓存', () => {
    portalQueryClient.setQueryData(['oem', 'transfers'], ['old-account-data'])
    usePortalAuth.getState().setLogin('token-1', account, false)

    expect(usePortalAuth.getState().generation).toBe(1)
    expect(createPortalQueryScope(usePortalAuth.getState())).toEqual([31, 1])
    expect(portalQueryClient.getQueryData(['oem', 'transfers'])).toBeUndefined()

    portalQueryClient.setQueryData(['oem', 'transfers'], ['current-account-data'])
    usePortalAuth.getState().setLogin('token-2', account, false, false)

    expect(usePortalAuth.getState().generation).toBe(1)
    expect(portalQueryClient.getQueryData(['oem', 'transfers'])).toEqual(['current-account-data'])

    usePortalAuth.getState().logout()
    expect(usePortalAuth.getState().generation).toBe(2)
    expect(portalQueryClient.getQueryData(['oem', 'transfers'])).toBeUndefined()
  })

  it('其他标签退出后同步清除当前 ODM/OEMA 门户会话', () => {
    usePortalAuth.getState().setLogin('token-1', account, false)
    renderHook(() => usePortalSessionSync())

    act(() => {
      window.dispatchEvent(new StorageEvent('storage', {
        key: 'yf-oem-portal',
        newValue: JSON.stringify({ state: { account: null }, version: 2 }),
      }))
    })

    expect(usePortalAuth.getState().token).toBeNull()
    expect(usePortalAuth.getState().account).toBeNull()
    expect(usePortalAuth.getState().generation).toBe(2)
  })
})

function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((ok) => { resolve = ok })
  return { promise, resolve }
}

function responseOf(config: InternalAxiosRequestConfig, data: unknown) {
  return { data, status: 200, statusText: 'OK', headers: {}, config }
}

function httpError(config: InternalAxiosRequestConfig, status: number, data: unknown, code = 'ERR_BAD_REQUEST') {
  return new AxiosError('failed', code, config, undefined, {
    status, statusText: '', headers: {}, config, data,
  })
}

describe('ODM/OEMA 门户 HTTP 拦截器', () => {
  beforeEach(() => {
    usePortalAuth.setState({
      token: 'old-token',
      account,
      mustChangePassword: false,
      booted: true,
      generation: 4,
    })
    window.history.replaceState({}, '', '/oem-portal/transfers')
  })

  afterEach(async () => {
    // refreshSession 用 setTimeout(0) 清掉模块级 refreshing；不等这一拍，下一例会复用已完成的刷新。
    await new Promise((resolve) => setTimeout(resolve, 0))
    vi.restoreAllMocks()
    window.history.replaceState({}, '', '/')
  })

  function mockRefresh() {
    const post = vi.spyOn(axios, 'post').mockResolvedValue({ data: { accessToken: 'new-token' } } as never)
    const get = vi.spyOn(axios, 'get').mockResolvedValue({ data: { account, mustChangePassword: false } } as never)
    return { post, get }
  }

  it('401 刷新后只重试一次', async () => {
    const { post, get } = mockRefresh()
    const toast = vi.spyOn(Message, 'error').mockImplementation(() => () => undefined)
    const calls: InternalAxiosRequestConfig[] = []
    const adapter: AxiosAdapter = async (config) => {
      calls.push(config)
      if ((config as { _retried?: boolean })._retried) return responseOf(config, { ok: true })
      throw httpError(config, 401, { message: '登录已过期' })
    }

    const response = await portalHttp.get('/oem/transfers', { adapter })

    expect(response.data).toEqual({ ok: true })
    expect(calls).toHaveLength(2)
    expect(post).toHaveBeenCalledTimes(1)
    expect(String(post.mock.calls[0][0])).toBe('/api/v1/oem/auth/refresh')
    expect(get).toHaveBeenCalledTimes(1)
    expect(String(get.mock.calls[0][0])).toBe('/api/v1/oem/auth/me')
    expect(usePortalAuth.getState()).toMatchObject({ token: 'new-token', generation: 4, account: { id: 31 } })
    expect(toast).not.toHaveBeenCalled()
  })

  it('并行 401 共用一次刷新', async () => {
    const refresh = deferred<{ data: { accessToken: string } }>()
    const post = vi.spyOn(axios, 'post').mockReturnValue(refresh.promise as never)
    const get = vi.spyOn(axios, 'get').mockResolvedValue({ data: { account, mustChangePassword: false } } as never)
    const attempts: boolean[] = []
    const adapter: AxiosAdapter = async (config) => {
      const retried = Boolean((config as { _retried?: boolean })._retried)
      attempts.push(retried)
      if (retried) return responseOf(config, { ok: true })
      throw httpError(config, 401, { message: '登录已过期' })
    }

    const pending = Promise.all([
      portalHttp.get('/oem/transfers', { adapter }),
      portalHttp.get('/oem/companies', { adapter }),
    ])
    await vi.waitFor(() => expect(attempts.filter((retried) => !retried)).toHaveLength(2))
    expect(post).toHaveBeenCalledTimes(1)
    refresh.resolve({ data: { accessToken: 'new-token' } })

    const [first, second] = await pending
    expect(first.data).toEqual({ ok: true })
    expect(second.data).toEqual({ ok: true })
    expect(post).toHaveBeenCalledTimes(1)
    expect(get).toHaveBeenCalledTimes(1)
    expect(attempts.filter((retried) => retried)).toHaveLength(2)
  })

  it('刷新进行中退出登录不会恢复会话', async () => {
    const refresh = deferred<{ data: { accessToken: string } }>()
    vi.spyOn(axios, 'post').mockReturnValue(refresh.promise as never)
    const get = vi.spyOn(axios, 'get').mockResolvedValue({ data: { account, mustChangePassword: false } } as never)
    const adapter = vi.fn(async (config: InternalAxiosRequestConfig) => {
      throw httpError(config, 401, { message: '登录已过期' })
    })
    const pending = portalHttp.get('/oem/transfers', { adapter })
    await vi.waitFor(() => expect(axios.post).toHaveBeenCalledOnce())

    usePortalAuth.getState().logout()
    refresh.resolve({ data: { accessToken: 'stale-token' } })
    await expect(pending).rejects.toBeTruthy()

    expect(adapter).toHaveBeenCalledTimes(1)
    expect(usePortalAuth.getState()).toMatchObject({ token: null, account: null, generation: 5 })
    expect(get.mock.calls.length).toBeLessThanOrEqual(1)
    expect(usePortalAuth.getState().token).not.toBe('stale-token')
  })

  it('刷新成功但读取账号时网络中断：保留会话并用新令牌重试', async () => {
    vi.spyOn(axios, 'post').mockResolvedValue({ data: { accessToken: 'new-token' } } as never)
    vi.spyOn(axios, 'get').mockRejectedValue(new AxiosError('Network Error', 'ERR_NETWORK'))
    const tokens: unknown[] = []
    const adapter: AxiosAdapter = async (config) => {
      tokens.push(config.headers.Authorization)
      if ((config as { _retried?: boolean })._retried) return responseOf(config, { ok: true })
      throw httpError(config, 401, { message: '登录已过期' })
    }

    const response = await portalHttp.get('/oem/transfers', { adapter })

    expect(response.data).toEqual({ ok: true })
    expect(tokens).toEqual(['Bearer old-token', 'Bearer new-token'])
    expect(usePortalAuth.getState()).toMatchObject({ token: 'new-token', generation: 4, account: { id: 31 } })
  })

  it('启动恢复时读取账号网络中断不退出，账号读取被拒才退出', async () => {
    const toast = vi.spyOn(Message, 'error').mockImplementation(() => () => undefined)
    vi.spyOn(axios, 'post').mockResolvedValue({ data: { accessToken: 'new-token' } } as never)
    const get = vi.spyOn(axios, 'get').mockRejectedValue(new AxiosError('Network Error', 'ERR_NETWORK'))
    usePortalAuth.setState({ token: null, account: { id: 31 } as PortalAccount, booted: false, generation: 4 })

    await bootPortalSession()

    expect(usePortalAuth.getState()).toMatchObject({ token: null, booted: true, generation: 4, account: { id: 31 } })
    expect(toast).toHaveBeenCalledOnce()

    await new Promise((resolve) => setTimeout(resolve, 0))
    get.mockImplementation(async (url) => { throw httpError({ url, headers: {} } as InternalAxiosRequestConfig, 401, {}) })
    usePortalAuth.setState({ booted: false })
    await bootPortalSession()
    expect(usePortalAuth.getState()).toMatchObject({ token: null, account: null, booted: true, generation: 5 })
  })

  it('刷新请求本身网络中断或服务端 5xx：保留会话，不退出也不跳转', async () => {
    const toast = vi.spyOn(Message, 'error').mockImplementation(() => () => undefined)
    const post = vi.spyOn(axios, 'post').mockRejectedValue(new AxiosError('Network Error', 'ERR_NETWORK'))
    const adapter: AxiosAdapter = async (config) => { throw httpError(config, 401, { message: '登录已过期' }) }

    await expect(portalHttp.get('/oem/transfers', { adapter })).rejects.toBeTruthy()

    expect(usePortalAuth.getState()).toMatchObject({ token: 'old-token', account: { id: 31 }, generation: 4 })
    expect(window.location.pathname).toBe('/oem-portal/transfers')
    expect(toast).toHaveBeenCalledOnce()

    await new Promise((resolve) => setTimeout(resolve, 0))
    post.mockImplementation(async (url) => { throw httpError({ url, headers: {} } as InternalAxiosRequestConfig, 503, {}) })
    await expect(portalHttp.get('/oem/transfers', { adapter })).rejects.toBeTruthy()
    expect(usePortalAuth.getState()).toMatchObject({ token: 'old-token', account: { id: 31 }, generation: 4 })
  })

  it('启动恢复时刷新请求网络中断不退出，刷新被拒才退出', async () => {
    vi.spyOn(Message, 'error').mockImplementation(() => () => undefined)
    const post = vi.spyOn(axios, 'post').mockRejectedValue(new AxiosError('Network Error', 'ERR_NETWORK'))
    usePortalAuth.setState({ token: null, account: { id: 31 } as PortalAccount, booted: false, generation: 4 })

    await bootPortalSession()
    expect(usePortalAuth.getState()).toMatchObject({ token: null, booted: true, generation: 4, account: { id: 31 } })

    await new Promise((resolve) => setTimeout(resolve, 0))
    post.mockImplementation(async (url) => { throw httpError({ url, headers: {} } as InternalAxiosRequestConfig, 401, {}) })
    usePortalAuth.setState({ booted: false })
    await bootPortalSession()
    expect(usePortalAuth.getState()).toMatchObject({ token: null, account: null, booted: true, generation: 5 })
  })

  it('40303 更新 mustChangePassword 且不触发整页跳转或弹出错误', async () => {
    const toast = vi.spyOn(Message, 'error').mockImplementation(() => () => undefined)
    // location.href 是不可配置的实例属性；jsdom 也不实现跨文档导航。拦截真正执行赋值的 Location 实现。
    const require = createRequire(import.meta.url)
    const { implementation } = require('jsdom/lib/jsdom/living/window/Location-impl.js') as {
      implementation: { prototype: object }
    }
    const desc = Object.getOwnPropertyDescriptor(implementation.prototype, 'href')
    if (!desc?.set || desc.configurable === false) throw new Error('无法拦截 location.href')
    const assigned: string[] = []
    Object.defineProperty(implementation.prototype, 'href', {
      configurable: true,
      enumerable: desc.enumerable,
      get: desc.get,
      set(value: string) { assigned.push(String(value)) },
    })
    try {
      const adapter: AxiosAdapter = async (config) => {
        throw httpError(config, 403, { code: 40303, message: '请先修改密码' })
      }

      await expect(portalHttp.get('/oem/transfers', { adapter })).rejects.toBeTruthy()

      expect(usePortalAuth.getState().mustChangePassword).toBe(true)
      expect(assigned).toEqual([])
      expect(toast).not.toHaveBeenCalled()
    } finally {
      Object.defineProperty(implementation.prototype, 'href', desc)
    }
  })

  it('刷新返回另一个厂商账号时递增代际且不重放旧账号请求', async () => {
    const toast = vi.spyOn(Message, 'error').mockImplementation(() => () => undefined)
    const switchedAccount: PortalAccount = { id: 99, employeeNo: 'OEM99', companyId: 12, companyName: '另一家代工厂' }
    vi.spyOn(axios, 'post').mockResolvedValue({ data: { accessToken: 'switched-token' } } as never)
    vi.spyOn(axios, 'get').mockResolvedValue({ data: { account: switchedAccount, mustChangePassword: false } } as never)
    const adapter = vi.fn(async (config: InternalAxiosRequestConfig) => {
      throw httpError(config, 401, { message: '登录已过期' })
    })

    await expect(portalHttp.get('/oem/transfers', { adapter })).rejects.toBeTruthy()

    expect(adapter).toHaveBeenCalledTimes(1)
    expect(usePortalAuth.getState()).toMatchObject({
      token: 'switched-token',
      account: switchedAccount,
      generation: 5,
    })
    expect(toast).not.toHaveBeenCalled()
  })

  it('代际过期抛出 SessionChangedError 且不弹出网络错误提示', async () => {
    const toast = vi.spyOn(Message, 'error').mockImplementation(() => () => undefined)
    const adapter = vi.fn(async (config: InternalAxiosRequestConfig) => responseOf(config, { ok: true }))

    await expect(
      portalHttp.get('/oem/transfers', { adapter, authGeneration: 3 } as never),
    ).rejects.toBeInstanceOf(SessionChangedError)
    expect(adapter).not.toHaveBeenCalled()

    const slow = deferred<ReturnType<typeof responseOf>>()
    const responsePending = portalHttp.get('/oem/transfers', {
      adapter: async (config) => {
        const result = await slow.promise
        return { ...result, config }
      },
    })
    await Promise.resolve()
    usePortalAuth.getState().logout()
    slow.resolve(responseOf({} as InternalAxiosRequestConfig, { ok: true }))
    await expect(responsePending).rejects.toBeInstanceOf(SessionChangedError)
    expect(toast).not.toHaveBeenCalled()
  })

  it('离开门户路径后迟到的 401 与 40303 不登出、不改状态也不跳转', async () => {
    const toast = vi.spyOn(Message, 'error').mockImplementation(() => () => undefined)
    const refresh = deferred<{ data: { accessToken: string } }>()
    const post = vi.spyOn(axios, 'post').mockReturnValue(refresh.promise as never)
    const adapter = vi.fn(async (config: InternalAxiosRequestConfig) => {
      throw httpError(config, 401, { message: '登录已过期' })
    })

    const pending = portalHttp.get('/oem/transfers', { adapter })
    await vi.waitFor(() => expect(post).toHaveBeenCalledOnce())
    window.history.replaceState({}, '', '/projects/1')
    refresh.resolve({ data: { accessToken: '' } })
    await expect(pending).rejects.toBeTruthy()

    expect(usePortalAuth.getState()).toMatchObject({ token: 'old-token', account: { id: 31 }, generation: 4 })
    expect(window.location.pathname).toBe('/projects/1')

    await expect(portalHttp.get('/oem/transfers', {
      adapter: async (config) => { throw httpError(config, 403, { code: 40303, message: '请先修改密码' }) },
    })).rejects.toBeTruthy()
    expect(usePortalAuth.getState().mustChangePassword).toBe(false)
    expect(toast).not.toHaveBeenCalled()
  })

  it('同代际已有更新令牌时 401 直接用新令牌重放而不再次旋转 cookie', async () => {
    const post = vi.spyOn(axios, 'post')
    const seenAuth: unknown[] = []
    const adapter: AxiosAdapter = async (config) => {
      seenAuth.push(config.headers.Authorization)
      if ((config as { _retried?: boolean })._retried) return responseOf(config, { ok: true })
      // 模拟并行请求已把 store 里的令牌更新为 newer-token
      usePortalAuth.getState().setToken('newer-token')
      throw httpError(config, 401, { message: '登录已过期' })
    }

    const response = await portalHttp.get('/oem/transfers', { adapter })
    expect(response.data).toEqual({ ok: true })
    expect(post).not.toHaveBeenCalled()
    expect(seenAuth).toEqual(['Bearer old-token', 'Bearer newer-token'])
  })

  it('启动失败标记 bootUnavailable，retryBootPortalSession 单飞恢复并在被拒时登出', async () => {
    vi.spyOn(Message, 'error').mockImplementation(() => () => undefined)
    const post = vi.spyOn(axios, 'post').mockRejectedValueOnce(new AxiosError('Network Error', 'ERR_NETWORK'))
    usePortalAuth.setState({ token: null, account: { id: 31 } as PortalAccount, booted: false, bootUnavailable: false, generation: 4 })

    await bootPortalSession()
    expect(usePortalAuth.getState()).toMatchObject({ token: null, booted: true, bootUnavailable: true, generation: 4 })

    await new Promise((resolve) => setTimeout(resolve, 0))
    const retryRefresh = deferred<{ data: { accessToken: string } }>()
    post.mockReturnValueOnce(retryRefresh.promise as never)
    vi.spyOn(axios, 'get').mockResolvedValueOnce({ data: { account, mustChangePassword: false } } as never)

    const p1 = retryBootPortalSession()
    const p2 = retryBootPortalSession()
    await vi.waitFor(() => expect(post).toHaveBeenCalledTimes(2))
    retryRefresh.resolve({ data: { accessToken: 'restored-token' } })
    await Promise.all([p1, p2])

    expect(usePortalAuth.getState()).toMatchObject({ token: 'restored-token', bootUnavailable: false, generation: 4 })

    await new Promise((resolve) => setTimeout(resolve, 0))
    usePortalAuth.setState({ token: null, account: { id: 31 } as PortalAccount, booted: true, bootUnavailable: true, generation: 4 })
    post.mockRejectedValueOnce(httpError({ url: '/api/v1/oem/auth/refresh', headers: {} } as InternalAxiosRequestConfig, 401, {}))
    await retryBootPortalSession()
    expect(usePortalAuth.getState()).toMatchObject({ token: null, account: null, bootUnavailable: false, generation: 5 })
  })

  it('portalLogout 对登出接口使用 quietNetworkError，网络故障时不弹错误提示', async () => {
    const toast = vi.spyOn(Message, 'error').mockImplementation(() => () => undefined)
    const previousAdapter = portalHttp.defaults.adapter
    portalHttp.defaults.adapter = async (config) => {
      throw httpError(config, 503, { message: '服务暂不可用' })
    }
    try {
      await portalLogout()
      expect(usePortalAuth.getState().token).toBeNull()
      expect(toast).not.toHaveBeenCalled()
    } finally {
      portalHttp.defaults.adapter = previousAdapter
    }
  })

  it('取消的请求不弹出 canceled', async () => {
    const toast = vi.spyOn(Message, 'error').mockImplementation(() => () => undefined)
    const post = vi.spyOn(axios, 'post')
    const canceled = new AxiosError('canceled', 'ERR_CANCELED')
    const adapter: AxiosAdapter = async () => { throw canceled }

    await expect(portalHttp.get('/oem/files/8/content', { adapter })).rejects.toMatchObject({ code: 'ERR_CANCELED' })

    expect(toast).not.toHaveBeenCalled()
    expect(post).not.toHaveBeenCalled()
    expect(usePortalAuth.getState()).toMatchObject({ token: 'old-token', generation: 4, account: { id: 31 } })
  })
})
