import { useRef, useState } from 'react'
import { Alert, Button, Input, Space, Typography } from '@arco-design/web-react'
import http from '../../../api/client'
import type { ApiResponses } from '../../../api/types'

export default function SmtpTestPanel({ configured, dirty, saving, configuration }: { configured: boolean; dirty: boolean; saving: boolean; configuration?: object }) {
  const [recipient, setRecipient] = useState('')
  const [sending, setSending] = useState(false)
  const [result, setResult] = useState<{ success: boolean; message: string; configuration?: object } | null>(null)
  const inFlight = useRef(false)
  const blocked = dirty || saving || !configured

  const send = async () => {
    if (inFlight.current || blocked) return
    const address = recipient.trim()
    if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(address)) {
      setResult({ success: false, message: '请输入有效的测试收件邮箱', configuration })
      return
    }
    inFlight.current = true
    setSending(true)
    setResult(null)
    try {
      const response = await http.post<ApiResponses['POST /admin/system/mail-settings/test']>(
        '/admin/system/mail-settings/test', { recipient: address }, { timeout: 45000 },
      )
      setResult({ ...response.data, configuration })
    } catch (error) {
      const message = (error as { response?: { data?: { message?: string } } }).response?.data?.message
      setResult({ success: false, message: message || '测试请求失败，请检查网络后重试；如请求超时，请先检查收件箱。', configuration })
    } finally {
      inFlight.current = false
      setSending(false)
    }
  }

  return <div className="system-smtp-test">
    <Space wrap>
      <Input aria-label="测试收件邮箱" placeholder="输入接收测试邮件的邮箱" value={recipient} maxLength={320}
        style={{ width: 300 }} disabled={sending} onChange={(value) => { setRecipient(value); setResult(null) }} onPressEnter={() => void send()} />
      <Button disabled={blocked} loading={sending} onClick={() => void send()}>发送测试邮件</Button>
      <Typography.Text type="secondary">{dirty || !configured ? '请先保存完整的邮箱设置，再发送测试邮件。' : '使用已保存的配置立即发送，不受邮件通知开关影响。'}</Typography.Text>
    </Space>
    {result && !blocked && result.configuration === configuration && <div role="status" style={{ marginTop: 8 }}><Alert type={result.success ? 'success' : 'error'} content={result.message} /></div>}
  </div>
}
