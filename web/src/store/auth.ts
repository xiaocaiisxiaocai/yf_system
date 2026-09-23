import { create } from 'zustand'
import { persist } from 'zustand/middleware'
import type { LoginResponse, ProfileResponse, UserBrief as ApiUserBrief } from '../api/generated/api-types'

// The signed-in user from the API, with userType narrowed to the two account kinds.
export type UserBrief = Omit<ApiUserBrief, 'userType'> & { userType: 'INTERNAL' | 'SUPPLIER' }
export type AuthLoginResponse = Omit<LoginResponse, 'user'> & { user: UserBrief }
export type AuthProfileResponse = Omit<ProfileResponse, 'user'> & { user: UserBrief }

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
  setUser: (user: UserBrief) => void
  setLogin: (payload: AuthLoginResponse, replaceSession?: boolean) => void
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
      setUser: (user) => set({ user }),
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
      version: 1,
      // Older schemas persisted more account data. Discard the complete record instead of
      // carrying any identity, authorization, or forced-password state into the current session.
      migrate: () => ({ user: null, permissions: [], menus: [], mustChangePassword: false }),
      // Only an account id is needed for boot/cross-tab account-switch detection.
      // Name, employee number, email and supplier details are fetched after refresh.
      partialize: (s) => ({
        user: s.user ? { id: s.user.id } : null,
        permissions: s.permissions,
        menus: s.menus,
        mustChangePassword: s.mustChangePassword,
      }),
    }
  )
)
