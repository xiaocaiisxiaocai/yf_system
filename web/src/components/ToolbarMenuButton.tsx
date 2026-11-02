import { Button, Dropdown, Menu } from '@arco-design/web-react'
import { IconDown } from '@arco-design/web-react/icon'

export interface ToolbarMenuItem {
  key: string
  label: string
  onClick: () => void
  loading?: boolean
}

// 工具栏中同类的次要操作（如导入、导出）收进一个下拉按钮，菜单项保留完整名称。
export default function ToolbarMenuButton({ label, items }: { label: string; items: (ToolbarMenuItem | false | null | undefined)[] }) {
  const visible = items.filter((item): item is ToolbarMenuItem => Boolean(item))
  if (visible.length === 0) return null
  return (
    <Dropdown
      trigger="click"
      position="br"
      droplist={(
        <Menu onClickMenuItem={(key) => visible.find((item) => item.key === key)?.onClick()}>
          {visible.map((item) => <Menu.Item key={item.key} disabled={item.loading}>{item.label}</Menu.Item>)}
        </Menu>
      )}
    >
      <Button size="small" loading={visible.some((item) => item.loading)}>{label}<IconDown /></Button>
    </Dropdown>
  )
}
