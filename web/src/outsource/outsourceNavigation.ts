export const OUTSOURCE_PERMISSIONS = {
  view: 'outsource:view',
  export: 'outsource:export',
  import: 'outsource:import',
  delete: 'outsource:delete',
  /** Complete/reopen, manual sync and columns assigned to everyone who manages the list. */
  manage: 'outsource:manage',
  /** Vendor mails, the mail records and the send-queue count. */
  send: 'outsource:send',
  /** The settings page (field settings included) and the IPMS data page. */
  settings: 'outsource:settings',
  /** Imports BOM时间 and 发图 from Excel; the list never edits these two columns. */
  dateImport: 'outsource:date_import',
} as const

export const OUTSOURCE_HOME = '/outsource/items'

export function outsourceHome(permissions: readonly string[]): string | null {
  if (permissions.includes(OUTSOURCE_PERMISSIONS.view)) return OUTSOURCE_HOME
  if (permissions.includes(OUTSOURCE_PERMISSIONS.send)) return '/outsource/mails'
  if (permissions.includes(OUTSOURCE_PERMISSIONS.settings)) return '/outsource/settings'
  return null
}
