import { useEffect } from 'react'
import { create } from 'zustand'
import http from '../api/client'
import type { ApiResponses } from '../api/types'
import { useAuth } from '../store/auth'
import { OUTSOURCE_PERMISSIONS } from './outsourceNavigation'

const POLL_MS = 60_000

interface SendQueueState {
  /** Open outsourcing items that wait for a sender: never mailed, or changed since the last mail. */
  count: number
  refresh: () => Promise<void>
}

/** Shared by the business rail, the outsourcing menu and the list, so a send or "no notice" updates every badge at once. */
export const useSendQueue = create<SendQueueState>((set) => ({
  count: 0,
  refresh: async () => {
    if (!useAuth.getState().permissions.includes(OUTSOURCE_PERMISSIONS.send)) { set({ count: 0 }); return }
    try {
      const response = await http.get<ApiResponses['GET /outsource/send-queue']>('/outsource/send-queue', { quietNetworkError: true } as never)
      set({ count: Number(response.data?.count) || 0 })
    } catch { /* Keep the last count; the next poll tries again. */ }
  },
}))

let subscribers = 0
let timer: ReturnType<typeof setInterval> | undefined
const refresh = () => { void useSendQueue.getState().refresh() }

/**
 * Keeps the count fresh while something shows it: once a minute and whenever the window regains focus. Several
 * components may use it at the same time; they share one timer.
 */
export function useSendQueuePolling(enabled: boolean) {
  useEffect(() => {
    if (!enabled) return
    subscribers += 1
    if (subscribers === 1) {
      refresh()
      timer = setInterval(refresh, POLL_MS)
      window.addEventListener('focus', refresh)
    }
    return () => {
      subscribers -= 1
      if (subscribers === 0) {
        clearInterval(timer)
        window.removeEventListener('focus', refresh)
      }
    }
  }, [enabled])
}

/** The list filtered to what waits for a sender, opened from a badge. */
export const SEND_QUEUE_PATH = '/outsource/items?sendQueue=1'
