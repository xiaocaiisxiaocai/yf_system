import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createHash, randomBytes } from 'node:crypto'

const workerHarness = vi.hoisted(() => ({
  instances: [] as Array<{
    onmessage: ((event: MessageEvent) => void) | null
    onerror: ((event: ErrorEvent) => void) | null
    postMessage: ReturnType<typeof vi.fn>
    terminate: ReturnType<typeof vi.fn>
  }>,
  throwOnConstruction: false,
  chunkInstances: [] as Array<{
    onmessage: ((event: MessageEvent) => void) | null
    onerror: ((event: ErrorEvent) => void) | null
    postMessage: ReturnType<typeof vi.fn>
    terminate: ReturnType<typeof vi.fn>
  }>,
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

vi.mock('../../../api/chunk-hash.worker?worker', () => ({
  default: class MockChunkHashWorker {
    onmessage: ((event: MessageEvent) => void) | null = null
    onerror: ((event: ErrorEvent) => void) | null = null
    postMessage = vi.fn()
    terminate = vi.fn()

    constructor() {
      if (workerHarness.throwOnConstruction) throw new Error('worker blocked')
      workerHarness.chunkInstances.push(this)
    }
  },
}))

import { blobSha256, createChunkHasher, fileMd5, uploadFingerprint } from '../../../api/file-hash'
import { fileMd5 as fileMd5InThread } from '../../../api/file-hash-core'
import { Sha256 } from '../../../api/sha256-core'

describe('file hashing contracts', () => {
  const originalWorker = globalThis.Worker

  beforeEach(() => {
    workerHarness.instances.length = 0
    workerHarness.chunkInstances.length = 0
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

  it('chunk digests are queued through one worker and settled in request order', async () => {
    const hasher = createChunkHasher()
    const worker = workerHarness.chunkInstances[0]
    const first = hasher.sha256(new Blob(['one']))
    const second = hasher.sha256(new Blob(['two']))
    const firstRejected = expect(first).rejects.toThrow('read failed')

    expect(worker.postMessage).toHaveBeenNthCalledWith(1, { id: 1, blob: expect.any(Blob) })
    expect(worker.postMessage).toHaveBeenCalledTimes(1)
    worker.onmessage?.({ data: { id: 1, type: 'error', message: 'read failed' } } as MessageEvent)
    expect(worker.postMessage).toHaveBeenNthCalledWith(2, { id: 2, blob: expect.any(Blob) })
    worker.onmessage?.({ data: { id: 2, type: 'done', digest: 'b'.repeat(64) } } as MessageEvent)
    await expect(second).resolves.toBe('b'.repeat(64))
    await firstRejected

    const abandoned = hasher.sha256(new Blob(['three']))
    hasher.dispose()
    await expect(abandoned).rejects.toThrow('上传已取消')
    expect(worker.terminate).toHaveBeenCalledOnce()
  })

  it('a chunk hash worker that fails to load or cannot be created falls back to the main thread', async () => {
    const abc = 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad'
    const hasher = createChunkHasher()
    const worker = workerHarness.chunkInstances[0]
    const pending = hasher.sha256(new Blob(['abc']))
    const preventDefault = vi.fn()
    worker.onerror?.({ preventDefault } as unknown as ErrorEvent)
    await expect(pending).resolves.toBe(abc)
    expect(preventDefault).toHaveBeenCalledOnce()
    expect(worker.terminate).toHaveBeenCalledOnce()
    await expect(hasher.sha256(new Blob(['abc']))).resolves.toBe(abc)
    expect(worker.postMessage).toHaveBeenCalledOnce()

    workerHarness.throwOnConstruction = true
    await expect(createChunkHasher().sha256(new Blob(['abc']))).resolves.toBe(abc)
  })

  it('computes lower-case SHA-256 for a bounded chunk', async () => {
    await expect(blobSha256(new Blob(['abc']))).resolves.toBe(
      'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad',
    )
  })

  it('matches SHA-256 standard vectors when crypto.subtle is absent', async () => {
    vi.stubGlobal('crypto', {})
    const vectors = [
      ['', 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855'],
      ['abc', 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad'],
      [
        'abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq',
        '248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1',
      ],
    ]
    for (const [value, expected] of vectors) {
      await expect(blobSha256(new Blob([value]))).resolves.toBe(expected)
    }
  })

  it('matches Node crypto for random bytes split across irregular incremental blocks', async () => {
    vi.stubGlobal('crypto', {})
    const bytes = new Uint8Array(randomBytes(2 * 1024 * 1024 + 137))
    const expected = createHash('sha256').update(bytes).digest('hex')
    await expect(blobSha256(new Blob([bytes]))).resolves.toBe(expected)

    const incremental = new Sha256()
    const blockSizes = [1, 63, 64, 65, 4093, 1024 * 1024]
    let offset = 0
    let block = 0
    while (offset < bytes.length) {
      const end = Math.min(bytes.length, offset + blockSizes[block++ % blockSizes.length])
      incremental.update(bytes.subarray(offset, end))
      offset = end
    }
    expect(Buffer.from(incremental.digest()).toString('hex')).toBe(expected)
  })

  it('matches the Python fixture fingerprint on an insecure HTTP-style crypto surface', async () => {
    vi.stubGlobal('crypto', {})
    const bytes = new Uint8Array(256 * 9)
    for (let index = 0; index < bytes.length; index++) bytes[index] = index % 256
    const file = new File([bytes], '测试 "edge".bin', { lastModified: 1_700_000_000_123 })

    await expect(uploadFingerprint(file)).resolves.toBe(
      'c608af5839cdbfe7894a89cd405ce34aac23e6be0d254ba0c3744070062f0f48',
    )
  })

  it('keeps quick resume identity separate from full-file integrity', async () => {
    const edge = new Uint8Array(1024 * 1024)
    edge.fill(7)
    const first = new File([edge, new Uint8Array([1]), edge], 'same.bin', { lastModified: 1234 })
    const changedMiddle = new File([edge, new Uint8Array([2]), edge], 'same.bin', { lastModified: 1234 })

    expect(await uploadFingerprint(first)).toBe(await uploadFingerprint(changedMiddle))
    expect(await fileMd5InThread(first)).not.toBe(await fileMd5InThread(changedMiddle))
    expect(await uploadFingerprint(new File([edge, new Uint8Array([1]), edge], 'same.bin', { lastModified: 1235 })))
      .not.toBe(await uploadFingerprint(first))
  })
})
