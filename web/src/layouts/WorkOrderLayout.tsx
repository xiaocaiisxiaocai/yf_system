import { Button, Result } from '@arco-design/web-react'
import { IconCheckCircle, IconEmail, IconFile, IconHistory, IconList, IconSettings, IconStorage, IconUserGroup } from '@arco-design/web-react/icon'
import { useNavigate } from 'react-router-dom'
import { useShallow } from 'zustand/react/shallow'
import AccountMenu from '../components/AccountMenu'
import { OEM_UI_ENABLED } from '../features'
import { OEM_SETTINGS_PERMISSIONS, OUTSOURCE_MANAGEMENT_PATH, OUTSOURCE_MANAGEMENT_PERMISSIONS } from '../oem/oemNavigation'
import { OUTSOURCE_HOME, OUTSOURCE_PERMISSIONS } from '../outsource/outsourceNavigation'
import { SEND_QUEUE_PATH, useSendQueue, useSendQueuePolling } from '../outsource/sendQueue'
import { useAuth } from '../store/auth'
import { WORK_ORDER_TITLE } from './navigation'
import SectionShell, { type MenuEntry } from './SectionShell'

export function WorkOrderUnavailable() {
  const navigate = useNavigate()
  return <Result status="403" title={`没有 ${WORK_ORDER_TITLE} 的可用功能`}
    extra={<Button type="primary" onClick={() => navigate('/')}>返回首页</Button>} />
}

export default function WorkOrderLayout() {
  const { permissions, user } = useAuth(useShallow((state) => ({ permissions: state.permissions, user: state.user })))
  const internal = user?.userType === 'INTERNAL'
  const has = (code: string) => internal && permissions.includes(code)
  const oem = OEM_UI_ENABLED && internal
  const canSend = has(OUTSOURCE_PERMISSIONS.send)
  useSendQueuePolling(canSend)
  const queued = useSendQueue((state) => state.count)

  const menu: MenuEntry[] = [
    { path: '/oem/transfers', label: '文件传递单', icon: <IconFile />, visible: oem && (has('oem:transfer_create') || has('oem:transfer_view')) },
    { path: '/oem/approvals', label: '审批', icon: <IconCheckCircle />, visible: oem && (has('oem:flow_approve') || has('oem:approval_recover')) },
    {
      path: OUTSOURCE_HOME, label: '外发工令', icon: <IconList />, visible: internal,
      badge: canSend ? queued : undefined, to: canSend && queued > 0 ? SEND_QUEUE_PATH : undefined,
    },
    { path: '/outsource/mails', label: '发送记录', icon: <IconEmail />, visible: canSend },
    { path: '/outsource/ipms-data', label: 'IPMS 资料', icon: <IconStorage />, visible: has(OUTSOURCE_PERMISSIONS.settings), admin: true },
    { path: OUTSOURCE_MANAGEMENT_PATH, label: '外协管理设定', icon: <IconUserGroup />, visible: oem && OUTSOURCE_MANAGEMENT_PERMISSIONS.some(has), admin: true },
    { path: '/outsource/settings', label: '外发工令设定', icon: <IconSettings />, visible: has(OUTSOURCE_PERMISSIONS.settings), admin: true },
    { path: '/oem/admin/settings', label: '文件传递设置', icon: <IconSettings />, visible: oem && OEM_SETTINGS_PERMISSIONS.some(has), admin: true },
    { path: '/oem/admin/audit', label: '审计日志', icon: <IconHistory />, visible: oem && has('oem:audit_view'), admin: true },
  ]
  return menu.some((item) => item.visible)
    ? <SectionShell title={WORK_ORDER_TITLE} menu={menu} line="workOrder" account={<AccountMenu />} />
    : <WorkOrderUnavailable />
}
