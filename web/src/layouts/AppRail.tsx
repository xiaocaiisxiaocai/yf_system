import { Tooltip } from '@arco-design/web-react'
import { IconRobot, IconSwap } from '@arco-design/web-react/icon'
import { useNavigate } from 'react-router-dom'
import { useShallow } from 'zustand/react/shallow'
import { useAuth } from '../store/auth'
import { oemHome } from '../oem/oemNavigation'
import { OEM_UI_ENABLED } from '../features'

export type BusinessLine = 'collaboration' | 'oem'

/**
 * Top-level switch between the collaboration and OEM business lines. Each line keeps its own
 * section and second-level menu; the rail only appears for staff who can use both.
 */
export default function AppRail({ active, variant = 'rail', onNavigate }: {
  active: BusinessLine
  variant?: 'rail' | 'drawer'
  onNavigate?: () => void
}) {
  const { permissions, user } = useAuth(useShallow((state) => ({ permissions: state.permissions, user: state.user })))
  const navigate = useNavigate()
  const oemPath = OEM_UI_ENABLED && user?.userType === 'INTERNAL' ? oemHome(permissions) : null
  if (!oemPath) return null
  const lines = [
    { key: 'collaboration' as const, label: 'Robot仿真协作', short: '仿真协作', icon: <IconRobot />, path: '/' },
    { key: 'oem' as const, label: 'ODM/OEM 文件传递', short: 'ODM/OEM', icon: <IconSwap />, path: oemPath },
  ]
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
