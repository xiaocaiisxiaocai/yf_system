import { Tooltip } from '@arco-design/web-react'
import { IconList, IconRobot, IconSwap } from '@arco-design/web-react/icon'
import { useNavigate } from 'react-router-dom'
import { useShallow } from 'zustand/react/shallow'
import { useAuth } from '../store/auth'
import { businessHomes } from './navigation'

export type BusinessLine = 'collaboration' | 'oem' | 'outsource'

/**
 * Top-level switch between business lines. Each line keeps its own section and second-level menu;
 * Only accessible sections appear. A sole section needs no switcher on its own pages.
 */
export default function AppRail({ active, variant = 'rail', onNavigate }: {
  active: BusinessLine
  variant?: 'rail' | 'drawer'
  onNavigate?: () => void
}) {
  const grants = useAuth(useShallow((state) => ({ menus: state.menus, permissions: state.permissions, user: state.user })))
  const navigate = useNavigate()
  const { collaboration: collaborationPath, oem: oemPath, outsource: outsourcePath } = businessHomes(grants)
  const lines = [
    ...(collaborationPath ? [{ key: 'collaboration' as const, label: 'Robot仿真协作', short: '仿真协作', icon: <IconRobot />, path: collaborationPath }] : []),
    ...(oemPath ? [{ key: 'oem' as const, label: 'ODM/OEMA 文件传递', short: 'ODM/OEMA', icon: <IconSwap />, path: oemPath }] : []),
    ...(outsourcePath ? [{ key: 'outsource' as const, label: '外协清单', short: '外协清单', icon: <IconList />, path: outsourcePath }] : []),
  ]
  if (lines.length === 0 || (lines.length === 1 && lines[0].key === active)) return null
  const go = (line: (typeof lines)[number]) => {
    if (line.key !== active) navigate(line.path)
    onNavigate?.()
  }
  return (
    <nav className={`app-rail app-rail--${variant}`} aria-label="业务切换">
      {lines.map((line) => {
        const button = (
          <button key={line.key} type="button" aria-label={line.label} aria-current={line.key === active ? 'page' : undefined}
            className={`app-rail-item${line.key === active ? ' app-rail-item--active' : ''}`} onClick={() => go(line)}>
            <span className="app-rail-icon">{line.icon}</span>
            <span className="app-rail-label">{variant === 'rail' ? line.short : line.label}</span>
          </button>
        )
        return variant === 'rail'
          ? <Tooltip key={line.key} position="right" content={line.label}>{button}</Tooltip>
          : button
      })}
    </nav>
  )
}
