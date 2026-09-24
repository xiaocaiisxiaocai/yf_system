import HashWorker from './file-hash.worker?worker'
import ChunkHashWorker from './chunk-hash.worker?worker'
import { fileMd5 as fileMd5InThread } from './file-hash-core'
import { hashBlobSha256, sha256Fallback } from './sha256-core'

const FINGERPRINT_SAMPLE_SIZE = 1024 * 1024

function bytesToHex(bytes: ArrayBuffer | Uint8Array): string {
  const view = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes)
  return Array.from(view, (value) => value.toString(16).padStart(2, '0')).join('')
}

function subtleCrypto(): SubtleCrypto | undefined {
  try { return globalThis.crypto?.subtle }
  catch { return undefined }
}

async function sha256Bytes(input: Uint8Array): Promise<string> {
  const subtle = subtleCrypto()
  if (subtle) {
    const webCryptoInput = new Uint8Array(new ArrayBuffer(input.byteLength))
    webCryptoInput.set(input)
    try { return bytesToHex(await subtle.digest('SHA-256', webCryptoInput.buffer)) }
    catch { /* HTTP origins and embedded browsers may expose crypto without usable subtle. */ }
  }
  return bytesToHex(sha256Fallback(input))
}

const yieldToMainThread = () => new Promise<void>((resolve) => setTimeout(resolve, 0))

/** SHA-256 on the calling thread, yielding between blocks; used for small samples and as the Worker fallback. */
export function blobSha256(blob: Blob): Promise<string> {
  return hashBlobSha256(blob, yieldToMainThread)
}

type ChunkHashMessage =
  | { id: number; type: 'done'; digest: string }
  | { id: number; type: 'error'; message: string }

export interface ChunkHasher {
  sha256(blob: Blob): Promise<string>
  dispose(): void
}

/**
 * Per-upload SHA-256 hasher for chunks. Digests are computed in a Worker so the pure-JS fallback on
 * HTTP origins does not occupy the page's main thread; if the Worker cannot be created or fails to
 * load, pending and later chunks are hashed on the main thread with identical results.
 */
export function createChunkHasher(): ChunkHasher {
  let worker: Worker | null = null
  if (typeof Worker !== 'undefined') {
    try { worker = new ChunkHashWorker() } catch { worker = null }
  }
  let nextId = 0
  const pending = new Map<number, { blob: Blob; resolve: (digest: string) => void; reject: (error: Error) => void }>()
  const fallBackToMainThread = () => {
    worker?.terminate()
    worker = null
    for (const [id, request] of pending) {
      pending.delete(id)
      blobSha256(request.blob).then(request.resolve, request.reject)
    }
  }
  if (worker) {
    worker.onmessage = (event: MessageEvent<ChunkHashMessage>) => {
      const message = event.data
      const request = pending.get(message.id)
      if (!request) return
      pending.delete(message.id)
      if (message.type === 'done') request.resolve(message.digest)
      else request.reject(new Error(message.message))
    }
    worker.onerror = (event) => {
      // 脚本加载失败（例如被策略拦截）时退回主线程，而不是让上传失败。
      event.preventDefault()
      fallBackToMainThread()
    }
  }
  return {
    sha256(blob) {
      const active = worker
      if (!active) return blobSha256(blob)
      return new Promise<string>((resolve, reject) => {
        const id = ++nextId
        pending.set(id, { blob, resolve, reject })
        active.postMessage({ id, blob })
      })
    },
    dispose() {
      worker?.terminate()
      worker = null
      for (const request of pending.values()) request.reject(new Error('上传已取消'))
      pending.clear()
    },
  }
}

/**
 * Fast resumable-upload identity. This deliberately is not the full-file integrity digest:
 * it binds browser file metadata plus the first and last 1 MiB so upload can start quickly.
 */
export async function uploadFingerprint(file: File): Promise<string> {
  const first = file.slice(0, Math.min(FINGERPRINT_SAMPLE_SIZE, file.size))
  const lastStart = Math.max(0, file.size - FINGERPRINT_SAMPLE_SIZE)
  const [firstSha256, lastSha256] = await Promise.all([
    blobSha256(first),
    blobSha256(file.slice(lastStart, file.size)),
  ])
  const canonical = JSON.stringify([
    'yf-upload-fingerprint-v2',
    file.name,
    file.size,
    file.lastModified,
    firstSha256,
    lastSha256,
  ])
  return sha256Bytes(new TextEncoder().encode(canonical))
}

type WorkerMessage =
  | { type: 'progress'; fraction: number }
  | { type: 'done'; digest: string }
  | { type: 'error'; message: string }

/** 取消检查间隔：Worker 无法同步读取主线程的取消状态，由主线程轮询后终止它。 */
const CANCEL_POLL_MS = 100

/**
 * 计算整文件 MD5。优先在 Web Worker 中增量计算，大文件（最大 2 GiB）校验时主线程保持响应；
 * 环境不支持 Worker 或 Worker 启动失败时退回主线程分段计算，结果完全一致。
 */
export async function fileMd5(
  file: Blob,
  cancelled: () => boolean = () => false,
  onProgress?: (fraction: number) => void,
): Promise<string> {
  if (typeof Worker === 'undefined') return fileMd5InThread(file, cancelled, onProgress)
  let worker: Worker
  try {
    worker = new HashWorker()
  } catch {
    return fileMd5InThread(file, cancelled, onProgress)
  }
  let fallback = false
  try {
    return await new Promise<string>((resolve, reject) => {
      const poll = setInterval(() => {
        if (!cancelled()) return
        clearInterval(poll)
        reject(new Error('上传已取消'))
      }, CANCEL_POLL_MS)
      worker.onmessage = (event: MessageEvent<WorkerMessage>) => {
        const message = event.data
        if (message.type === 'progress') {
          if (!cancelled()) onProgress?.(message.fraction)
          return
        }
        clearInterval(poll)
        if (message.type === 'done') resolve(message.digest)
        else reject(new Error(message.message))
      }
      worker.onerror = (event) => {
        // 脚本加载失败（例如被策略拦截）时退回主线程，而不是让上传失败。
        event.preventDefault()
        clearInterval(poll)
        fallback = true
        reject(new Error('hash worker unavailable'))
      }
      worker.postMessage({ file })
    })
  } catch (error) {
    if (fallback && !cancelled()) return fileMd5InThread(file, cancelled, onProgress)
    throw error
  } finally {
    worker.terminate()
  }
}
