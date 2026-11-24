import { useMemo } from 'react'
import http from '../api/client'
import { createSessionQueryScope } from '../api/queryClient'
import { useAuth } from '../store/auth'
import { OemApi } from './api/OemApi'
import type { OemContextValue } from './OemContext'

const internalApi = new OemApi(http)

export function useInternalOem(): OemContextValue {
  const session = useAuth()
  const permissions = useMemo(() => new Set(session.permissions), [session.permissions])
  return {
    api: internalApi,
    realm: 'internal',
    base: '/oem',
    permissions,
    userId: session.user?.id ?? null,
    queryScope: ['internal', ...createSessionQueryScope(session)],
  }
}
