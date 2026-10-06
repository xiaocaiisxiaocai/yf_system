import { forwardRef, useCallback, type PointerEvent as ReactPointerEvent, useContext, useEffect, useImperativeHandle, useLayoutEffect, useMemo, useRef, useState } from 'react'
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
  SUBPROJECT_PANE_COMPONENT, SUBPROJECT_TAB_COMPONENT, buildDefaultLayout, clearSavedLayout, layoutKey, layoutStorageKey, loadSavedLayout,
  panelIdFor, projectIdOfPanel, saveLayout, type DockSubproject, type SubprojectPanelParams,
} from './subprojectDockLayout'
import './SubprojectDock.css'

const COMPACT_QUERY = '(max-width: 720px)'
const SAVE_DELAY_MS = 300
/** 拖动这些元素（分隔条、浮动面板的移动/缩放区域）是用户调整尺寸或位置。 */
const RESIZE_HANDLE_SELECTOR = '.dv-sash, .dv-resize-container'
const MAX_GRID_COLUMNS = 3

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
      {!!project?.unreadMessages && (
        <span role="img" aria-label={`${project.unreadMessages} 条未读`}>
          <Badge count={project.unreadMessages} dot={false} />
        </span>
      )}
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

/** 新增子项目面板的位置：不足三列时在最右侧新开一组，否则作为标签加入面板最少的一组；窄屏单组布局始终加入该组。 */
function addPosition(api: DockviewApi, compact: boolean) {
  const grid = api.groups.filter((group) => group.api.location.type === 'grid')
  if (!grid.length) return undefined
  if (!compact && grid.length < MAX_GRID_COLUMNS) return { referenceGroup: grid[grid.length - 1], direction: 'right' as const }
  const target = grid.reduce((least, group) => (group.panels.length < least.panels.length ? group : least))
  return { referenceGroup: target, direction: 'within' as const }
}

