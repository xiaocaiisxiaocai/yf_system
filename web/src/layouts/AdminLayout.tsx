import { useState } from 'react'
import { Layout, Menu, Button, Drawer } from '@arco-design/web-react'
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
  IconMenuFold,
  IconMenuUnfold,
} from '@arco-design/web-react/icon'
import { Outlet, useLocation, useNavigate } from 'react-router-dom'
import { useShallow } from 'zustand/react/shallow'
import { useAuth } from '../store/auth'
import CollaborationNotifications from '../components/CollaborationNotifications'
import RouteErrorBoundary from '../components/RouteErrorBoundary'
import AccountMenu from '../components/AccountMenu'
import AppRail from './AppRail'
import { businessHomes, collaborationPages } from './navigation'

const { Sider, Header, Content } = Layout

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
  const [siderCollapsed, setSiderCollapsed] = useState(() => {
    try {
      return window.localStorage.getItem('yf:sider-collapsed') === 'true'
    } catch {
      return false
    }
  })

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

  const toggleSider = () => {
    setSiderCollapsed((current) => {
      const next = !current
      try {
        window.localStorage.setItem('yf:sider-collapsed', String(next))
      } catch {
        /* 本地存储不可用时仍保留当前会话状态 */
      }
      return next
    })
  }

  const menu = (closeAfterNavigate = false, collapsed = false) => (
    <Menu
      theme="dark"
      collapse={collapsed}
      selectedKeys={selected}
      onClickMenuItem={(key) => {
        nav(key)
        if (closeAfterNavigate) setMobileMenuOpen(false)
      }}
      style={{ width: '100%' }}
    >
      {items.map((m) => (
        <Menu.Item
          key={m.path}
          aria-label={collapsed ? m.label : undefined}
          renderItemInTooltip={() => m.label}
        >
          {MENU_ICONS[m.code]}
          {!collapsed && m.label}
        </Menu.Item>
      ))}
    </Menu>
  )

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
        {hasMenus && <Sider
          className={`layout-sider${siderCollapsed ? ' layout-sider--collapsed' : ''}`}
          width={200}
          collapsedWidth={64}
          collapsed={siderCollapsed}
          collapsible
          trigger={null}
          theme="dark"
        >
          {menu(false, siderCollapsed)}
          <button
            className="layout-sider-toggle"
            type="button"
            aria-label={siderCollapsed ? '展开侧边栏' : '折叠侧边栏'}
            onClick={toggleSider}
          >
            {siderCollapsed ? <IconMenuUnfold /> : <><IconMenuFold /><span>收起导航</span></>}
          </button>
        </Sider>}
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
        {hasMenus && menu(true)}
      </Drawer>}
    </Layout>
  )
}
