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

/** 容器比各列寬度合計更寬時，指定列保持原寬，其餘列平分多出的寬度。 */
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

export function withHiddenColumns(layout: ColumnLayout, specs: readonly ColumnLayoutSpec[], hiddenKeys: ReadonlySet<string>) {
  const hidden = specs.filter((spec) => !spec.locked && hiddenKeys.has(spec.key)).map((spec) => spec.key)
  if (hidden.length === layout.hidden.length && hidden.every((key, index) => key === layout.hidden[index])) return layout
  return { hidden, widths: layout.widths }
}
