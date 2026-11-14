import { useCallback, useEffect, useRef, useState } from 'react'
import { Alert, Button, Collapse, Input, Message, Modal, Select, Space, Spin, Tag } from '@arco-design/web-react'
import http, { type QuietRequestConfig } from '../../api/client'
import type { ApiResponses } from '../../api/types'
import './OutsourceMailsPage.css'
import { requestErrorMessage } from '../../api/requestError'

type Preview = ApiResponses['POST /outsource/mails/preview']
type Draft = Preview['mails'][number]
type CcUser = ApiResponses['GET /outsource/settings/cc-user-options'][number]

interface Extra { users: CcUser[]; emails: string[] }

const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]+$/

function newRequestKey() {
  return typeof crypto !== 'undefined' && 'randomUUID' in crypto
    ? crypto.randomUUID()
    : `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 12)}`
}

/**
 * Second confirmation before mailing vendors (外协清单契约 → 发送): one card per mail with its fixed vendor recipients
 * and default copies, extra copies for that mail only, and the exact body. Confirming sends the preview fingerprint;
 * if anything changed meanwhile the server refuses and the preview reloads.
 */
export default function SendMailDialog({ itemIds, onClose, onSent }: {
  itemIds: number[] | null
  onClose: () => void
  onSent: () => void
}) {
  const visible = itemIds !== null
  const [preview, setPreview] = useState<Preview | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [extra, setExtra] = useState<Record<string, Extra>>({})
  const [sending, setSending] = useState(false)
  const requestKey = useRef(newRequestKey())

  const load = useCallback(async (ids: number[]) => {
    setPreview(null); setError(null)
    try {
      const response = await http.post<ApiResponses['POST /outsource/mails/preview']>('/outsource/mails/preview', { itemIds: ids }, { quietClientError: true } as QuietRequestConfig)
      setPreview(response.data)
    } catch (failure) {
      setError(requestErrorMessage(failure, '预览失败，请稍后重试'))
    }
  }, [])

  useEffect(() => {
    if (!itemIds) return
    let active = true
    requestKey.current = newRequestKey()
    void Promise.resolve().then(() => { if (active) { setExtra({}); void load(itemIds) } })
    return () => { active = false }
  }, [itemIds, load])

  const send = async () => {
    if (!preview || !itemIds || sending) return
    const risky = preview.mails.filter((mail) => mail.customerCount > 1 && (extra[mail.key]?.emails.length ?? 0) > 0)
    if (risky.length > 0) {
      const confirmed = await new Promise<boolean>((resolve) => Modal.confirm({
        title: '确认抄送外部地址？',
        content: risky.map((mail) => `${mail.vendorName}：本封含 ${mail.customerCount} 个客户的工令`).join('；'),
        okText: '仍要发送', cancelText: '返回修改',
        onOk: () => resolve(true), onCancel: () => resolve(false),
      }))
      if (!confirmed) return
    }
    setSending(true)
    try {
      await http.post<ApiResponses['POST /outsource/mails/send']>('/outsource/mails/send', {
        itemIds, fingerprint: preview.fingerprint, requestKey: requestKey.current,
        extraCc: Object.entries(extra).map(([key, value]) => ({ key, userIds: value.users.map((user) => user.userId), emails: value.emails })),
      })
      Message.success(`已排入发送 ${preview.mails.length} 封邮件`)
      onSent()
    } catch (failure) {
      // 资料在预览后变动：重新预览，保留另加的抄送。
      if ((failure as { response?: { data?: { code?: number } } })?.response?.data?.code === 40903) {
        requestKey.current = newRequestKey()
        await load(itemIds)
      }
    } finally {
      setSending(false)
    }
  }

  const notices = preview?.mails.filter((mail) => mail.kind === 'NOTICE').length ?? 0
  const withdrawals = (preview?.mails.length ?? 0) - notices
  return <Modal title="确认发送邮件" visible={visible} style={{ width: 860, maxWidth: 'calc(100vw - 32px)' }}
    onCancel={() => { if (!sending) onClose() }} closable={!sending} maskClosable={false}
    footer={<Space>
      <Button disabled={sending} onClick={onClose}>取消</Button>
      <Button type="primary" loading={sending} disabled={!preview} onClick={() => void send()}>
        确认发送{preview ? ` ${preview.mails.length} 封` : ''}
      </Button>
    </Space>}>
    {error ? <Alert type="error" content={error} />
      : !preview ? <div className="outsource-mail-loading"><Spin /></div>
        : <div className="outsource-mail-drafts">
          <div className="outsource-mail-summary">外发通知 {notices} 封{withdrawals > 0 ? `，撤回通知 ${withdrawals} 封` : ''}</div>
          {preview.mails.map((mail) => <DraftCard key={mail.key} draft={mail} extra={extra[mail.key] ?? { users: [], emails: [] }}
            onChange={(value) => setExtra((current) => ({ ...current, [mail.key]: value }))} disabled={sending} />)}
        </div>}
  </Modal>
}

