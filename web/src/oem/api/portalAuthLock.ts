import { withAuthLock, withStorageLease, type AuthLockNames } from '../../api/client'

/**
 * Cross-tab coordination of OEM portal refresh-token rotation. Same mechanism as the collaboration
 * realm (Web Locks, falling back to a localStorage lease on plain-HTTP origins) but under its own
 * names: the two realms rotate different refresh cookies, so a slow or stuck collaboration refresh
 * must never hold up a portal refresh, and vice versa.
 */
export const PORTAL_AUTH_LOCK_NAME = 'yf-oem-portal-session'
export const PORTAL_STORAGE_LOCK_KEY = 'yf:oem-portal-refresh-lock'
const PORTAL_AUTH_LOCK: AuthLockNames = { lock: PORTAL_AUTH_LOCK_NAME, leaseKey: PORTAL_STORAGE_LOCK_KEY }

export const withPortalAuthLock = <T>(operation: () => Promise<T>): Promise<T> => withAuthLock(operation, PORTAL_AUTH_LOCK)

export const withPortalStorageLease = <T>(operation: () => Promise<T>): Promise<T> =>
  withStorageLease(operation, PORTAL_STORAGE_LOCK_KEY)
