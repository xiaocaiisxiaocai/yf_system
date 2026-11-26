import { useEffect } from 'react'
import { Badge, Tooltip } from '@arco-design/web-react'
import { IconList, IconRobot, IconSwap } from '@arco-design/web-react/icon'
import { useNavigate } from 'react-router-dom'
import { useShallow } from 'zustand/react/shallow'
import { useAuth } from '../store/auth'
import { refreshCollaborationSummary, useCollaboration } from '../store/collaboration'
import { businessHomes } from './navigation'
import { OUTSOURCE_PERMISSIONS } from '../outsource/outsourceNavigation'
import { SEND_QUEUE_PATH, useSendQueue, useSendQueuePolling } from '../outsource/sendQueue'

export type BusinessLine = 'collaboration' | 'oem' | 'outsource'

const COLLABORATION_BADGE_POLL_MS = 60_000

/**
 * Top-level switch between business lines. Each line keeps its own section and second-level menu;
 * Only accessible sections appear. A sole section needs no switcher on its own pages.
 */
export default function AppRail({ active, variant = 'rail', onNavigate }: {
  active: BusinessLine
  variant?: 'rail' | 'drawer'
  onNavigate?: () => void
}) {
  const grants = useAuth(useShallow((state) => ({
    generation: state.generation,
    menus: state.menus,
    permissions: state.permissions,
    token: state.token,
    user: state.user,
  })))
  const navigate = useNavigate()
  const { collaboration: collaborationPath, oem: oemPath, outsource: outsourcePath } = businessHomes(grants)
  // Outsourcing items waiting for a sender are counted on the rail from every section, not only inside outsourcing.
  const canSend = Boolean(outsourcePath) && grants.user?.userType === 'INTERNAL' && grants.permissions.includes(OUTSOURCE_PERMISSIONS.send)
  useSendQueuePolling(canSend)
  const queued = useSendQueue((state) => state.count)

  // Collaboration unread count is maintained by CollaborationNotifications inside AdminLayout;
  // when viewing another section, quietly refresh the summary so the Robot rail badge stays up to date.
  const canUseCollaboration = Boolean(
    collaborationPath
    && grants.token
    && grants.menus.includes('project:list')
    && grants.permissions.includes('project:list'),
  )
  const shouldPollCollaboration = variant === 'rail' && active !== 'collaboration' && canUseCollaboration
  useEffect(() => {
    if (!shouldPollCollaboration) return
    let controller: AbortController | null = null
    const poll = () => {
      if (document.visibilityState === 'hidden' || navigator.onLine === false) return
      controller?.abort()
      controller = new AbortController()
      void refreshCollaborationSummary(controller.signal)
    }
    poll()
    const timer = window.setInterval(poll, COLLABORATION_BADGE_POLL_MS)
    window.addEventListener('focus', poll)
    return () => {
      controller?.abort()
      window.clearInterval(timer)
      window.removeEventListener('focus', poll)
    }
  }, [shouldPollCollaboration, grants.generation, grants.user?.id])

  const collaborationUnread = useCollaboration((state) => (
    state.status === 'idle' || state.status === 'loading' ? 0 : state.unreadCount
  ))

  const lines = [
    ...(collaborationPath ? [{
      key: 'collaboration' as const, label: 'Robot仿真协作', short: '仿真协作', icon: <IconRobot />,
      path: collaborationPath, badge: canUseCollaboration ? collaborationUnread : 0,
    }] : []),
    ...(outsourcePath ? [{
      key: 'outsource' as const, label: '外协清单', short: '外协清单', icon: <IconList />,
      path: canSend && queued > 0 ? SEND_QUEUE_PATH : outsourcePath, badge: canSend ? queued : 0,
    }] : []),
    ...(oemPath ? [{ key: 'oem' as const, label: 'ODM/OEMA 文件传递', short: 'ODM/OEMA', icon: <IconSwap />, path: oemPath, badge: 0 }] : []),
  ]
  if (lines.length === 0 || (lines.length === 1 && lines[0].key === active)) return null
  const go = (line: (typeof lines)[number]) => {
    if (line.key !== active) navigate(line.path)
    onNavigate?.()
  }
  return (
    <nav className={`app-rail app-rail--${variant}`} aria-label="业务切换">
      {lines.map((line) => {
        const hasBadge = line.badge > 0
        const button = (
          <button key={line.key} type="button" aria-label={line.label} aria-current={line.key === active ? 'page' : undefined}
            className={`app-rail-item${line.key === active ? ' app-rail-item--active' : ''}`} onClick={() => go(line)}>
            <span className="app-rail-icon">
              {variant === 'rail' && hasBadge ? <Badge count={line.badge} maxCount={99}>{line.icon}</Badge> : line.icon}
            </span>
            <span className="app-rail-label">{variant === 'rail' ? line.short : line.label}</span>
            {variant === 'drawer' && hasBadge && <Badge className="app-rail-drawer-badge" count={line.badge} maxCount={99} />}
          </button>
        )
        return variant === 'rail'
          ? <Tooltip key={line.key} position="right" content={line.label}>{button}</Tooltip>
          : button
      })}
    </nav>
  )
}
