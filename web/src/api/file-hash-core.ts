import SparkMD5 from 'spark-md5'

/**
 * 增量计算整文件 MD5（主线程或 Worker 内均可运行；调用方请用 file-hash.ts 的 fileMd5），
 * 用于服务端组装后的最终完整性校验；不把整份大文件读入内存。
 * 每段之间让出主线程，onProgress 报告 0–1 的进度，便于大文件显示校验进度。
 */
export async function fileMd5(
  file: Blob,
  cancelled: () => boolean = () => false,
  onProgress?: (fraction: number) => void,
): Promise<string> {
  const hash = new SparkMD5.ArrayBuffer()
  try {
    const blockSize = 4 * 1024 * 1024
    for (let offset = 0; offset < file.size; offset += blockSize) {
      if (cancelled()) throw new Error('上传已取消')
      hash.append(await file.slice(offset, offset + blockSize).arrayBuffer())
      onProgress?.(Math.min(1, (offset + blockSize) / file.size))
    }
    if (cancelled()) throw new Error('上传已取消')
    return hash.end()
  } finally {
    hash.destroy()
  }
}
