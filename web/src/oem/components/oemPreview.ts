export type OemPreviewKind = 'pdf' | 'image'

/** Formats shown in-page with the watermark; anything else keeps the legacy new-window preview. */
export function oemPreviewKind(ext: string): OemPreviewKind | null {
  const normalized = ext.toLowerCase()
  if (normalized === 'pdf') return 'pdf'
  if (['png', 'jpg', 'jpeg'].includes(normalized)) return 'image'
  return null
}
