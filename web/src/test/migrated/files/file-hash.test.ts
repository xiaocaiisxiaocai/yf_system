import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const workerHarness = vi.hoisted(() => ({
  instances: [] as Array<{
    onmessage: ((event: MessageEvent) => void) | null
    onerror: ((event: ErrorEvent) => void) | null
    postMessage: ReturnType<typeof vi.fn>
    terminate: ReturnType<typeof vi.fn>
  }>,
  throwOnConstruction: false,
}))

vi.mock('../../../api/file-hash.worker?worker', () => ({
  default: class MockHashWorker {
    onmessage: ((event: MessageEvent) => void) | null = null
    onerror: ((event: ErrorEvent) => void) | null = null
    postMessage = vi.fn()
    terminate = vi.fn()

    constructor() {
      if (workerHarness.throwOnConstruction) throw new Error('worker blocked')
      workerHarness.instances.push(this)
    }
  },
}))

import { fileMd5 } from '../../../api/file-hash'
import { fileMd5 as fileMd5InThread } from '../../../api/file-hash-core'

describe('file hashing contracts', () => {
  const originalWorker = globalThis.Worker

  beforeEach(() => {
    workerHarness.instances.length = 0
    workerHarness.throwOnConstruction = false
    globalThis.Worker = class {} as unknown as typeof Worker
  })

  afterEach(() => {
    globalThis.Worker = originalWorker
    vi.useRealTimers()
  })

  it('hashing runs in the worker and forwards its progress', async () => {
    const progress: number[] = []
    const pending = fileMd5(new Blob(['worker']), () => false, (fraction) => progress.push(fraction))
    const worker = workerHarness.instances[0]

    expect(worker.postMessage).toHaveBeenCalledWith({ file: expect.any(Blob) })
    worker.onmessage?.({ data: { type: 'progress', fraction: 0.5 } } as MessageEvent)
    worker.onmessage?.({ data: { type: 'done', digest: 'from-worker' } } as MessageEvent)

    await expect(pending).resolves.toBe('from-worker')
    expect(progress).toEqual([0.5])
    expect(worker.terminate).toHaveBeenCalledOnce()
  })

  it('without Worker support, or when the worker fails to load, hashing falls back to the main thread', async () => {
    globalThis.Worker = undefined as unknown as typeof Worker
    await expect(fileMd5(new Blob(['test']))).resolves.toBe('098f6bcd4621d373cade4e832627b4f6')
    expect(workerHarness.instances).toHaveLength(0)

    globalThis.Worker = class {} as unknown as typeof Worker
    workerHarness.throwOnConstruction = true
    await expect(fileMd5(new Blob(['test']))).resolves.toBe('098f6bcd4621d373cade4e832627b4f6')
  })

  it('cancelling stops the worker', async () => {
    vi.useFakeTimers()
    let cancelled = false
    const pending = fileMd5(new Blob(['pending']), () => cancelled)
    const rejected = expect(pending).rejects.toThrow('上传已取消')
    const worker = workerHarness.instances[0]

    cancelled = true
    await vi.advanceTimersByTimeAsync(101)

    await rejected
    expect(worker.terminate).toHaveBeenCalledOnce()
  })

  it('upload identity is based on bytes rather than filename and size', async () => {
    const first = new Blob(['one!'])
    const second = new Blob(['two!'])

    await expect(fileMd5InThread(new Blob(['test']))).resolves.toBe('098f6bcd4621d373cade4e832627b4f6')
    expect(await fileMd5InThread(first)).not.toBe(await fileMd5InThread(second))
    await expect(fileMd5InThread(first, () => true)).rejects.toThrow('上传已取消')
  })

  it('large-file hashing reports monotonic progress up to completion', async () => {
    const progress: number[] = []

    await fileMd5InThread(
      new Blob([new Uint8Array(9 * 1024 * 1024)]),
      () => false,
      (fraction) => progress.push(fraction),
    )

    expect(progress.map((value) => Math.round(value * 1000) / 1000)).toEqual([0.444, 0.889, 1])
  })
})
