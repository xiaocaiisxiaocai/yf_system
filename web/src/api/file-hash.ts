import HashWorker from './file-hash.worker?worker'
import { fileMd5 as fileMd5InThread } from './file-hash-core'

type WorkerMessage =
  | { type: 'progress'; fraction: number }
  | { type: 'done'; digest: string }
  | { type: 'error'; message: string }

/** 取消检查间隔：Worker 无法同步读取主线程的取消状态，由主线程轮询后终止它。 */
const CANCEL_POLL_MS = 100

/**
 * 计算上传内容标识。优先在 Web Worker 中计算，大文件（最大 2 GiB）校验时主线程保持响应；
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
