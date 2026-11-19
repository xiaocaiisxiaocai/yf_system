/**
 * Permission points of the outsourcing list (外协清单契约 → 权限). Every internal account sees the list without one;
 * who fills a column in is set per column on the field settings page.
 */
export const OUTSOURCE_PERMISSIONS = {
  /** Import, delete, complete/reopen, manual sync, and the columns whose editor setting is "everyone who manages". */
  manage: 'outsource:manage',
  /** Vendor mails, the mail records and the send-queue count. */
  send: 'outsource:send',
  /** The settings page (field settings included) and the IPMS data page. */
  settings: 'outsource:settings',
} as const

/** The outsourcing section's first page; every internal account may open it. */
export const OUTSOURCE_HOME = '/outsource/items'
