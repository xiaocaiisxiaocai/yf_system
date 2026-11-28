import type { OrderRow } from './announcementOrderApi'

export const MAX_ORDER = 2_147_483_647

// The API timestamps are ISO UTC. Keep the fractional seconds, since they can exceed JS Date precision.
function compareRows(a: OrderRow, b: OrderRow): number {
  if (a.sortOrder !== b.sortOrder) {
    if (a.sortOrder === null) return 1
    if (b.sortOrder === null) return -1
    return a.sortOrder - b.sortOrder
  }
  const left = (a.pinnedAt ?? a.publishedAt).replace(/Z$/, '')
  const right = (b.pinnedAt ?? b.publishedAt).replace(/Z$/, '')
  if (left !== right) return left < right ? 1 : -1
  return b.id - a.id
}

export function previewOrder(rows: OrderRow[]) {
  return [...rows].sort(compareRows)
}

export function moveOrderRow(rows: OrderRow[], sourceId: number, targetId: number) {
  const from = rows.findIndex(row => row.id === sourceId)
  const to = rows.findIndex(row => row.id === targetId)
  if (from < 0 || to < 0 || from === to) return rows
  const desired = [...rows]
  const [moving] = desired.splice(from, 1)
  desired.splice(to, 0, moving)
  const before = desired[to - 1]?.sortOrder
  const after = desired[to + 1]?.sortOrder
  const candidates = [
    after == null ? (before ?? 0) + 10 : Math.floor(((before ?? -1) + after) / 2),
    before, after, null,
  ]
  for (const rank of candidates) {
    if (rank === undefined || (rank !== null && (rank < 0 || rank > MAX_ORDER))) continue
    const moved = { ...moving, sortOrder: rank }
    desired[to] = moved
    if (desired.every((row, i) => !i || compareRows(desired[i - 1], row) <= 0)) return desired
  }

  // When ranks collide or an automatic row must precede another, assign distinct ranks
  // to the necessary prefix. Leave the automatic tail and unaffected higher ranks alone.
  const end = Math.max(from, to, desired.findLastIndex(row => row.sortOrder !== null))
  let previous = -1
  const adjusted = desired.map((row, index) => {
    if (index > end) return row
    const value = row.sortOrder !== null && row.sortOrder > previous ? row.sortOrder : previous + 1
    previous = value
    return value === row.sortOrder ? row : { ...row, sortOrder: value }
  })
  if (previous <= MAX_ORDER) return adjusted
  return desired.map((row, i) => i <= end ? { ...row, sortOrder: i + 1 } : row)
}
