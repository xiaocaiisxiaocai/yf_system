import { useEffect, useMemo } from 'react'
import { Button, Dropdown, Menu, Result, Spin } from '@arco-design/web-react'
import {
  IconCheckCircle, IconDown, IconFile, IconHistory, IconPoweroff, IconSettings, IconUserGroup,
} from '@arco-design/web-react/icon'
import { Navigate, useLocation, useNavigate } from 'react-router-dom'
import { QueryClientProvider } from '@tanstack/react-query'
import http from '../../api/client'
import { useSessionQueryScope } from '../../api/queryClient'
import { useAuth } from '../../store/auth'
import { OemApi } from '../api/OemApi'
import {
  bootPortalSession, createPortalQueryScope, portalHttp, portalLogout, portalQueryClient, PORTAL_BASE, usePortalAuth,
  usePortalSessionSync,
} from '../api/portalSession'
import { OEM_SETTINGS_PERMISSIONS, oemHome } from '../oemNavigation'
import { OemProvider } from '../OemContext'
import Shell, { type MenuEntry } from '../../layouts/SectionShell'
import AccountMenu from '../../components/AccountMenu'
import './OemLayouts.css'

const internalApi = new OemApi(http)

export function OemLanding() {
  const permissions = useAuth((state) => state.permissions)
  const home = oemHome(permissions)
  return home ? <Navigate to={home} replace /> : <Result status="403" title="没有可用的 ODM/OEMA 功能" />
}

/**
 * OEM section for internal staff: a separate entry with its own navigation, sharing
 * only the internal login with the collaboration platform. Nothing from the
 * collaboration line is shown here, and nothing from here appears there.
 */
export function OemInternalLayout() {
  const { permissions, user } = useAuth()
  const sessionScope = useSessionQueryScope()
  const navigate = useNavigate()
  const set = useMemo(() => new Set(permissions), [permissions])
  const has = (code: string) => set.has(code)
  if (user?.userType !== 'INTERNAL') return <Result status="403" title="ODM/OEMA 内部入口仅供公司员工使用" />
  const menu: MenuEntry[] = [
    { path: '/oem/transfers', label: '文件传递单', icon: <IconFile />, visible: has('oem:transfer_create') || has('oem:transfer_view') },
    { path: '/oem/approvals', label: '审批', icon: <IconCheckCircle />, visible: has('oem:flow_approve') || has('oem:approval_recover') },
    { path: '/oem/admin/companies', label: '厂商与账号', icon: <IconUserGroup />, visible: has('oem:company_manage') || has('oem:account_manage'), admin: true },
    { path: '/oem/admin/settings', label: '设置', icon: <IconSettings />, visible: OEM_SETTINGS_PERMISSIONS.some(has), admin: true },
    { path: '/oem/admin/audit', label: '审计日志', icon: <IconHistory />, visible: has('oem:audit_view'), admin: true },
  ]
  if (!menu.some((item) => item.visible)) return <Result status="403" title="没有 ODM/OEMA 文件传递权限" extra={<Button onClick={() => navigate('/')}>返回</Button>} />
  return (
    <OemProvider value={{ api: internalApi, realm: 'internal', base: '/oem', permissions: set, userId: user?.id ?? null, queryScope: ['internal', ...sessionScope] }}>
      <Shell title="ODM/OEMA 文件传递" menu={menu} line="oem" account={<AccountMenu />} />
    </OemProvider>
  )
}

const portalApi = new OemApi(portalHttp)

/** Vendor portal: OEM accounts only; never loads collaboration routes or menus. */
export function PortalLayout() {
  const { booted, token, account, mustChangePassword, generation } = usePortalAuth()
  usePortalSessionSync()
  const navigate = useNavigate()
  const location = useLocation()
  useEffect(() => { void bootPortalSession() }, [])
  const permissions = useMemo(() => new Set<string>(), [])
  const queryScope = useMemo(
    () => ['portal', ...createPortalQueryScope({ account, generation })],
    [account, generation],
  )
  const oemValue = useMemo(() => ({
    api: portalApi,
    realm: 'oem' as const,
    base: PORTAL_BASE,
    permissions,
    userId: account?.id ?? null,
    queryScope,
  }), [permissions, account?.id, queryScope])
  if (!booted) return <Spin style={{ display: 'block', marginTop: 120 }} tip="正在恢复登录状态…" />
  if (!token) return <Navigate to={`${PORTAL_BASE}/login`} state={{ from: location.pathname }} replace />
  if (mustChangePassword) return <Navigate to={`${PORTAL_BASE}/change-password`} replace />
  return (
    <QueryClientProvider client={portalQueryClient}>
      <OemProvider value={oemValue}>
        <Shell title="ODM/OEMA 文件传递" subtitle={account?.companyName} menu={[
          { path: `${PORTAL_BASE}/transfers`, label: '文件传递单', icon: <IconFile />, visible: true },
        ]} account={
          <Dropdown trigger="click" droplist={
            <Menu onClickMenuItem={async (key) => {
              if (key === 'password') navigate(`${PORTAL_BASE}/change-password`)
              if (key === 'logout') { await portalLogout(); navigate(`${PORTAL_BASE}/login`) }
            }}>
              <Menu.Item key="password">修改密码</Menu.Item>
              <Menu.Item key="logout"><IconPoweroff style={{ marginRight: 8 }} />退出登录</Menu.Item>
            </Menu>
          }>
            <Button className="oem-account-button" type="text" style={{ color: 'inherit' }} title={account?.employeeNo}><span className="oem-account-name">{account?.employeeNo}</span><IconDown /></Button>
          </Dropdown>
        } />
      </OemProvider>
    </QueryClientProvider>
  )
}
