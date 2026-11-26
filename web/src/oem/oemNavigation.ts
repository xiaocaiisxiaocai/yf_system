import { OUTSOURCE_PERMISSIONS } from '../outsource/outsourceNavigation'

export const OUTSOURCE_MANAGEMENT_PATH = '/oem/admin/outsource-settings'
export const OEM_DIRECTORY_PERMISSIONS = ['oem:company_manage', 'oem:account_manage', 'oem:directory_view']
export const OUTSOURCE_MANAGEMENT_PERMISSIONS = [...OEM_DIRECTORY_PERMISSIONS, OUTSOURCE_PERMISSIONS.settings]

/** Permissions that each unlock one tab of the file transfer settings page. */
export const OEM_SETTINGS_PERMISSIONS = ['oem:flow_template_manage', 'oem:retention_template_manage', 'oem:file_policy_manage', 'oem:notify_manage']

export function oemHome(permissions: readonly string[]): string | null {
  if (permissions.includes('oem:transfer_create') || permissions.includes('oem:transfer_view')) return '/oem/transfers'
  if (permissions.includes('oem:flow_approve') || permissions.includes('oem:approval_recover')) return '/oem/approvals'
  if (OEM_DIRECTORY_PERMISSIONS.some((code) => permissions.includes(code))) return OUTSOURCE_MANAGEMENT_PATH
  if (OEM_SETTINGS_PERMISSIONS.some((code) => permissions.includes(code))) return '/oem/admin/settings'
  if (permissions.includes('oem:audit_view')) return '/oem/admin/audit'
  return null
}
