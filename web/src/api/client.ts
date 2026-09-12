import axios from 'axios'
import type { AxiosError, AxiosRequestConfig } from 'axios'
import { Message } from '@arco-design/web-react'
import { useAuth } from '../store/auth'

export const http = axios.create({ baseURL: '/api/v1', timeout: 60000 })

let refreshing: Promise<boolean> | null = null
let refreshingGeneration: number | undefined
type ProfileRefresh = { generation: number; token: string; promise: Promise<void> }
let refreshingProfile: ProfileRefresh | null = null
type SessionConfig = AxiosRequestConfig & { _retried?: boolean; authGeneration?: number }
const isCurrentSession = (config?: SessionConfig) => !config || config.authGeneration === useAuth.getState().generation

/** 同源标签共享 refresh cookie，必须在拿到浏览器锁后才发送旋转请求。生产使用 HTTPS。 */
export function withAuthLock<T>(operation: () => Promise<T>): Promise<T> {
  if (typeof navigator !== 'undefined' && navigator.locks) {
    return navigator.locks.request('yf-auth-session', operation)
  }
  return operation()
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
      .post('/api/v1/auth/refresh', null, { withCredentials: true, timeout: 60000 })
      .then(async (r) => {
        const accessToken: string = r.data.accessToken
        const profile = await axios.get('/api/v1/auth/profile', {
          headers: { Authorization: `Bearer ${accessToken}` },
          timeout: 60000,
        })
        if (useAuth.getState().generation !== generation) return false
        useAuth.getState().setLogin({ ...profile.data, accessToken }, false)
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
  async (error: AxiosError<{ code?: number; message?: string }>) => {
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
    const msg = error.response?.data?.message || connectionMessage || error.message || '网络错误'
    // 40303 由跳转承载
    if (biz !== 40303) Message.error(msg)
    return Promise.reject(error)
  }
)

export default http
