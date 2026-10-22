import { useEffect, useState } from 'react'
import { hasActiveUpload, subscribeUploadActivity } from './uploadActivity'

export const UPLOAD_LEAVE_MESSAGE = '文件仍在上传，离开后服务端上传会话会保留，提交或终止可能被拒绝。'

/** 应用使用 BrowserRouter，不能用 useBlocker。这里只拦浏览器离开。 */
export default function UploadLeaveGuard() {
  const [active, setActive] = useState(hasActiveUpload)

  useEffect(() => subscribeUploadActivity(() => setActive(hasActiveUpload())), [])

  useEffect(() => {
    if (!active) return undefined
    const onBeforeUnload = (event: BeforeUnloadEvent) => {
      event.preventDefault()
      event.returnValue = UPLOAD_LEAVE_MESSAGE
      return UPLOAD_LEAVE_MESSAGE
    }
    window.addEventListener('beforeunload', onBeforeUnload)
    return () => window.removeEventListener('beforeunload', onBeforeUnload)
  }, [active])

  return null
}
