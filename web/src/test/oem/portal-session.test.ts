import { createRequire } from 'node:module'
import axios, { AxiosError, type AxiosAdapter, type InternalAxiosRequestConfig } from 'axios'
import { Message } from '@arco-design/web-react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, renderHook } from '@testing-library/react'
import {
  createPortalQueryScope,
  portalHttp,
  portalQueryClient,
  usePortalAuth,
  usePortalSessionSync,
  type PortalAccount,
} from '../../oem/api/portalSession'

const account: PortalAccount = {
  id: 31,
  employeeNo: 'OEM31',
  realName: '厂商用户',
  email: 'oem31@example.test',
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

describe('OEM 厂商会话缓存隔离', () => {
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

  it('其他标签退出后同步清除当前 OEM 门户会话', () => {
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

describe('OEM 门户 HTTP 拦截器', () => {
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

  it('40303 进入修改密码且不弹出错误', async () => {
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
      expect(assigned).toContain('/oem-portal/change-password')
      expect(toast).not.toHaveBeenCalled()
    } finally {
      Object.defineProperty(implementation.prototype, 'href', desc)
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
