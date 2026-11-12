export function extensionOf(name: string): string {
  const dot = name.lastIndexOf('.')
  return dot > 0 && dot < name.length - 1 ? name.slice(dot + 1).toLowerCase() : ''
}

/**
 * Splits picked files by the server's extension whitelist so an unsupported file is refused at once
 * (nothing queued, no draft, no upload session). An unknown or empty whitelist lets everything through;
 * the server still has the final say.
 */
export function splitSupported(files: readonly File[], allowed: readonly string[] | null | undefined) {
  if (!allowed || allowed.length === 0) return { accepted: [...files], rejected: [] as File[] }
  const set = new Set(allowed.map((item) => item.toLowerCase()))
  const accepted: File[] = []
  const rejected: File[] = []
  for (const file of files) (set.has(extensionOf(file.name)) ? accepted : rejected).push(file)
  return { accepted, rejected }
}

export function unsupportedMessage(rejected: readonly File[]): string {
  const names = rejected.slice(0, 3).map((file) => file.name).join('、')
  return `不支持的文件格式，已拒绝：${names}${rejected.length > 3 ? ` 等 ${rejected.length} 个文件` : ''}`
}
