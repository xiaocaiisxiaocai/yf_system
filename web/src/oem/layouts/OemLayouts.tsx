import { useEffect, useMemo, useState, type ReactNode } from 'react'
import { Button, Drawer, Dropdown, Layout, Menu, Result, Space, Spin } from '@arco-design/web-react'
import {
  IconApps, IconCheckCircle, IconDown, IconFile, IconHistory, IconLeft, IconMindMapping, IconPoweroff, IconSafe, IconSettings,
  IconMenu, IconStorage, IconUserGroup,
} from '@arco-design/web-react/icon'
import { Navigate, Outlet, useLocation, useNavigate } from 'react-router-dom'
import http from '../../api/client'
import { useAuth } from '../../store/auth'
import { OemApi } from '../api/OemApi'
import { bootPortalSession, portalHttp, portalLogout, PORTAL_BASE, usePortalAuth } from '../api/portalSession'
import { oemHome } from '../oemNavigation'
import { OemProvider } from '../OemContext'
import './OemLayouts.css'

const { Header, Sider, Content } = Layout

interface MenuEntry { path: string; label: string; icon: ReactNode; visible: boolean }

function Shell({ title, subtitle, menu, account }: { title: string; subtitle?: string; menu: MenuEntry[]; account: ReactNode }) {
  const navigate = useNavigate()
  const location = useLocation()
  const [mobileMenuOpen, setMobileMenuOpen] = useState(false)
  const items = menu.filter((item) => item.visible)
  const selected = items.filter((item) => location.pathname.startsWith(item.path)).sort((a, b) => b.path.length - a.path.length)[0]?.path
  const menuContent = (closeAfterNavigate = false) => (
    <Menu theme="dark" selectedKeys={selected ? [selected] : []} onClickMenuItem={(key) => {
      navigate(key)
      if (closeAfterNavigate) setMobileMenuOpen(false)
    }} style={{ width: '100%' }}>
      {items.map((item) => <Menu.Item key={item.path}>{item.icon}{item.label}</Menu.Item>)}
    </Menu>
  )
  return (
    <Layout className="layout-shell oem-shell">
      <Header className="layout-header">
        {items.length > 0 && <Button className="mobile-menu-trigger" type="text" aria-label="打开导航菜单"
          icon={<IconMenu />} onClick={() => setMobileMenuOpen(true)} />}
        <div className="layout-logo" title={subtitle ? `${title} · ${subtitle}` : title}>
          <img src="/saa-logo.svg" alt="SAA" />
          <span>{title}</span>
          {subtitle && <span style={{ marginLeft: 12, fontSize: 13, opacity: 0.8 }}>{subtitle}</span>}
        </div>
        <div className="layout-header-account">{account}</div>
      </Header>
      <Layout className="layout-main">
        <Sider className="layout-sider" width={200} theme="dark">
          {menuContent()}
        </Sider>
        <Content className="layout-content"><Outlet /></Content>
      </Layout>
      {items.length > 0 && <Drawer className="mobile-nav-drawer" title={(
        <div className="drawer-brand"><img className="drawer-logo" src="/saa-logo.svg" alt="SAA" /><span>{title}</span></div>
      )} placement="left" width={240} visible={mobileMenuOpen} footer={null} unmountOnExit onCancel={() => setMobileMenuOpen(false)}>
        {menuContent(true)}
      </Drawer>}
    </Layout>
  )
}

const internalApi = new OemApi(http)

export function OemLanding() {
  const permissions = useAuth((state) => state.permissions)
  const home = oemHome(permissions)
  return home ? <Navigate to={home} replace /> : <Result status="403" title="没有可用的 OEM 功能" />
}

/**
 * OEM section for internal staff: a separate entry with its own navigation, sharing
 * only the internal login with the collaboration platform. Nothing from the
 * collaboration line is shown here, and nothing from here appears there.
 */
