import { useRef, useState } from 'react'

export default function useOutsourceSaving() {
  const [saving, setSaving] = useState(false)
  const lock = useRef(false)

  const run = async (action: () => Promise<void>) => {
    if (lock.current) return
    lock.current = true
    setSaving(true)
    try {
      await action()
    } catch {
      // 统一请求层展示具体错误，保留表单供修正。
    } finally {
      lock.current = false
      setSaving(false)
    }
  }

  return { saving, run }
}
