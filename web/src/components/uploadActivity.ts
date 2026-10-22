type Listener = () => void

let activeUploaders = 0
const listeners = new Set<Listener>()

function notify() {
  for (const listener of listeners) listener()
}

/** 一个上传器在存在 hashing、uploading 或 merging 条目时持有一次。重复释放不计入。 */
export function retainActiveUpload(): () => void {
  activeUploaders += 1
  notify()
  let released = false
  return () => {
    if (released) return
    released = true
    activeUploaders = Math.max(0, activeUploaders - 1)
    notify()
  }
}

export function hasActiveUpload(): boolean {
  return activeUploaders > 0
}

export function subscribeUploadActivity(listener: Listener): () => void {
  listeners.add(listener)
  return () => { listeners.delete(listener) }
}
