import { Card, Spin, Tabs } from '@arco-design/web-react'
import { useNavigate } from 'react-router-dom'
import http from '../../api/client'
import { fmtTime } from '../../api/types'
import { renderMailSettingsPanel } from './components/MailSettingsPanel'
import { renderSystemConfigTable } from './components/SystemConfigTable'
import { useSystemConfig } from './hooks/useSystemConfig'
import LoadError from '../../components/LoadError'
import { useAuth } from '../../store/auth'
import StorageLocationsPanel from './components/StorageLocationsPanel'
import { useMemo, useState, type ReactElement } from 'react'
import { OemApi } from '../../oem/api/OemApi'
import OemMailRulesPanel from '../../oem/components/OemMailRulesPanel'

export default function SysConfig() {
  const { hasPerm } = useAuth()
  const canManageConfig = hasPerm('config:manage')
  const storageTab = hasPerm('storage:manage') ? (
    <Tabs.TabPane key="storage" title="存储位置"><StorageLocationsPanel /></Tabs.TabPane>
  ) : null
  if (!canManageConfig && !storageTab) return null
  return (
    <Card className="page-card page-card--table system-page">
      <h1 className="visually-hidden">系统参数</h1>
      {canManageConfig ? <ConfigurationTabs storageTab={storageTab} /> : (
        <Tabs className="system-config-tabs" defaultActiveTab="storage">{storageTab}</Tabs>
      )}
    </Card>
  )
}

function ConfigurationTabs({ storageTab }: { storageTab: ReactElement | null }) {
  const navigate = useNavigate()
  const state = useSystemConfig(http)
  const canManageOemNotify = useAuth().hasPerm('oem:notify_manage')
  const [rulesBusiness, setRulesBusiness] = useState<'robot' | 'oem'>('robot')
  const oemApi = useMemo(() => new OemApi(http), [])

  const systemPanel = renderSystemConfigTable({
    configs: state.configs,
    editing: state.editing,
    dirty: state.dirty,
    dirtyKeys: state.dirtyKeys,
    saving: state.saving,
    formatTime: fmtTime,
    onReset: () => state.setEditing({}),
    onSave: state.save,
    onSetValue: state.setValue,
  })

  const mailPanelProps = {
    mail: state.mail,
    smtp: state.smtp,
    smtpDraft: state.smtpDraft,
    smtpPassword: state.smtpPassword,
    smtpSaving: state.smtpSaving,
    smtpDirty: state.smtpDirty,
    smtpIdentityChanged: state.smtpIdentityChanged,
    notificationDraft: state.notificationDraft,
    notificationSaving: state.notificationSaving,
    notificationDirty: state.notificationDirty,
    formatTime: fmtTime,
    onSmtpDraftChange: state.setSmtpDraft,
    onSmtpPasswordChange: state.setSmtpPassword,
    onNotificationDraftChange: state.updateNotificationDraft,
    onCancelSmtp: () => {
      state.setSmtpDraft(state.smtp)
      state.setSmtpPassword('')
    },
    onCancelNotifications: state.cancelNotifications,
    onSaveSmtp: state.saveSmtp,
    onTested: state.refreshMailStatus,
    onSaveNotifications: state.saveNotifications,
    onViewLogs: () => navigate('/logs'),
    rulesBusiness,
    onRulesBusinessChange: setRulesBusiness,
    oemRules: canManageOemNotify ? <OemMailRulesPanel api={oemApi} /> : undefined,
  }
  const smtpPanel = renderMailSettingsPanel(mailPanelProps)
  const reminderPanel = renderMailSettingsPanel(mailPanelProps, 'rules')

  if (state.loadError) return <LoadError onRetry={state.load} />
  if (state.loading) return <Spin loading style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', width: '100%', minHeight: 120 }} />
  return (
    <Tabs key={storageTab ? 'with-storage' : 'config'} className="system-config-tabs" defaultActiveTab="system">
      <Tabs.TabPane key="system" title="系统参数">{systemPanel}</Tabs.TabPane>
      <Tabs.TabPane key="smtp" title="SMTP 配置">{smtpPanel}</Tabs.TabPane>
      <Tabs.TabPane key="mail-rules" title="邮件提醒规则">{reminderPanel}</Tabs.TabPane>
      {storageTab}
    </Tabs>
  )
}
