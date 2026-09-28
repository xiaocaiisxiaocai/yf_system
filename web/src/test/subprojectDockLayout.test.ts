import { beforeEach, describe, expect, it, vi } from 'vitest'
import {
  LAYOUT_INDEX_KEY, MAX_SAVED_LAYOUTS, buildDefaultLayout, clearSavedLayout, gridShape, groupRows, layoutStorageKey,
  loadSavedLayout, panelIdFor, projectIdOfPanel, saveLayout,
} from '../pages/project/subprojectDockLayout'

const projects = (count: number) => Array.from({ length: count }, (_, index) => ({ id: index + 1, name: `子项目 ${index + 1}` }))

describe('subproject dock default layout', () => {
  beforeEach(() => localStorage.clear())

  it('places 1-3 subprojects in one row, 4 in a 2x2 grid and caps larger sets at 2x3', () => {
    expect(gridShape(0)).toEqual({ rows: 0, columns: 0 })
    expect(gridShape(1)).toEqual({ rows: 1, columns: 1 })
    expect(gridShape(3)).toEqual({ rows: 1, columns: 3 })
    expect(gridShape(4)).toEqual({ rows: 2, columns: 2 })
    expect(gridShape(5)).toEqual({ rows: 2, columns: 3 })
    expect(gridShape(6)).toEqual({ rows: 2, columns: 3 })
    expect(gridShape(9)).toEqual({ rows: 2, columns: 3 })
  })

  it('groups extra subprojects as tabs instead of adding more rows', () => {
    const ids = (rows: { id: number }[][][]) => rows.map((row) => row.map((group) => group.map((item) => item.id)))
    expect(ids(groupRows(projects(3)))).toEqual([[[1], [2], [3]]])
    expect(ids(groupRows(projects(4)))).toEqual([[[1], [2]], [[3], [4]]])
    expect(ids(groupRows(projects(5)))).toEqual([[[1], [2], [3]], [[4], [5]]])
    expect(ids(groupRows(projects(8)))).toEqual([[[1, 7], [2, 8], [3]], [[4], [5], [6]]])
  })

  it('builds equally sized rows and columns that fill the dock', () => {
    const single = buildDefaultLayout(projects(3), 1200, 600)
    expect(single.grid.orientation).toBe('HORIZONTAL')
    const singleLeaves = (single.grid.root as { data: { size: number; data: { views: string[] } }[] }).data
    expect(singleLeaves.map((leaf) => leaf.size)).toEqual([400, 400, 400])
    expect(singleLeaves.map((leaf) => leaf.data.views)).toEqual([['subproject-1'], ['subproject-2'], ['subproject-3']])
    expect(Object.keys(single.panels)).toHaveLength(3)
    expect(single.activeGroup).toBe('group-1')

    const grid = buildDefaultLayout(projects(4), 1001, 641)
    expect(grid.grid.orientation).toBe('VERTICAL')
    const rows = (grid.grid.root as { data: { size: number; data: { size: number }[] }[] }).data
    expect(rows.map((row) => row.size)).toEqual([320, 321])
    expect(rows.map((row) => row.data.map((leaf) => leaf.size))).toEqual([[500, 501], [500, 501]])
  })

  it('stacks every subproject into one tab group for narrow viewports', () => {
    const layout = buildDefaultLayout(projects(4), 360, 560, { singleGroup: true })
    const leaves = (layout.grid.root as { data: { data: { views: string[] } }[] }).data
    expect(leaves).toHaveLength(1)
    expect(leaves[0].data.views).toEqual(projects(4).map((project) => panelIdFor(project.id)))
  })

  it('restores a saved layout only for the same subproject set and refreshes titles from the server', () => {
    const key = layoutStorageKey(7, 3)
    expect(key).toBe('yf-subproject-dock:v1:7:3')
    saveLayout(key, buildDefaultLayout(projects(2), 800, 600))
    const renamed = [{ id: 1, name: '新名称' }, { id: 2, name: '子项目 2' }]
    expect(loadSavedLayout(key, renamed)?.panels['subproject-1'].title).toBe('新名称')
    expect(loadSavedLayout(key, projects(3))).toBeNull()
    localStorage.setItem(key, '{broken')
    expect(loadSavedLayout(key, projects(2))).toBeNull()
  })

  it('parses only valid panel ids', () => {
    expect(projectIdOfPanel(panelIdFor(42))).toBe(42)
    expect(projectIdOfPanel('subproject-abc')).toBeNull()
    expect(projectIdOfPanel('subproject-0')).toBeNull()
  })
  it('keeps only the most recently used saved layouts and adopts legacy keys as the oldest', () => {
    const legacy = layoutStorageKey(9, 999)
    localStorage.setItem(legacy, JSON.stringify(buildDefaultLayout(projects(1), 800, 600)))
    const now = vi.spyOn(Date, 'now')
    try {
      for (let group = 1; group <= MAX_SAVED_LAYOUTS + 5; group += 1) {
        now.mockReturnValue(1_000 + group)
        saveLayout(layoutStorageKey(9, group), buildDefaultLayout(projects(1), 800, 600))
      }
      // Loading a layout marks it as recently used, so it survives the next eviction.
      now.mockReturnValue(5_000)
      expect(loadSavedLayout(layoutStorageKey(9, 6), projects(1))).not.toBeNull()
      now.mockReturnValue(6_000)
      saveLayout(layoutStorageKey(9, 1000), buildDefaultLayout(projects(1), 800, 600))
    } finally {
      now.mockRestore()
    }
    const layoutKeys = Object.keys(localStorage).filter((key) => key.startsWith('yf-subproject-dock:v1:') && key !== LAYOUT_INDEX_KEY)
    expect(layoutKeys).toHaveLength(MAX_SAVED_LAYOUTS)
    expect(localStorage.getItem(legacy)).toBeNull()
    expect(localStorage.getItem(layoutStorageKey(9, 6))).not.toBeNull()
    expect(localStorage.getItem(layoutStorageKey(9, 7))).toBeNull()
    expect(localStorage.getItem(layoutStorageKey(9, 1000))).not.toBeNull()
    expect(Object.keys(JSON.parse(localStorage.getItem(LAYOUT_INDEX_KEY)!))).toHaveLength(MAX_SAVED_LAYOUTS)

    clearSavedLayout(layoutStorageKey(9, 1000))
    expect(JSON.parse(localStorage.getItem(LAYOUT_INDEX_KEY)!)).not.toHaveProperty(layoutStorageKey(9, 1000))
  })

  it('tolerates storage that throws on every access', () => {
    const getItem = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('blocked') })
    const setItem = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('blocked') })
    const removeItem = vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(() => { throw new Error('blocked') })
    try {
      const key = layoutStorageKey(1, 1)
      expect(() => saveLayout(key, buildDefaultLayout(projects(1), 800, 600))).not.toThrow()
      expect(loadSavedLayout(key, projects(1))).toBeNull()
      expect(() => clearSavedLayout(key)).not.toThrow()
    } finally {
      getItem.mockRestore()
      setItem.mockRestore()
      removeItem.mockRestore()
    }
  })
})
