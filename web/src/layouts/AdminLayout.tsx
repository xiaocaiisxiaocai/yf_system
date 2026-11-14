import { useState } from 'react'
import { Layout, Button, Drawer } from '@arco-design/web-react'
import {
  IconHome,
  IconFile,
  IconUserGroup,
  IconUser,
  IconMindMapping,
  IconSafe,
  IconHistory,
  IconSettings,
  IconBook,
  IconMenu,
} from '@arco-design/web-react/icon'
import { Outlet, useLocation, useNavigate } from 'react-router-dom'
import { useShallow } from 'zustand/react/shallow'
import { useAuth } from '../store/auth'
import CollaborationNotifications from '../components/CollaborationNotifications'
import RouteErrorBoundary from '../components/RouteErrorBoundary'
import AccountMenu from '../components/AccountMenu'
import AppRail from './AppRail'
import NavSider, { NavMenu } from './NavSider'
import { businessHomes, collaborationPages } from './navigation'

const { Header, Content } = Layout

const MENU_ICONS = {
  dashboard: <IconHome />, 'project:list': <IconFile />, 'supplier:list': <IconUserGroup />,
  'org:user': <IconUser />, 'org:dept': <IconMindMapping />, 'rbac:role': <IconSafe />,
  'log:audit': <IconHistory />, 'system:config': <IconSettings />, 'system:dict': <IconBook />,
}

export default function AdminLayout() {
  const { generation, menus, permissions, token, user } = useAuth(useShallow((state) => ({
    generation: state.generation,
    menus: state.menus,
    permissions: state.permissions,
    token: state.token,
    user: state.user,
  })))
  const nav = useNavigate()
  const loc = useLocation()
  const [mobileMenuOpen, setMobileMenuOpen] = useState(false)

  const items = collaborationPages({ menus, permissions, user })
  const hasMenus = items.length > 0
  const homes = businessHomes({ menus, permissions, user })
  const hasNavigation = hasMenus || Boolean(homes.oem || homes.outsource)
  const canUseCollaboration = Boolean(
    token && menus.includes('project:list') && permissions.includes('project:list'),
  )
  const selected =
    items
      .filter((m) => (m.path === '/'
        ? loc.pathname === '/'
        : m.path === '/projects'
          ? loc.pathname.startsWith('/projects')
          : loc.pathname.startsWith(m.path)))
      .map((m) => m.path)
      .slice(0, 1) || []

  const navItems = items.map((m) => ({ key: m.path, label: m.label, icon: MENU_ICONS[m.code] }))

  return (
    <Layout className="layout-shell">
      <Header className="layout-header">
        {hasNavigation && <Button
          className="mobile-menu-trigger"
          type="text"
          aria-label="打开导航菜单"
          icon={<IconMenu />}
          onClick={() => setMobileMenuOpen(true)}
        />}
        <div className="layout-logo" title="供应商协作平台">
          <img src="/saa-logo.svg" alt="SAA" />
          <span>供应商协作平台</span>
        </div>
        <div className="layout-header-account">
          {canUseCollaboration && <CollaborationNotifications key={`${generation}:${user?.id}`} />}
          <AccountMenu />
        </div>
      </Header>
      <Layout className="layout-main">
        <AppRail active="collaboration" />
        {hasMenus && <NavSider items={navItems} selectedKey={selected[0]} onSelect={(key) => nav(key)} />}
        <Content className="layout-content">
          <RouteErrorBoundary>
            <Outlet />
          </RouteErrorBoundary>
        </Content>
      </Layout>
      {hasNavigation && <Drawer
        className="mobile-nav-drawer"
        title={(
          <div className="drawer-brand">
            <img className="drawer-logo" src="/saa-logo.svg" alt="SAA" />
            <span>供应商协作平台</span>
          </div>
        )}
        placement="left"
        width={240}
        visible={mobileMenuOpen}
        footer={null}
        unmountOnExit
        onCancel={() => setMobileMenuOpen(false)}
      >
        <AppRail active="collaboration" variant="drawer" onNavigate={() => setMobileMenuOpen(false)} />
        {hasMenus && <NavMenu items={navItems} selectedKey={selected[0]} onSelect={(key) => { nav(key); setMobileMenuOpen(false) }} />}
      </Drawer>}
    </Layout>
  )
}
