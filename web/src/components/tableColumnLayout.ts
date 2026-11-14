import type { ReactNode } from 'react'

export interface ColumnLayoutSpec {
  key: string
  title: string
  defaultWidth: number
  minWidth?: number
  /** 已不再限制列宽，保留字段只为兼容现有列配置。 */
  maxWidth?: number
  /** 始终显示，仍可调整列宽。 */
  locked?: boolean
}

export interface ColumnLayout {
  hidden: string[]
  widths: Record<string, number>
  /** 用户排过的列顺序（列 key）；空数组表示按页面定义的顺序。 */
  order?: string[]
  /** 用户固定在左侧的列；不存在表示按页面定义（`fixed: 'left'` 的列）。 */
  pinned?: string[]
}

const EMPTY_LAYOUT: ColumnLayout = { hidden: [], widths: {} }

const isDefaultArrangement = (layout: ColumnLayout) => !layout.order?.length && layout.pinned === undefined

export function columnLayoutStorageKey(tableId: string, userId: number | undefined) {
  return `yf:table-columns:${tableId}:${userId ?? 0}`
}

export function clampColumnWidth(width: number, spec: ColumnLayoutSpec) {
  const min = spec.minWidth ?? 72
  if (!Number.isFinite(width)) return spec.defaultWidth
  // 列宽没有上限：宽出容器时表格出现横向滚动。
  return Math.round(Math.max(min, width))
}

export function normalizeColumnLayout(value: unknown, specs: readonly ColumnLayoutSpec[]): ColumnLayout {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return EMPTY_LAYOUT
  const raw = value as { hidden?: unknown; widths?: unknown; order?: unknown; pinned?: unknown }
  const locked = new Set(specs.filter((spec) => spec.locked).map((spec) => spec.key))
  const hiddenKeys = new Set(Array.isArray(raw.hidden)
    ? raw.hidden.filter((key): key is string => typeof key === 'string' && !locked.has(key))
    : [])
  const hidden = specs.filter((spec) => !spec.locked && hiddenKeys.has(spec.key)).map((spec) => spec.key)
  const widths: Record<string, number> = {}
  if (raw.widths && typeof raw.widths === 'object' && !Array.isArray(raw.widths)) {
    for (const spec of specs) {
      const width = (raw.widths as Record<string, unknown>)[spec.key]
      if (typeof width !== 'number') continue
      const next = clampColumnWidth(width, spec)
      if (next !== spec.defaultWidth) widths[spec.key] = next
    }
  }
  const known = new Set(specs.map((spec) => spec.key))
  const keys = (list: unknown) => Array.isArray(list)
    ? [...new Set(list.filter((key): key is string => typeof key === 'string' && known.has(key)))]
    : undefined
  const order = keys(raw.order)
  const pinned = keys(raw.pinned)
  return { hidden, widths, ...(order?.length ? { order } : {}), ...(pinned ? { pinned } : {}) }
}

function storage(): Storage | undefined {
  try { return globalThis.localStorage ?? undefined } catch { return undefined }
}

export function readColumnLayout(storageKey: string, specs: readonly ColumnLayoutSpec[]): ColumnLayout {
  try {
    const raw = storage()?.getItem(storageKey)
    if (!raw) return EMPTY_LAYOUT
    return normalizeColumnLayout(JSON.parse(raw) as unknown, specs)
  } catch {
    return EMPTY_LAYOUT
  }
}

export function writeColumnLayout(storageKey: string, layout: ColumnLayout) {
  const store = storage()
  if (!store) return
  try {
    if (!layout.hidden.length && !Object.keys(layout.widths).length && isDefaultArrangement(layout)) store.removeItem(storageKey)
    else store.setItem(storageKey, JSON.stringify({
      hidden: layout.hidden, widths: layout.widths,
      ...(layout.order?.length ? { order: layout.order } : {}),
      ...(layout.pinned !== undefined ? { pinned: layout.pinned } : {}),
    }))
  } catch { /* 隐私模式或配额不足时只保留当前页的调整。 */ }
}

