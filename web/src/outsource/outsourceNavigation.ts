/** Permission points of the outsourcing list (外协清单契约 → 权限). */
export const OUTSOURCE_PERMISSIONS = {
  view: 'outsource:view',
  import: 'outsource:import',
  manage: 'outsource:manage',
  send: 'outsource:send',
  settings: 'outsource:settings',
  fieldSettings: 'outsource:field_settings',
} as const

/** The settings page holds the reference data (outsource:settings) and the field settings (outsource:field_settings). */
export const canOpenOutsourceSettings = (permissions: readonly string[]) =>
  permissions.includes(OUTSOURCE_PERMISSIONS.settings) || permissions.includes(OUTSOURCE_PERMISSIONS.fieldSettings)

/** First page the account can use in the outsourcing section, or null when it has none. */
export function outsourceHome(permissions: readonly string[]): string | null {
  if (permissions.includes(OUTSOURCE_PERMISSIONS.view)) return '/outsource/items'
  if (canOpenOutsourceSettings(permissions)) return '/outsource/settings'
  return null
}
