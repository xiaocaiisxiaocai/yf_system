import { useCallback, useEffect, useRef, useState } from 'react'
import { Message } from '@arco-design/web-react'
import type { QuietRequestConfig } from '../../../api/client'
import type { ApiResponses } from '../../../api/types'
import {
  EMPTY_MAIL_STATUS,
  EMPTY_NOTIFICATION_POLICY,
  EMPTY_SMTP,
  NOTIFICATION_CONFIG_KEYS,
  type Cfg,
  type MailStatus,
  type NotificationPolicy,
  type SmtpSettings,
  normalizeMailStatus,
} from '../components/systemConfigModel'

interface HttpResponse<T> {
  data: T
}

interface HttpClient {
  get<T = unknown>(url: string, config?: unknown): Promise<HttpResponse<T>>
  put<T = unknown>(url: string, body?: unknown): Promise<HttpResponse<T>>
}

interface Snapshot {
  configs: Cfg[]
  mail: MailStatus
  smtp: SmtpSettings
}

const quietRequest = { quietNetworkError: true } as QuietRequestConfig

export function useSystemConfig(http: HttpClient) {
  const [configs, setConfigs] = useState<Cfg[]>([])
  const [mail, setMail] = useState<MailStatus | null>(null)
  const [editing, setEditing] = useState<Record<string, string>>({})
  const [saving, setSaving] = useState(false)
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState(false)
  const [reloadKey, setReloadKey] = useState(0)
  const [smtp, setSmtp] = useState<SmtpSettings>(EMPTY_SMTP)
  const [smtpDraft, setSmtpDraft] = useState<SmtpSettings>(EMPTY_SMTP)
  const [smtpPassword, setSmtpPassword] = useState('')
  const [smtpSaving, setSmtpSaving] = useState(false)
  const [notificationDraft, setNotificationDraft] = useState<NotificationPolicy>(EMPTY_NOTIFICATION_POLICY)
  const [notificationSaving, setNotificationSaving] = useState(false)
  const saveInFlight = useRef(false)
  const smtpSaveInFlight = useRef(false)
  const notificationSaveInFlight = useRef(false)

  const fetchSnapshot = useCallback(async (): Promise<Snapshot> => {
    const [configsResponse, mailResponse, smtpResponse] = await Promise.all([
      http.get<ApiResponses['GET /admin/system/configs']>('/admin/system/configs', quietRequest),
      http.get<ApiResponses['GET /admin/system/mail-status']>('/admin/system/mail-status', quietRequest),
      http.get<ApiResponses['GET /admin/system/mail-settings']>('/admin/system/mail-settings', quietRequest),
    ])
    return {
      configs: (configsResponse.data as Cfg[]).filter((item) => item.key !== 'storage.warn_percent' && !NOTIFICATION_CONFIG_KEYS.has(item.key)),
      mail: normalizeMailStatus(mailResponse.data),
      smtp: { ...EMPTY_SMTP, ...smtpResponse.data } as SmtpSettings,
    }
  }, [http])

  const applySnapshot = useCallback((next: Snapshot) => {
    setConfigs(next.configs)
    setMail(next.mail || EMPTY_MAIL_STATUS)
    setNotificationDraft(next.mail?.notificationPolicy || EMPTY_NOTIFICATION_POLICY)
    setSmtp(next.smtp)
    setSmtpDraft(next.smtp)
    setSmtpPassword('')
    setEditing({})
  }, [])

  const load = useCallback(() => {
    setLoading(true)
    setLoadError(false)
    setReloadKey((value) => value + 1)
  }, [])

  useEffect(() => {
    let active = true
    fetchSnapshot().then((next) => {
      if (active) {
        applySnapshot(next)
        setLoadError(false)
      }
    }).catch(() => {
      if (active) setLoadError(true)
    }).finally(() => {
      if (active) setLoading(false)
    })
    return () => {
      active = false
    }
  }, [applySnapshot, fetchSnapshot, reloadKey])

  const dirty = Object.entries(editing).filter(([key, value]) => configs.find((config) => config.key === key)?.value !== value)
  const dirtyKeys = new Set(dirty.map(([key]) => key))
  const smtpDirty = smtpPassword.length > 0 || (['host', 'port', 'username', 'from', 'security'] as const).some((key) => smtpDraft[key] !== smtp[key])
  const smtpIdentityChanged = smtpDraft.host.trim().toLowerCase() !== smtp.host.toLowerCase() || smtpDraft.username.trim() !== smtp.username
  const notificationDirty = mail ? JSON.stringify(notificationDraft) !== JSON.stringify(mail.notificationPolicy) : false

  const updateNotificationDraft = (update: (current: NotificationPolicy) => NotificationPolicy) => {
    if (notificationSaveInFlight.current) return
    setNotificationDraft(update)
  }

  const cancelNotifications = () => {
    if (!notificationSaveInFlight.current) setNotificationDraft(mail?.notificationPolicy || EMPTY_NOTIFICATION_POLICY)
  }

  const saveSmtp = async () => {
    if (smtpSaveInFlight.current || !smtpDirty) return
    if (smtp.passwordNeedsUpdate && !smtpPassword) {
      Message.warning('已保存的授权码无法读取，请重新填写并保存')
      return
    }
    if (smtp.hasPassword && smtpIdentityChanged && !smtpPassword) {
      Message.warning('更改 SMTP 服务器或登录账号时，请重新填写授权码')
      return
    }
    if (!smtpDraft.host.trim() || !smtpDraft.username.trim() || !smtpDraft.from.trim() || (!smtp.hasPassword && !smtpPassword)) {
      Message.warning('请填写 SMTP 服务器、登录账号、发件邮箱和邮箱密码或授权码')
      return
    }
    smtpSaveInFlight.current = true
    setSmtpSaving(true)
    try {
      const response = await http.put<ApiResponses['PUT /admin/system/mail-settings']>('/admin/system/mail-settings', {
        host: smtpDraft.host.trim(), port: smtpDraft.port, username: smtpDraft.username.trim(),
        from: smtpDraft.from.trim(), security: smtpDraft.security, password: smtpPassword || null,
      })
      const saved = response.data as SmtpSettings
      setSmtp(saved)
      setSmtpDraft(saved)
      setSmtpPassword('')
      try {
        setMail(normalizeMailStatus((await http.get<ApiResponses['GET /admin/system/mail-status']>('/admin/system/mail-status', quietRequest)).data))
        Message.success('邮箱设置已保存，无需重启')
      } catch {
        Message.warning('邮箱设置已保存，但邮件状态刷新失败，请稍后刷新页面')
      }
    } catch { /* 拦截器已提示，保留输入以便重试。 */ }
    finally {
      smtpSaveInFlight.current = false
      setSmtpSaving(false)
    }
  }

  const saveNotifications = async () => {
    if (notificationSaveInFlight.current || !notificationDirty) return
    notificationSaveInFlight.current = true
    setNotificationSaving(true)
    const items = [
      { key: 'notify.enabled', value: String(notificationDraft.globalEnabled) },
      { key: 'notify.internal.enabled', value: String(notificationDraft.internalEnabled) },
      { key: 'notify.supplier.enabled', value: String(notificationDraft.supplierEnabled) },
      { key: 'notify.event.message_created', value: String(notificationDraft.events.messageCreated) },
      { key: 'notify.event.file_uploaded', value: String(notificationDraft.events.fileUploaded) },
      { key: 'notify.event.project_submitted', value: String(notificationDraft.events.projectSubmitted) },
      { key: 'notify.event.project_confirmed', value: String(notificationDraft.events.projectConfirmed) },
      { key: 'notify.event.project_rejected', value: String(notificationDraft.events.projectRejected) },
      { key: 'notify.event.project_withdrawn', value: String(notificationDraft.events.projectWithdrawn) },
    ]
    try {
      await http.put<ApiResponses['PUT /admin/system/configs']>('/admin/system/configs', { items })
      setMail((current) => current ? { ...current, notificationsEnabled: notificationDraft.globalEnabled, notificationPolicy: notificationDraft } : current)
      try {
        const refreshed = normalizeMailStatus((await http.get<ApiResponses['GET /admin/system/mail-status']>('/admin/system/mail-status', quietRequest)).data)
        setMail(refreshed)
        setNotificationDraft(refreshed.notificationPolicy)
        Message.success('邮件提醒设置已保存')
      } catch {
        Message.warning('邮件提醒设置已保存，但状态刷新失败，请稍后刷新页面')
      }
    } catch { /* 拦截器已提示，保留草稿以便重试。 */ }
    finally {
      notificationSaveInFlight.current = false
      setNotificationSaving(false)
    }
  }

  const setValue = (key: string, value: string) => {
    if (saving) return
    setEditing((current) => ({ ...current, [key]: value }))
  }

  const save = async () => {
    if (saveInFlight.current || dirty.length === 0) return
    saveInFlight.current = true
    const items = dirty.map(([key, value]) => ({ key, value }))
    const savedValues = new Map(items.map((item) => [item.key, item.value]))
    setSaving(true)
    try {
      await http.put<ApiResponses['PUT /admin/system/configs']>('/admin/system/configs', { items })
      setConfigs((current) => current.map((config) => (
        savedValues.has(config.key) ? { ...config, value: savedValues.get(config.key)! } : config
      )))
      setEditing((current) => {
        const next = { ...current }
        for (const item of items) {
          if (next[item.key] === item.value) delete next[item.key]
        }
        return next
      })
      try {
        const next = await fetchSnapshot()
        setConfigs(next.configs)
        setMail(next.mail)
        Message.success('参数已保存')
      } catch {
        Message.warning('参数已保存，但最新状态刷新失败，请稍后刷新页面')
      }
    } catch {
      /* 拦截器已提示 */
    } finally {
      saveInFlight.current = false
      setSaving(false)
    }
  }

  return {
    configs,
    mail,
    editing,
    saving,
    loading,
    loadError,
    smtp,
    smtpDraft,
    smtpPassword,
    smtpSaving,
    notificationDraft,
    notificationSaving,
    dirty,
    dirtyKeys,
    smtpDirty,
    smtpIdentityChanged,
    notificationDirty,
    setEditing,
    setSmtpDraft,
    setSmtpPassword,
    setNotificationDraft,
    updateNotificationDraft,
    cancelNotifications,
    load,
    save,
    saveSmtp,
    saveNotifications,
    setValue,
  }
}
