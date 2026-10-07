import axios from 'axios'
import type { AxiosError, InternalAxiosRequestConfig } from 'axios'
import { create } from 'zustand'
import { persist } from 'zustand/middleware'
import { Message } from '@arco-design/web-react'
import { useEffect } from 'react'
import { REFRESH_TIMEOUT_MS, type QuietRequestConfig } from '../../api/client'
import { withPortalAuthLock } from './portalAuthLock'
import { isQuietedError } from './quietErrors'
import { createAppQueryClient } from '../../api/queryClient'
import type { AccountBrief, ApiResponses } from './types'

/** OEM vendor account (realm=oem). Never mixed with the collaboration user store. */
export type PortalAccount = AccountBrief

export interface PortalAuthState {
  /** Access token lives in memory only; a page reload re-issues it from the OEM refresh cookie. */
  token: string | null
  account: PortalAccount | null
  mustChangePassword: boolean
  booted: boolean
  generation: number
  setLogin: (token: string, account: PortalAccount, mustChangePassword: boolean, replaceSession?: boolean) => void
  setToken: (token: string) => void
  setBooted: (booted: boolean) => void
  logout: () => void
}

export const PORTAL_BASE = '/oem-portal'

export const usePortalAuth = create<PortalAuthState>()(
  persist(
    (set, get) => ({
      token: null,
      account: null,
      mustChangePassword: false,
      booted: false,
      generation: 0,
      setLogin: (token, account, mustChangePassword, replaceSession = true) => set({
        token,
        account,
        mustChangePassword,
        booted: true,
        generation: get().generation + (replaceSession ? 1 : 0),
      }),
      setToken: (token) => set({ token }),
      setBooted: (booted) => set({ booted }),
      logout: () => set({
        token: null,
        account: null,
        mustChangePassword: false,
        generation: get().generation + 1,
      }),
    }),
    {
      name: 'yf-oem-portal',
      version: 2,
      migrate: (persisted) => {
        const previous = persisted as { account?: { id?: unknown } } | undefined
        return {
          account: typeof previous?.account?.id === 'number'
            ? ({ id: previous.account.id } as PortalAccount)
            : null,
        }
      },
      partialize: (state) => ({ account: state.account ? ({ id: state.account.id } as PortalAccount) : null }),
    },
  ),
)

/** HTTP client of the OEM portal: its own token, refresh endpoint and redirects. */
export const portalHttp = axios.create({ baseURL: '/api/v1', timeout: 60000 })
export const portalQueryClient = createAppQueryClient()

export function createPortalQueryScope(state: Pick<PortalAuthState, 'account' | 'generation'>): readonly [number | null, number] {
  return [state.account?.id ?? null, state.generation]
}

usePortalAuth.subscribe((state, previous) => {
  if (JSON.stringify(createPortalQueryScope(state)) === JSON.stringify(createPortalQueryScope(previous))) return
  void portalQueryClient.cancelQueries()
  portalQueryClient.clear()
})

let refreshing: Promise<boolean> | null = null
let refreshingGeneration: number | undefined
type PortalSessionConfig = InternalAxiosRequestConfig & QuietRequestConfig & { _retried?: boolean; authGeneration?: number }

const isCurrentSession = (config?: PortalSessionConfig) =>
  !config || config.authGeneration === usePortalAuth.getState().generation

function isPortalAccount(value: unknown): value is PortalAccount {
  if (!value || typeof value !== 'object') return false
  const account = value as Partial<PortalAccount>
  return typeof account.id === 'number' && account.id > 0
    && typeof account.employeeNo === 'string' && account.employeeNo.length > 0
    && typeof account.realName === 'string' && account.realName.length > 0
    && typeof account.email === 'string'
    && typeof account.companyId === 'number' && account.companyId > 0
    && typeof account.companyName === 'string' && account.companyName.length > 0
}

async function refreshSession(generation = usePortalAuth.getState().generation): Promise<boolean> {
  if (usePortalAuth.getState().generation !== generation) return false
  if (!refreshing || refreshingGeneration !== generation) {
    refreshingGeneration = generation
    const pending = withPortalAuthLock(async () => {
      if (usePortalAuth.getState().generation !== generation) return false
      const response = await axios.post<ApiResponses['POST /oem/auth/refresh']>('/api/v1/oem/auth/refresh', null, { withCredentials: true, timeout: REFRESH_TIMEOUT_MS })
      const token = response.data?.accessToken
      if (typeof token !== 'string' || token.length === 0) throw new Error('OEM 刷新响应格式无效')
      const me = await axios.get<ApiResponses['GET /oem/auth/me']>('/api/v1/oem/auth/me', { headers: { Authorization: `Bearer ${token}` }, timeout: REFRESH_TIMEOUT_MS })
      if (usePortalAuth.getState().generation !== generation
        || !isPortalAccount(me.data?.account)
        || typeof me.data?.mustChangePassword !== 'boolean') return false
      usePortalAuth.getState().setLogin(token, me.data.account, me.data.mustChangePassword, false)
      return true
    })
    .catch(() => false)
    .finally(() => setTimeout(() => { if (refreshing === pending) refreshing = null }, 0))
    refreshing = pending
  }
  return refreshing
}

