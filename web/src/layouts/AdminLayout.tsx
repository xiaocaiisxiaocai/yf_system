import { useState } from 'react'
import { Layout, Menu, Dropdown, Avatar, Button, Drawer } from '@arco-design/web-react'
import {
  IconHome,
  IconFile,
  IconUserGroup,
  IconUser,
  IconMindMapping,
  IconSafe,
  IconHistory,
  IconSettings,
  IconDown,
  IconPoweroff,
  IconLock,
  IconMenu,
  IconMenuFold,
  IconMenuUnfold,
} from '@arco-design/web-react/icon'
import { Outlet, useLocation, useNavigate } from 'react-router-dom'
import { useAuth } from '../store/auth'
import http, { withAuthLock } from '../api/client'

const { Sider, Header, Content } = Layout

const MENU_ITEMS = [
  { code: 'dashboard', path: '/', label: '工作台', icon: <IconHome /> },
  { code: 'project:list', path: '/projects', label: '项目协作', icon: <IconFile /> },
  { code: 'supplier:list', path: '/suppliers', label: '供应商管理', icon: <IconUserGroup /> },
  { code: 'org:user', path: '/org/users', label: '用户管理', icon: <IconUser /> },
  { code: 'org:dept', path: '/org/depts', label: '组织架构', icon: <IconMindMapping /> },
  { code: 'rbac:role', path: '/rbac/roles', label: '角色权限', icon: <IconSafe /> },
  { code: 'log:audit', path: '/logs', label: '操作日志', icon: <IconHistory /> },
  { code: 'system:config', path: '/system/config', label: '系统参数', icon: <IconSettings /> },
]

export default function AdminLayout() {
  const { menus, user, logout } = useAuth()
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

  const items = MENU_ITEMS.filter((m) => menus.includes(m.code))
  const selected =
    items
      .filter((m) => (m.path === '/' ? loc.pathname === '/' : loc.pathname.startsWith(m.path)))
      .map((m) => m.path)
      .slice(0, 1) || []

  const doLogout = async () => {
    await withAuthLock(async () => {
    try {
      await http.post('/auth/logout')
    } catch {
      /* 忽略 */
    }
    logout()
    })
    nav('/login')
  }

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
        <Menu.Item key={m.path} title={collapsed ? m.label : undefined}>
          {m.icon}
          {!collapsed && m.label}
        </Menu.Item>
      ))}
    </Menu>
  )

  return (
    <Layout className="layout-shell">
      <Header className="layout-header">
        <Button
          className="mobile-menu-trigger"
          type="text"
          aria-label="打开导航菜单"
          icon={<IconMenu />}
          onClick={() => setMobileMenuOpen(true)}
        />
        <div className="layout-logo" title="供应商协作平台">
          <img src="/saa-logo.svg" alt="SAA" />
          <span>供应商协作平台</span>
        </div>
        <Dropdown
          trigger={['hover', 'click']}
          droplist={
            <Menu
              onClickMenuItem={(k) => {
                if (k === 'pwd') nav('/change-password')
                if (k === 'logout') doLogout()
              }}
            >
              <Menu.Item key="pwd">
                <IconLock style={{ marginRight: 8 }} />
                修改密码
              </Menu.Item>
              <Menu.Item key="logout">
                <IconPoweroff style={{ marginRight: 8 }} />
                退出登录
              </Menu.Item>
            </Menu>
          }
        >
          <button className="layout-profile" type="button" aria-label={`账号菜单：${user?.realName || '当前用户'}`}>
            <Avatar size={30} style={{ background: 'rgb(var(--primary-6))' }}>
              {user?.realName?.slice(0, 1)}
            </Avatar>
            <span>{user?.realName}</span>
            <IconDown />
          </button>
        </Dropdown>
      </Header>
      <Layout className="layout-main">
        <Sider
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
            title={siderCollapsed ? '展开侧边栏' : '折叠侧边栏'}
            onClick={toggleSider}
          >
            {siderCollapsed ? <IconMenuUnfold /> : <><IconMenuFold /><span>收起导航</span></>}
          </button>
        </Sider>
        <Content className="layout-content">
          <Outlet />
        </Content>
      </Layout>
      <Drawer
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
        {menu(true)}
      </Drawer>
    </Layout>
  )
}
