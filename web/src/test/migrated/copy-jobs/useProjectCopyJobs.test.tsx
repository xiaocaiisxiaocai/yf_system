import { act, renderHook, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { ProjectCopyJob } from '../../../api/types'

const mocks = vi.hoisted(() => ({
  list: vi.fn(),
}))

vi.mock('../../../api/copyJobs', () => ({
  listProjectCopyJobs: mocks.list,
  isCopyJob: (value: unknown) => Boolean(value && typeof value === 'object'),
  isCopyJobAbort: (error: unknown) => (error as { code?: unknown } | null)?.code === 'ERR_CANCELED',
  isCopyJobAccessError: (error: unknown) => [403, 404].includes((error as { response?: { status?: number } } | null)?.response?.status || 0),
}))

import { useProjectCopyJobs } from '../../../hooks/useProjectCopyJobs'

function deferred<T>() {
  let resolve!: (value: T | PromiseLike<T>) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((ok, fail) => { resolve = ok; reject = fail })
  return { promise, resolve, reject }
}

const finishedJob: ProjectCopyJob = {
  jobId: 5,
  sourceProjectId: 9,
  projectGroupId: 3,
  targetName: '副本',
  status: 'succeeded',
  filesTotal: 1,
  filesCopied: 1,
  bytesTotal: 10,
  bytesCopied: 10,
  error: null,
  result: { projectId: 10, copyFileCount: 1 },
  createdAt: '2026-09-23T00:00:00Z',
  startedAt: '2026-09-23T00:00:01Z',
  completedAt: '2026-09-23T00:00:02Z',
}

describe('useProjectCopyJobs', () => {
  beforeEach(() => {
    mocks.list.mockReset()
  })

  it('aborts the current list request on unmount and generation change', async () => {
    const first = deferred<{ data: { jobs: ProjectCopyJob[] } }>()
    const second = deferred<{ data: { jobs: ProjectCopyJob[] } }>()
    mocks.list.mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise)
    const rendered = renderHook(({ generation }) => useProjectCopyJobs(3, true, generation), {
      initialProps: { generation: 0 },
    })

    await waitFor(() => expect(mocks.list).toHaveBeenCalledTimes(1))
    const firstSignal = mocks.list.mock.calls[0][1] as AbortSignal
    rendered.rerender({ generation: 1 })
    expect(firstSignal.aborted).toBe(true)
    expect(rendered.result.current.jobs).toEqual([])
    await waitFor(() => expect(mocks.list).toHaveBeenCalledTimes(2))
    const secondSignal = mocks.list.mock.calls[1][1] as AbortSignal
    rendered.unmount()
    expect(secondSignal.aborted).toBe(true)
  })

  it('clears all previous jobs when access is revoked', async () => {
    mocks.list
      .mockResolvedValueOnce({ data: { jobs: [finishedJob] } })
      .mockRejectedValueOnce({ response: { status: 403 } })
    const rendered = renderHook(() => useProjectCopyJobs(3, true, 0))

    await waitFor(() => expect(rendered.result.current.jobs).toHaveLength(1))
    await act(async () => { await rendered.result.current.refresh() })
    await waitFor(() => expect(rendered.result.current.unavailable).toBe(true))
    expect(rendered.result.current.jobs).toEqual([])
  })

  it('keeps the last snapshot and exposes a usable retry after a transient refresh failure', async () => {
    mocks.list
      .mockResolvedValueOnce({ data: { jobs: [finishedJob] } })
      .mockRejectedValueOnce(new Error('temporary network failure'))
    const rendered = renderHook(() => useProjectCopyJobs(3, true, 0))

    await waitFor(() => expect(rendered.result.current.jobs).toHaveLength(1))
    await act(async () => { await rendered.result.current.refresh() })
    await waitFor(() => expect(rendered.result.current.error).toBe(true))
    expect(rendered.result.current.jobs).toEqual([finishedJob])
    expect(rendered.result.current.loading).toBe(false)
  })

  it('does not retain a previous group snapshot after disabled permission state', async () => {
    mocks.list.mockResolvedValue({ data: { jobs: [finishedJob] } })
    const rendered = renderHook(({ enabled }) => useProjectCopyJobs(3, enabled, 0), {
      initialProps: { enabled: true },
    })
    await waitFor(() => expect(rendered.result.current.jobs).toHaveLength(1))
    rendered.rerender({ enabled: false })
    await waitFor(() => expect(rendered.result.current.jobs).toEqual([]))
    expect(mocks.list).toHaveBeenCalledTimes(1)
  })
})
