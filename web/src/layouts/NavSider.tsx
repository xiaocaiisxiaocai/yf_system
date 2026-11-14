import { useState, type ReactNode } from 'react'
import { Layout, Menu } from '@arco-design/web-react'
import { IconMenuFold, IconMenuUnfold } from '@arco-design/web-react/icon'

const { Sider } = Layout
const STORAGE_KEY = 'yf:sider-collapsed'

export interface NavItem {
  key: string
  label: string
  icon: ReactNode
  /** Starts the administration group; a thin rule separates it from the items above. */
  dividerBefore?: boolean
}

/** Second-level navigation menu shared by the desktop sider and the mobile drawer of every section. */
export function NavMenu({ items, selectedKey, onSelect, collapsed = false }: {
  items: NavItem[]; selectedKey?: string; onSelect: (key: string) => void; collapsed?: boolean
}) {
  return <Menu theme="dark" collapse={collapsed} selectedKeys={selectedKey ? [selectedKey] : []}
    onClickMenuItem={onSelect} style={{ width: '100%' }}>
    {items.flatMap((item) => [
      item.dividerBefore && <div key={`${item.key}:divider`} className="oem-menu-divider" role="separator" />,
      <Menu.Item key={item.key} aria-label={collapsed ? item.label : undefined} renderItemInTooltip={() => item.label}>
        {item.icon}{!collapsed && item.label}
      </Menu.Item>,
    ])}
  </Menu>
}

/**
 * Desktop second-level sider used by every section (collaboration, ODM/OEMA, outsourcing, vendor portal).
 * The collapsed state is one per-browser preference shared by all sections.
 */
export default function NavSider({ items, selectedKey, onSelect }: {
  items: NavItem[]; selectedKey?: string; onSelect: (key: string) => void
}) {
  const [collapsed, setCollapsed] = useState(() => {
    try { return window.localStorage.getItem(STORAGE_KEY) === 'true' } catch { return false }
  })
  const toggle = () => setCollapsed((current) => {
    const next = !current
    try { window.localStorage.setItem(STORAGE_KEY, String(next)) } catch { /* 本地存储不可用时仍保留当前会话状态 */ }
    return next
  })
  return <Sider className={`layout-sider${collapsed ? ' layout-sider--collapsed' : ''}`} width={200} collapsedWidth={64}
    collapsed={collapsed} collapsible trigger={null} theme="dark">
    <NavMenu items={items} selectedKey={selectedKey} onSelect={onSelect} collapsed={collapsed} />
    <button className="layout-sider-toggle" type="button" aria-label={collapsed ? '展开侧边栏' : '折叠侧边栏'} onClick={toggle}>
      {collapsed ? <IconMenuUnfold /> : <><IconMenuFold /><span>收起导航</span></>}
    </button>
  </Sider>
}
