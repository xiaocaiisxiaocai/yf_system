import axios from 'axios'
import type { AxiosError, InternalAxiosRequestConfig } from 'axios'
import { create } from 'zustand'
import { persist } from 'zustand/middleware'
import { Message } from '@arco-design/web-react'

/** OEM vendor account (realm=oem). Never mixed with the collaboration user store. */
export interface PortalAccount {
  id: number
  employeeNo: string
  realName: string
  email: string
  companyId: number
  companyName: string
}

interface PortalAuthState {
  /** Access token lives in memory only; a page reload re-issues it from the OEM refresh cookie. */
  token: string | null
  account: PortalAccount | null
  mustChangePassword: boolean
  booted: boolean
  setLogin: (token: string, account: PortalAccount, mustChangePassword: boolean) => void
  setToken: (token: string) => void
  setBooted: (booted: boolean) => void
  logout: () => void
}

export const PORTAL_BASE = '/oem-portal'

export const usePortalAuth = create<PortalAuthState>()(
  persist(
    (set) => ({
      token: null,
      account: null,
      mustChangePassword: false,
      booted: false,
      setLogin: (token, account, mustChangePassword) => set({ token, account, mustChangePassword, booted: true }),
      setToken: (token) => set({ token }),
      setBooted: (booted) => set({ booted }),
      logout: () => set({ token: null, account: null, mustChangePassword: false }),
    }),
    {
      name: 'yf-oem-portal',
      version: 1,
      partialize: (state) => ({ account: state.account ? ({ id: state.account.id } as PortalAccount) : null }),
    },
  ),
)

/** HTTP client of the OEM portal: its own token, refresh endpoint and redirects. */
export const portalHttp = axios.create({ baseURL: '/api/v1', timeout: 60000 })

let refreshing: Promise<boolean> | null = null

async function refreshSession(): Promise<boolean> {
  refreshing ??= axios
    .post('/api/v1/oem/auth/refresh', null, { withCredentials: true, timeout: 60000 })
    .then(async (response) => {
      const token: string = response.data.accessToken
      const me = await axios.get('/api/v1/oem/auth/me', { headers: { Authorization: `Bearer ${token}` } })
      usePortalAuth.getState().setLogin(token, me.data.account, me.data.mustChangePassword)
      return true
    })
    .catch(() => false)
    .finally(() => setTimeout(() => { refreshing = null }, 0))
  return refreshing
}

export async function bootPortalSession(): Promise<void> {
  const state = usePortalAuth.getState()
  if (state.booted) return
  if (state.account && !state.token && !(await refreshSession())) usePortalAuth.getState().logout()
  usePortalAuth.getState().setBooted(true)
}

portalHttp.interceptors.request.use((config) => {
  const token = usePortalAuth.getState().token
  if (token) config.headers.Authorization = `Bearer ${token}`
  return config
})

portalHttp.interceptors.response.use(
  (response) => response,
  async (error: AxiosError<{ code?: number; message?: string }>) => {
    const config = error.config as (InternalAxiosRequestConfig & { _retried?: boolean }) | undefined
    const status = error.response?.status
    const code = error.response?.data?.code
    if (status === 401 && config && !config._retried && !config.url?.includes('/oem/auth/')) {
      config._retried = true
      if (await refreshSession()) return portalHttp(config)
    }
    if (status === 401) {
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
    Message.error(message || '网络错误')
    return Promise.reject(error)
  },
)

export async function portalLogin(employeeNo: string, password: string): Promise<boolean> {
  const response = await portalHttp.post('/oem/auth/login', { employeeNo, password }, { withCredentials: true })
  usePortalAuth.getState().setLogin(response.data.accessToken, response.data.account, response.data.mustChangePassword)
  return response.data.mustChangePassword
}

export async function portalLogout(): Promise<void> {
  try { await portalHttp.post('/oem/auth/logout', null, { withCredentials: true }) } catch { /* 忽略 */ }
  usePortalAuth.getState().logout()
}
