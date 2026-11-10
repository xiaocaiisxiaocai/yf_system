import { Button, Result } from '@arco-design/web-react'
import { IconEmail, IconList, IconSettings } from '@arco-design/web-react/icon'
import { Navigate, useNavigate } from 'react-router-dom'
import { useShallow } from 'zustand/react/shallow'
import { useAuth } from '../store/auth'
import AccountMenu from '../components/AccountMenu'
import Shell, { type MenuEntry } from '../layouts/SectionShell'
import { OUTSOURCE_PERMISSIONS, outsourceHome } from './outsourceNavigation'

export function OutsourceLanding() {
  const permissions = useAuth((state) => state.permissions)
  const home = outsourceHome(permissions)
  return home ? <Navigate to={home} replace /> : <Result status="403" title="没有可用的外协清单功能" />
}

/** Outsourcing list section for internal staff; shares only the internal login with the other business lines. */
export default function OutsourceLayout() {
  const { permissions, user } = useAuth(useShallow((state) => ({ permissions: state.permissions, user: state.user })))
  const navigate = useNavigate()
  if (user?.userType !== 'INTERNAL') return <Result status="403" title="外协清单仅供公司员工使用" />
  const menu: MenuEntry[] = [
    { path: '/outsource/items', label: '外协清单', icon: <IconList />, visible: permissions.includes(OUTSOURCE_PERMISSIONS.view) },
    { path: '/outsource/mails', label: '发送记录', icon: <IconEmail />, visible: permissions.includes(OUTSOURCE_PERMISSIONS.view) },
    { path: '/outsource/settings', label: '设定', icon: <IconSettings />, visible: permissions.includes(OUTSOURCE_PERMISSIONS.settings), admin: true },
  ]
  if (!menu.some((item) => item.visible)) {
    return <Result status="403" title="没有外协清单权限" extra={<Button onClick={() => navigate('/')}>返回</Button>} />
  }
  return <Shell title="外协清单" menu={menu} line="outsource" account={<AccountMenu />} />
}
