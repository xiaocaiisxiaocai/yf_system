export type OemPreviewKind = 'pdf' | 'image' | 'excel' | 'pptx' | 'docx'

/** Same 50 MiB cap as the content endpoint (`OemDeliveryService.PreviewLimitBytes`). */
export const OEM_PREVIEW_MAX_BYTES = 50 * 1024 * 1024

/**
 * Formats shown in-page with the watermark, rendered by the collaboration preview components.
 * Anything else (legacy .doc/.ppt, TIFF/PSD, CAD, archives…) is download-only.
 */
export function oemPreviewKind(ext: string): OemPreviewKind | null {
  const normalized = ext.toLowerCase()
  if (normalized === 'pdf') return 'pdf'
  if (['png', 'jpg', 'jpeg', 'gif', 'webp', 'bmp'].includes(normalized)) return 'image'
  if (['xls', 'xlsx', 'xlsm', 'xlsb'].includes(normalized)) return 'excel'
  if (normalized === 'pptx') return 'pptx'
  if (normalized === 'docx') return 'docx'
  return null
}
