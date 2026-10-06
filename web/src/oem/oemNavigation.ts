export function oemHome(permissions: readonly string[]): string | null {
  if (permissions.includes('oem:transfer_create') || permissions.includes('oem:transfer_view')) return '/oem/transfers'
  if (permissions.includes('oem:flow_approve')) return '/oem/approvals'
  if (permissions.includes('oem:approval_recover')) return '/oem/recovery'
  if (permissions.includes('oem:company_manage') || permissions.includes('oem:account_manage')) return '/oem/admin/companies'
  if (permissions.includes('oem:flow_template_manage')) return '/oem/admin/flow-templates'
  if (permissions.includes('oem:retention_template_manage')) return '/oem/admin/retention'
  if (permissions.includes('oem:file_policy_manage') || permissions.includes('oem:notify_manage')) return '/oem/admin/settings'
  if (permissions.includes('oem:audit_view')) return '/oem/admin/audit'
  return null
}
