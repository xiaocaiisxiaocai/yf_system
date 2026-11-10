/** Permission points of the outsourcing list (外协清单契约 → 权限). */
export const OUTSOURCE_PERMISSIONS = {
  view: 'outsource:view',
  import: 'outsource:import',
  manage: 'outsource:manage',
  settings: 'outsource:settings',
} as const

/** First page the account can use in the outsourcing section, or null when it has none. */
export function outsourceHome(permissions: readonly string[]): string | null {
  if (permissions.includes(OUTSOURCE_PERMISSIONS.view)) return '/outsource/items'
  if (permissions.includes(OUTSOURCE_PERMISSIONS.settings)) return '/outsource/settings'
  return null
}
