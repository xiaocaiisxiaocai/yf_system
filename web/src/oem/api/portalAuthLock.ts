import { STORAGE_LEASE_MS } from '../../api/client'

/**
 * Cross-tab coordination of OEM portal refresh-token rotation. It mirrors the collaboration
 * realm's `withAuthLock` (Web Locks, falling back to a localStorage lease on plain-HTTP origins)
 * but uses its own lock and lease names: the two realms rotate different refresh cookies, so a
 * slow or stuck collaboration refresh must never hold up a portal refresh, and vice versa.
 */
export const PORTAL_AUTH_LOCK_NAME = 'yf-oem-portal-session'
export const PORTAL_STORAGE_LOCK_KEY = 'yf:oem-portal-refresh-lock'

const LEASE_RENEW_MS = 20_000
const LEASE_WAIT_MS = 95_000
const sleep = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms))

export function withPortalAuthLock<T>(operation: () => Promise<T>): Promise<T> {
  if (typeof navigator !== 'undefined' && navigator.locks) {
    return navigator.locks.request(PORTAL_AUTH_LOCK_NAME, operation)
  }
  return withPortalStorageLease(operation)
}

function readLease(): { owner: string; expires: number } | null {
  const raw = window.localStorage.getItem(PORTAL_STORAGE_LOCK_KEY)
  if (!raw) return null
  try {
    const value = JSON.parse(raw) as { owner?: unknown; expires?: unknown }
    return typeof value.owner === 'string' && typeof value.expires === 'number'
      ? { owner: value.owner, expires: value.expires }
      : null
  } catch {
    return null
  }
}

const writeLease = (owner: string) =>
  window.localStorage.setItem(PORTAL_STORAGE_LOCK_KEY, JSON.stringify({ owner, expires: Date.now() + STORAGE_LEASE_MS }))

/**
 * Write-then-read-back lease (localStorage has no compare-and-swap), renewed while held.
 * Unavailable storage or a wait timeout fails safely instead of rotating without the lock.
 */
export async function withPortalStorageLease<T>(operation: () => Promise<T>): Promise<T> {
  if (typeof window === 'undefined') return operation()
  const owner = `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`
  const giveUpAt = Date.now() + LEASE_WAIT_MS
  for (;;) {
    let acquired = false
    try {
      const held = readLease()
      if (!held || held.expires <= Date.now()) {
        writeLease(owner)
        await sleep(40)
        acquired = readLease()?.owner === owner
      }
    } catch {
      throw new Error('无法安全协调登录状态，请检查浏览器存储设置后重试')
    }
    if (acquired) {
      const renew = window.setInterval(() => {
        try {
          if (readLease()?.owner === owner) writeLease(owner)
        } catch {
          // The current lease still covers the refresh timeout.
        }
      }, LEASE_RENEW_MS)
      try {
        return await operation()
      } finally {
        window.clearInterval(renew)
        try {
          if (readLease()?.owner === owner) window.localStorage.removeItem(PORTAL_STORAGE_LOCK_KEY)
        } catch {
          // Expires naturally.
        }
      }
    }
    if (Date.now() >= giveUpAt) throw new Error('登录状态协调超时，请稍后重试')
    await sleep(60 + Math.random() * 120)
  }
}