export async function bootPortalSession(): Promise<void> {
  const state = usePortalAuth.getState()
  if (state.booted) return
  const generation = state.generation
  if (state.account && !state.token && !(await refreshSession(generation))
    && usePortalAuth.getState().generation === generation) usePortalAuth.getState().logout()
  usePortalAuth.getState().setBooted(true)
}

/** Keep OEM portal tabs aligned without sharing any state with the internal realm. */
export function usePortalSessionSync(): void {
  useEffect(() => {
    const onStorage = (event: StorageEvent) => {
      if (event.key !== 'yf-oem-portal') return
      try {
        const nextId = event.newValue ? JSON.parse(event.newValue)?.state?.account?.id : null
        const current = usePortalAuth.getState()
        if (!nextId && current.account) current.logout()
        else if (typeof nextId === 'number' && nextId !== current.account?.id) location.reload()
      } catch {
        // A malformed value is ignored; the refresh endpoint remains authoritative.
      }
    }
    window.addEventListener('storage', onStorage)
    return () => window.removeEventListener('storage', onStorage)
  }, [])
}

portalHttp.interceptors.request.use((config) => {
  const sessionConfig = config as PortalSessionConfig
  sessionConfig.authGeneration ??= usePortalAuth.getState().generation
  if (!isCurrentSession(sessionConfig)) throw new Error('OEM 登录状态已变化，请重新操作')
  const token = usePortalAuth.getState().token
  if (token) config.headers.Authorization = `Bearer ${token}`
  return config
})

portalHttp.interceptors.response.use(
  (response) => {
    if (!isCurrentSession(response.config as PortalSessionConfig)) throw new Error('OEM 登录状态已变化，请重新操作')
    return response
  },
  async (error: AxiosError<{ code?: number; message?: string }>) => {
    const config = error.config as PortalSessionConfig | undefined
    if (!isCurrentSession(config)) return Promise.reject(error)
    const status = error.response?.status
    const code = error.response?.data?.code
    if (status === 401 && config && !config._retried && !config.url?.includes('/oem/auth/')) {
      config._retried = true
      if (await refreshSession(config.authGeneration) && isCurrentSession(config)) return portalHttp(config)
    }
    if (status === 401 && isCurrentSession(config)) {
      usePortalAuth.getState().logout()
      if (location.pathname !== `${PORTAL_BASE}/login`) location.href = `${PORTAL_BASE}/login`
    }
    if (code === 40303) {
      usePortalAuth.setState({ mustChangePassword: true })
      if (location.pathname !== `${PORTAL_BASE}/change-password`) location.href = `${PORTAL_BASE}/change-password`
      return Promise.reject(error)
    }
    const message = error.response?.data?.message
      || (error.code === 'ERR_NETWORK' ? '网络连接失败，请稍后重试' : error.code === 'ECONNABORTED' ? '请求超时，请稍后重试' : error.message)
    if (!isQuietedError(error)) Message.error(message || '网络错误')
    return Promise.reject(error)
  },
)

export async function portalLogin(employeeNo: string, password: string): Promise<boolean> {
  const response = await portalHttp.post<ApiResponses['POST /oem/auth/login']>('/oem/auth/login', { employeeNo, password }, { withCredentials: true })
  if (typeof response.data?.accessToken !== 'string'
    || !isPortalAccount(response.data?.account)
    || typeof response.data?.mustChangePassword !== 'boolean') throw new Error('OEM 登录响应格式无效')
  usePortalAuth.getState().setLogin(response.data.accessToken, response.data.account, response.data.mustChangePassword)
  return response.data.mustChangePassword
}

export async function portalLogout(): Promise<void> {
  try { await portalHttp.post<ApiResponses['POST /oem/auth/logout']>('/oem/auth/logout', null, { withCredentials: true }) } catch { /* 忽略 */ }
  usePortalAuth.getState().logout()
}
