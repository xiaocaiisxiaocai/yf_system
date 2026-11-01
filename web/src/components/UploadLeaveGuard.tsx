import { useEffect, useState } from 'react'
import { hasActiveUpload, subscribeUploadActivity } from './uploadActivity'

export const UPLOAD_LEAVE_MESSAGE = '文件仍在上传，离开后服务端上传会话会保留，提交或终止可能被拒绝。'

/** 拦浏览器层面的离开（刷新、关闭标签页）；站内路由切换由 UploadNavigationGuard 确认。 */
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
