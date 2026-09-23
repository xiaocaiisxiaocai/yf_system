import axios from 'axios'
import type { AxiosError, AxiosRequestConfig } from 'axios'
import { Message } from '@arco-design/web-react'
import { useAuth, type AuthLoginResponse } from '../store/auth'

export const http = axios.create({ baseURL: '/api/v1', timeout: 60000 })

let refreshing: Promise<boolean> | null = null
let refreshingGeneration: number | undefined
type ProfileRefresh = { generation: number; token: string; promise: Promise<void> }
let refreshingProfile: ProfileRefresh | null = null
export type QuietRequestConfig = AxiosRequestConfig & {
  /** 仅抑制可重试的网络、超时、限流和服务端错误提示；鉴权与权限错误仍正常处理。 */
  quietNetworkError?: boolean
}
type SessionConfig = QuietRequestConfig & { _retried?: boolean; authGeneration?: number }
const isCurrentSession = (config?: SessionConfig) => !config || config.authGeneration === useAuth.getState().generation

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value)
const isNonEmptyString = (value: unknown): value is string => typeof value === 'string' && value.trim().length > 0
const isStringArray = (value: unknown): value is string[] =>
  Array.isArray(value) && value.every((item) => typeof item === 'string')
const hasTokenEnvelope = (value: Record<string, unknown>): boolean =>
  isNonEmptyString(value.accessToken) && typeof value.expiresAt === 'number'
  && Number.isSafeInteger(value.expiresAt) && value.expiresAt > 0

function isAuthLoginResponse(value: unknown): value is AuthLoginResponse {
  if (!isRecord(value) || !isRecord(value.user)) return false
  const user = value.user
  return typeof user.id === 'number' && Number.isFinite(user.id) && user.id > 0
    && isNonEmptyString(user.employeeNo) && isNonEmptyString(user.realName)
    && typeof user.email === 'string' && (user.userType === 'INTERNAL' || user.userType === 'SUPPLIER')
    && (user.supplierId === null
      || (typeof user.supplierId === 'number' && Number.isFinite(user.supplierId) && user.supplierId > 0))
    && typeof user.isSystemAdmin === 'boolean'
    && typeof value.mustChangePassword === 'boolean'
    && isStringArray(value.permissions) && isStringArray(value.menus)
    && hasTokenEnvelope(value)
}

/**
 * 同源标签共享 refresh cookie，必须在拿到浏览器锁后才发送旋转请求：两个标签同时用同一个旧 cookie 刷新，
 * 后到的一次会被服务端判为令牌重放并吊销整个会话。优先使用 Web Locks；它只在安全上下文（HTTPS/localhost）
 * 可用，内网 HTTP 部署时退回基于 localStorage 的租约锁。
 */
export function withAuthLock<T>(operation: () => Promise<T>): Promise<T> {
  if (typeof navigator !== 'undefined' && navigator.locks) {
    return navigator.locks.request('yf-auth-session', operation)
  }
  return withStorageLease(operation)
}

const STORAGE_LOCK_KEY = 'yf:auth-refresh-lock'
/** 租约上限：持有者崩溃或标签关闭后，其他标签最多等这么久即可接管。 */
const STORAGE_LEASE_MS = 15_000
const sleep = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms))

function readLease(): { owner: string; expires: number } | null {
  const raw = window.localStorage.getItem(STORAGE_LOCK_KEY)
  if (!raw) return null
  try {
    const value = JSON.parse(raw) as { owner?: unknown; expires?: unknown }
    return typeof value.owner === 'string' && typeof value.expires === 'number'
      ? { owner: value.owner, expires: value.expires }
      : null
  } catch {
    return null
  }
}

/**
 * localStorage 没有原子比较交换：写入后稍等再读回，只有读回仍是自己时才算拿到锁，
 * 足以把并发刷新串行化。存储不可用（隐私模式等）时直接执行，行为与改动前一致。
 */
export async function withStorageLease<T>(operation: () => Promise<T>): Promise<T> {
  if (typeof window === 'undefined') return operation()
  const owner = `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`
  const giveUpAt = Date.now() + STORAGE_LEASE_MS
  for (;;) {
    let acquired = false
    try {
      const held = readLease()
      if (!held || held.expires <= Date.now()) {
        window.localStorage.setItem(STORAGE_LOCK_KEY, JSON.stringify({ owner, expires: Date.now() + STORAGE_LEASE_MS }))
        await sleep(40)
        acquired = readLease()?.owner === owner
      }
    } catch {
      return operation()
    }
    if (acquired) {
      try {
        return await operation()
      } finally {
        try {
          if (readLease()?.owner === owner) window.localStorage.removeItem(STORAGE_LOCK_KEY)
        } catch {
          // 租约到期后自然失效。
        }
      }
    }
    if (Date.now() >= giveUpAt) return operation()
    await sleep(60 + Math.random() * 120)
  }
}