export function OemInternalLayout() {
  const { permissions, user } = useAuth()
  const navigate = useNavigate()
  const set = useMemo(() => new Set(permissions), [permissions])
  const has = (code: string) => set.has(code)
  const menu: MenuEntry[] = [
    { path: '/oem/transfers', label: '文件传递单', icon: <IconFile />, visible: has('oem:transfer_create') || has('oem:transfer_view') },
    { path: '/oem/approvals', label: '待我审批', icon: <IconCheckCircle />, visible: has('oem:flow_approve') },
    { path: '/oem/admin/companies', label: '厂商与账号', icon: <IconUserGroup />, visible: has('oem:company_manage') || has('oem:account_manage') },
    { path: '/oem/admin/flow-templates', label: '审批模板', icon: <IconSafe />, visible: has('oem:flow_template_manage') },
    { path: '/oem/admin/leaders', label: '组织主管', icon: <IconMindMapping />, visible: has('dept:leader_manage') },
    { path: '/oem/admin/retention', label: '删除策略', icon: <IconStorage />, visible: has('oem:retention_template_manage') },
    { path: '/oem/admin/settings', label: '文件策略与提醒', icon: <IconSettings />, visible: has('oem:file_policy_manage') || has('oem:notify_manage') },
    { path: '/oem/admin/audit', label: 'OEM 审计日志', icon: <IconHistory />, visible: has('oem:audit_view') },
  ]
  if (!menu.some((item) => item.visible)) return <Result status="403" title="没有 OEM 文件传递权限" extra={<Button onClick={() => navigate('/')}>返回</Button>} />
  return (
    <OemProvider value={{ api: internalApi, realm: 'internal', base: '/oem', permissions: set, userId: user?.id ?? null }}>
      <Shell title="OEM 文件传递" menu={menu} account={
        <Space>
          <Button type="text" icon={<IconLeft />} aria-label="返回协作平台" title="返回协作平台" style={{ color: 'inherit' }} onClick={() => navigate('/')}><span className="oem-return-label">返回协作平台</span></Button>
          <span className="oem-account-name" title={user?.realName}>{user?.realName}</span>
        </Space>
      } />
    </OemProvider>
  )
}

/** Entry shown in the collaboration header only for staff holding any OEM permission. */
export function OemEntryButton() {
  const permissions = useAuth((state) => state.permissions)
  const navigate = useNavigate()
  const first = oemHome(permissions)
  if (!first) return null
  // Collapses to its icon on narrow screens (see collaboration.css); the label stays accessible.
  return (
    <Button className="oem-entry-button" type="text" icon={<IconApps />} style={{ color: 'inherit' }}
      aria-label="OEM 文件传递" title="OEM 文件传递" onClick={() => navigate(first)}>
      <span className="oem-entry-label">OEM 文件传递</span>
    </Button>
  )
}

const portalApi = new OemApi(portalHttp)

/** Vendor portal: OEM accounts only; never loads collaboration routes or menus. */
export function PortalLayout() {
  const { booted, token, account, mustChangePassword } = usePortalAuth()
  const navigate = useNavigate()
  const location = useLocation()
  useEffect(() => { void bootPortalSession() }, [])
  if (!booted) return <Spin style={{ display: 'block', marginTop: 120 }} tip="正在恢复登录状态…" />
  if (!token) return <Navigate to={`${PORTAL_BASE}/login`} state={{ from: location.pathname }} replace />
  if (mustChangePassword) return <Navigate to={`${PORTAL_BASE}/change-password`} replace />
  return (
    <OemProvider value={{ api: portalApi, realm: 'oem', base: PORTAL_BASE, permissions: new Set(), userId: account?.id ?? null }}>
      <Shell title="OEM 文件传递" subtitle={account?.companyName} menu={[
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
          <Button className="oem-account-button" type="text" style={{ color: 'inherit' }} title={account?.realName}><span className="oem-account-name">{account?.realName}</span><IconDown /></Button>
        </Dropdown>
      } />
    </OemProvider>
  )
}
