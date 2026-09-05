import { create } from 'zustand'
import { persist } from 'zustand/middleware'

export interface UserBrief {
  id: number
  username: string
  realName: string
  userType: 'INTERNAL' | 'SUPPLIER'
  supplierId?: number | null
}

interface AuthState {
  /** 仅驻留内存，不落 localStorage（XSS 防线）；页面刷新后由 refresh cookie 静默换新 */
  token: string | null
  user: UserBrief | null
  permissions: string[]
  menus: string[]
  mustChangePassword: boolean
  /** 启动时静默刷新是否已完成（无持久化用户则立即完成） */
  booted: boolean
  generation: number
  setToken: (t: string) => void
  setBooted: (v: boolean) => void
  setLogin: (payload: {
    accessToken: string
    user: UserBrief
    permissions: string[]
    menus: string[]
    mustChangePassword: boolean
  }, replaceSession?: boolean) => void
  clearMustChange: () => void
  logout: () => void
  hasPerm: (code: string) => boolean
}

export const useAuth = create<AuthState>()(
  persist(
    (set, get) => ({
      token: null,
      user: null,
      permissions: [],
      menus: [],
      mustChangePassword: false,
      booted: false,
      generation: 0,
      setToken: (t) => set({ token: t }),
      setBooted: (v) => set({ booted: v }),
      setLogin: (p, replaceSession = true) =>
        set({
          token: p.accessToken,
          user: p.user,
          permissions: p.permissions,
          menus: p.menus,
          mustChangePassword: p.mustChangePassword,
          booted: true,
          generation: get().generation + (replaceSession ? 1 : 0),
        }),
      clearMustChange: () => set({ mustChangePassword: false }),
      logout: () => set({ token: null, user: null, permissions: [], menus: [], mustChangePassword: false, generation: get().generation + 1 }),
      hasPerm: (code) => get().permissions.includes(code),
    }),
    {
      name: 'yf-auth',
      // token 不持久化；持久化的用户信息用于启动时决定是否尝试静默刷新
      partialize: (s) => ({
        user: s.user,
        permissions: s.permissions,
        menus: s.menus,
        mustChangePassword: s.mustChangePassword,
      }),
    }
  )
)
