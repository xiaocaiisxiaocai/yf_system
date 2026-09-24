import type { SerializedDockview } from 'dockview-react'

// The default grid follows the page requirement: 1-3 subprojects share one row, 4 form a 2 x 2 grid.
// More than 4 use at most 2 rows x 3 columns; the extra subprojects become tabs in those groups
// instead of shrinking every pane below a usable size.
const MAX_COLUMNS = 3
const MAX_ROWS = 2

export const SUBPROJECT_PANE_COMPONENT = 'subproject'
export const SUBPROJECT_TAB_COMPONENT = 'subproject-tab'

export interface SubprojectPanelParams { projectId: number }
export interface DockSubproject { id: number; name: string }

export function panelIdFor(projectId: number) { return `subproject-${projectId}` }

export function projectIdOfPanel(panelId: string) {
  const value = Number(panelId.replace(/^subproject-/, ''))
  return Number.isSafeInteger(value) && value > 0 ? value : null
}

/** Rows and columns for the default layout of `count` subprojects (every row has the same column count except possibly the last). */
export function gridShape(count: number): { rows: number; columns: number } {
  if (count <= 0) return { rows: 0, columns: 0 }
  if (count <= MAX_COLUMNS) return { rows: 1, columns: count }
  if (count === 4) return { rows: 2, columns: 2 }
  return { rows: Math.min(MAX_ROWS, Math.ceil(count / MAX_COLUMNS)), columns: MAX_COLUMNS }
}

/** Splits the subprojects into rows of groups; each group lists the panels it holds as tabs. */
export function groupRows<T>(items: readonly T[]): T[][][] {
  const { rows, columns } = gridShape(items.length)
  const slots = Math.min(items.length, rows * columns)
  const groups: T[][] = Array.from({ length: slots }, () => [])
  items.forEach((item, index) => groups[index % slots].push(item))
  const result: T[][][] = []
  for (let row = 0; row < rows; row += 1) {
    const rowGroups = groups.slice(row * columns, (row + 1) * columns)
    if (rowGroups.length) result.push(rowGroups)
  }
  return result
}

function split(total: number, parts: number) {
  const base = Math.floor(total / parts)
  return Array.from({ length: parts }, (_, index) => index === parts - 1 ? total - base * (parts - 1) : base)
}

/**
 * A complete, equally sized dockview layout for the given subprojects.
 * `singleGroup` stacks every subproject as a tab of one group, for viewports too narrow for side-by-side panes.
 */
export function buildDefaultLayout(
  projects: readonly DockSubproject[], width: number, height: number, options: { singleGroup?: boolean } = {},
): SerializedDockview {
  const safeWidth = Math.max(1, Math.round(width))
  const safeHeight = Math.max(1, Math.round(height))
  const rows = options.singleGroup && projects.length ? [[[...projects]]] : groupRows(projects)
  const panels: SerializedDockview['panels'] = {}
  projects.forEach((project) => {
    const id = panelIdFor(project.id)
    panels[id] = {
      id,
      contentComponent: SUBPROJECT_PANE_COMPONENT,
      tabComponent: SUBPROJECT_TAB_COMPONENT,
      title: project.name,
      params: { projectId: project.id } satisfies SubprojectPanelParams,
    }
  })
  const leaf = (group: DockSubproject[], size: number) => ({
    type: 'leaf' as const,
    size,
    data: { id: `group-${group[0].id}`, views: group.map((item) => panelIdFor(item.id)), activeView: panelIdFor(group[0].id) },
  })
  const rowHeights = split(safeHeight, Math.max(1, rows.length))
  const root = rows.length === 1
    ? { type: 'branch' as const, size: safeHeight, data: split(safeWidth, rows[0].length).map((size, index) => leaf(rows[0][index], size)) }
    : {
      type: 'branch' as const,
      size: safeWidth,
      data: rows.map((row, rowIndex) => ({
        type: 'branch' as const,
        size: rowHeights[rowIndex],
        data: split(safeWidth, row.length).map((size, index) => leaf(row[index], size)),
      })),
    }
  return {
    // A single row lays its groups left to right; several rows stack top to bottom and split each row horizontally.
    grid: { root, width: safeWidth, height: safeHeight, orientation: (rows.length === 1 ? 'HORIZONTAL' : 'VERTICAL') as SerializedDockview['grid']['orientation'] },
    panels,
    ...(rows.length ? { activeGroup: `group-${rows[0][0][0].id}` } : {}),
  }
}

const STORAGE_PREFIX = 'yf-subproject-dock:v1'

export function layoutStorageKey(userId: number | undefined, groupId: number) {
  return `${STORAGE_PREFIX}:${userId ?? 0}:${groupId}`
}

function sameIds(a: readonly string[], b: readonly string[]) {
  if (a.length !== b.length) return false
  const set = new Set(a)
  return b.every((item) => set.has(item))
}

/**
 * A layout the viewer arranged earlier, only when it holds exactly the current subprojects.
 * Storage may be unavailable (private mode, blocked site data); the default layout is used then.
 */
export function loadSavedLayout(key: string, projects: readonly DockSubproject[]): SerializedDockview | null {
  try {
    const raw = globalThis.localStorage?.getItem(key)
    if (!raw) return null
    const parsed = JSON.parse(raw) as SerializedDockview
    if (!parsed?.grid?.root || !parsed.panels || typeof parsed.panels !== 'object') return null
    if (!sameIds(Object.keys(parsed.panels), projects.map((project) => panelIdFor(project.id)))) return null
    // Titles come from the server, never from the stored copy.
    projects.forEach((project) => {
      const panel = parsed.panels[panelIdFor(project.id)]
      panel.title = project.name
      panel.contentComponent = SUBPROJECT_PANE_COMPONENT
      panel.tabComponent = SUBPROJECT_TAB_COMPONENT
      panel.params = { projectId: project.id } satisfies SubprojectPanelParams
    })
    // Popout windows are disabled on this page; a stale entry would reference panels that no longer exist.
    if (parsed.popoutGroups?.length) return null
    return parsed
  } catch {
    return null
  }
}

export function saveLayout(key: string, layout: SerializedDockview) {
  try { globalThis.localStorage?.setItem(key, JSON.stringify(layout)) } catch { /* 存储不可用时仅本次会话保留布局。 */ }
}

export function clearSavedLayout(key: string) {
  try { globalThis.localStorage?.removeItem(key) } catch { /* 存储不可用时无需清理。 */ }
}
