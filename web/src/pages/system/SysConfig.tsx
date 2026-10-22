import { Spin, Tabs } from '@arco-design/web-react'
import { useNavigate } from 'react-router-dom'
import http from '../../api/client'
import { fmtTime } from '../../api/types'
import { renderMailSettingsPanel } from './components/MailSettingsPanel'
import { renderSystemConfigTable } from './components/SystemConfigTable'
import { useSystemConfig } from './hooks/useSystemConfig'
import LoadError from '../../components/LoadError'

export default function SysConfig() {
  const navigate = useNavigate()
  const state = useSystemConfig(http)

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

  const smtpPanel = renderMailSettingsPanel({
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
    onSaveNotifications: state.saveNotifications,
    onViewLogs: () => navigate('/logs'),
  })

  return (
    <div className="system-page">
      <div className="page-heading"><div><h1>系统参数</h1></div></div>
      {state.loadError ? (
        <LoadError onRetry={state.load} />
      ) : state.loading ? (
        <Spin loading style={{ width: '100%', minHeight: 120 }} />
      ) : (
        <>
          <Tabs className="system-config-tabs" defaultActiveTab="system">
            <Tabs.TabPane key="system" title="系统参数">
              {systemPanel}
            </Tabs.TabPane>
            <Tabs.TabPane key="smtp" title="SMTP 配置">
              {smtpPanel}
            </Tabs.TabPane>
          </Tabs>
        </>
      )}
    </div>
  )
}
