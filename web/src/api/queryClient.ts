import { QueryClient } from '@tanstack/react-query'
import { useAuth } from '../store/auth'

export type SessionQueryIdentity = {
  user?: { id: number } | null
  generation?: number
  permissions?: readonly string[]
  menus?: readonly string[]
  mustChangePassword?: boolean
}

/** Authorization changes get a fresh cache scope even if the account is unchanged. */
export function createSessionQueryScope(state: SessionQueryIdentity): readonly [number | null, number, string] {
  return [state.user?.id ?? null, state.generation ?? 0, JSON.stringify([
    [...(state.permissions ?? [])].sort(), [...(state.menus ?? [])].sort(), Boolean(state.mustChangePassword),
  ])]
}

export function useSessionQueryScope() {
  return createSessionQueryScope(useAuth())
}

export function createAppQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
        staleTime: 0,
        gcTime: 5 * 60 * 1000,
        refetchOnWindowFocus: false,
        refetchOnReconnect: true,
      },
      mutations: { retry: false },
    },
  })
}

export const queryClient = createAppQueryClient()

/** Mounted once at the application boundary; never persist server responses to localStorage. */
export function bindQuerySessionLifecycle(client: QueryClient = queryClient) {
  return useAuth.subscribe((state, previous) => {
    if (JSON.stringify(createSessionQueryScope(state)) === JSON.stringify(createSessionQueryScope(previous))) return
    void client.cancelQueries()
    client.clear()
  })
}
