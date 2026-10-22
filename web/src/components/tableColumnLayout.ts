import type { ReactNode } from 'react'

export interface ColumnLayoutSpec {
  key: string
  title: string
  defaultWidth: number
  minWidth?: number
  maxWidth?: number
  /** 始终显示，仍可调整列宽。 */
  locked?: boolean
}

export interface ColumnLayout {
  hidden: string[]
  widths: Record<string, number>
}

const EMPTY_LAYOUT: ColumnLayout = { hidden: [], widths: {} }

export function columnLayoutStorageKey(tableId: string, userId: number | undefined) {
  return `yf:table-columns:${tableId}:${userId ?? 0}`
}

export function clampColumnWidth(width: number, spec: ColumnLayoutSpec) {
  const min = spec.minWidth ?? 72
  const max = Math.max(min, spec.maxWidth ?? 480)
  if (!Number.isFinite(width)) return spec.defaultWidth
  return Math.round(Math.min(max, Math.max(min, width)))
}

export function normalizeColumnLayout(value: unknown, specs: readonly ColumnLayoutSpec[]): ColumnLayout {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return EMPTY_LAYOUT
  const raw = value as { hidden?: unknown; widths?: unknown }
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
  return { hidden, widths }
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
    if (!layout.hidden.length && !Object.keys(layout.widths).length) store.removeItem(storageKey)
    else store.setItem(storageKey, JSON.stringify({ hidden: layout.hidden, widths: layout.widths }))
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
  return { hidden: layout.hidden, widths }
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

/** 容器比各列宽度合计更宽时，指定列保持原宽，其余列平分多出的宽度。 */
export function withFlexibleColumn<T extends { key: string; width: number }>(
  columns: readonly T[],
  containerWidth: number,
  fixedKey: string,
): T[] {
  if (!Number.isFinite(containerWidth) || columns.length === 0) return [...columns]
  const sum = columns.reduce((total, column) => total + column.width, 0)
  const slack = Math.floor(containerWidth) - sum
  if (!(slack > 0)) return [...columns]
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
  return { hidden, widths: layout.widths }
}