const SubprojectDock = forwardRef<SubprojectDockHandle, Props>(function SubprojectDock({ groupId, userId, projects, context }, ref) {
  const apiRef = useRef<DockviewApi | null>(null)
  const appliedSetRef = useRef<string | null>(null)
  const storageKeyRef = useRef('')
  const compactRef = useRef(false)
  /** 自己调用 fromJSON/addPanel/removePanel 引起的布局事件不当作用户调整。 */
  const programmaticRef = useRef(false)
  /** 只有恢复过保存的布局或用户动过布局后才写入存储；默认布局不落盘。 */
  const customizedRef = useRef(false)
  /** 最近一次程序化套用后的布局结构（不含活动标签与尺寸）；只切换标签或容器尺寸变化不算用户调整。 */
  const baselineRef = useRef<string | null>(null)
  const projectsRef = useRef(projects)
  // 布局回调在 dockview 的事件里读取最新子项目；布局副作用先于下面的同步 effect 执行。
  useLayoutEffect(() => { projectsRef.current = projects }, [projects])
  const setKey = useMemo(() => panelSetKey(projects), [projects])

  const saveTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)
  const pendingSave = useRef<(() => void) | undefined>(undefined)
  const layoutSubscription = useRef<{ dispose: () => void } | null>(null)

  const cancelPendingSave = () => {
    if (saveTimer.current) clearTimeout(saveTimer.current)
    saveTimer.current = undefined
    pendingSave.current = undefined
  }

  /** 卸载或离开页面时立即写入尚未落盘的调整，而不是丢弃。 */
  const flushPendingSave = () => {
    const save = pendingSave.current
    cancelPendingSave()
    try { save?.() } catch { /* dockview 已销毁时放弃本次保存。 */ }
  }

  const scheduleSave = useCallback((api: DockviewApi) => {
    cancelPendingSave()
    const save = () => {
      if (customizedRef.current && appliedSetRef.current && api.panels.length) saveLayout(storageKeyRef.current, api.toJSON())
    }
    pendingSave.current = save
    saveTimer.current = setTimeout(() => {
      saveTimer.current = undefined
      pendingSave.current = undefined
      save()
    }, SAVE_DELAY_MS)
  }, [])

  const markCustomized = useCallback((api: DockviewApi) => {
    customizedRef.current = true
    scheduleSave(api)
  }, [scheduleSave])

  /** dockview 在微任务里合并派发布局事件；标记在其之后清除，期间的事件都来自本组件的程序化调整。 */
  const programmatic = (api: DockviewApi, mutate: () => void) => {
    programmaticRef.current = true
    try {
      mutate()
    } finally {
      baselineRef.current = layoutKey(api.toJSON(), { ignoreSizes: true })
      queueMicrotask(() => { programmaticRef.current = false })
    }
  }

  /** 整体套用布局：仅在首次挂载（或首次有子项目）与用户点击“重置布局”时使用。 */
  const applyLayout = useCallback((api: DockviewApi, preferSaved: boolean) => {
    const current = projectsRef.current
    // 窄屏（≤720px）只在挂载和重置时判断：之后窗口跨过断点不重排、不重建面板，避免子项目面板被卸载重载。
    const compact = typeof window.matchMedia === 'function' && window.matchMedia(COMPACT_QUERY).matches
    compactRef.current = compact
    storageKeyRef.current = `${layoutStorageKey(userId, groupId)}${compact ? ':compact' : ''}`
    appliedSetRef.current = panelSetKey(current)
    cancelPendingSave()
    if (!current.length) { programmatic(api, () => api.clear()); return }
    const saved = preferSaved ? loadSavedLayout(storageKeyRef.current, current) : null
    customizedRef.current = saved !== null
    const layout = saved ?? buildDefaultLayout(current, api.width || 1200, api.height || 640, { singleGroup: compact })
    programmatic(api, () => {
      try {
        api.fromJSON(layout)
      } catch {
        // 保存的布局与当前版本不兼容时回退默认布局，不能让整个页面失效。
        clearSavedLayout(storageKeyRef.current)
        customizedRef.current = false
        api.fromJSON(buildDefaultLayout(current, api.width || 1200, api.height || 640, { singleGroup: compact }))
      }
    })
  }, [groupId, userId])

  /** 子项目增删时原地增删面板，其余面板保持挂载（不重新 fromJSON，面板里的列表、滚动与播放不受影响）。 */
  const syncPanels = useCallback((api: DockviewApi) => {
    const current = projectsRef.current
    if (appliedSetRef.current === null || api.panels.length === 0 || current.length === 0) {
      applyLayout(api, true)
      return
    }
    const wanted = new Set(current.map((project) => panelIdFor(project.id)))
    programmatic(api, () => {
      api.panels
        .filter((panel) => projectIdOfPanel(panel.id) !== null && !wanted.has(panel.id))
        .forEach((panel) => api.removePanel(panel))
      current.forEach((project) => {
        const id = panelIdFor(project.id)
        if (api.getPanel(id)) return
        const position = addPosition(api, compactRef.current)
        api.addPanel<SubprojectPanelParams>({
          id,
          component: SUBPROJECT_PANE_COMPONENT,
          tabComponent: SUBPROJECT_TAB_COMPONENT,
          title: project.name,
          params: { projectId: project.id },
          inactive: true,
          ...(position ? { position } : {}),
        })
      })
    })
    appliedSetRef.current = panelSetKey(current)
    // 用户保存过的布局随面板集合一起更新，否则下次打开时集合不符会退回默认布局。
    if (customizedRef.current) scheduleSave(api)
  }, [applyLayout, scheduleSave])

  const onReady = useCallback((event: DockviewReadyEvent) => {
    apiRef.current = event.api
    applyLayout(event.api, true)
    // 用户拖拽、调整大小或切换标签后保存布局，同一账号再次打开同一主项目时恢复。
    layoutSubscription.current?.dispose()
    // dockview 切换活动标签、改标题、容器尺寸变化时也会派发 onDidLayoutChange：只有结构变化（拖动停靠、
    // 浮动、最大化）才算用户调整；分隔条与浮动面板的拖动由下面的指针事件判断。
    layoutSubscription.current = event.api.onDidLayoutChange(() => {
      const structure = layoutKey(event.api.toJSON(), { ignoreSizes: true })
      if (programmaticRef.current) { baselineRef.current = structure; return }
      if (structure !== baselineRef.current) {
        baselineRef.current = structure
        markCustomized(event.api)
      } else if (customizedRef.current) {
        scheduleSave(event.api)
      }
    })
  }, [applyLayout, markCustomized, scheduleSave])

  /** 拖动分隔条或浮动面板：松开时尺寸/位置确有变化才算用户调整。 */
  const onPointerDownCapture = useCallback((event: ReactPointerEvent<HTMLDivElement>) => {
    const api = apiRef.current
    if (!api || !(event.target instanceof Element) || !event.target.closest(RESIZE_HANDLE_SELECTOR)) return
    const before = layoutKey(api.toJSON())
    const finish = () => {
      window.removeEventListener('pointerup', finish)
      window.removeEventListener('pointercancel', finish)
      if (apiRef.current === api && layoutKey(api.toJSON()) !== before) markCustomized(api)
    }
    window.addEventListener('pointerup', finish)
    window.addEventListener('pointercancel', finish)
  }, [markCustomized])

  useEffect(() => () => {
    flushPendingSave()
    layoutSubscription.current?.dispose()
  }, [])

  // 子项目增删后增量增删面板；只改名时原地更新标题。
  useEffect(() => {
    const api = apiRef.current
    if (!api) return
    if (appliedSetRef.current !== setKey) {
      syncPanels(api)
      return
    }
    projects.forEach((project) => {
      const panel = api.getPanel(panelIdFor(project.id))
      if (panel && panel.title !== project.name) panel.api.setTitle(project.name)
    })
  }, [projects, setKey, syncPanels])

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
      <div className="subproject-dock" role="region" aria-label="子项目工作区" onPointerDownCapture={onPointerDownCapture}>
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
