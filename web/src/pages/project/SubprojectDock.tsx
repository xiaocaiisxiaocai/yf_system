import { forwardRef, useCallback, useContext, useEffect, useImperativeHandle, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { Badge, Button, Tag } from '@arco-design/web-react'
import { IconFullscreen, IconFullscreenExit, IconLaunch } from '@arco-design/web-react/icon'
import {
  DockviewReact, themeLight,
  type DockviewApi, type DockviewReadyEvent, type DockviewTheme, type IDockviewHeaderActionsProps, type IDockviewPanelHeaderProps,
} from 'dockview-react'
import 'dockview-react/dist/styles/dockview.css'
import { useNavigate } from 'react-router-dom'
import { PROJECT_STATUS, type ProjectSummary } from '../../api/types'
import SubprojectPane from './SubprojectPane'
import { SubprojectDockContext, type SubprojectDockContextValue } from './subprojectDockContext'
import {
  SUBPROJECT_PANE_COMPONENT, SUBPROJECT_TAB_COMPONENT, buildDefaultLayout, clearSavedLayout, layoutStorageKey, loadSavedLayout,
  panelIdFor, projectIdOfPanel, saveLayout, type DockSubproject, type SubprojectPanelParams,
} from './subprojectDockLayout'
import './SubprojectDock.css'

const COMPACT_QUERY = '(max-width: 720px)'
const SAVE_DELAY_MS = 300

const dockTheme: DockviewTheme = { ...themeLight, name: 'yf-light', className: 'dockview-theme-light yf-dockview-theme', gap: 8 }

function SubprojectTab({ params, api }: IDockviewPanelHeaderProps<SubprojectPanelParams>) {
  const { projects } = useContext(SubprojectDockContext)
  const project = projects.get(params.projectId)
  const status = project ? PROJECT_STATUS[project.status] : undefined
  const name = project?.name ?? api.title ?? ''
  return (
    <div
      className="subproject-dock-tab"
      title={name}
      onDoubleClick={() => { if (api.isMaximized()) api.exitMaximized(); else api.maximize() }}
    >
      <span className="subproject-dock-tab-name">{name}</span>
      {project && <Tag size="small" color={status?.color}>{status?.text || project.status}</Tag>}
      {!!project?.unreadMessages && <Badge count={project.unreadMessages} dot={false} />}
    </div>
  )
}

function GroupHeaderActions({ api, containerApi, activePanel }: IDockviewHeaderActionsProps) {
  const navigate = useNavigate()
  const [maximized, setMaximized] = useState(() => api.location.type === 'grid' && api.isMaximized())
  useEffect(() => {
    const disposable = containerApi.onDidMaximizedGroupChange(() => setMaximized(api.location.type === 'grid' && api.isMaximized()))
    return () => disposable.dispose()
  }, [api, containerApi])
  const projectId = activePanel ? projectIdOfPanel(activePanel.id) : null
  return (
    <div className="subproject-dock-header-actions">
      {projectId && (
        <Button size="mini" type="text" icon={<IconLaunch />} title="在独立页面打开" aria-label="在独立页面打开当前子项目" onClick={() => navigate(`/projects/${projectId}`)} />
      )}
      {api.location.type === 'grid' && containerApi.groups.length > 1 && (
        <Button
          size="mini"
          type="text"
          icon={maximized ? <IconFullscreenExit /> : <IconFullscreen />}
          title={maximized ? '还原布局' : '最大化'}
          aria-label={maximized ? '还原布局' : '最大化当前面板组'}
          onClick={() => { if (maximized) api.exitMaximized(); else api.maximize() }}
        />
      )}
    </div>
  )
}

const components = { [SUBPROJECT_PANE_COMPONENT]: SubprojectPane }
const tabComponents = { [SUBPROJECT_TAB_COMPONENT]: SubprojectTab }
// 不提供关闭项：每个子项目始终保留一个面板；弹出新窗口会脱离当前页面的登录和实时同步上下文，同样不提供。
const tabContextMenuItems = () => ['maximize' as const, 'float' as const]

export interface SubprojectDockHandle {
  resetLayout: () => void
  focusProject: (projectId: number) => boolean
}

interface Props {
  groupId: number
  userId?: number
  projects: readonly ProjectSummary[]
  context: SubprojectDockContextValue
}

function panelSetKey(projects: readonly DockSubproject[]) {
  return projects.map((project) => project.id).sort((a, b) => a - b).join(',')
}

const SubprojectDock = forwardRef<SubprojectDockHandle, Props>(function SubprojectDock({ groupId, userId, projects, context }, ref) {
  const apiRef = useRef<DockviewApi | null>(null)
  const appliedSetRef = useRef<string | null>(null)
  const storageKeyRef = useRef('')
  const projectsRef = useRef(projects)
  // 布局回调在 dockview 的事件里读取最新子项目；布局副作用先于下面的同步 effect 执行。
  useLayoutEffect(() => { projectsRef.current = projects }, [projects])
  const setKey = useMemo(() => panelSetKey(projects), [projects])

  const applyLayout = useCallback((api: DockviewApi, preferSaved: boolean) => {
    const current = projectsRef.current
    const compact = typeof window.matchMedia === 'function' && window.matchMedia(COMPACT_QUERY).matches
    storageKeyRef.current = `${layoutStorageKey(userId, groupId)}${compact ? ':compact' : ''}`
    appliedSetRef.current = panelSetKey(current)
    if (!current.length) { api.clear(); return }
    const saved = preferSaved ? loadSavedLayout(storageKeyRef.current, current) : null
    const layout = saved ?? buildDefaultLayout(current, api.width || 1200, api.height || 640, { singleGroup: compact })
    try {
      api.fromJSON(layout)
    } catch {
      // 保存的布局与当前版本不兼容时回退默认布局，不能让整个页面失效。
      clearSavedLayout(storageKeyRef.current)
      api.fromJSON(buildDefaultLayout(current, api.width || 1200, api.height || 640, { singleGroup: compact }))
    }
  }, [groupId, userId])

  const saveTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)
  const layoutSubscription = useRef<{ dispose: () => void } | null>(null)

  const onReady = useCallback((event: DockviewReadyEvent) => {
    apiRef.current = event.api
    applyLayout(event.api, true)
    // 用户拖拽、调整大小或切换标签后保存布局，同一账号再次打开同一主项目时恢复。
    layoutSubscription.current?.dispose()
    layoutSubscription.current = event.api.onDidLayoutChange(() => {
      if (saveTimer.current) clearTimeout(saveTimer.current)
      saveTimer.current = setTimeout(() => {
        if (appliedSetRef.current && event.api.panels.length) saveLayout(storageKeyRef.current, event.api.toJSON())
      }, SAVE_DELAY_MS)
    })
  }, [applyLayout])

  useEffect(() => () => {
    if (saveTimer.current) clearTimeout(saveTimer.current)
    layoutSubscription.current?.dispose()
  }, [])

  // 子项目增删后按新数量重新套用默认网格（1-3 个一行、4 个 2×2）；只改名时原地更新标题。
  useEffect(() => {
    const api = apiRef.current
    if (!api) return
    if (appliedSetRef.current !== setKey) {
      applyLayout(api, true)
      return
    }
    projects.forEach((project) => {
      const panel = api.getPanel(panelIdFor(project.id))
      if (panel && panel.title !== project.name) panel.api.setTitle(project.name)
    })
  }, [applyLayout, projects, setKey])

  useImperativeHandle(ref, () => ({
    resetLayout: () => {
      const api = apiRef.current
      if (!api) return
      clearSavedLayout(storageKeyRef.current)
      if (api.hasMaximizedGroup()) api.exitMaximizedGroup()
      applyLayout(api, false)
    },
    focusProject: (projectId: number) => {
      const panel = apiRef.current?.getPanel(panelIdFor(projectId))
      if (!panel) return false
      if (apiRef.current?.hasMaximizedGroup() && !panel.api.isMaximized()) apiRef.current.exitMaximizedGroup()
      panel.api.setActive()
      return true
    },
  }), [applyLayout])

  return (
    <SubprojectDockContext.Provider value={context}>
      <div className="subproject-dock" aria-label="子项目工作区">
        <DockviewReact
          className="subproject-dock-view"
          theme={dockTheme}
          components={components}
          tabComponents={tabComponents}
          rightHeaderActionsComponent={GroupHeaderActions}
          getTabContextMenuItems={tabContextMenuItems}
          singleTabMode="default"
          onReady={onReady}
        />
      </div>
    </SubprojectDockContext.Provider>
  )
})

export default SubprojectDock