export function withColumnWidth(layout: ColumnLayout, specs: readonly ColumnLayoutSpec[], key: string, width: number | null) {
  const spec = specs.find((item) => item.key === key)
  if (!spec) return layout
  const widths = { ...layout.widths }
  if (width === null || clampColumnWidth(width, spec) === spec.defaultWidth) {
    if (!(key in widths)) return layout
    delete widths[key]
  } else {
    const next = clampColumnWidth(width, spec)
    if (widths[key] === next) return layout
    widths[key] = next
  }
  return { ...layout, widths }
}

/** 表格外框的可用宽度。表体在列总宽较小时会跟着列缩回去，不能拿它当容器宽度。 */
export function measureTableWidth(host: HTMLElement | null) {
  if (!host) return 0
  const container = host.querySelector<HTMLElement>('.arco-table-container')
  const available = container?.clientWidth || host.clientWidth
  const body = host.querySelector<HTMLElement>('.arco-table-body')
  const scrollbar = body ? Math.max(0, body.offsetWidth - body.clientWidth) : 0
  return Math.max(0, available - scrollbar)
}

/**
 * 容器比各列宽度合计更宽时，指定列保持原宽，其余列平分多出的宽度。
 * `growFixed` 时反过来：多出的宽度全部给指定列（如文件名），其余列保持设定宽度。
 */
export function withFlexibleColumn<T extends { key: string; width: number }>(
  columns: readonly T[],
  containerWidth: number,
  fixedKey: string,
  growFixed = false,
): T[] {
  if (!Number.isFinite(containerWidth) || columns.length === 0) return [...columns]
  const sum = columns.reduce((total, column) => total + column.width, 0)
  const slack = Math.floor(containerWidth) - sum
  if (!(slack > 0)) return [...columns]
  if (growFixed && columns.some((column) => column.key === fixedKey)) {
    return columns.map((column) => column.key === fixedKey ? { ...column, width: column.width + slack } : column)
  }
  const flexible = columns.some((column) => column.key === fixedKey)
    ? columns.filter((column) => column.key !== fixedKey)
    : [...columns]
  if (flexible.length === 0) return [...columns]
  const share = Math.floor(slack / flexible.length)
  let remainder = slack - share * flexible.length
  const extras = new Map(flexible.map((column) => {
    const extra = share + (remainder > 0 ? 1 : 0)
    if (remainder > 0) remainder -= 1
    return [column.key, extra] as const
  }))
  return columns.map((column) => {
    const extra = extras.get(column.key) ?? 0
    return extra > 0 ? { ...column, width: column.width + extra } : column
  })
}

/**
 * 手动调过列宽后不再重新分配各列，但容器仍比合计宽时，多出的宽度给最后一个不固定的列
 * （固定在右侧的操作列保持原宽；全是固定列时给最后一列），表格右侧不留空白。
 * 正在拖的列（`resizingKey`）不接收多出的宽度，否则拖窄它会被立刻补回去：改给它右边最近的不固定列，没有就给左边最近的。
 */
export function withTrailingFill<T extends { key: string; width: number; fixed?: unknown }>(
  columns: readonly T[],
  containerWidth: number,
  resizingKey?: string,
): T[] {
  if (!Number.isFinite(containerWidth) || columns.length === 0) return [...columns]
  const slack = Math.floor(containerWidth) - columns.reduce((total, column) => total + column.width, 0)
  if (!(slack > 0)) return [...columns]
  const unfixed = columns.findLastIndex((column) => !column.fixed)
  let target = unfixed >= 0 ? unfixed : columns.length - 1
  const resizing = resizingKey === undefined ? -1 : columns.findIndex((column) => column.key === resizingKey)
  if (resizing === target) {
    const right = columns.findIndex((column, index) => index > resizing && !column.fixed)
    const left = columns.findLastIndex((column, index) => index < resizing && !column.fixed)
    const other = right >= 0 ? right : left
    if (other >= 0) target = other
  }
  return columns.map((column, index) => index === target ? { ...column, width: column.width + slack } : column)
}

/** 正式环境里未知列没有有效宽度时使用，避免固定表格把该列压成不可见。 */
export const UNKNOWN_COLUMN_FALLBACK_WIDTH = 160

