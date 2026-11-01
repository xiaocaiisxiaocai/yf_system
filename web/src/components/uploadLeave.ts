import { Modal } from '@arco-design/web-react'
import { hasActiveUpload } from './uploadActivity'

export const UPLOAD_ROUTE_LEAVE_TITLE = '文件仍在上传'
export const UPLOAD_ROUTE_LEAVE_CONTENT = '离开后正在上传的文件会中止，服务端上传会话保留，可稍后续传。'

let confirming = false
let confirmed = false

/**
 * 站内离开当前页面前的统一确认：没有进行中的上传时直接放行；有则二次确认，确认后才执行 proceed
 * （上传器卸载时会中止本次传输）。确认对话框打开期间的重复请求被忽略。proceed 同步执行期间再触发的
 * 离开（例如退出登录后的 navigate）视为已确认，不重复询问。
 */
export function confirmUploadLeave(proceed: () => void) {
  if (confirmed || !hasActiveUpload()) {
    proceed()
    return
  }
  if (confirming) return
  confirming = true
  Modal.confirm({
    title: UPLOAD_ROUTE_LEAVE_TITLE,
    content: UPLOAD_ROUTE_LEAVE_CONTENT,
    okText: '离开并中止',
    cancelText: '继续上传',
    onOk: () => {
      confirming = false
      confirmed = true
      try {
        proceed()
      } finally {
        confirmed = false
      }
    },
    onCancel: () => { confirming = false },
    afterClose: () => { confirming = false },
  })
}
