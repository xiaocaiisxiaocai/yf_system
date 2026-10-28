/** Permissions that each unlock one tab of the merged OEM settings page. */
export const OEM_SETTINGS_PERMISSIONS = ['oem:flow_template_manage', 'oem:retention_template_manage', 'oem:file_policy_manage', 'oem:notify_manage']

export function oemHome(permissions: readonly string[]): string | null {
  if (permissions.includes('oem:transfer_create') || permissions.includes('oem:transfer_view')) return '/oem/transfers'
  if (permissions.includes('oem:flow_approve') || permissions.includes('oem:approval_recover')) return '/oem/approvals'
  if (permissions.includes('oem:company_manage') || permissions.includes('oem:account_manage')) return '/oem/admin/companies'
  if (OEM_SETTINGS_PERMISSIONS.some((code) => permissions.includes(code))) return '/oem/admin/settings'
  if (permissions.includes('oem:audit_view')) return '/oem/admin/audit'
  return null
}
