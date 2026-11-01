import { MutationObserver } from '@tanstack/react-query'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { bindQuerySessionLifecycle, createAppQueryClient, createSessionQueryScope } from '../../../api/queryClient'
import { useAuth } from '../../../store/auth'

afterEach(() => useAuth.getState().logout())

describe('server query session boundary', () => {
  it('aborts in-flight queries and removes responses immediately on logout', async () => {
    const client = createAppQueryClient()
    const unbind = bindQuerySessionLifecycle(client)
    let signal: AbortSignal | undefined
    let finish!: (value: string) => void
    const request = client.fetchQuery({
      queryKey: ['private', createSessionQueryScope(useAuth.getState())],
      queryFn: ({ signal: incoming }) => {
        signal = incoming
        return new Promise<string>(resolve => { finish = resolve })
      },
    }).catch(() => undefined)
    useAuth.getState().logout()
    expect(signal?.aborted).toBe(true)
    finish('late private response')
    await request
    expect(client.getQueryCache().getAll()).toHaveLength(0)
    unbind()
    client.clear()
  })

  it('keeps cached data for token renewal but clears it on permission changes', () => {
    const client = createAppQueryClient()
    const unbind = bindQuerySessionLifecycle(client)
    client.setQueryData(['private'], 'cached')
    useAuth.getState().setToken('renewed-access-token')
    expect(client.getQueryData(['private'])).toBe('cached')
    useAuth.setState({ permissions: ['project:view'] })
    expect(client.getQueryCache().getAll()).toHaveLength(0)
    unbind()
    client.clear()
  })

  it('isolates accounts and new login generations without depending on permission order', () => {
    const original = { user: { id: 1 }, generation: 4, permissions: ['b', 'a'] }
    expect(createSessionQueryScope(original)).toEqual(createSessionQueryScope({ ...original, permissions: ['a', 'b'] }))
    expect(createSessionQueryScope(original)).not.toEqual(createSessionQueryScope({ ...original, user: { id: 2 } }))
    expect(createSessionQueryScope(original)).not.toEqual(createSessionQueryScope({ ...original, generation: 5 }))
  })
})

describe('write requests are never auto-replayed', () => {
  it('a rejected mutation (e.g. 409/40902 lock wait) runs its function exactly once', async () => {
    vi.useFakeTimers()
    try {
      const client = createAppQueryClient()
      const lockWait = Object.assign(new Error('lock wait'), { response: { status: 409, data: { code: 40902 } } })
      const mutationFn = vi.fn().mockRejectedValue(lockWait)
      const observer = new MutationObserver(client, { mutationFn })

      await expect(observer.mutate({ id: 1 })).rejects.toBe(lockWait)
      await vi.advanceTimersByTimeAsync(60_000)

      expect(mutationFn).toHaveBeenCalledOnce()
      client.clear()
    } finally {
      vi.useRealTimers()
    }
  })
})
