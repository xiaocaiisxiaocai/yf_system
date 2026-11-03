import { useRef, useState } from 'react'
import { Alert, Button, Input, Space, Typography } from '@arco-design/web-react'
import { IconCheckCircleFill, IconCloseCircleFill, IconMinusCircle } from '@arco-design/web-react/icon'
import http from '../../../api/client'
import type { ApiResponses } from '../../../api/types'

type Diagnosis = ApiResponses['POST /admin/system/mail-settings/diagnose']

export default function SmtpTestPanel({ configured, dirty, saving, configuration, onTested }: { configured: boolean; dirty: boolean; saving: boolean; configuration?: object; onTested?: () => Promise<void> }) {
  const [recipient, setRecipient] = useState('')
  const [sending, setSending] = useState(false)
  const [diagnosing, setDiagnosing] = useState(false)
  const [result, setResult] = useState<{ success: boolean; message: string; deliveryUncertain?: boolean; configuration?: object } | null>(null)
  const [diagnosis, setDiagnosis] = useState<{ value?: Diagnosis; error?: string; configuration?: object } | null>(null)
  const inFlight = useRef(false)
  const blocked = dirty || saving || !configured
  const busy = sending || diagnosing

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
      try { await onTested?.() } catch { /* The send result remains authoritative if status refresh fails. */ }
      inFlight.current = false
      setSending(false)
    }
  }

  // 依次检查连接、加密和登录，不发送邮件；与测试邮件共用防重入，二者同一时间只运行一个。
  const diagnose = async () => {
    if (inFlight.current || blocked) return
    inFlight.current = true
    setDiagnosing(true)
    setDiagnosis(null)
    try {
      const response = await http.post<Diagnosis>('/admin/system/mail-settings/diagnose', {}, { timeout: 75000 })
      setDiagnosis({ value: response.data, configuration })
    } catch (error) {
      const message = (error as { response?: { data?: { message?: string } } }).response?.data?.message
      setDiagnosis({ error: message || '检测请求失败，请检查网络后重试。', configuration })
    } finally {
      try { await onTested?.() } catch { /* The check result remains authoritative if status refresh fails. */ }
      inFlight.current = false
      setDiagnosing(false)
    }
  }

  const showDiagnosis = diagnosis && !blocked && diagnosis.configuration === configuration
  return <div className="system-smtp-test">
    <Space wrap>
      <Input aria-label="测试收件邮箱" placeholder="输入接收测试邮件的邮箱" value={recipient} maxLength={320}
        style={{ width: 300 }} disabled={busy} onChange={(value) => { setRecipient(value); setResult(null) }} onPressEnter={() => void send()} />
      <Button disabled={blocked || diagnosing} loading={sending} onClick={() => void send()}>发送测试邮件</Button>
      <Button disabled={blocked || sending} loading={diagnosing} onClick={() => void diagnose()}>检测连接</Button>
      <Typography.Text type="secondary">{dirty || !configured ? '请先保存完整的邮箱设置，再发送测试邮件或检测连接。' : '测试邮件立即发送一次；检测连接只检查连接、加密和登录，不发送邮件。'}</Typography.Text>
    </Space>
    {result && !blocked && result.configuration === configuration && <div role="status" style={{ marginTop: 8 }}><Alert type={result.success ? 'success' : result.deliveryUncertain ? 'warning' : 'error'} content={result.message} /></div>}
    {showDiagnosis && (diagnosis.error
      ? <div role="status" style={{ marginTop: 8 }}><Alert type="error" content={diagnosis.error} /></div>
      : diagnosis.value && <ol className="smtp-diagnosis" role="status" aria-label="连接检测结果">
        {diagnosis.value.steps.map((step) => (
          <li key={step.name} className={`smtp-diagnosis-step smtp-diagnosis-step--${step.status.toLowerCase()}`}>
            {step.status === 'OK' ? <IconCheckCircleFill /> : step.status === 'FAILED' ? <IconCloseCircleFill /> : <IconMinusCircle />}
            <span className="smtp-diagnosis-name">{step.name}</span>
            <span className="smtp-diagnosis-detail">{step.status === 'SKIPPED' ? '未检查（前一步失败）' : step.detail}</span>
            {step.status !== 'SKIPPED' && <span className="smtp-diagnosis-time">{step.elapsedMs} ms</span>}
          </li>
        ))}
      </ol>)}
  </div>
}
