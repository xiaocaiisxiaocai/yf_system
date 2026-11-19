import { Button, Result } from '@arco-design/web-react'
import { IconEmail, IconList, IconSettings, IconStorage } from '@arco-design/web-react/icon'
import { Navigate, useNavigate } from 'react-router-dom'
import { useShallow } from 'zustand/react/shallow'
import { useAuth } from '../store/auth'
import AccountMenu from '../components/AccountMenu'
import Shell, { type MenuEntry } from '../layouts/SectionShell'
import { OUTSOURCE_PERMISSIONS, canOpenOutsourceSettings, outsourceHome } from './outsourceNavigation'
import { SEND_QUEUE_PATH, useSendQueue, useSendQueuePolling } from './sendQueue'

export function OutsourceLanding() {
  const permissions = useAuth((state) => state.permissions)
  const home = outsourceHome(permissions)
  return home ? <Navigate to={home} replace /> : <Result status="403" title="没有可用的外协清单功能" />
}

/** Outsourcing list section for internal staff; shares only the internal login with the other business lines. */
export default function OutsourceLayout() {
  const { permissions, user } = useAuth(useShallow((state) => ({ permissions: state.permissions, user: state.user })))
  const navigate = useNavigate()
  const canSend = user?.userType === 'INTERNAL' && permissions.includes(OUTSOURCE_PERMISSIONS.send)
  useSendQueuePolling(canSend)
  const queued = useSendQueue((state) => state.count)
  if (user?.userType !== 'INTERNAL') return <Result status="403" title="外协清单仅供公司员工使用" />
  const menu: MenuEntry[] = [
    {
      path: '/outsource/items', label: '外协清单', icon: <IconList />, visible: permissions.includes(OUTSOURCE_PERMISSIONS.view),
      // Senders see how many items wait for them and open the list filtered to those.
      badge: canSend ? queued : undefined, to: canSend && queued > 0 ? SEND_QUEUE_PATH : undefined,
    },
    { path: '/outsource/mails', label: '发送记录', icon: <IconEmail />, visible: permissions.includes(OUTSOURCE_PERMISSIONS.view) },
    { path: '/outsource/ipms-data', label: 'IPMS 资料', icon: <IconStorage />, visible: permissions.includes(OUTSOURCE_PERMISSIONS.settings), admin: true },
    { path: '/outsource/settings', label: '设定', icon: <IconSettings />, visible: canOpenOutsourceSettings(permissions), admin: true },
  ]
  if (!menu.some((item) => item.visible)) {
    return <Result status="403" title="没有外协清单权限" extra={<Button onClick={() => navigate('/')}>返回</Button>} />
  }
  return <Shell title="外协清单" menu={menu} line="outsource" account={<AccountMenu />} />
}
