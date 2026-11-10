const pad = (value: number) => String(value).padStart(2, '0')

function valid(year: number, month: number, day: number) {
  const date = new Date(Date.UTC(year, month - 1, day))
  return date.getUTCFullYear() === year && date.getUTCMonth() === month - 1 && date.getUTCDate() === day
}

/**
 * Normalizes an Excel date cell to YYYY-MM-DD. Accepts year-first text (2026-11-04, 2026/11/4, 2026.11.4) and
 * Excel's default display of date cells (11/4/26, 11/4/2026). Anything else is passed through so the server
 * reports it as an invalid date on that row.
 */
export function normalizeImportDate(raw: string | undefined): string {
  const value = (raw ?? '').trim()
  let match = /^(\d{4})[-/.](\d{1,2})[-/.](\d{1,2})$/.exec(value)
  if (match) {
    const [year, month, day] = [Number(match[1]), Number(match[2]), Number(match[3])]
    return valid(year, month, day) ? `${year}-${pad(month)}-${pad(day)}` : value
  }
  match = /^(\d{1,2})\/(\d{1,2})\/(\d{2}|\d{4})$/.exec(value)
  if (match) {
    const year = match[3].length === 2 ? 2000 + Number(match[3]) : Number(match[3])
    const [month, day] = [Number(match[1]), Number(match[2])]
    return valid(year, month, day) ? `${year}-${pad(month)}-${pad(day)}` : value
  }
  return value
}
