export class ArchiveLimitError extends Error {}

export interface ArchiveLimits {
  maxEntries: number
  maxEntryUncompressed: number
  maxTotalUncompressed: number
  requireZip?: boolean
}

export const spreadsheetArchiveLimits: ArchiveLimits
export const presentationArchiveLimits: ArchiveLimits

export function assertArchiveWithinLimits(input: ArrayBuffer | ArrayBufferView, limits: ArchiveLimits): Promise<void>