function DraftCard({ draft, extra, onChange, disabled }: {
  draft: Draft; extra: Extra; onChange: (value: Extra) => void; disabled: boolean
}) {
  const [options, setOptions] = useState<CcUser[]>([])
  const [email, setEmail] = useState('')
  const [emailError, setEmailError] = useState(false)
  const searchSequence = useRef(0)
  const taken = new Set([...draft.to, ...draft.defaultCc].map((address) => address.email.toLowerCase()))

  const search = async (keyword: string) => {
    const seq = ++searchSequence.current
    try {
      const response = await http.get<ApiResponses['GET /outsource/settings/cc-user-options']>('/outsource/settings/cc-user-options', { params: { keyword } })
      if (seq === searchSequence.current) setOptions(response.data)
    } catch { /* 统一请求层提示 */ }
  }
  const addEmail = () => {
    const value = email.trim()
    if (!EMAIL.test(value)) { setEmailError(true); return }
    if (!taken.has(value.toLowerCase()) && !extra.emails.some((item) => item.toLowerCase() === value.toLowerCase()))
      onChange({ ...extra, emails: [...extra.emails, value] })
    setEmail(''); setEmailError(false)
  }

  const withdraw = draft.kind === 'WITHDRAW'
  return <section className="outsource-mail-draft" aria-label={`${draft.vendorName} ${withdraw ? '撤回通知' : '外发通知'}`}>
    <header className="outsource-mail-draft-head">
      <Tag color={withdraw ? 'orange' : 'arcoblue'}>{withdraw ? '撤回通知' : '外发通知'}</Tag>
      <strong>{draft.vendorName}</strong>
      <span className="outsource-mail-count">{draft.itemIds.length} 笔</span>
    </header>
    <div className="outsource-mail-row">
      <span className="outsource-mail-label">收件人</span>
      <Space wrap size={4}>{draft.to.map((address) => <Tag key={address.email}>{address.email}</Tag>)}</Space>
    </div>
    <div className="outsource-mail-row">
      <span className="outsource-mail-label">抄送</span>
      <Space wrap size={4}>
        {draft.defaultCc.map((address) => <Tag key={address.email} color="gray">{address.name ?? address.email}</Tag>)}
        {extra.users.map((user) => <Tag key={user.userId} closable={!disabled} color="green"
          onClose={() => onChange({ ...extra, users: extra.users.filter((item) => item.userId !== user.userId) })}>{user.realName}</Tag>)}
        {extra.emails.map((value) => <Tag key={value} closable={!disabled} color="orangered"
          onClose={() => onChange({ ...extra, emails: extra.emails.filter((item) => item !== value) })}>{value}</Tag>)}
      </Space>
    </div>
    <div className="outsource-mail-row">
      <span className="outsource-mail-label" />
      <Space wrap size={8}>
        <Select aria-label={`${draft.vendorName} 另加内部抄送`} placeholder="另加内部人员" showSearch filterOption={false} allowClear
          disabled={disabled} style={{ width: 220 }} value={undefined} onSearch={(value) => void search(value)}
          onFocus={() => { if (!options.length) void search('') }}
          onChange={(value: number | undefined) => {
            const user = options.find((option) => option.userId === value)
            if (user && !taken.has(user.email.toLowerCase()) && !extra.users.some((item) => item.userId === user.userId))
              onChange({ ...extra, users: [...extra.users, user] })
          }}
          options={options.map((option) => ({ label: `${option.realName}（${option.email}）`, value: option.userId }))} />
        <Input aria-label={`${draft.vendorName} 另加外部抄送`} placeholder="外部邮箱，例如客户" disabled={disabled} style={{ width: 220 }}
          value={email} status={emailError ? 'error' : undefined} onChange={(value) => { setEmail(value); setEmailError(false) }}
          onPressEnter={addEmail} />
        <Button disabled={disabled || !email.trim()} onClick={addEmail}>加入</Button>
      </Space>
    </div>
    <Collapse bordered={false} className="outsource-mail-body">
      <Collapse.Item name="body" header={draft.subject}>
        <iframe title={`${draft.vendorName} 邮件内容`} sandbox="" srcDoc={draft.htmlBody} className="outsource-mail-frame" />
      </Collapse.Item>
    </Collapse>
  </section>
}