function finiteWidth(column: object) {
  if ('width' in column && typeof column.width === 'number' && Number.isFinite(column.width)) return column.width
  return undefined
}

/**
 * 开发环境对不上列配置时抛错。正式环境记下同一句说明后降级：
 * 未知列保留原字段，没有有效宽度时改用 UNKNOWN_COLUMN_FALLBACK_WIDTH；标题不一致时改用规格标题，隐藏规则不变。
 */
export function applyColumnLayout<T extends { key: string; title?: ReactNode }>(
  columns: readonly T[],
  specs: readonly ColumnLayoutSpec[],
  layout: ColumnLayout,
  fatal: boolean,
): Array<T & { width: number }> {
  return columns.flatMap((column): Array<T & { width: number }> => {
    const spec = specs.find((item) => item.key === column.key)
    if (!spec) {
      const message = `缺少列配置：${column.key}`
      if (fatal) throw new Error(message)
      console.error(message)
      return [Object.assign({}, column, { width: finiteWidth(column) ?? UNKNOWN_COLUMN_FALLBACK_WIDTH })]
    }
    if (typeof column.title === 'string' && column.title !== spec.title) {
      const message = `列标题不一致：${column.key}`
      if (fatal) throw new Error(message)
      console.error(message)
    }
    if (!spec.locked && layout.hidden.includes(column.key)) return []
    return [Object.assign({}, column, {
      title: spec.title,
      width: layout.widths[column.key] ?? spec.defaultWidth,
    })]
  })
}

export function withHiddenColumns(layout: ColumnLayout, specs: readonly ColumnLayoutSpec[], hiddenKeys: ReadonlySet<string>) {
  const hidden = specs.filter((spec) => !spec.locked && hiddenKeys.has(spec.key)).map((spec) => spec.key)
  if (hidden.length === layout.hidden.length && hidden.every((key, index) => key === layout.hidden[index])) return layout
  return { ...layout, hidden }
}

type Arrangeable = { key: string; fixed?: unknown }

/**
 * 按用户的顺序和固定设置排列列：固定在左侧的列排在最前（彼此保持顺序），固定在右侧的列（如操作）始终在最后、不参与排序。
 * 页面没给顺序的列（新增的列）按页面定义的位置排在已排序的列之后。
 */
export function arrangeColumns<T extends Arrangeable>(columns: readonly T[], layout: ColumnLayout): T[] {
  const right = columns.filter((column) => column.fixed === 'right')
  const movable = columns.filter((column) => column.fixed !== 'right')
  const order = layout.order ?? []
  const rank = (column: T, index: number) => {
    const position = order.indexOf(column.key)
    return position >= 0 ? position : order.length + index
  }
  const sorted = movable.map((column, index) => ({ column, rank: rank(column, index) }))
    .sort((a, b) => a.rank - b.rank).map(({ column }) => column)
  const pinned = new Set(layout.pinned ?? movable.filter((column) => column.fixed === 'left').map((column) => column.key))
  const withFix = sorted.map((column) => {
    const fixed = pinned.has(column.key) ? 'left' : undefined
    return column.fixed === fixed ? column : Object.assign({}, column, { fixed })
  })
  return [...withFix.filter((column) => column.fixed === 'left'), ...withFix.filter((column) => column.fixed !== 'left'), ...right]
}

/** 把 `key` 移到 `target` 的位置（`arranged` 是当前显示顺序的全部可移动列）。 */
export function withMovedColumn(layout: ColumnLayout, arranged: readonly string[], key: string, target: string) {
  const from = arranged.indexOf(key)
  const to = arranged.indexOf(target)
  if (from < 0 || to < 0 || from === to) return layout
  const order = [...arranged]
  order.splice(from, 1)
  order.splice(to, 0, key)
  return { ...layout, order }
}

/** 固定或取消固定某列；`current` 是当前固定在左侧的列。 */
export function withPinnedColumn(layout: ColumnLayout, current: readonly string[], key: string, pinned: boolean) {
  const next = pinned ? [...new Set([...current, key])] : current.filter((item) => item !== key)
  return { ...layout, pinned: next }
}
