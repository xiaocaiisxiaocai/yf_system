import { useRef, useState, type ReactNode } from 'react'
import { Button, Drawer, Layout, Menu } from '@arco-design/web-react'
import { IconMenu } from '@arco-design/web-react/icon'
import { Outlet, useLocation, useNavigate } from 'react-router-dom'
import AppRail, { type BusinessLine } from './AppRail'
import './SectionShell.css'

const { Header, Sider, Content } = Layout

export interface MenuEntry { path: string; label: string; icon: ReactNode; visible: boolean; admin?: boolean }

/**
 * Shell for a self-contained business section (ODM/OEMA, outsourcing list, vendor portal): its own header
 * title and second-level menu. The `oem-shell` class carries the section card styling shared by these areas.
 */
export default function SectionShell({ title, subtitle, menu, account, line }: {
  title: string; subtitle?: string; menu: MenuEntry[]; account: ReactNode; line?: BusinessLine
}) {
  const navigate = useNavigate()
  const location = useLocation()
  const [mobileMenuOpen, setMobileMenuOpen] = useState(false)
  const mobileMenuTrigger = useRef<HTMLElement | null>(null)
  const items = menu.filter((item) => item.visible)
  const selected = items.filter((item) => location.pathname.startsWith(item.path)).sort((a, b) => b.path.length - a.path.length)[0]?.path
  const menuContent = (closeAfterNavigate = false) => (
    <Menu theme="dark" selectedKeys={selected ? [selected] : []} onClickMenuItem={(key) => {
      navigate(key)
      if (closeAfterNavigate) setMobileMenuOpen(false)
    }} style={{ width: '100%' }}>
      {items.flatMap((item, index) => [
        // A thin rule separates daily work from administration; no heading text.
        item.admin && index > 0 && !items[index - 1].admin && <div key="divider" className="oem-menu-divider" role="separator" />,
        <Menu.Item key={item.path}>{item.icon}{item.label}</Menu.Item>,
      ])}
    </Menu>
  )
  return (
    <Layout className="layout-shell oem-shell">
      <Header className="layout-header">
        {items.length > 0 && <Button ref={(node) => { mobileMenuTrigger.current = node as HTMLElement | null }}
          className="mobile-menu-trigger" type="text" aria-label="打开导航菜单" aria-expanded={mobileMenuOpen}
          icon={<IconMenu />} onClick={() => setMobileMenuOpen(true)} />}
        <div className="layout-logo" title={subtitle ? `${title} · ${subtitle}` : title}>
          <img src="/saa-logo.svg" alt="SAA" />
          <span>{title}</span>
          {subtitle && <span style={{ marginLeft: 12, fontSize: 13, opacity: 0.8 }}>{subtitle}</span>}
        </div>
        <div className="layout-header-account">{account}</div>
      </Header>
      <Layout className="layout-main">
        {line && <AppRail active={line} />}
        <Sider className="layout-sider" width={200} theme="dark">
          {menuContent()}
        </Sider>
        <Content className="layout-content"><Outlet /></Content>
      </Layout>
      {items.length > 0 && <Drawer className="mobile-nav-drawer" title={(
        <div className="drawer-brand"><img className="drawer-logo" src="/saa-logo.svg" alt="SAA" /><span>{title}</span></div>
      )} placement="left" width={240} visible={mobileMenuOpen} footer={null} unmountOnExit focusLock autoFocus
        onCancel={() => setMobileMenuOpen(false)} afterClose={() => mobileMenuTrigger.current?.focus({ preventScroll: true })}>
        {line && <AppRail active={line} variant="drawer" onNavigate={() => setMobileMenuOpen(false)} />}
        {menuContent(true)}
      </Drawer>}
    </Layout>
  )
}
