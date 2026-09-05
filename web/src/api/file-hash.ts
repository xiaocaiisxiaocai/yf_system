import SparkMD5 from 'spark-md5'

/** 分段计算内容标识，避免同名同大小的新文件续接旧分片；不把整份大文件读入内存。 */
export async function fileMd5(file: Blob, cancelled: () => boolean = () => false): Promise<string> {
  const hash = new SparkMD5.ArrayBuffer()
  try {
    const blockSize = 4 * 1024 * 1024
    for (let offset = 0; offset < file.size; offset += blockSize) {
      if (cancelled()) throw new Error('上传已取消')
      hash.append(await file.slice(offset, offset + blockSize).arrayBuffer())
    }
    if (cancelled()) throw new Error('上传已取消')
    return hash.end()
  } finally {
    hash.destroy()
  }
}
