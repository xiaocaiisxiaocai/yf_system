/**
 * Normalize editable string lists using the same semantics as the API:
 * trim values, ignore blanks, and keep the first value for a
 * case-insensitive duplicate.
 */
export function normalizeList(values?: string[]) {
  const unique: string[] = []
  const seen = new Set<string>()
  for (const value of values ?? []) {
    const normalized = String(value).trim()
    if (!normalized) continue
    const key = normalized.toLowerCase()
    if (seen.has(key)) continue
    seen.add(key)
    unique.push(normalized)
  }
  return unique
}
