import axios from 'axios'
import type { AxiosError, InternalAxiosRequestConfig } from 'axios'
import { create } from 'zustand'
import { persist } from 'zustand/middleware'
import { Message } from '@arco-design/web-react'
import { useEffect } from 'react'
import { REFRESH_TIMEOUT_MS, type QuietRequestConfig } from '../../api/client'
import { withPortalAuthLock } from './portalAuthLock'
import { isQuietRequestError } from '../../api/quietRequest'
import { oemRequestError } from './requestError'
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

let refreshing: Promise<RefreshOutcome> | null = null
let refreshingGeneration: number | undefined
type PortalSessionConfig = InternalAxiosRequestConfig & QuietRequestConfig & { _retried?: boolean; authGeneration?: number }

const isCurrentSession = (config?: PortalSessionConfig) =>
  !config || config.authGeneration === usePortalAuth.getState().generation

function isPortalAccount(value: unknown): value is PortalAccount {
  if (!value || typeof value !== 'object') return false
  const account = value as Partial<PortalAccount>
  return typeof account.id === 'number' && account.id > 0
    && typeof account.employeeNo === 'string' && account.employeeNo.length > 0
    && typeof account.companyId === 'number' && account.companyId > 0
    && typeof account.companyName === 'string' && account.companyName.length > 0
}

/**
 * ok: the session is usable (a new token is stored). denied: the refresh cookie or the account was
 * rejected, so the session is over. unavailable: refresh worked but the account could not be loaded
 * for a reason other than auth (network blip); the rotated cookie stays valid, so keep the session.
 */
type RefreshOutcome = 'ok' | 'denied' | 'unavailable'

const isAuthFailure = (error: unknown) =>
  axios.isAxiosError(error) && (error.response?.status === 401 || error.response?.status === 403)

/**
 * A refresh request that got no definite answer (network, timeout, 429, 5xx) or failed to take the
 * cross-tab lock says nothing about the cookie, so it must not end the session.
 */
function classifyRefreshFailure(error: unknown): RefreshOutcome {
  if (!axios.isAxiosError(error)) return 'unavailable'
  const status = error.response?.status
  if (status === undefined || status === 408 || status === 425 || status === 429 || status >= 500) return 'unavailable'
  return 'denied'
}

async function refreshSession(generation = usePortalAuth.getState().generation): Promise<RefreshOutcome> {
  if (usePortalAuth.getState().generation !== generation) return 'denied'
  if (!refreshing || refreshingGeneration !== generation) {
    refreshingGeneration = generation
    const pending = withPortalAuthLock(async (): Promise<RefreshOutcome> => {
      if (usePortalAuth.getState().generation !== generation) return 'denied'
      const response = await axios.post<ApiResponses['POST /oem/auth/refresh']>('/api/v1/oem/auth/refresh', null, { withCredentials: true, timeout: REFRESH_TIMEOUT_MS })
      const token = response.data?.accessToken
      if (typeof token !== 'string' || token.length === 0) return 'denied'
      let me: { data?: ApiResponses['GET /oem/auth/me'] }
      try {
        me = await axios.get<ApiResponses['GET /oem/auth/me']>('/api/v1/oem/auth/me', { headers: { Authorization: `Bearer ${token}` }, timeout: REFRESH_TIMEOUT_MS })
      } catch (error) {
        if (isAuthFailure(error) || usePortalAuth.getState().generation !== generation) return 'denied'
        // A signed-in tab already holds the full account: the new token alone restores the session.
        // A booting tab only has the persisted id and cannot render without the account.
        if (isPortalAccount(usePortalAuth.getState().account)) {
          usePortalAuth.getState().setToken(token)
          return 'ok'
        }
        return 'unavailable'
      }
      if (usePortalAuth.getState().generation !== generation
        || !isPortalAccount(me.data?.account)
        || typeof me.data?.mustChangePassword !== 'boolean') return 'denied'
      usePortalAuth.getState().setLogin(token, me.data.account, me.data.mustChangePassword, false)
      return 'ok'
    })
    .catch(classifyRefreshFailure)
    .finally(() => setTimeout(() => { if (refreshing === pending) refreshing = null }, 0))
    refreshing = pending
  }
  return refreshing
}

const UNAVAILABLE_MESSAGE = '网络错误，暂时无法恢复登录状态，请稍后重试'

export async function bootPortalSession(): Promise<void> {
  const state = usePortalAuth.getState()
  if (state.booted) return
  const generation = state.generation
  if (state.account && !state.token) {
    const outcome = await refreshSession(generation)
    if (usePortalAuth.getState().generation === generation) {
      if (outcome === 'denied') usePortalAuth.getState().logout()
      else if (outcome === 'unavailable') Message.error(UNAVAILABLE_MESSAGE)
    }
  }
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
    if (error.code === 'ERR_CANCELED') return Promise.reject(error)
    const config = error.config as PortalSessionConfig | undefined
    if (!isCurrentSession(config)) return Promise.reject(error)
    const status = error.response?.status
    const code = error.response?.data?.code
    if (status === 401 && config && !config._retried && !config.url?.includes('/oem/auth/')) {
      config._retried = true
      const outcome = await refreshSession(config.authGeneration)
      if (outcome === 'ok' && isCurrentSession(config)) return portalHttp(config)
      if (outcome === 'unavailable') {
        Message.error(UNAVAILABLE_MESSAGE)
        return Promise.reject(error)
      }
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
    if (!isQuietRequestError(error)) {
      Message.error(oemRequestError(error, typeof status === 'number' ? `请求失败（${status}），请稍后重试` : '网络错误'))
    }
    return Promise.reject(error)
  },
)

/*
 * Login, logout and password change all replace or revoke the portal refresh cookie, so they run under
 * the same cross-tab lock as refresh: a login landing while another tab rotates the old cookie would
 * otherwise be overwritten (or trip replay revocation) and lose the new session.
 */
export async function portalLogin(employeeNo: string, password: string): Promise<boolean> {
  return withPortalAuthLock(async () => {
    const response = await portalHttp.post<ApiResponses['POST /oem/auth/login']>('/oem/auth/login', { employeeNo, password }, { withCredentials: true })
    if (typeof response.data?.accessToken !== 'string'
      || !isPortalAccount(response.data?.account)
      || typeof response.data?.mustChangePassword !== 'boolean') throw new Error('OEM 登录响应格式无效')
    usePortalAuth.getState().setLogin(response.data.accessToken, response.data.account, response.data.mustChangePassword)
    return response.data.mustChangePassword
  })
}

export async function portalLogout(): Promise<void> {
  try {
    await withPortalAuthLock(async () => {
      try { await portalHttp.post<ApiResponses['POST /oem/auth/logout']>('/oem/auth/logout', null, { withCredentials: true }) } catch { /* 忽略 */ }
    })
  } catch {
    /* 锁失败也要完成本地退出 */
  }
  usePortalAuth.getState().logout()
}

/** A password change revokes every portal session; the caller signs in again with the new password. */
export async function portalChangePassword(oldPassword: string, newPassword: string): Promise<void> {
  await withPortalAuthLock(async () => {
    await portalHttp.put<ApiResponses['PUT /oem/auth/password']>('/oem/auth/password', { oldPassword, newPassword })
    usePortalAuth.getState().logout()
  })
}
