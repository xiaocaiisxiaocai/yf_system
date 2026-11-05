// 邮件队列与审计日志里的原因是服务端内部代码，界面上显示为中文说明；未识别的代码原样显示。
export const MAIL_REASON_LABELS: Record<string, string> = {
  PENDING_EXPIRED: '待发送邮件超过有效期，已自动取消',
  SMTP_UNAVAILABLE: 'SMTP 暂不可用，邮件未能发送',
  SMTP_REPEATED_DISCONNECT: 'SMTP 连接反复中断，已停止重试',
  NOTIFICATION_POLICY_DISABLED: '邮件提醒规则已关闭该通知',
  PROJECT_ACCEPTANCE_RETIRED: '验收流程已停用，通知已取消',
  PROJECT_RECIPIENT_UNAUTHORIZED: '收件人已无权查看该项目，通知已取消',
  RECIPIENT_ADDRESS_STALE: '收件账号或邮箱已变更，通知已取消',
  RECIPIENT_EMAIL_MISSING: '收件账号未填写邮箱，通知未入队',
  OEM_NOTIFICATION_POLICY_DISABLED: 'ODM/OEMA 邮件提醒规则已关闭该通知',
  OEM_RECIPIENT_UNAUTHORIZED: '收件人已无权接收该通知，通知已取消',
  OEM_MAIL_INVALID: 'ODM/OEMA 邮件缺少收件人或传递单信息',
  OEM_TRANSFER_MISSING: '传递单已不存在，通知已取消',
}

export function mailReasonText(value: unknown): string {
  if (typeof value !== 'string') return ''
  const code = value.trim()
  return MAIL_REASON_LABELS[code] ?? code
}
