import { createContext, useContext } from 'react'
import type { OemApi } from './api/OemApi'
import type { Realm } from './api/types'

/**
 * What an OEM page needs to know about where it runs: which HTTP session to use,
 * which realm the caller belongs to, which route prefix links must use and which
 * internal permissions the caller holds (always empty for vendor accounts, whose
 * capabilities are fixed by the server).
 */
export interface OemContextValue {
  api: OemApi
  realm: Realm
  base: string
  permissions: ReadonlySet<string>
  userId: number | null
  queryScope: readonly unknown[]
}

const OemContext = createContext<OemContextValue | null>(null)

export const OemProvider = OemContext.Provider

export function useOem(): OemContextValue {
  const value = useContext(OemContext)
  if (!value) throw new Error('OEM pages must be rendered inside an OemProvider')
  return value
}

export function useCan() {
  const { realm, permissions } = useOem()
  return {
    createTransfer: realm === 'oem' || permissions.has('oem:transfer_create'),
    viewTransfers: realm === 'oem' || permissions.has('oem:transfer_view') || permissions.has('oem:transfer_create'),
    approve: permissions.has('oem:flow_approve'),
    recover: permissions.has('oem:approval_recover'),
    manageCompanies: permissions.has('oem:company_manage'),
    deleteCompanies: permissions.has('oem:company_delete'),
    manageAccounts: permissions.has('oem:account_manage'),
    deleteAccounts: permissions.has('oem:account_delete'),
    manageFlows: permissions.has('oem:flow_template_manage'),
    manageRetention: permissions.has('oem:retention_template_manage'),
    manageFilePolicy: permissions.has('oem:file_policy_manage'),
    manageNotify: permissions.has('oem:notify_manage'),
    viewAudit: permissions.has('oem:audit_view'),
  }
}
