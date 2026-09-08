import { useState } from 'react'
import { Layout, Menu, Dropdown, Avatar, Space, Button, Drawer } from '@arco-design/web-react'
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
  { code: 'org:dept', path: '/org/depts', label: '部门管理', icon: <IconMindMapping /> },
  { code: 'rbac:role', path: '/rbac/roles', label: '角色权限', icon: <IconSafe /> },
  { code: 'log:audit', path: '/logs', label: '操作日志', icon: <IconHistory /> },
  { code: 'system:config', path: '/system/config', label: '系统参数', icon: <IconSettings /> },
]

export default function AdminLayout() {
  const { menus, user, logout } = useAuth()
  const nav = useNavigate()
  const loc = useLocation()
  const [mobileMenuOpen, setMobileMenuOpen] = useState(false)

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

  const menu = (closeAfterNavigate = false) => (
    <Menu
      theme="dark"
      selectedKeys={selected}
      onClickMenuItem={(key) => {
        nav(key)
        if (closeAfterNavigate) setMobileMenuOpen(false)
      }}
      style={{ width: '100%' }}
    >
      {items.map((m) => (
        <Menu.Item key={m.path}>
          {m.icon}
          {m.label}
        </Menu.Item>
      ))}
    </Menu>
  )

  return (
    <Layout className="layout-shell">
      <Sider className="layout-sider" width={180} theme="dark">
        <div className="layout-logo">
          <img src="/saa-logo.png" alt="SAA" />
        </div>
        {menu()}
      </Sider>
      <Layout className="layout-main">
        <Header
          className="layout-header"
          style={{
            height: 56,
            background: '#fff',
            borderBottom: '1px solid var(--color-border-2)',
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'flex-end',
            padding: '0 20px',
          }}
        >
          <Button
            className="mobile-menu-trigger"
            type="text"
            aria-label="打开导航菜单"
            icon={<IconMenu />}
            onClick={() => setMobileMenuOpen(true)}
          />
          <Dropdown
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
            <Space style={{ cursor: 'pointer' }}>
              <Avatar size={28} style={{ background: '#165dff' }}>
                {user?.realName?.slice(0, 1)}
              </Avatar>
              <span>{user?.realName}</span>
              <IconDown />
            </Space>
          </Dropdown>
        </Header>
        <Content className="layout-content">
          <Outlet />
        </Content>
      </Layout>
      <Drawer
        className="mobile-nav-drawer"
        title={<img className="drawer-logo" src="/saa-logo.png" alt="SAA" />}
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
