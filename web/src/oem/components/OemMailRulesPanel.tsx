import { useCallback, useRef, useState } from 'react'
import { Alert, Button, Message, Space, Spin, Switch, Typography } from '@arco-design/web-react'
import type { OemApi } from '../api/OemApi'
import type { SettingItem } from '../api/types'
import { useLatestRequest, useLoadEffect } from '../useLoadEffect'

const MASTER = 'oem.notify.enabled'
const RULES = [
  { key: 'oem.notify.event.approval_pending', label: '待审批提醒', description: '审批任务生效或改派时，通知当前审批人。' },
  { key: 'oem.notify.event.transfer_released', label: '文件送达提醒', description: '公司发给厂商：通知该厂商全部启用联络人；厂商回传：通知有查看及下载权限的内部人员。' },
  { key: 'oem.notify.event.sender_result', label: '发送结果提醒', description: '审批通过、驳回、终止、阻断及附件异常时通知发送方；厂商发送方会通知全部启用联络人。' },
  { key: 'oem.notify.event.receipt', label: '接收与清理进度', description: '首次完整下载、附件按策略清理后通知发送方。' },
]

export default function OemMailRulesPanel({ api }: { api: Pick<OemApi, 'settings' | 'updateSettings'> }) {
  const [items, setItems] = useState<SettingItem[]>([])
  const [values, setValues] = useState<Record<string, string>>({})
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')
  const inFlight = useRef(false)
  const beginRequest = useLatestRequest()
  const apply = useCallback((rows: SettingItem[]) => {
    if (![MASTER, ...RULES.map(rule => rule.key)].every(key => rows.some(row => row.key === key))) throw new Error('Incomplete notification settings')
    setItems(rows)
    setValues(Object.fromEntries(rows.map(row => [row.key, row.value])))
  }, [])
  const load = useCallback(async () => {
    const isLatest = beginRequest()
    setLoading(true)
    setError('')
    try { const rows = await api.settings('notify'); if (isLatest()) apply(rows) }
    catch { if (isLatest()) setError('ODM/OEMA 邮件提醒规则加载失败，请重试') }
    finally { if (isLatest()) setLoading(false) }
  }, [api, beginRequest, apply])
  useLoadEffect(load)
  const changed = items.filter(item => !item.readOnly && values[item.key] !== item.value)
  const save = async () => {
    if (inFlight.current || !changed.length) return
    inFlight.current = true
    setSaving(true)
    try {
      const rows = await api.updateSettings('notify', changed.map(item => ({ key: item.key, value: values[item.key] })))
      apply(rows)
      setError('')
      Message.success('ODM/OEMA 邮件提醒规则已保存')
    } catch { setError('ODM/OEMA 邮件提醒规则保存失败，修改已保留，请重试') }
    finally { inFlight.current = false; setSaving(false) }
  }
  const disabled = loading || saving || !items.length
  const readOnly = (key: string) => disabled || Boolean(items.find(item => item.key === key)?.readOnly)
  return <div className="system-notification-settings oem-mail-rules">
    <div className="system-notification-heading">
      <Typography.Text type="secondary">与 Robot 共用系统 SMTP，以下开关只控制 ODM/OEMA 邮件。</Typography.Text>
      <Space><Typography.Text>ODM/OEMA 邮件通知总开关</Typography.Text><Switch aria-label="ODM/OEMA 邮件通知总开关" disabled={readOnly(MASTER)} checked={values[MASTER] === 'true'} onChange={checked => setValues(previous => ({ ...previous, [MASTER]: String(checked) }))} /></Space>
    </div>
    {error && <Alert type="error" content={error} action={<Button size="small" disabled={saving} onClick={() => void load()}>重新加载</Button>} />}
    <div className="system-notification-grid oem-mail-rules-grid">
      {loading ? <Spin loading /> : RULES.map(rule => <div className="system-notification-panel" key={rule.key}>
        <div className="system-notification-row">
          <div><Typography.Text bold>{rule.label}</Typography.Text><Typography.Text type="secondary">{rule.description}</Typography.Text></div>
          <Switch aria-label={rule.label} disabled={readOnly(rule.key)} checked={values[rule.key] === 'true'} onChange={checked => setValues(previous => ({ ...previous, [rule.key]: String(checked) }))} />
        </div>
      </div>)}
    </div>
    <Space wrap><Button type="primary" size="small" loading={saving} disabled={disabled || !changed.length} onClick={() => void save()}>保存 ODM/OEMA 邮件提醒</Button><Button size="small" disabled={disabled || !changed.length} onClick={() => setValues(Object.fromEntries(items.map(item => [item.key, item.value])))}>取消修改</Button><Typography.Text type="secondary">独立于 Robot 总开关；已入队邮件发送前会重新核对规则和联络人邮箱。</Typography.Text></Space>
  </div>
}
