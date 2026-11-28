export interface ContactColumnShape {
  id: number
  colKey: string
  title: string
  dataType: string
  personDisplay: string
  phoneTitle?: string | null
  autoMerge: boolean
  vendorVisible?: boolean
  isGroupColumn: boolean
  sortNo: number
}

export interface ContactCellPersonShape {
  personId: number
  prefixLabel?: string | null
  name: string
  phone: string
  email?: string | null
  status?: string | null
}

export interface ContactRowShape {
  id: number
  sortNo: number
  visibility?: string
  version?: number
  vendorIds?: number[]
  values: Record<string, string>
  people: Record<string, ContactCellPersonShape[]>
}

export interface VisualContactColumn<C extends ContactColumnShape = ContactColumnShape> {
  visualId: string
  col: C
  kind: 'TEXT' | 'MULTILINE' | 'PERSON' | 'PERSON_NAME' | 'PERSON_PHONE'
  title: string
  autoMerge: boolean
  isGroupColumn: boolean
}

export interface MergedContactCell<R extends ContactRowShape = ContactRowShape, C extends ContactColumnShape = ContactColumnShape> {
  visualCol: VisualContactColumn<C>
  row: R
  rowIds: number[]
  rowSpan: number
  skip: boolean
}

export interface MergedContactGrid<R extends ContactRowShape = ContactRowShape, C extends ContactColumnShape = ContactColumnShape> {
  visualCols: VisualContactColumn<C>[]
  rows: R[]
  grid: MergedContactCell<R, C>[][]
  groupColumn: C | null
}

export function getGroupColumn<C extends ContactColumnShape>(columns: readonly C[]): C | null {
  return columns.find((col) => col.isGroupColumn) ?? null
}

export function getDistinctGroups<R extends ContactRowShape, C extends ContactColumnShape>(
  rows: readonly R[],
  columns: readonly C[],
): string[] {
  const groupCol = getGroupColumn(columns)
  if (!groupCol) return []
  const seen = new Set<string>()
  const result: string[] = []
  for (const row of rows) {
    const value = (row.values[groupCol.colKey] ?? '').trim()
    if (value && !seen.has(value)) {
      seen.add(value)
      result.push(value)
    }
  }
  return result
}

export function expandVisualColumns<C extends ContactColumnShape>(columns: readonly C[]): VisualContactColumn<C>[] {
  const sorted = [...columns].sort((a, b) => (a.sortNo - b.sortNo) || (a.id - b.id))
  const result: VisualContactColumn<C>[] = []
  for (const col of sorted) {
    if (col.dataType === 'PERSON' && col.personDisplay === 'SPLIT') {
      const phoneTitle = col.phoneTitle?.trim() || `${col.title}电话`
      result.push({
        visualId: `${col.colKey}__name`,
        col,
        kind: 'PERSON_NAME',
        title: col.title,
        autoMerge: col.autoMerge,
        isGroupColumn: col.isGroupColumn,
      })
      result.push({
        visualId: `${col.colKey}__phone`,
        col,
        kind: 'PERSON_PHONE',
        title: phoneTitle,
        autoMerge: col.autoMerge,
        isGroupColumn: col.isGroupColumn,
      })
    } else {
      result.push({
        visualId: col.colKey,
        col,
        kind: (col.dataType === 'MULTILINE' || col.dataType === 'PERSON') ? col.dataType : 'TEXT',
        title: col.title,
        autoMerge: col.autoMerge,
        isGroupColumn: col.isGroupColumn,
      })
    }
  }
  return result
}

function cellMergeKey<R extends ContactRowShape, C extends ContactColumnShape>(
  row: R,
  vc: VisualContactColumn<C>,
): string | null {
  if (!vc.autoMerge) return null
  const col = vc.col
  if (col.dataType === 'TEXT' || col.dataType === 'MULTILINE') {
    const value = (row.values[col.colKey] ?? '').trim()
    return value ? `T:${value}` : null
  }
  if (col.dataType === 'PERSON') {
    const people = row.people[col.colKey] ?? []
    if (people.length === 0) return null
    return 'P:' + people.map((p) => `${p.personId}:${(p.prefixLabel ?? '').trim()}`).join('|')
  }
  return null
}

export function buildMergedContactGrid<R extends ContactRowShape, C extends ContactColumnShape>(
  columns: readonly C[],
  rows: readonly R[],
  options?: { groupFilter?: string; keyword?: string; vendorNamesById?: ReadonlyMap<number, string> },
): MergedContactGrid<R, C> {
  const visualCols = expandVisualColumns(columns)
  const groupCol = getGroupColumn(columns)
  const groupFilter = options?.groupFilter?.trim() || ''
  const keyword = options?.keyword?.trim().toLowerCase() || ''

  let filtered = [...rows]
  if (groupCol && groupFilter && groupFilter !== 'ALL') {
    filtered = filtered.filter((r) => (r.values[groupCol.colKey] ?? '').trim() === groupFilter)
  }
  if (keyword) {
    filtered = filtered.filter((r) => {
      if (Object.values(r.values).some((v) => v.toLowerCase().includes(keyword))) return true
      for (const list of Object.values(r.people)) {
        if (list.some((p) =>
          p.name.toLowerCase().includes(keyword)
          || p.phone.toLowerCase().includes(keyword)
          || (p.prefixLabel ?? '').toLowerCase().includes(keyword))) {
          return true
        }
      }
      if (options?.vendorNamesById && r.vendorIds) {
        if (r.vendorIds.some((id) => (options.vendorNamesById?.get(id) ?? '').toLowerCase().includes(keyword))) {
          return true
        }
      }
      return false
    })
  }

  const rowCount = filtered.length
  const colCount = visualCols.length
  const grid: MergedContactCell<R, C>[][] = []
  const mergeKeys: (string | null)[][] = []
  const groupKeys: string[] = []

  for (let r = 0; r < rowCount; r += 1) {
    const row = filtered[r]
    groupKeys.push(groupCol ? (row.values[groupCol.colKey] ?? '').trim() : '')
    const rowCells: MergedContactCell<R, C>[] = []
    const rowKeys: (string | null)[] = []
    for (let c = 0; c < colCount; c += 1) {
      const vc = visualCols[c]
      rowCells.push({ visualCol: vc, row, rowIds: [row.id], rowSpan: 1, skip: false })
      rowKeys.push(cellMergeKey(row, vc))
    }
    grid.push(rowCells)
    mergeKeys.push(rowKeys)
  }

  for (let c = 0; c < colCount; c += 1) {
    const vc = visualCols[c]
    if (!vc.autoMerge) continue
    let r = 0
    while (r < rowCount) {
      const key = mergeKeys[r][c]
      if (key === null) {
        r += 1
        continue
      }
      let span = 1
      const ids = [filtered[r].id]
      while (
        r + span < rowCount
        && mergeKeys[r + span][c] === key
        && (vc.isGroupColumn || groupKeys[r + span] === groupKeys[r])
      ) {
        grid[r + span][c].skip = true
        ids.push(filtered[r + span].id)
        span += 1
      }
      grid[r][c].rowSpan = span
      grid[r][c].rowIds = ids
      r += span
    }
  }

  return { visualCols, rows: filtered, grid, groupColumn: groupCol }
}
