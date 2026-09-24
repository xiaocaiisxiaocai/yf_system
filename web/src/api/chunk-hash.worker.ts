import { hashBlobSha256 } from './sha256-core'

// The project compiles against the DOM lib only; describe just the worker scope used here.
const scope = self as unknown as {
  onmessage: ((event: MessageEvent<{ id: number; blob: Blob }>) => void) | null
  postMessage: (message: unknown) => void
}

/** 在后台线程计算上传分片的 SHA-256；HTTP 部署缺少 SubtleCrypto 时纯 JS 计算也不占用页面主线程。协议见 file-hash.ts。 */
scope.onmessage = async (event) => {
  const { id, blob } = event.data
  try {
    scope.postMessage({ id, type: 'done', digest: await hashBlobSha256(blob) })
  } catch (error) {
    scope.postMessage({ id, type: 'error', message: error instanceof Error ? error.message : String(error) })
  }
}