/** 权限被管理员调整后，403 会触发一次资料刷新，让菜单和按钮及时收敛到服务端状态。 */
async function refreshProfileAfterForbidden(): Promise<void> {
  const { token, generation } = useAuth.getState()
  if (!token) return
  const pending = refreshingProfile
  let promise: Promise<void>
  if (pending && pending.generation === generation && pending.token === token) {
    promise = pending.promise
  } else {
    promise = axios
      .get('/api/v1/auth/profile', { headers: { Authorization: `Bearer ${token}` }, timeout: 60000 })
      .then((response) => {
        if (useAuth.getState().generation === generation && useAuth.getState().token === token) {
          useAuth.getState().setLogin({ ...response.data, accessToken: token }, false)
        }
      })
      .catch(() => undefined)
      .finally(() => {
        setTimeout(() => {
          if (refreshingProfile?.promise === promise) refreshingProfile = null
        }, 0)
      })
    refreshingProfile = { generation, token, promise }
  }
  await promise
}

/** 单飞刷新：并发 401 共享同一次请求 */
async function tryRefresh(generation = useAuth.getState().generation): Promise<boolean> {
  if (useAuth.getState().generation !== generation) return false
  if (!refreshing || refreshingGeneration !== generation) {
    refreshingGeneration = generation
    const pending = withAuthLock(async () => {
      if (useAuth.getState().generation !== generation) return false
      return axios
      .post<unknown>('/api/v1/auth/refresh', null, { withCredentials: true, timeout: 60000 })
      .then((r) => {
        if (useAuth.getState().generation !== generation) return false
        if (!isAuthLoginResponse(r.data)) throw new Error('刷新响应格式无效')
        useAuth.getState().setLogin(r.data, false)
        return true
      })
    })
      .catch(() => false)
      .finally(() => {
        setTimeout(() => { if (refreshing === pending) refreshing = null }, 0)
      })
    refreshing = pending
  }
  return refreshing
}

/** 启动引导：有持久化用户但内存无 token 时，用 refresh cookie 静默换新 */
export async function bootAuth(): Promise<void> {
  const s = useAuth.getState()
  const generation = s.generation
  if (s.booted) return
  if (s.user && !s.token) {
    const ok = await tryRefresh()
    if (!ok && useAuth.getState().generation === generation) {
      // refresh cookie 已失效，清空持久化用户态
      s.logout()
    }
  }
  useAuth.getState().setBooted(true)
}

http.interceptors.request.use((config) => {
  const sessionConfig = config as typeof config & SessionConfig
  sessionConfig.authGeneration ??= useAuth.getState().generation
  if (!isCurrentSession(sessionConfig)) throw new Error('登录状态已变化，请重新操作')
  const token = useAuth.getState().token
  if (token) config.headers.Authorization = `Bearer ${token}`
  return config
})

http.interceptors.response.use(
  (resp) => {
    if (!isCurrentSession(resp.config)) throw new Error('登录状态已变化，请重新操作')
    return resp
  },
  async (error: AxiosError<{ code?: number; message?: string; requestId?: string }>) => {
    if (error.code === 'ERR_CANCELED') return Promise.reject(error)
    const cfg = error.config as SessionConfig
    if (!isCurrentSession(cfg)) return Promise.reject(error)
    const status = error.response?.status
    const biz = error.response?.data?.code
    if (status === 401 && cfg && !cfg._retried && !cfg.url?.includes('/auth/')) {
      cfg._retried = true
      const refreshed = await tryRefresh(cfg.authGeneration)
      if (!isCurrentSession(cfg)) return Promise.reject(error)
      if (refreshed) return http(cfg)
    }
    if (status === 401) {
      useAuth.getState().logout()
      if (location.pathname !== '/login') location.href = '/login'
    }
    // 40303 需先改密：跳强制改密页
    if (biz === 40303 && location.pathname !== '/change-password') {
      useAuth.setState({ mustChangePassword: true })
      location.href = '/change-password'
    }
    if (status === 403 && biz !== 40303 && !cfg?.url?.includes('/auth/profile')) {
      await refreshProfileAfterForbidden()
    }
    const connectionMessage = error.code === 'ERR_NETWORK'
      ? '网络连接失败，请稍后重试'
      : error.code === 'ECONNABORTED' ? '请求超时，请稍后重试' : undefined
    const baseMsg = error.response?.data?.message || connectionMessage || error.message || '网络错误'
    // 服务端 500 附带请求编号，便于用户反馈时对照服务器日志与操作日志。
    const requestId = status === 500 ? error.response?.data?.requestId : undefined
    const msg = requestId ? `${baseMsg}（请求编号：${requestId}）` : baseMsg
    const quietTransientError = cfg?.quietNetworkError === true && (
      error.code === 'ERR_NETWORK'
      || error.code === 'ECONNABORTED'
      || status === 404
      || status === 408
      || status === 429
      || (typeof status === 'number' && status >= 500)
    )
    // 40303 由跳转承载
    if (biz !== 40303 && !quietTransientError) Message.error(msg)
    return Promise.reject(error)
  }
)

export default http
