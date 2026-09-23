import { fileMd5 } from './file-hash-core'

// The project compiles against the DOM lib only; describe just the worker scope used here.
const scope = self as unknown as {
  onmessage: ((event: MessageEvent<{ file: Blob }>) => void) | null
  postMessage: (message: unknown) => void
}

/** 在后台线程计算上传内容标识，避免大文件校验期间页面卡顿。协议见 file-hash.ts。 */
scope.onmessage = async (event) => {
  try {
    const digest = await fileMd5(event.data.file, () => false, (fraction) => {
      scope.postMessage({ type: 'progress', fraction })
    })
    scope.postMessage({ type: 'done', digest })
  } catch (error) {
    scope.postMessage({ type: 'error', message: error instanceof Error ? error.message : String(error) })
  }
}
